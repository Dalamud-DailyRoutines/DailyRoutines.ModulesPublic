using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Lumina;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterScanner
    (
        AutoMarksFinder module
    ) : IDisposable
    {
        private enum ScanState
        {
            Stopped,
            Preparing,
            SelectingPoint,
            Teleporting,
            Mounting,
            Pathfinding,
            Moving,
            Observing,
            SwitchingZone,
            SwitchingInstance
        }

        private readonly Dictionary<uint, List<Vector3>>     zones            = [];
        private readonly HashSet<(uint Zone, uint Instance)> completedAreas   = [];
        private readonly List<Vector3>                       pendingPoints    = [];
        private readonly List<Vector3>                       candidates       = [];
        private readonly List<Vector3>                       failedCandidates = [];
        private readonly List<Vector3>                       targetPoints     = [];
        private readonly Queue<Vector3>                      retryTargets     = [];
        private          CancellationTokenSource?            pathCancellation;
        private          Task<List<Vector3>>?                pathTask;
        private          ScanState                           state;
        private          long                                stateStartedAt;
        private          long                                lastProgressAt;
        private          long                                mountRequestedAt;
        private          float                               closestDistance;
        private          Vector3                             target;
        private          Vector3                             anchor;
        private          Vector3                             observationPosition;
        private          uint                                currentZone;
        private          uint                                currentInstance;
        private          uint?                               originalAroundRangeMode;
        private          bool                                disposed;

        public bool IsRunning => state != ScanState.Stopped;

        public Dictionary<uint, HashSet<NotoriousMonsterFound>> FoundOnes { get; } = [];

        public void StartScan()
        {
            if (disposed ||
                IsRunning)
                return;

            foreach (var zone in module.config.SelectedZones)
            {
                if (module.config.ScanPoints.TryGetValue(zone, out var points) &&
                    points.Count > 0)
                    zones[zone] = [.. points.Select(point => point.Position).Distinct()];
            }

            if (zones.Count == 0)
                return;

            if (IGameConfig.Instance().System.TryGetUInt(nameof(SystemConfigOption.DynamicAroundRangeMode), out var mode) &&
                mode <= 2)
            {
                originalAroundRangeMode = mode;

                if (mode != 2)
                {
                    IGameConfig.Instance().System.Set(nameof(SystemConfigOption.DynamicAroundRangeMode), 2u);
                    ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.SetAroundRangeMode, 2);
                }
            }

            ChangeState(ScanState.Preparing);
            FrameworkManager.Instance().Reg(OnUpdate, UPDATE_INTERVAL_MS);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            var previousState = state;
            disposed = true;
            state    = ScanState.Stopped;
            FrameworkManager.Instance().Unreg(OnUpdate);
            module.TaskHelper.Abort();
            CancelPath();
            if (vnavmeshIPC.IsPluginEnabled())
                vnavmeshIPC.StopPathfind();

            switch (previousState)
            {
                case ScanState.SwitchingInstance:
                    ChatManager.Instance().SendMessage("/pdr insc abort");
                    break;
                case ScanState.SwitchingZone:
                    MovementManager.Instance().AbortTasks();
                    break;
            }

            if (originalAroundRangeMode is { } mode)
            {
                IGameConfig.Instance().System.Set(nameof(SystemConfigOption.DynamicAroundRangeMode), mode);
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.SetAroundRangeMode, mode);
                originalAroundRangeMode = null;
            }
        }

        public void ObserveMonster(uint dataID, Vector3 position)
        {
            if (!IsRunning                                                                  ||
                !zones.ContainsKey(GameState.TerritoryType)                                 ||
                !NotoriousMonsterFound.NotoriousMonsters.TryGetValue(dataID, out var rowID) ||
                !LuminaGetter.TryGetRow<NotoriousMonster>(rowID, out var row)               ||
                row.Rank != 2)
                return;

            var zone = GameState.TerritoryType;
            if (!FoundOnes.TryGetValue(zone, out var found))
                FoundOnes[zone] = found = [];

            if (!found.Add(new(dataID, position, zone, InstancesManager.CurrentInstance)))
                return;

            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoMarksFinder-DetectPowerfulMark",
                    new Dictionary<string, object>
                    {
                        ["name"]     = row.BNpcName.Value.Singular.ToString(),
                        ["position"] = ReadOnlySeString.CreateMapLink(position, zone, GameState.Map)
                    }
                )
            );
        }

        private void OnUpdate(IFramework framework)
        {
            if (!IsRunning)
                return;

            try
            {
                if (!GameState.IsLoggedIn ||
                    Environment.TickCount64 - stateStartedAt > STATE_TIMEOUT_MS)
                {
                    StopWithWarning("AutoMarksFinder-ScanInterrupted");
                    return;
                }

                if (!UIModule.IsScreenReady()       ||
                    LocalPlayerState.Object == null ||
                    ICondition.Instance().IsBetweenAreas)
                    return;

                if (LocalPlayerState.Object.IsDead)
                {
                    StopWithWarning("AutoMarksFinder-ScanInterrupted");
                    return;
                }

                if (state is ScanState.SwitchingZone or ScanState.SwitchingInstance or ScanState.Teleporting)
                {
                    if (module.TaskHelper.IsBusy)
                        return;

                    if (GameState.TerritoryType          != currentZone ||
                        InstancesManager.CurrentInstance != currentInstance)
                    {
                        StopWithWarning("AutoMarksFinder-ScanInterrupted");
                        return;
                    }

                    observationPosition = LocalPlayerState.Object.Position;
                    ChangeState
                    (
                        state == ScanState.Teleporting ?
                            ScanState.Observing :
                            ScanState.Preparing
                    );
                    return;
                }

                if (!vnavmeshIPC.IsPluginEnabled() ||
                    !vnavmeshIPC.GetIsNavReady()   ||
                    MovementManager.Instance().IsManagerBusy)
                    return;

                if (state != ScanState.Preparing &&
                    (GameState.TerritoryType          != currentZone ||
                     InstancesManager.CurrentInstance != currentInstance))
                {
                    StopWithWarning("AutoMarksFinder-ScanInterrupted");
                    return;
                }

                if (state != ScanState.Preparing &&
                    HasEnoughMonsters())
                {
                    CompleteArea();
                    return;
                }

                switch (state)
                {
                    case ScanState.Preparing:
                        PrepareArea();
                        break;
                    case ScanState.SelectingPoint:
                        SelectPoint();
                        break;
                    case ScanState.Mounting:
                        Mount();
                        break;
                    case ScanState.Pathfinding:
                        UpdatePathfinding();
                        break;
                    case ScanState.Moving:
                        UpdateMovement();
                        break;
                    case ScanState.Observing:
                        ObserveArea();
                        break;
                }
            }
            catch (Exception exception)
            {
                DLog.Error(exception);
                StopWithWarning("AutoMarksFinder-ScanInterrupted");
            }
        }

        private void PrepareArea()
        {
            currentZone     = GameState.TerritoryType;
            currentInstance = InstancesManager.CurrentInstance;

            if (!zones.TryGetValue(currentZone, out var points))
            {
                SwitchZone();
                return;
            }

            pendingPoints.Clear();
            pendingPoints.AddRange(points);
            failedCandidates.Clear();
            retryTargets.Clear();
            BuildCandidates();
            ChangeState(ScanState.Observing);
            observationPosition = LocalPlayerState.Object.Position;
        }

        private void SelectPoint()
        {
            if (pendingPoints.Count == 0)
            {
                CompleteArea();
                return;
            }

            var              position     = LocalPlayerState.Object.Position;
            var              bestCoverage = 0;
            var              bestCost     = float.MaxValue;
            AetheryteRecord? teleport     = null;

            foreach (var candidate in candidates)
            {
                if (failedCandidates.Any(failed => DistanceSquared(failed, candidate) < 1))
                    continue;

                var coveredPoints = pendingPoints.Where(point => DistanceSquared(point, candidate) <= PLANNING_RADIUS * PLANNING_RADIUS).ToList();
                var coverage      = coveredPoints.Count;
                if (coverage == 0 ||
                    coverage < bestCoverage)
                    continue;

                var margin      = PLANNING_RADIUS - coveredPoints.Max(point => MathF.Sqrt(DistanceSquared(point, candidate)));
                var cost        = MathF.Max(0, MathF.Sqrt(DistanceSquared(position,                              candidate)) - margin) / MOUNT_SPEED;
                var aetheryte   = AetheryteRecordManager.Instance().GetNearestAetheryte(currentZone, candidate);
                var useTeleport = false;

                if (aetheryte != null)
                {
                    var teleportCost = TELEPORT_DURATION + (MathF.Max(0, MathF.Sqrt(DistanceSquared(aetheryte.Position, candidate)) - margin) / MOUNT_SPEED);

                    if (teleportCost < cost)
                    {
                        cost        = teleportCost;
                        useTeleport = true;
                    }
                }

                if (coverage == bestCoverage &&
                    cost     >= bestCost)
                    continue;

                bestCoverage = coverage;
                bestCost     = cost;
                target       = candidate;
                teleport = useTeleport ?
                               aetheryte :
                               null;
            }

            if (bestCoverage == 0)
            {
                StopWithWarning("AutoMarksFinder-UnreachablePoints");
                return;
            }

            anchor = pendingPoints.MinBy(point => DistanceSquared(point, target));
            targetPoints.Clear();
            targetPoints.AddRange(pendingPoints.Where(point => DistanceSquared(point, target) <= PLANNING_RADIUS * PLANNING_RADIUS));

            if (teleport != null)
            {
                var destination = teleport;
                module.TaskHelper.Enqueue(IsAbleToTeleport,       "等待传送");
                module.TaskHelper.Enqueue(destination.TeleportTo, "传送至以太之光");
                module.TaskHelper.DelayNext(500, "等待传送开始");
                module.TaskHelper.Enqueue
                (
                    () => UIModule.IsScreenReady() &&
                          DistanceSquared(LocalPlayerState.Object.Position, destination.Position) <= AETHERYTE_RADIUS * AETHERYTE_RADIUS,
                    "等待传送完成"
                );
                ChangeState(ScanState.Teleporting);
                return;
            }

            retryTargets.Clear();
            retryTargets.Enqueue(target);

            for (var ring = 1; ring <= RETRY_RINGS; ring++)
            for (var direction = 0; direction < RETRY_DIRECTIONS; direction++)
            {
                var angle  = MathF.Tau * direction / RETRY_DIRECTIONS;
                var nearby = target + (new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (ring * RETRY_DISTANCE));
                if (DistanceSquared(nearby, anchor) <= PLANNING_RADIUS * PLANNING_RADIUS)
                    retryTargets.Enqueue(nearby);
            }

            ChangeState(ScanState.Mounting);
        }

        private void Mount()
        {
            if (ICondition.Instance()[ConditionFlag.Mounted])
            {
                StartPathfinding();
                return;
            }

            if (Environment.TickCount64 - stateStartedAt > MOUNT_TIMEOUT_MS)
            {
                StopWithWarning("AutoMarksFinder-ScanInterrupted");
                return;
            }

            if (ICondition.Instance().IsCasting ||
                Environment.TickCount64 - mountRequestedAt < MOUNT_RETRY_MS)
                return;

            mountRequestedAt = Environment.TickCount64;
            UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
        }

        private void StartPathfinding()
        {
            CancelPath();

            while (retryTargets.TryDequeue(out var candidate))
            {
                if (vnavmeshIPC.QueryNearestPointOnMesh(candidate, MESH_SEARCH_RADIUS, MESH_SEARCH_HEIGHT) is not { } meshPoint ||
                    DistanceSquared(meshPoint, anchor) > PLANNING_RADIUS * PLANNING_RADIUS)
                    continue;

                var fly = ICondition.Instance()[ConditionFlag.Mounted] && Control.CanFly;
                pathCancellation = new();
                pathTask         = vnavmeshIPC.PathfindCancelable(LocalPlayerState.Object.Position, meshPoint, fly, pathCancellation.Token);
                ChangeState(ScanState.Pathfinding);
                return;
            }

            failedCandidates.Add(target);
            ChangeState(ScanState.SelectingPoint);
        }

        private void UpdatePathfinding()
        {
            if (pathTask == null)
            {
                StartPathfinding();
                return;
            }

            if (!pathTask.IsCompleted)
            {
                if (Environment.TickCount64 - stateStartedAt >= PATHFIND_TIMEOUT_MS)
                    StartPathfinding();

                return;
            }

            if (!pathTask.IsCompletedSuccessfully)
            {
                StartPathfinding();
                return;
            }

            var path = pathTask.Result;

            if (path.Count                        == 0 ||
                DistanceSquared(path[^1], anchor) > PLANNING_RADIUS * PLANNING_RADIUS)
            {
                StartPathfinding();
                return;
            }

            CancelPath();
            var endIndex = path.FindIndex(point => DistanceSquared(point, target) <= ARRIVAL_RADIUS * ARRIVAL_RADIUS);
            if (endIndex >= 0)
                path.RemoveRange(endIndex + 1, path.Count - endIndex - 1);

            path[0] = LocalPlayerState.Object.Position;
            vnavmeshIPC.SetPathfindTolerance(2f);
            vnavmeshIPC.PathfindWithPath(path, ICondition.Instance()[ConditionFlag.Mounted] && Control.CanFly);
            closestDistance = DistanceSquared(LocalPlayerState.Object.Position, target);
            lastProgressAt  = Environment.TickCount64;
            ChangeState(ScanState.Moving);
        }

        private void UpdateMovement()
        {
            var position = LocalPlayerState.Object.Position;
            var distance = DistanceSquared(position, target);

            if (distance <= ARRIVAL_RADIUS * ARRIVAL_RADIUS ||
                targetPoints.All(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS))
            {
                vnavmeshIPC.StopPathfind();
                observationPosition = position;
                ChangeState(ScanState.Observing);
                return;
            }

            if (distance < closestDistance - (PROGRESS_DISTANCE * PROGRESS_DISTANCE))
            {
                closestDistance = distance;
                lastProgressAt  = Environment.TickCount64;
            }

            if (!vnavmeshIPC.GetIsPathfindRunning() ||
                Environment.TickCount64 - lastProgressAt > MOVEMENT_TIMEOUT_MS)
            {
                vnavmeshIPC.StopPathfind();

                if (pendingPoints.Any(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS))
                {
                    observationPosition = position;
                    ChangeState(ScanState.Observing);
                }
                else
                    StartPathfinding();
            }
        }

        private void ObserveArea()
        {
            var position = LocalPlayerState.Object.Position;

            if (DistanceSquared(position, observationPosition) > 1)
            {
                observationPosition = position;
                stateStartedAt      = Environment.TickCount64;
                return;
            }

            if (Environment.TickCount64 - stateStartedAt < OBSERVATION_DELAY_MS)
                return;

            foreach (var obj in IObjectTable.Instance())
            {
                if (obj is { ObjectKind: ObjectKind.BattleNpc, IsDead: false })
                    ObserveMonster(obj.DataID, obj.Position);
            }

            pendingPoints.RemoveAll(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS);
            ChangeState(ScanState.SelectingPoint);
        }

        private bool HasEnoughMonsters() =>
            FoundOnes.TryGetValue(currentZone, out var found) &&
            found.Count(monster => monster.Instance == currentInstance) >=
            (GameState.TerritoryTypeData.ExVersion.RowId == 0 ?
                 1 :
                 2);

        private void CompleteArea()
        {
            CancelPath();
            vnavmeshIPC.StopPathfind();
            completedAreas.Add((currentZone, currentInstance));
            var instanceCount = InstancesManager.IsInstancedArea ?
                                    InstancesManager.Instance().GetInstancesCount(currentZone) :
                                    0;

            for (uint instance = 1; instance <= instanceCount; instance++)
            {
                if (completedAreas.Contains((currentZone, instance)))
                    continue;

                currentInstance = instance;
                module.TaskHelper.Enqueue(IsAbleToTeleport, "等待切换分线");
                var aetheryte = AetheryteRecordManager.Instance().GetNearestAetheryte(currentZone, Vector3.Zero);

                if (aetheryte == null)
                {
                    StopWithWarning("AutoMarksFinder-ScanInterrupted");
                    return;
                }

                if (DistanceSquared(LocalPlayerState.Object.Position, aetheryte.Position) > AETHERYTE_RADIUS * AETHERYTE_RADIUS)
                {
                    module.TaskHelper.Enqueue(aetheryte.TeleportTo, "传送至以太之光");
                    module.TaskHelper.DelayNext(500, "等待传送开始");
                    module.TaskHelper.Enqueue
                    (
                        () => UIModule.IsScreenReady() &&
                              DistanceSquared(LocalPlayerState.Object.Position, aetheryte.Position) <= AETHERYTE_RADIUS * AETHERYTE_RADIUS,
                        "等待靠近以太之光"
                    );
                }

                module.TaskHelper.Enqueue(() => ChatManager.Instance().SendMessage($"/pdr insc {currentInstance}"), "切换分线");
                module.TaskHelper.Enqueue
                (
                    () => UIModule.IsScreenReady() &&
                          InstancesManager.CurrentInstance == currentInstance,
                    "等待分线切换完成"
                );
                ChangeState(ScanState.SwitchingInstance);
                return;
            }

            zones.Remove(currentZone);
            SwitchZone();
        }

        private void SwitchZone()
        {
            if (zones.Count == 0)
            {
                Dispose();
                return;
            }

            currentZone = zones.Keys.Min();
            module.TaskHelper.Enqueue(IsAbleToTeleport,                                                  "等待区域传送");
            module.TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(currentZone), "传送至扫描区域");
            module.TaskHelper.DelayNext(500, "等待传送开始");
            module.TaskHelper.Enqueue
            (
                () => GameState.TerritoryType == currentZone &&
                      UIModule.IsScreenReady(),
                "等待区域传送完成"
            );
            module.TaskHelper.Enqueue(() => currentInstance = InstancesManager.CurrentInstance, "记录当前分线");
            ChangeState(ScanState.SwitchingZone);
        }

        private void BuildCandidates()
        {
            candidates.Clear();
            candidates.AddRange(pendingPoints);

            for (var first = 0; first < pendingPoints.Count; first++)
            for (var second = first + 1; second < pendingPoints.Count; second++)
            {
                var start    = pendingPoints[first];
                var end      = pendingPoints[second];
                var distance = DistanceSquared(start, end);
                if (distance is <= 0 or > 4 * PLANNING_RADIUS * PLANNING_RADIUS)
                    continue;

                var middle = (start + end)                                       / 2;
                var offset = new Vector3(-(end.Z - start.Z), 0, end.X - start.X) * MathF.Sqrt(((PLANNING_RADIUS * PLANNING_RADIUS) - (distance / 4)) / distance);
                candidates.Add(middle);
                candidates.Add(middle + offset);
                candidates.Add(middle - offset);
            }
        }

        private void ChangeState(ScanState next)
        {
            state          = next;
            stateStartedAt = Environment.TickCount64;
        }

        private void CancelPath()
        {
            pathCancellation?.Cancel();
            pathCancellation?.Dispose();
            pathCancellation = null;

            if (pathTask != null)
            {
                _ = pathTask.ContinueWith
                (
                    completed => DLog.Error(completed.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
                pathTask = null;
            }
        }

        private void StopWithWarning(string key)
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get(key), Lang.Get("AutoMarksFinderTitle"));
            Dispose();
        }

        private static float DistanceSquared(Vector3 first, Vector3 second) =>
            Vector2.DistanceSquared(new(first.X, first.Z), new(second.X, second.Z));

        #region 常量

        private const float DETECTION_RADIUS     = 300;
        private const float PLANNING_RADIUS      = 280;
        private const float ARRIVAL_RADIUS       = 20;
        private const float MOUNT_SPEED          = 12;
        private const float TELEPORT_DURATION    = 10;
        private const float AETHERYTE_RADIUS     = 60;
        private const float MESH_SEARCH_RADIUS   = 30;
        private const float MESH_SEARCH_HEIGHT   = 10000;
        private const float RETRY_DISTANCE       = 80;
        private const float PROGRESS_DISTANCE    = 2;
        private const int   RETRY_RINGS          = 2;
        private const int   RETRY_DIRECTIONS     = 8;
        private const int   UPDATE_INTERVAL_MS   = 250;
        private const int   STATE_TIMEOUT_MS     = 180_000;
        private const int   PATHFIND_TIMEOUT_MS  = 15_000;
        private const int   MOVEMENT_TIMEOUT_MS  = 15_000;
        private const int   MOUNT_TIMEOUT_MS     = 20_000;
        private const int   MOUNT_RETRY_MS       = 1000;
        private const int   OBSERVATION_DELAY_MS = 1500;

        #endregion
    }
}
