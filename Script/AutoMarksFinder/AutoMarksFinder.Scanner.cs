using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using OmenTools.Threading.TaskHelper.Enums;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterScanner : IDisposable
    {
        private enum ScanState
        {
            Idle,
            Initializing,
            MovingToPoint,
            Scanning,
            SwitchingInstance,
            SwitchingZone,
            Completed
        }

        private const float MOUNT_SPEED       = 12f;
        private const float TELEPORT_DURATION = 10f;
        private const float DETECTION_RADIUS  = 100f;

        private readonly AutoMarksFinder module;

        private ScanState                       currentState;
        private uint                            currentScanZone;
        private uint                            currentInstance;
        private int                             currentScanPointIndex;
        private List<NotoriousMonsterScanPoint> currentSortedPoints = [];
        private Vector3?                        currentTargetPoint;
        private long                            lastMonsterFoundTime;

        private Dictionary<ulong, ZoneScanProgress> zoneScanProgress = [];

        public Dictionary<uint, HashSet<NotoriousMonsterFound>> FoundOnes { get; set; } = [];

        public NotoriousMonsterScanner
        (
            AutoMarksFinder                   module,
            IEnumerable<NotoriousMonsterRank> ranks,
            IEnumerable<uint>                 zones
        )
        {
            this.module = module;

            module.config.SelectedRanks = ranks.Distinct().ToHashSet();
            module.config.SelectedZones = zones.Distinct().ToHashSet();

            currentState = ScanState.Idle;

            FrameworkManager.Instance().Reg(OnUpdate, 100);
        }

        public void Dispose() =>
            FrameworkManager.Instance().Unreg(OnUpdate);

        public (uint ZoneID, List<NotoriousMonsterScanPoint> Points, int CurrentIndex) GetCurrentScanState() =>
            (currentScanZone, currentSortedPoints, currentScanPointIndex);

        public void StartScan()
        {
            currentState = ScanState.Initializing;
            StateCheck();
        }

        private void StateCheck()
        {
            module.TaskHelper.Abort();

            if (!CheckPreCondition())
                return;

            switch (currentState)
            {
                case ScanState.Initializing:
                    HandleInitializing();
                    break;

                case ScanState.MovingToPoint:
                    HandleMovingToPoint();
                    break;

                case ScanState.Scanning:
                    HandleScanning();
                    break;

                case ScanState.SwitchingInstance:
                    HandleSwitchingInstance();
                    break;

                case ScanState.SwitchingZone:
                    HandleSwitchingZone();
                    break;

                case ScanState.Completed:
                    module.StopScanner();
                    break;
            }
        }

        private void EnqueueCheckState
        (
            string name
        ) =>
            module.TaskHelper.Enqueue(StateCheck, name);

        private bool CheckPreCondition()
        {
            if (!UIModule.IsScreenReady() || IObjectTable.Instance().LocalPlayer == null)
            {
                EnqueueCheckState("等待屏幕就绪, 进入新循环");
                return false;
            }

            if (!IDalamudPluginInterface.Instance().IsPluginEnabled(vnavmeshIPC.INTERNAL_NAME) || !vnavmeshIPC.GetIsNavReady())
            {
                EnqueueCheckState("等待导航就绪, 进入新循环");
                return false;
            }

            return true;
        }

        private void HandleInitializing()
        {
            var currentZone = GameState.TerritoryType;

            if (!module.config.SelectedZones.Contains(currentZone))
            {
                currentState = ScanState.SwitchingZone;
                EnqueueCheckState("当前区域不在扫描列表，切换区域");
                return;
            }

            if (!module.config.ScanPoints.TryGetValue(currentZone, out var points) || points.Count == 0)
            {
                module.config.SelectedZones.Remove(currentZone);
                currentState = ScanState.SwitchingZone;
                EnqueueCheckState("当前区域无扫描点，切换区域");
                return;
            }

            currentScanZone       = currentZone;
            currentInstance       = InstancesManager.CurrentInstance;
            currentScanPointIndex = 0;
            currentSortedPoints   = SortPoints(points);

            currentState = ScanState.MovingToPoint;
            EnqueueCheckState("初始化完成，开始移动");
        }

        private void HandleMovingToPoint()
        {
            if (currentScanPointIndex >= currentSortedPoints.Count)
            {
                currentState = ScanState.SwitchingZone;
                EnqueueCheckState("所有扫描点已完成");
                return;
            }

            if (IsCurrentAreaCompleted(currentScanZone, currentInstance))
            {
                if (InstancesManager.IsInstancedArea)
                {
                    currentState = ScanState.SwitchingInstance;
                    EnqueueCheckState("当前副本线完成，切换副本线");
                }
                else
                {
                    currentState = ScanState.SwitchingZone;
                    EnqueueCheckState("当前区域完成，切换区域");
                }

                return;
            }

            var currentPoint = currentSortedPoints[currentScanPointIndex];
            currentTargetPoint = currentPoint.Position;

            var decision = DecideOptimalPath(currentPoint.Position, currentScanZone);

            switch (decision.Type)
            {
                case PathDecision.PathType.TeleportThenNav:
                    EnqueueTeleportRoute(decision);
                    break;
                case PathDecision.PathType.DirectNav:
                    EnqueueDirectRoute(decision);
                    break;
                case PathDecision.PathType.SmartTP:
                    EnqueueSmartTPRoute(decision);
                    break;
            }

            currentState = ScanState.Scanning;
            EnqueueCheckState("开始扫描");
        }

        private void HandleScanning()
        {
            if (!currentTargetPoint.HasValue)
            {
                MoveToNextPoint("无目标点");
                return;
            }

            var distance2D = LocalPlayerState.DistanceTo2D(currentTargetPoint.Value.ToVector2());

            if (distance2D <= DETECTION_RADIUS)
            {
                module.TaskHelper.DelayNext(500, "等待怪物加载");
                module.TaskHelper.Enqueue
                (
                    () =>
                    {
                        vnavmeshIPC.StopPathfind();
                        module.TaskHelper.Abort();
                        MoveToNextPoint("已进入检测范围");
                        return true;
                    },
                    "进入检测范围"
                );
                return;
            }

            module.TaskHelper.Enqueue
            (
                () =>
                {
                    if (!ICondition.Instance().Any(ConditionFlag.BetweenAreas, ConditionFlag.Jumping))
                    {
                        var currentDistance = LocalPlayerState.DistanceTo2D(currentTargetPoint.Value.ToVector2());

                        if (currentDistance <= DETECTION_RADIUS)
                        {
                            vnavmeshIPC.StopPathfind();
                            module.TaskHelper.Abort();
                            MoveToNextPoint("移动中进入检测范围");
                            return true;
                        }
                    }

                    return false;
                },
                "等待进入检测范围"
            );
        }

        private void MoveToNextPoint
        (
            string reason
        )
        {
            currentScanPointIndex++;
            currentState = currentScanPointIndex >= currentSortedPoints.Count ?
                               ScanState.SwitchingZone :
                               ScanState.MovingToPoint;
            EnqueueCheckState($"{reason}，跳转下一点");
        }

        private unsafe void HandleSwitchingInstance()
        {
            var currentZone     = GameState.TerritoryType;
            var currentInstance = InstancesManager.CurrentInstance;
            var totalInstances  = InstancesManager.Instance().GetInstancesCount(currentZone);

            var nextInstance = FindNextIncompleteInstance(currentZone, currentInstance, totalInstances);

            if (nextInstance.HasValue)
            {
                vnavmeshIPC.StopPathfind();
                module.TaskHelper.Abort();

                if (!EventFramework.Instance()->TryGetNearestEventID
                    (
                        x => x.EventId.ContentId == EventHandlerContent.Aetheryte,
                        x => x.NameString.Equals
                        (
                            LuminaGetter.GetRowOrDefault<Aetheryte>(0).Singular.ToString(),
                            StringComparison.OrdinalIgnoreCase
                        ),
                        default,
                        out _
                    ))
                {
                    module.TaskHelper.Enqueue
                    (
                        IsAbleToTeleport,
                        "等待停止移动"
                    );
                    module.TaskHelper.Enqueue
                    (
                        () => AetheryteRecordManager.Instance().GetNearestAetheryte(currentZone, Vector3.Zero)?.TeleportTo(),
                        "传送至以太之光"
                    );
                    module.TaskHelper.Enqueue
                    (
                        () => UIModule.IsScreenReady() &&
                              EventFramework.Instance()->TryGetNearestEventID
                              (
                                  x => x.EventId.ContentId == EventHandlerContent.Aetheryte,
                                  x => x.NameString.Equals
                                  (
                                      LuminaGetter.GetRowOrDefault<Aetheryte>(0).Singular.ToString(),
                                      StringComparison.OrdinalIgnoreCase
                                  ),
                                  default,
                                  out _
                              ),
                        "等待靠近以太之光"
                    );
                }

                module.TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage($"/pdr insc {nextInstance.Value}"),              $"切换至 {nextInstance.Value} 线");
                module.TaskHelper.Enqueue(() => UIModule.IsScreenReady() && InstancesManager.CurrentInstance == nextInstance.Value, "等待切换完毕");

                this.currentInstance  = nextInstance.Value;
                currentScanPointIndex = 0;
                currentState          = ScanState.Initializing;

                EnqueueCheckState("副本线切换完成，重新开始扫描");
            }
            else
            {
                currentState = ScanState.SwitchingZone;
                EnqueueCheckState("当前区域所有副本线完成");
            }
        }

        private void HandleSwitchingZone()
        {
            var currentZone = GameState.TerritoryType;

            if (module.config.SelectedZones.Contains(currentZone))
            {
                vnavmeshIPC.StopPathfind();
                module.TaskHelper.Abort();

                module.config.SelectedZones.Remove(currentZone);
            }

            var targetZone = module.config.SelectedZones.FirstOrDefault();

            if (targetZone == 0)
            {
                currentState = ScanState.Completed;
                EnqueueCheckState("所有区域扫描完成");
                return;
            }

            if (!module.config.ScanPoints.TryGetValue(targetZone, out var points) || points.Count == 0)
            {
                module.config.SelectedZones.Remove(targetZone);
                EnqueueCheckState("目标区域无扫描点，移除并重新检查");
                return;
            }

            vnavmeshIPC.StopPathfind();

            module.TaskHelper.Enqueue
            (
                IsAbleToTeleport,
                "等待停止移动"
            );
            module.TaskHelper.Enqueue
            (
                () => MovementManager.Instance().TPSmart_BetweenZone(targetZone),
                $"传送至区域 {targetZone}"
            );
            module.TaskHelper.Enqueue
            (
                () => GameState.TerritoryType == targetZone && UIModule.IsScreenReady(),
                "等待传送完毕"
            );
            module.TaskHelper.Enqueue
            (
                () =>
                {
                    if (!InstancesManager.IsInstancedArea) return;

                    if (InstancesManager.CurrentInstance != 1)
                    {
                        module.TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/pdr insc 1"),                 "切换至 1 线", weight: 1);
                        module.TaskHelper.Enqueue(() => UIModule.IsScreenReady() && InstancesManager.CurrentInstance == 1, "等待切换完毕",  weight: 1);
                    }
                },
                "检查副本区切换"
            );

            currentState = ScanState.Initializing;
            EnqueueCheckState("传送完毕，开始扫描");
        }

        private bool IsCurrentAreaCompleted
        (
            uint zoneID,
            uint instance
        )
        {
            var expectedAmount = GameState.TerritoryTypeData.ExVersion.RowId == 0 ?
                                     1 :
                                     2;

            var key = GetProgressKey(zoneID, instance);

            if (!zoneScanProgress.TryGetValue(key, out var progress))
            {
                progress              = new ZoneScanProgress { ZoneID = zoneID, Instance = instance };
                zoneScanProgress[key] = progress;
            }

            if (FoundOnes.TryGetValue(zoneID, out var monsters))
            {
                progress.FoundARank = monsters.Count(m => m.Rank == NotoriousMonsterRank.A && m.Instance == instance);
                progress.FoundBRank = monsters.Count(m => m.Rank == NotoriousMonsterRank.B && m.Instance == instance);
            }

            var hasEnoughMonsters = progress.IsCompleted(module.config.SelectedRanks, expectedAmount);
            var allPointsScanned  = currentScanPointIndex >= currentSortedPoints.Count;

            return hasEnoughMonsters || allPointsScanned;
        }

        private uint? FindNextIncompleteInstance
        (
            uint zoneID,
            uint currentInstance,
            int  totalInstances
        )
        {
            for (var i = currentInstance + 1; i <= totalInstances; i++)
                if (!IsCurrentAreaCompleted(zoneID, i))
                    return i;

            return null;
        }

        private static ulong GetProgressKey
        (
            uint zoneID,
            uint instance
        ) =>
            ((ulong)zoneID << 32) | instance;

        private void OnUpdate
        (
            IFramework _
        )
        {
            var currentZone     = GameState.TerritoryType;
            var currentInstance = InstancesManager.CurrentInstance;

            if (!module.config.SelectedZones.Contains(currentZone) || !module.config.ScanPoints.TryGetValue(currentZone, out var _))
                return;

            var foundNewMonster = false;

            foreach (var obj in IObjectTable.Instance())
            {
                if (obj is not { ObjectKind: ObjectKind.BattleNpc, IsDead: false }) continue;
                if (!NotoriousMonsterFound.NotoriousMonsters.TryGetValue(obj.DataID, out var dataRowID)) continue;
                if (!LuminaGetter.TryGetRow<NotoriousMonster>(dataRowID, out var dataRow)) continue;
                if (!module.config.SelectedRanks.Contains((NotoriousMonsterRank)dataRow.Rank)) continue;

                FoundOnes.TryAdd(currentZone, []);

                var parsed = new NotoriousMonsterFound(obj.DataID, obj.Position, currentZone, currentInstance);
                if (!FoundOnes[currentZone].Add(parsed)) continue;

                foundNewMonster      = true;
                lastMonsterFoundTime = StandardTimeManager.Instance().UTCNowOffset.ToUnixTimeMilliseconds();

                var mapPos = PositionHelper.WorldToMap(obj.Position.ToVector2(), GameState.MapData);
                NotifyHelper.Instance().Chat
                (
                    Lang.GetSe
                    (
                        "AutoMarksFinder-DetectPowerfulMark",
                        (NotoriousMonsterRank)dataRow.Rank,
                        dataRow.BNpcName.Value.Singular.ToString(),
                        SeString.CreateMapLink(currentZone, GameState.Map, mapPos.X, mapPos.Y)
                    )
                );
            }

            if (foundNewMonster && module.TaskHelper.IsBusy && currentScanZone == currentZone)
            {
                if (IsCurrentAreaCompleted(currentZone, currentInstance))
                {
                    vnavmeshIPC.StopPathfind();
                    module.TaskHelper.Abort();

                    if (InstancesManager.IsInstancedArea)
                    {
                        currentState = ScanState.SwitchingInstance;
                        EnqueueCheckState("发现足够怪物，切换副本线");
                    }
                    else
                    {
                        currentState = ScanState.SwitchingZone;
                        EnqueueCheckState("发现足够怪物，切换区域");
                    }
                }
                else
                {
                    vnavmeshIPC.StopPathfind();
                    module.TaskHelper.Abort();
                    MoveToNextPoint("发现新怪物");
                }
            }

            if (!module.config.SelectedZones.Any() && !module.TaskHelper.IsBusy)
                module.StopScanner();
        }

        private static PathDecision DecideOptimalPath
        (
            Vector3 targetPoint,
            uint    zoneID
        )
        {
            var currentPos     = LocalPlayerState.Object?.Position ?? Vector3.Zero;
            var directDistance = Vector3.Distance(currentPos, targetPoint);

            // if ((GameState.IsCN || GameState.IsTC) && AuthState.IsPremium)
            // {
            //     return new PathDecision
            //     {
            //         Type           = PathDecision.PathType.SmartTP,
            //         TargetPosition = targetPoint,
            //         EstimatedTime  = 0
            //     };
            // }

            if (directDistance <= 100)
            {
                return new PathDecision
                {
                    Type           = PathDecision.PathType.DirectNav,
                    TargetPosition = targetPoint,
                    EstimatedTime  = directDistance / MOUNT_SPEED
                };
            }

            var nearestAetheryte = AetheryteRecordManager.Instance().GetNearestAetheryte(zoneID, targetPoint);

            if (nearestAetheryte == null)
            {
                return new PathDecision
                {
                    Type           = PathDecision.PathType.DirectNav,
                    TargetPosition = targetPoint,
                    EstimatedTime  = directDistance / MOUNT_SPEED
                };
            }

            var aetherytePos                  = vnavmeshIPC.QueryNearestPointOnMesh(nearestAetheryte.Position, 15, 10000) ?? nearestAetheryte.Position;
            var distanceFromAetheryteToTarget = Vector3.Distance(aetherytePos, targetPoint);

            var directTime   = directDistance / MOUNT_SPEED;
            var teleportTime = TELEPORT_DURATION + (distanceFromAetheryteToTarget / MOUNT_SPEED);

            if (teleportTime < directTime)
            {
                return new PathDecision
                {
                    Type           = PathDecision.PathType.TeleportThenNav,
                    TargetPosition = targetPoint,
                    Aetheryte      = nearestAetheryte,
                    EstimatedTime  = teleportTime
                };
            }

            return new PathDecision
            {
                Type           = PathDecision.PathType.DirectNav,
                TargetPosition = targetPoint,
                EstimatedTime  = directTime
            };
        }

        private void EnqueueTeleportRoute
        (
            PathDecision decision
        )
        {
            module.TaskHelper.Enqueue(IsAbleToTeleport, "等待停止移动");
            module.TaskHelper.Enqueue
            (
                () => AetheryteRecordManager.Instance().GetNearestAetheryte(currentScanZone, decision.TargetPosition)?.TeleportTo(),
                $"传送至距离 {decision.TargetPosition} 最近的以太之光"
            );

            var aetherytePos = vnavmeshIPC.QueryNearestPointOnMesh(decision.Aetheryte.Position, 15, 10000) ?? decision.Aetheryte.Position;
            module.TaskHelper.Enqueue
            (
                () => UIModule.IsScreenReady() && LocalPlayerState.DistanceTo2D(aetherytePos.ToVector2()) <= 60,
                "等待以太之光传送完毕"
            );

            EnqueueMount(module.TaskHelper);
            EnqueuePathfind(decision.TargetPosition);
        }

        private void EnqueueDirectRoute
        (
            PathDecision decision
        )
        {
            module.TaskHelper.Enqueue(IsAbleToTeleport, "等待停止移动");

            if (vnavmeshIPC.QueryNearestPointOnMesh(decision.TargetPosition, 10, 10000) is not { } finalPos)
            {
                MoveToNextPoint("跳过无效扫描点");
                return;
            }

            EnqueueMount(module.TaskHelper);
            EnqueuePathfind(finalPos);
        }

        private void EnqueueSmartTPRoute
        (
            PathDecision decision
        )
        {
            EnqueueMount(module.TaskHelper);
            module.TaskHelper.Enqueue
            (
                () => MovementManager.Instance().TPSmart_InZone(decision.TargetPosition),
                $"TP 至: {decision.TargetPosition}"
            );
            module.TaskHelper.DelayNext(module.config.DelayMS);
        }

        private void EnqueuePathfind
        (
            Vector3 targetPos
        ) =>
            module.TaskHelper.EnqueueAsync
            (
                async ct =>
                {
                    var isOnMount    = ICondition.Instance()[ConditionFlag.Mounted];
                    var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var paths = await vnavmeshIPC.PathfindCancelable
                                (
                                    LocalPlayerState.Object?.Position ?? Vector3.Zero,
                                    targetPos,
                                    isOnMount,
                                    cancelSource.Token
                                );

                    if (paths.Count == 0)
                    {
                        vnavmeshIPC.StopPathfind();
                        module.TaskHelper.Abort();
                        MoveToNextPoint("寻路失败");
                        return;
                    }

                    if (Vector2.DistanceSquared(paths.Last().ToVector2(), targetPos.ToVector2()) > 625)
                    {
                        vnavmeshIPC.StopPathfind();
                        module.TaskHelper.Abort();
                        MoveToNextPoint("路径无法到达");
                        return;
                    }

                    vnavmeshIPC.SetPathfindTolerance(2f);
                    vnavmeshIPC.PathfindWithPath(paths, isOnMount);
                },
                $"移动至: {targetPos}",
                30_000,
                TaskAbortBehaviour.AbortAll,
                timeoutAction: () =>
                {
                    vnavmeshIPC.StopPathfind();
                    module.TaskHelper.Abort();
                    MoveToNextPoint("寻路超时");
                }
            );

        private static bool IsAbleToTeleport() =>
            !LocalPlayerState.Instance().IsMoving                     &&
            !ICondition.Instance().Any(ConditionFlag.Casting) &&
            !ICondition.Instance().IsBetweenAreas             &&
            UIModule.IsScreenReady();

        private static void EnqueueMount
        (
            TaskHelper taskHelper
        ) =>
            taskHelper.Enqueue
            (
                () =>
                {
                    if (ICondition.Instance()[ConditionFlag.Mounted]) return true;
                    if (!Throttler.Shared.Throttle("AutoMarksFinder-Mount", 1000)) return false;
                    if (ICondition.Instance().IsCasting) return false;

                    UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                    return false;
                },
                "上坐骑"
            );

        private static List<NotoriousMonsterScanPoint> SortPoints
        (
            List<NotoriousMonsterScanPoint> points
        )
        {
            if (points.Count == 0) return [];

            var sorted    = new List<NotoriousMonsterScanPoint>();
            var remaining = new List<NotoriousMonsterScanPoint>(points);

            var current = LocalPlayerState.Object?.Position ?? Vector3.Zero;

            while (remaining.Count > 0)
            {
                var nearest = remaining.OrderBy(p => Vector3.DistanceSquared(current, p.Position)).First();
                sorted.Add(nearest);
                remaining.Remove(nearest);
                current = nearest.Position;
            }

            for (var iteration = 0; iteration < 500; iteration++)
            {
                var improved = false;

                for (var i = 0; i < sorted.Count - 1; i++)
                for (var j = i + 1; j < sorted.Count; j++)
                {
                    var currentDistance = Vector3.Distance(sorted[i].Position, sorted[i + 1].Position) +
                                          Vector3.Distance(sorted[j].Position, sorted[(j + 1) % sorted.Count].Position);

                    var newDistance = Vector3.Distance(sorted[i].Position,     sorted[j].Position) +
                                      Vector3.Distance(sorted[i + 1].Position, sorted[(j + 1) % sorted.Count].Position);

                    if (newDistance < currentDistance)
                    {
                        sorted.Reverse(i + 1, j - i);
                        improved = true;
                    }
                }

                if (!improved) break;
            }

            return sorted;
        }
    }
}
