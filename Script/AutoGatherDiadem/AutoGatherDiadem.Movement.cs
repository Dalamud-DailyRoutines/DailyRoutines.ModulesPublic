using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    private bool pathStarted;
    private long pathStartedTick;

    private readonly Queue<Vector3> pathCandidates       = new();
    private readonly List<Vector3>  attemptedPathTargets = [];

    private Vector3? immediatePathTarget;
    private Vector3  pathProgressPosition;
    private long     pathProgressTick;
    private bool     pathMadeProgress;
    private bool     isFinalApproach;

    private void BeginMove
    (
        Vector3 destination,
        bool    isMarker,
        bool    isRedirect = false
    )
    {
        StopActivePathfind();

        if (isMarker &&
            vnavmeshIPC.QueryNearestPointOnMesh(destination, 80, 10000) is { } ground)
            destination = ground;

        machine.Destination = destination;
        machine.IsMarker    = isMarker;
        machine.IsRedirect  = isRedirect;
        machine.IsWindmire  = false;

        if (!isRedirect &&
            WindmireTeleporter.FindBestTeleporter(Teleporters, LocalPlayerState.Object.Position, destination, out var entry))
        {
            machine.Destination = entry;
            machine.IsWindmire  = true;
        }

        PreparePathCandidates();

        machine.SetPhase(Phase.Moving);
    }

    private void HandleMoving()
    {
        if (GameState.TerritoryType != ZONE_DIADEM   ||
            !vnavmeshIPC.IsNavReady                  ||
            MovementManager.Instance().IsManagerBusy ||
            ICondition.Instance().IsCasting          ||
            ICondition.Instance().IsOccupiedInEvent)
            return;

        if (config.Mode == GatheringMode.绿材 &&
            !machine.IsRedirect             &&
            (config.BlacklistWeathers.Contains(WeatherManager.Instance()->GetCurrentWeather()) ||
             !SpecialWeathers.ContainsKey(WeatherManager.Instance()->GetCurrentWeather())))
        {
            BeginLeave(false, true);
            return;
        }

        if (machine is { IsMarker: true, IsWindmire: false } &&
            TryFindNearestGatherPoint(out var nearby, out _))
        {
            BeginMove(nearby, false);
            return;
        }

        var distance = LocalPlayerState.DistanceTo3DSquared(machine.Destination);

        if (machine is { IsMarker: false, IsWindmire: false }                         &&
            distance                                                              < 9 &&
            MathF.Abs(machine.Destination.Y - LocalPlayerState.Object.Position.Y) < 2)
        {
            StopActivePathfind();
            machine.SetPhase
            (
                machine.IsRedirect ?
                    Phase.Searching :
                    Phase.Interacting
            );
            return;
        }

        if (!ICondition.Instance().IsOnMount &&
            !IsAbleToWalkTo(machine.Destination))
        {
            if (Throttler.Shared.Throttle("AutoGatherDiadem.Mount", 1500))
                UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
            return;
        }

        if (!ICondition.Instance().IsOnMount       &&
            distance >= 300                        &&
            !LocalPlayerState.HasStatus(50, out _) &&
            ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, 3))
            UseActionManager.Instance().UseAction(ActionType.Action, 3);

        if (pathStarted)
        {
            var now      = Environment.TickCount64;
            var position = LocalPlayerState.Object.Position;

            if (Vector3.DistanceSquared(position, pathProgressPosition) >= 4)
            {
                pathProgressPosition = position;
                pathProgressTick     = now;
                pathMadeProgress     = true;
            }

            var isPathfinding = vnavmeshIPC.IsPathfindInProgress;
            var isMoving      = vnavmeshIPC.IsPathfindRunning;

            if ((isPathfinding && now - pathStartedTick  < 30_000) ||
                (isMoving      && now - pathProgressTick < 15_000) ||
                now - pathStartedTick < 1000)
                return;

            if (!isPathfinding                            &&
                !isMoving                                 &&
                !isFinalApproach                          &&
                (machine.IsWindmire || !machine.IsMarker) &&
                distance <= 64)
            {
                vnavmeshIPC.PathfindTolerance = 0.1f;
                vnavmeshIPC.PathfindWithPath([machine.Destination], ICondition.Instance().IsOnMount);
                pathStartedTick      = now;
                pathProgressPosition = position;
                pathProgressTick     = now;
                pathMadeProgress     = false;
                isFinalApproach      = true;
                return;
            }

            vnavmeshIPC.StopPathfind();
            pathStarted = false;

            if (pathMadeProgress)
            {
                attemptedPathTargets.Clear();
                immediatePathTarget = machine.Destination;
            }

            isFinalApproach = false;
        }

        StartPathfinding();
    }

    private void PreparePathCandidates()
    {
        pathCandidates.Clear();
        attemptedPathTargets.Clear();

        var destination    = machine.Destination;
        var flightPosition = destination with { Y = LocalPlayerState.Object.Position.Y };

        pathCandidates.Enqueue(destination);
        pathCandidates.Enqueue(flightPosition);

        for (var ring = 1; ring <= 2; ring++)
        {
            pathCandidates.Enqueue(destination with { Y = destination.Y + (ring * PATH_RETRY_DISTANCE) });
            pathCandidates.Enqueue(destination with { Y = destination.Y - (ring * PATH_RETRY_DISTANCE) });

            for (var direction = 0; direction < 8; direction++)
            {
                var angle  = MathF.Tau * direction                              / 8;
                var offset = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (ring * PATH_RETRY_DISTANCE);

                pathCandidates.Enqueue(destination    + offset);
                pathCandidates.Enqueue(flightPosition + offset);
            }
        }
    }

    private void StartPathfinding()
    {
        var fly = ICondition.Instance().IsOnMount;

        while (true)
        {
            Vector3 candidate;

            if (immediatePathTarget is { } immediate)
            {
                candidate           = immediate;
                immediatePathTarget = null;
            }
            else if (!pathCandidates.TryDequeue(out candidate))
            {
                PreparePathCandidates();
                machine.ResumeTick = Environment.TickCount64 + 1000;
                return;
            }

            Vector3? target;

            if (machine is { IsMarker: true, IsWindmire: false })
                target = vnavmeshIPC.QueryNearestPointInVoxel(candidate, PATH_RETRY_DISTANCE);
            else if (fly)
                target = vnavmeshIPC.QueryNearestPointInVoxel(candidate, PATH_RETRY_DISTANCE, PATH_RETRY_DISTANCE);
            else
                target = vnavmeshIPC.QueryNearestPointReachable(candidate, PATH_RETRY_DISTANCE, PATH_RETRY_DISTANCE);

            if (target is not { } destination ||
                attemptedPathTargets.Any(attempted => Vector3.DistanceSquared(attempted, destination) < 1))
                continue;

            attemptedPathTargets.Add(destination);

            vnavmeshIPC.PathfindTolerance = machine.IsWindmire ?
                                                0.1f :
                                                1.5f;

            pathStarted = vnavmeshIPC.PathfindAndMoveTo(destination, fly);

            if (!pathStarted)
                continue;

            pathStartedTick      = Environment.TickCount64;
            pathProgressPosition = LocalPlayerState.Object.Position;
            pathProgressTick     = pathStartedTick;
            pathMadeProgress     = false;
            return;
        }
    }

    private void HandleInteracting()
    {
        if (!TryFindNearestGatherPoint(out var position, out var obj))
        {
            machine.SetPhase(Phase.Searching);
            return;
        }

        if (MovementManager.Instance().IsManagerBusy ||
            ICondition.Instance().IsOccupiedInEvent)
            return;

        TargetManager.Target = obj;

        if (ICondition.Instance().IsOnMount)
        {
            if (Throttler.Shared.Throttle("AutoGatherDiadem.Dismount", 1000))
                MovementManager.Instance().Dismount(false);
            return;
        }

        if (LocalPlayerState.DistanceTo3DSquared(position) > 9)
        {
            BeginMove(position, false);
            return;
        }

        if (!obj.ToBCStruct()->TargetableStatus.IsSetAll(ObjectTargetableFlags.ReadyToDraw, ObjectTargetableFlags.IsTargetable))
            return;

        if (Throttler.Shared.Throttle("AutoGatherDiadem.Interact", 1000) &&
            TryResolveGatheringEvent(obj, out var eventID, out var objectID))
            new EventStartPackt(objectID, eventID).Send();
    }

    private void StopActivePathfind()
    {
        if (pathStarted ||
            (machine.CurrentPhase != Phase.Idle &&
             (vnavmeshIPC.IsPathfindRunning ||
              vnavmeshIPC.IsPathfindInProgress)))
            vnavmeshIPC.StopPathfind();

        pathStarted     = false;
        pathStartedTick = 0;

        pathCandidates.Clear();
        attemptedPathTargets.Clear();

        immediatePathTarget  = null;
        pathProgressPosition = default;
        pathProgressTick     = 0;
        pathMadeProgress     = false;
        isFinalApproach      = false;
    }

    #region 常量

    private const float PATH_RETRY_DISTANCE = 10;

    #endregion
}
