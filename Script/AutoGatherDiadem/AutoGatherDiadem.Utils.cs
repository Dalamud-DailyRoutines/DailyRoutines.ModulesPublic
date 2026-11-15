using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Data;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    private bool TryFindNearestGatherPoint
    (
        out                     Vector3      position,
        [NotNullWhen(true)] out IGameObject? obj
    )
    {
        position = Vector3.Zero;
        obj      = null;

        if (!JobInfos.TryGetValue(LocalPlayerState.ClassJob, out var info)) return false;

        var nearestDistance = float.MaxValue;

        foreach (var candidate in IObjectTable.Instance().SearchObjects
                 (
                     candidate => candidate.ObjectKind == ObjectKind.GatheringPoint         &&
                                  info.GatherPointNames.Contains(candidate.Name.ToString()) &&
                                  candidate.ToBCStruct()->TargetableStatus.IsSetAll(ObjectTargetableFlags.IsTargetable),
                     IObjectTable.EventRange
                 ))
        {
            var clouded = CloudedGatherPointNames.Contains(candidate.Name);
            if ((config.Mode == GatheringMode.练级 && clouded) ||
                (config.Mode == GatheringMode.绿材 && !clouded))
                continue;

            var distance = LocalPlayerState.DistanceTo3DSquared(candidate.Position);
            if (distance >= nearestDistance)
                continue;

            if (!TryResolveGatheringEvent(candidate, out _, out _))
                continue;

            nearestDistance = distance;
            obj             = candidate;
            position        = candidate.Position;
        }

        return obj != null;
    }

    private static bool TryResolveGatheringEvent
    (
        IGameObject      candidate,
        out EventId      eventID,
        out GameObjectId objectID
    )
    {
        eventID  = default;
        objectID = default;

        var point = (GatheringPointObject*)candidate.ToStruct();
        if (point->Impl == null)
            return false;

        var handler = point->Impl->EventHandler;
        if (handler == null || handler->RemainingCount == 0)
            return false;

        eventID  = handler->Info.EventId;
        objectID = point->GetGameObjectId();
        return true;
    }

    private bool TryFindNearestMonster
    (
        [NotNullWhen(true)] out IGameObject? gameObject
    )
    {
        gameObject = null;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var obj = IObjectTable.Instance()
                              .Where
                              (x => x is { ObjectKind: ObjectKind.BattleNpc, IsTargetable: true, IsDead: false } &&
                                    !config.BlacklistMonsters.Contains(x.Name.ToString())
                              )
                              .Select
                              (x => new
                                  {
                                      Object   = x,
                                      Distance = Vector3.DistanceSquared(x.Position, localPlayer->Position)
                                  }
                              )
                              .Where(x => x.Distance                                                                                <= 625)
                              // 看不见目标
                              .Where(x => ActionManager.GetActionInRangeOrLoS(19700, (GameObject*)localPlayer, x.Object.ToStruct()) != 562)
                              .OrderBy(x => x.Distance)
                              .FirstOrDefault();
        if (obj == null) return false;

        gameObject = obj.Object;
        return true;
    }

    private bool TryGetMiniGatheringMarker
    (
        out Vector3           position,
        out GatheringNodeType type
    )
    {
        type     = GatheringNodeType.普通;
        position = Vector3.Zero;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var agent = AgentMap.Instance();
        if (agent == null) return false;

        var normalMarker   = agent->MiniMapGatheringMarkers[0];
        var minerMarker    = agent->MiniMapGatheringMarkers[1];
        var botanistMarker = agent->MiniMapGatheringMarkers[2];
        if (normalMarker.ShouldRender   == 0 &&
            minerMarker.ShouldRender    == 0 &&
            botanistMarker.ShouldRender == 0)
            return false;

        Vector2 worldPos2D;

        if (config.Mode == GatheringMode.绿材)
        {
            if (minerMarker.ShouldRender == 1 && LocalPlayerState.ClassJob == 16)
            {
                worldPos2D = new(minerMarker.MapMarker.X / 16f, minerMarker.MapMarker.Y / 16f);
                type       = GatheringNodeType.采矿梦幻;
            }
            else if (botanistMarker.ShouldRender == 1 && LocalPlayerState.ClassJob == 17)
            {
                worldPos2D = new(botanistMarker.MapMarker.X / 16f, botanistMarker.MapMarker.Y / 16f);
                type       = GatheringNodeType.园艺梦幻;
            }
            else
            {
                if (normalMarker.ShouldRender != 1)
                    return false;
                worldPos2D = new(normalMarker.MapMarker.X / 16f, normalMarker.MapMarker.Y / 16f);
                type       = GatheringNodeType.普通;
            }
        }
        else
        {
            if (normalMarker.ShouldRender != 1)
                return false;
            worldPos2D = new(normalMarker.MapMarker.X / 16f, normalMarker.MapMarker.Y / 16f);
            type       = GatheringNodeType.普通;
        }

        if (worldPos2D == Vector2.Zero)
            return false;

        position = worldPos2D.ToVector3(localPlayer->Position.Y);
        return true;
    }

    private bool TryGetAnyCordialToUse
    (
        [NotNullWhen(true)] out InventoryItem? item
    )
    {
        item = null;

        if (!config.UseCordial) return false;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var gp    = localPlayer->GatheringPoints;
        var maxGP = localPlayer->MaxGatheringPoints;
        if (gp + 50 >= maxGP) return false;

        foreach (var (cordial, baseGP) in CordialItems)
        {
            if (!ActionManager.Instance()->IsActionOffCooldown(ActionType.Item, cordial)) continue;

            if (Inventories.Player.TryGetItems(x => x.GetBaseItemId() == cordial, out var validItems))
            {
                var firstItem = validItems.MaxBy(candidate => candidate.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                var isItemHQ  = firstItem.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                var gpToRecover = baseGP;
                if (cordial != 12669 && isItemHQ)
                    gpToRecover += 50;
                if (gpToRecover + 50 + gp > maxGP) continue;

                item = firstItem;
                return true;
            }
        }

        return false;
    }

    private static bool IsAnyCordialOffCooldown()
    {
        foreach (var (cordial, _) in CordialItems)
        {
            if (ActionManager.Instance()->IsActionOffCooldown(ActionType.Item, cordial))
                return true;
        }

        return false;
    }

    private static bool IsAbleToWalkTo
    (
        Vector3 targetPos
    )
    {
        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        return MathF.Abs(targetPos.Y - localPlayer->Position.Y)          <= 5 &&
               Vector3.DistanceSquared(localPlayer->Position, targetPos) <= 900;
    }

    [IPCSubscriber("DailyRoutines.Modules.AutoDiscard.IsBusy", DefaultValue = "false")]
    private IPCSubscriber<bool>? IsAutoDiscardModuleBusy;
}
