using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class MonsterScanner
    (
        AutoMarksFinder module
    ) : IDisposable
    {
        private readonly Dictionary<uint, List<Vector3>>     zones             = [];
        private readonly HashSet<(uint Zone, uint Instance)> completedAreas    = [];
        private readonly List<Vector3>                       candidates        = [];
        private readonly List<Vector3>                       failedCandidates  = [];
        private readonly List<Vector3>                       targetPoints      = [];
        private readonly Queue<Vector3>                      retryTargets      = [];
        private readonly List<Vector3>                       observedPositions = [];

        private ScanState state;
        private long      stateStartedAt;
        private long      lastProgressAt;
        private long      mountRequestedAt;
        private float     closestDistance;
        private Vector3   target;
        private Vector3   anchor;
        private Vector3   observationPosition;
        private Vector3   navigationTarget;
        private float     flightHeight;
        private uint?     originalAroundRangeMode;

        private bool disposed;

        public bool IsRunning => state != ScanState.Stopped;

        public uint ZoneID { get; private set; }

        public uint Instance { get; private set; }

        public List<Vector3> ScanPoints { get; private set; } = [];

        public List<Vector3> PendingPoints { get; } = [];

        public IReadOnlyList<Vector3> ObservedPositions => observedPositions;

        public List<Vector3> ActivePath { get; } = [];

        public Vector3? NavigationTarget =>
            state is ScanState.Pathfinding or ScanState.Moving ?
                navigationTarget :
                null;

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

            disposed = true;
            state    = ScanState.Stopped;
            FrameworkManager.Instance().Unreg(OnUpdate);
            CancelPath();
            module.movement.Stop();

            if (originalAroundRangeMode is { } mode)
            {
                IGameConfig.Instance().System.Set(nameof(SystemConfigOption.DynamicAroundRangeMode), mode);
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.SetAroundRangeMode, mode);
                originalAroundRangeMode = null;
            }
        }

        public void ObserveMonster(uint dataID, Vector3 position)
        {
            if (!IsRunning                                                                   ||
                !zones.ContainsKey(GameState.TerritoryType)                                  ||
                !NotoriousMonsterFound.NOTORIOUS_MONSTERS.TryGetValue(dataID, out var rowID) ||
                !LuminaGetter.TryGetRow<NotoriousMonster>(rowID, out var row)                ||
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
                    if (module.movement.IsBusy)
                        return;

                    if (!module.movement.HasArrived       ||
                        GameState.TerritoryType != ZoneID ||
                        (state                            != ScanState.SwitchingZone &&
                         InstancesManager.CurrentInstance != Instance))
                    {
                        StopWithWarning("AutoMarksFinder-ScanInterrupted");
                        return;
                    }

                    observationPosition = LocalPlayerState.Object.Position;
                    Instance            = InstancesManager.CurrentInstance;
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
                    (GameState.TerritoryType          != ZoneID ||
                     InstancesManager.CurrentInstance != Instance))
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
            ZoneID   = GameState.TerritoryType;
            Instance = InstancesManager.CurrentInstance;

            if (!zones.TryGetValue(ZoneID, out var points))
            {
                SwitchZone();
                return;
            }

            PendingPoints.Clear();
            PendingPoints.AddRange(points);
            ScanPoints   = points;
            flightHeight = points.Max(point => point.Y) + FLIGHT_HEIGHT;
            observedPositions.Clear();
            ActivePath.Clear();
            failedCandidates.Clear();
            retryTargets.Clear();
            BuildCandidates();
            ChangeState(ScanState.Observing);
            observationPosition = LocalPlayerState.Object.Position;
        }

        private void SelectPoint()
        {
            if (PendingPoints.Count == 0)
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

                var coveredPoints = PendingPoints.Where(point => DistanceSquared(point, candidate) <= PLANNING_RADIUS * PLANNING_RADIUS).ToList();
                var coverage      = coveredPoints.Count;
                if (coverage == 0 ||
                    coverage < bestCoverage)
                    continue;

                var margin      = PLANNING_RADIUS - coveredPoints.Max(point => MathF.Sqrt(DistanceSquared(point, candidate)));
                var cost        = MathF.Max(0, MathF.Sqrt(DistanceSquared(position,                              candidate)) - margin) / MOUNT_SPEED;
                var aetheryte   = AetheryteRecordManager.Instance().GetNearestAetheryte(ZoneID, candidate);
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

                var destination = candidate with { Y = MathF.Max(flightHeight, position.Y) };
                if (RaycastHelper.IsInsideCollision(destination))
                    continue;

                bestCoverage = coverage;
                bestCost     = cost;
                target       = destination;
                teleport = useTeleport ?
                               aetheryte :
                               null;
            }

            if (bestCoverage == 0)
            {
                StopWithWarning("AutoMarksFinder-UnreachablePoints");
                return;
            }

            anchor = PendingPoints.MinBy(point => DistanceSquared(point, target));
            targetPoints.Clear();
            targetPoints.AddRange(PendingPoints.Where(point => DistanceSquared(point, target) <= PLANNING_RADIUS * PLANNING_RADIUS));

            if (teleport != null)
            {
                module.movement.Travel(ZoneID, Instance, teleport);
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
                if (!Control.CanFly)
                {
                    StopWithWarning("AutoMarksFinder-FlightUnavailable");
                    return;
                }

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
                candidate.Y = MathF.Max(flightHeight, LocalPlayerState.Object.Position.Y);
                if (RaycastHelper.IsInsideCollision(candidate))
                    continue;

                navigationTarget = candidate;
                module.movement.BeginPath(candidate, true);
                ChangeState(ScanState.Pathfinding);
                return;
            }

            failedCandidates.Add(target);
            ChangeState(ScanState.SelectingPoint);
        }

        private void UpdatePathfinding()
        {
            if (module.movement.IsPathPending)
            {
                if (Environment.TickCount64 - stateStartedAt >= PATHFIND_TIMEOUT_MS)
                    StartPathfinding();

                return;
            }

            var path = module.movement.TakePath();

            if (path == null)
            {
                StartPathfinding();
                return;
            }

            if (path.Count                        == 0                                ||
                DistanceSquared(path[^1], anchor) > PLANNING_RADIUS * PLANNING_RADIUS ||
                RaycastHelper.IsInsideCollision(path[^1]))
            {
                StartPathfinding();
                return;
            }

            CancelPath();
            var endIndex = path.FindIndex(point => DistanceSquared(point, navigationTarget) <= ARRIVAL_RADIUS * ARRIVAL_RADIUS);
            if (endIndex >= 0)
                path.RemoveRange(endIndex + 1, path.Count - endIndex - 1);

            ActivePath.Add(LocalPlayerState.Object.Position);
            ActivePath.AddRange(path);
            module.movement.FollowPath(path, true);
            closestDistance = DistanceSquared(LocalPlayerState.Object.Position, navigationTarget);
            lastProgressAt  = Environment.TickCount64;
            ChangeState(ScanState.Moving);
        }

        private void UpdateMovement()
        {
            var position = LocalPlayerState.Object.Position;
            var distance = DistanceSquared(position, navigationTarget);

            if (distance <= ARRIVAL_RADIUS * ARRIVAL_RADIUS ||
                targetPoints.All(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS))
            {
                module.movement.StopNavigation();
                observationPosition = position;
                ChangeState(ScanState.Observing);
                return;
            }

            if (distance < closestDistance - (PROGRESS_DISTANCE * PROGRESS_DISTANCE))
            {
                closestDistance = distance;
                lastProgressAt  = Environment.TickCount64;
            }

            if (!module.movement.IsPathRunning ||
                Environment.TickCount64 - lastProgressAt > MOVEMENT_TIMEOUT_MS)
            {
                module.movement.StopNavigation();

                if (PendingPoints.Any(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS))
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

            PendingPoints.RemoveAll(point => DistanceSquared(position, point) <= DETECTION_RADIUS * DETECTION_RADIUS);
            observedPositions.Add(position);
            ActivePath.Clear();
            ChangeState(ScanState.SelectingPoint);
        }

        private bool HasEnoughMonsters() =>
            FoundOnes.TryGetValue(ZoneID, out var found) &&
            found.Count(monster => monster.Instance == Instance) >=
            (GameState.TerritoryTypeData.ExVersion.RowId == 0 ?
                 1 :
                 2);

        private void CompleteArea()
        {
            CancelPath();
            completedAreas.Add((ZoneID, Instance));
            var instanceCount = InstancesManager.IsInstancedArea ?
                                    InstancesManager.Instance().GetInstancesCount(ZoneID) :
                                    0;

            for (uint instance = 1; instance <= instanceCount; instance++)
            {
                if (completedAreas.Contains((ZoneID, instance)))
                    continue;

                Instance = instance;
                var aetheryte = AetheryteRecordManager.Instance().GetNearestAetheryte(ZoneID, Vector3.Zero);

                if (aetheryte == null)
                {
                    StopWithWarning("AutoMarksFinder-ScanInterrupted");
                    return;
                }

                module.movement.Travel(ZoneID, Instance, aetheryte);
                ChangeState(ScanState.SwitchingInstance);
                return;
            }

            zones.Remove(ZoneID);
            SwitchZone();
        }

        private void SwitchZone()
        {
            if (zones.Count == 0)
            {
                Dispose();
                return;
            }

            var costs = TeleportCostCalculator.GetRouteCosts(GameState.TerritoryType, zones.Keys, applyTeleportDiscounts: false);

            if (costs.Count == 0)
            {
                StopWithWarning("AutoMarksFinder-ScanInterrupted");
                return;
            }

            ZoneID     = costs.MinBy(pair => pair.Value).Key;
            ScanPoints = [];
            PendingPoints.Clear();
            observedPositions.Clear();
            module.movement.Travel(ZoneID);
            ChangeState(ScanState.SwitchingZone);
        }

        private void BuildCandidates()
        {
            candidates.Clear();
            candidates.AddRange(PendingPoints);

            for (var first = 0; first < PendingPoints.Count; first++)
            for (var second = first + 1; second < PendingPoints.Count; second++)
            {
                var start    = PendingPoints[first];
                var end      = PendingPoints[second];
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
            ActivePath.Clear();
            module.movement.StopNavigation();
        }

        private void StopWithWarning(string key)
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get(key), Lang.Get("AutoMarksFinderTitle"));
            Dispose();
        }

        private static float DistanceSquared(Vector3 first, Vector3 second) =>
            Vector2.DistanceSquared(new(first.X, first.Z), new(second.X, second.Z));

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

        #region 常量

        public const  float DETECTION_RADIUS     = 400;
        private const float PLANNING_RADIUS      = 280;
        private const float ARRIVAL_RADIUS       = 20;
        private const float MOUNT_SPEED          = 12;
        private const float TELEPORT_DURATION    = 10;
        private const float FLIGHT_HEIGHT        = 100;
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
