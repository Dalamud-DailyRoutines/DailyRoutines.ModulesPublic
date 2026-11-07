using System.Numerics;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class HuntMovement : IDisposable
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

        public bool IsBusy        => tasks.IsBusy;
        public bool HasArrived    => stage == MovementStage.Arrived;
        public bool IsPathPending => pathTask is { IsCompleted: false };
        public bool IsPathRunning => ownsNavigation && vnavmeshIPC.IsPluginEnabled() && vnavmeshIPC.GetIsPathfindRunning();

        public void Travel(uint zone, uint instance = 0, AetheryteRecord? aetheryte = null)
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
            pathTask         = vnavmeshIPC.PathfindCancelable(LocalPlayerState.Object.Position, target, fly, pathCancellation.Token);
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

            stage = MovementStage.Idle;
        }

        public void Dispose()
        {
            Stop();
            tasks.Dispose();
        }

        private static bool IsReady() =>
            LocalPlayerState.Object != null       &&
            !ICondition.Instance().IsBetweenAreas &&
            !ICondition.Instance().IsCasting      &&
            UIModule.IsScreenReady();

        private static bool IsReadyToTeleport() =>
            IsReady() &&
            !LocalPlayerState.Instance().IsMoving;

        #region 常量

        private const float AETHERYTE_RADIUS = 30;

        #endregion
    }
}
