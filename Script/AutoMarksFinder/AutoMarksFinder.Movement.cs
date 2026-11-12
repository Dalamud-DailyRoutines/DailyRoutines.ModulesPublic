using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Threading.TaskHelper;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoMarksFinder
{
    private sealed class HuntMovement
    (
        Config config
    ) : IDisposable
    {
        private readonly TaskHelper tasks = new()
        {
            TimeoutMS       = 180_000,
            RetryIntervalMS = 250
        };
        private CancellationTokenSource? pathCancellation;
        private Task<List<Vector3>>?     pathTask;
        private MovementStage            stage;
        private bool                     ownsNavigation;

        private NotoriousMonsterFound? targetMonster;
        private HuntNavigationMode     navigationMode;
        private float                  nearbyDistance;
        private Vector3                destination;
        private bool                   fly;
        private long                   mountRequestedAt;
        private long                   pathStartedAt;
        private long                   lastProgressAt;
        private float                  closestDistance;

        public bool IsBusy        => tasks.IsBusy;
        public bool HasArrived    => stage == MovementStage.Arrived;
        public bool IsPathPending => pathTask is { IsCompleted: false };
        public bool IsPathRunning => ownsNavigation && vnavmeshIPC.IsPluginEnabled() && vnavmeshIPC.GetIsPathfindRunning();

        public void NavigateToMonster(NotoriousMonsterFound monster, bool afterBroadcast = false)
        {
            monster.OpenMap();
            var needsTravel = GameState.TerritoryType != monster.ZoneID ||
                              (monster.Instance != 0 && InstancesManager.CurrentInstance != monster.Instance);
            var aetheryte = monster.NearestAetheryteRecord;

            if (!needsTravel      &&
                aetheryte != null &&
                LocalPlayerState.Object is { } player)
            {
                var directCost   = MathF.Sqrt(HorizontalDistanceSquared(player.Position, monster.Position)) / MOUNT_SPEED;
                var teleportCost = TELEPORT_DURATION + (MathF.Sqrt(HorizontalDistanceSquared(aetheryte.Position, monster.Position)) / MOUNT_SPEED);
                if (teleportCost >= directCost)
                    aetheryte = null;
            }

            Travel
            (
                monster.ZoneID,
                monster.Instance,
                aetheryte,
                afterBroadcast
            );

            targetMonster  = monster;
            navigationMode = config.NavigationMode;
            nearbyDistance = Math.Max(1, config.NearbyDistance);
            FrameworkManager.Instance().Reg(UpdateTarget, TARGET_INTERVAL_MS);

            tasks.Enqueue(Mount,    timeoutMS: 30_000,              timeoutAction: StopWithWarning, exceptionAction: StopWithWarning);
            tasks.Enqueue(FindPath, timeoutMS: 60_000,              timeoutAction: StopWithWarning, exceptionAction: StopWithWarning);
            tasks.Enqueue(Move,     timeoutAction: StopWithWarning, exceptionAction: StopWithWarning);
        }

        public void Travel(uint zone, uint instance = 0, AetheryteRecord? aetheryte = null, bool afterBroadcast = false)
        {
            Stop();
            stage = MovementStage.Travelling;

            var arrivalZone = aetheryte?.ZoneID ?? zone;

            if (aetheryte != null &&
                (GameState.TerritoryType != arrivalZone    ||
                 LocalPlayerState.Object is not { } player ||
                 Vector3.DistanceSquared(player.Position, aetheryte.Position) > AETHERYTE_RADIUS * AETHERYTE_RADIUS))
            {
                tasks.Enqueue(IsReadyToTeleport);
                if (afterBroadcast)
                    tasks.DelayNext(BROADCAST_TRAVEL_DELAY_MS);

                tasks.Enqueue(aetheryte.TeleportTo);
                tasks.DelayNext(500);
                tasks.Enqueue
                (() => IsReady()                                                                                    &&
                       GameState.TerritoryType                                                       == arrivalZone &&
                       Vector3.DistanceSquared(LocalPlayerState.Object.Position, aetheryte.Position) <= AETHERYTE_RADIUS * AETHERYTE_RADIUS
                );
            }

            if (arrivalZone != zone ||
                (aetheryte               == null &&
                 GameState.TerritoryType != zone))
            {
                tasks.Enqueue(IsReadyToTeleport);
                if (afterBroadcast)
                    tasks.DelayNext(BROADCAST_TRAVEL_DELAY_MS);

                tasks.Enqueue
                (() =>
                    {
                        stage = MovementStage.SwitchingZone;
                        MovementManager.Instance().TPSmart_BetweenZone(zone);
                    }
                );
                tasks.DelayNext(500);
                tasks.Enqueue(() => IsReady() && GameState.TerritoryType == zone);
                tasks.Enqueue(() => stage = MovementStage.Travelling);
            }

            if (instance != 0)
            {
                tasks.Enqueue(IsReadyToTeleport);
                if (afterBroadcast &&
                    (GameState.TerritoryType          != zone ||
                     InstancesManager.CurrentInstance != instance))
                    tasks.DelayNext(BROADCAST_TRAVEL_DELAY_MS);

                tasks.Enqueue
                (() =>
                    {
                        if (InstancesManager.CurrentInstance == instance)
                            return;

                        stage = MovementStage.SwitchingInstance;
                        ChatManager.Instance().SendMessage($"/pdr insc {instance}");
                    }
                );
                tasks.Enqueue(() => IsReady() && InstancesManager.CurrentInstance == instance);
            }

            tasks.Enqueue(() => stage = MovementStage.Arrived);
        }

        public void BeginPath(Vector3 target, bool fly)
        {
            StopNavigation();
            pathCancellation = new();
            pathTask = vnavmeshIPC.PathfindCancelable
            (
                LocalPlayerState.Object.Position + new Vector3(0, 2, 0),
                target,
                fly,
                pathCancellation.Token
            );
        }

        public List<Vector3>? TakePath()
        {
            if (pathTask is not { IsCompletedSuccessfully: true })
                return null;

            var result = pathTask.Result;
            pathTask = null;
            pathCancellation?.Dispose();
            pathCancellation = null;
            return result;
        }

        public void FollowPath(List<Vector3> path, bool fly)
        {
            ownsNavigation = true;
            vnavmeshIPC.SetPathfindTolerance(2f);
            vnavmeshIPC.PathfindWithPath(path, fly);
        }

        public void StopNavigation()
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

            if (ownsNavigation &&
                vnavmeshIPC.IsPluginEnabled())
                vnavmeshIPC.StopPathfind();

            ownsNavigation = false;
        }

        public void Stop()
        {
            FrameworkManager.Instance().Unreg(UpdateTarget);
            tasks.Abort();
            StopNavigation();

            switch (stage)
            {
                case MovementStage.SwitchingInstance:
                    ChatManager.Instance().SendMessage("/pdr insc abort");
                    break;
                case MovementStage.SwitchingZone:
                    MovementManager.Instance().AbortTasks();
                    break;
            }

            stage            = MovementStage.Idle;
            targetMonster    = null;
            mountRequestedAt = 0;
            pathStartedAt    = 0;
        }

        public void Dispose()
        {
            Stop();
            tasks.Dispose();
        }

        private bool Mount()
        {
            if (!IsInTargetArea())
            {
                StopWithWarning();
                return false;
            }

            if (!vnavmeshIPC.IsPluginEnabled() ||
                !vnavmeshIPC.GetIsNavReady())
                return false;

            if (ICondition.Instance()[ConditionFlag.Mounted])
            {
                fly = Control.CanFly;

                if (navigationMode == HuntNavigationMode.Nearby &&
                    !fly)
                {
                    StopWithWarning();
                    return false;
                }

                return true;
            }

            if (ICondition.Instance().IsCasting ||
                Environment.TickCount64 - mountRequestedAt < MOUNT_RETRY_MS)
                return false;

            mountRequestedAt = Environment.TickCount64;
            UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
            return false;
        }

        private bool FindPath()
        {
            if (!IsInTargetArea())
            {
                StopWithWarning();
                return false;
            }

            if (pathStartedAt == 0)
            {
                var candidate = targetMonster.Position;
                var position  = LocalPlayerState.Object.Position;

                if (navigationMode == HuntNavigationMode.Nearby)
                {
                    var distance = MathF.Sqrt(HorizontalDistanceSquared(position, candidate));
                    if (distance > 0)
                        candidate = Vector3.Lerp(candidate, position, MathF.Min(Math.Max(0, nearbyDistance - (ARRIVAL_RADIUS * 2)) / distance, 1));

                    candidate.Y = targetMonster.Position.Y + FLIGHT_HEIGHT;
                }

                var nearestPoint = fly ?
                                       vnavmeshIPC.QueryNearestPointInVoxel(candidate, nearbyDistance) :
                                       vnavmeshIPC.QueryNearestPointOnMesh(candidate, nearbyDistance, 10000);

                if (nearestPoint is not { } point ||
                    (navigationMode                                         == HuntNavigationMode.Nearby &&
                     HorizontalDistanceSquared(point, targetMonster.Position) > nearbyDistance * nearbyDistance))
                {
                    StopWithWarning();
                    return false;
                }

                destination   = point;
                pathStartedAt = Environment.TickCount64;
                BeginPath(destination, fly);
                return false;
            }

            if (IsPathPending &&
                Environment.TickCount64 - pathStartedAt < PATH_TIMEOUT_MS)
                return false;

            var path = TakePath();
            StopNavigation();

            if (path is { Count: > 0 }                                                            &&
                Vector3.DistanceSquared(path[^1], destination) <= ARRIVAL_RADIUS * ARRIVAL_RADIUS &&
                (navigationMode                                              == HuntNavigationMode.Exact ||
                 HorizontalDistanceSquared(path[^1], targetMonster.Position) <= nearbyDistance * nearbyDistance))
            {
                destination     = path[^1];
                closestDistance = Vector3.DistanceSquared(LocalPlayerState.Object.Position, destination);
                lastProgressAt  = Environment.TickCount64;
                FollowPath(path, fly);
                return true;
            }

            StopWithWarning();
            return false;
        }

        private bool Move()
        {
            if (!IsInTargetArea())
            {
                StopWithWarning();
                return false;
            }

            var position        = LocalPlayerState.Object.Position;
            var currentDistance = Vector3.DistanceSquared(position, destination);

            if (currentDistance <= ARRIVAL_RADIUS * ARRIVAL_RADIUS &&
                (navigationMode                                              == HuntNavigationMode.Exact ||
                 HorizontalDistanceSquared(position, targetMonster.Position) <= nearbyDistance * nearbyDistance))
            {
                StopNavigation();
                FrameworkManager.Instance().Unreg(UpdateTarget);
                targetMonster = null;
                return true;
            }

            if (currentDistance < closestDistance - (PROGRESS_DISTANCE * PROGRESS_DISTANCE))
            {
                closestDistance = currentDistance;
                lastProgressAt  = Environment.TickCount64;
            }

            if (!IsPathRunning ||
                Environment.TickCount64 - lastProgressAt >= MOVEMENT_TIMEOUT_MS)
                StopWithWarning();

            return false;
        }

        private void StopWithWarning()
        {
            Stop();

            var message = Lang.Get("AutoMarksFinder-NavigationInterrupted");
            NotifyHelper.ToastError(message);
            NotifyHelper.Instance().TrayError(message, Lang.Get("AutoMarksFinderTitle"));
            NotifyHelper.Instance().ChatError(message);
        }

        private void UpdateTarget(IFramework framework)
        {
            if (!IsBusy)
            {
                FrameworkManager.Instance().Unreg(UpdateTarget);
                return;
            }

            if (!IsInTargetArea() ||
                !UIModule.IsScreenReady())
                return;

            var current = TargetManager.Target;
            if (current is { IsTargetable: true }                                                         &&
                current.DataID                                                    == targetMonster.DataID &&
                Vector3.DistanceSquared(current.Position, targetMonster.Position) <= MONSTER_MATCH_RADIUS * MONSTER_MATCH_RADIUS)
                return;

            var target = IObjectTable.Instance().SearchObject
            (
                obj => obj.IsTargetable                                                                      &&
                       obj.DataID                                                    == targetMonster.DataID &&
                       Vector3.DistanceSquared(obj.Position, targetMonster.Position) <= MONSTER_MATCH_RADIUS * MONSTER_MATCH_RADIUS,
                IObjectTable.CharactersRange
            );
            if (target != null)
                TargetManager.Target = target;
        }

        private bool IsInTargetArea() =>
            GameState.IsLoggedIn                            &&
            targetMonster != null                           &&
            LocalPlayerState.Object is { IsDead: false }    &&
            !ICondition.Instance().IsBetweenAreas           &&
            GameState.TerritoryType == targetMonster.ZoneID &&
            (targetMonster.Instance           == 0 ||
             InstancesManager.CurrentInstance == targetMonster.Instance);

        private static float HorizontalDistanceSquared(Vector3 first, Vector3 second) =>
            Vector2.DistanceSquared(new(first.X, first.Z), new(second.X, second.Z));

        private static bool IsReady() =>
            LocalPlayerState.Object != null       &&
            !ICondition.Instance().IsBetweenAreas &&
            !ICondition.Instance().IsCasting      &&
            UIModule.IsScreenReady();

        private static bool IsReadyToTeleport() =>
            IsReady() &&
            !LocalPlayerState.Instance().IsMoving;

        #region 常量

        private const float AETHERYTE_RADIUS          = 30;
        private const float MOUNT_SPEED               = 12;
        private const float TELEPORT_DURATION         = 10;
        private const float MONSTER_MATCH_RADIUS      = 200;
        private const uint  TARGET_INTERVAL_MS        = 250;
        private const float FLIGHT_HEIGHT             = 15;
        private const float ARRIVAL_RADIUS            = 2;
        private const float PROGRESS_DISTANCE         = 2;
        private const int   MOUNT_RETRY_MS            = 1000;
        private const int   PATH_TIMEOUT_MS           = 15_000;
        private const int   MOVEMENT_TIMEOUT_MS       = 15_000;
        private const int   BROADCAST_TRAVEL_DELAY_MS = 2000;

        #endregion
    }
}
