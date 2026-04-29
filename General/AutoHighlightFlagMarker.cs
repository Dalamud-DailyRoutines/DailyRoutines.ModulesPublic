using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Dalamud.Helpers;
using OmenTools.OmenService;
using AgentReceiveEventDelegate = OmenTools.Interop.Game.Models.Native.AgentReceiveEventDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightFlagMarker : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("AutoHighlightFlagMarkerTitle"),
        Description     = Lang.Get("AutoHighlightFlagMarkerDescription"),
        Category        = ModuleCategory.General,
        ModulesConflict = ["MultiTargetTracker"]
    };

    private delegate void SetFlagMarkerDelegate
    (
        AgentMap* agent,
        uint      zoneID,
        uint      mapID,
        float     worldX,
        float     worldZ,
        uint      iconID = 60561
    );
    private Hook<SetFlagMarkerDelegate>? SetFlagMarkerHook;

    private Hook<AgentReceiveEventDelegate>? AgentMapReceiveEventHook;

    private Config config = null!;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 15_000 };

        SetFlagMarkerHook ??= DService.Instance().Hook.HookFromAddress<SetFlagMarkerDelegate>
        (
            DalamudReflector.GetMemberFuncByName(typeof(AgentMap.MemberFunctionPointers), "SetFlagMapMarker"),
            SetFlagMarkerDetour
        );
        SetFlagMarkerHook.Enable();

        AgentMapReceiveEventHook ??= AgentMap.Instance()->VirtualTable->HookVFuncFromName
        (
            "ReceiveEvent",
            (AgentReceiveEventDelegate)AgentMapReceiveEventDetour
        );
        AgentMapReceiveEventHook.Enable();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Instance().Reg(OnUpdate, 3000);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoHighlightFlagMarker-ConstantlyUpdate"), ref config.ConstantlyUpdate))
            config.Save(this);
    }

    private void SetFlagMarkerDetour(AgentMap* agent, uint zoneID, uint mapID, float worldX, float worldZ, uint iconID = 60561)
    {
        SetFlagMarkerHook.Original(agent, zoneID, mapID, worldX, worldZ, iconID);
        if (mapID != DService.Instance().ClientState.MapId || iconID != 60561) return;

        OnZoneChanged(0);
    }

    private AtkValue* AgentMapReceiveEventDetour(AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        var ret = AgentMapReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        if (eventKind == 0 && valueCount > 0 && values->Int == 10)
            OnZoneChanged(0);

        return ret;
    }

    private void OnZoneChanged(uint u)
    {
        if (!IsFlagMarkerValid()) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(() => DService.Instance().ObjectTable.LocalPlayer != null && !DService.Instance().Condition[ConditionFlag.BetweenAreas]);
        TaskHelper.Enqueue
        (() =>
            {
                if (IsFlagMarkerValid()) return;
                TaskHelper.Abort();
            }
        );
        TaskHelper.Enqueue
        (() =>
            {
                var agent    = AgentMap.Instance();
                var flagPos  = new Vector2(agent->FlagMapMarkers[0].XFloat, agent->FlagMapMarkers[0].YFloat);
                var currentY = DService.Instance().ObjectTable.LocalPlayer?.Position.Y ?? 0;

                var counter = 0;

                foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
                {
                    var targetPos  = flagPos.ToVector3(currentY - 2 + counter * 5);
                    var currentPos = fieldMarkerPoint.GetPosition();
                    if (Vector3.DistanceSquared(targetPos, currentPos) <= 9) continue;

                    MarkingController.Instance()->PlaceFieldMarkerLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + counter * 5));
                    counter++;
                }
            }
        );
    }

    private static void ClearMarkers()
    {
        var instance = MarkingController.Instance();
        if (instance == null) return;

        var array = instance->FieldMarkers.ToArray();
        if (array.Count(x => x.Active) != 8) return;
        if (array.Select(x => x.Position.ToVector2()).ToHashSet().Count == 1)
            instance->FieldMarkers.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        if (!config.ConstantlyUpdate) return;

        if (!IsFlagMarkerValid())
        {
            ClearMarkers();
            return;
        }

        if (TaskHelper.IsBusy) return;

        var counter = 0;

        foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
        {
            var agent    = AgentMap.Instance();
            var flagPos  = new Vector2(agent->FlagMapMarkers[0].XFloat, agent->FlagMapMarkers[0].YFloat);
            var currentY = DService.Instance().ObjectTable.LocalPlayer?.Position.Y ?? 0;

            var targetPos  = flagPos.ToVector3(currentY - 2 + counter * 5);
            var currentPos = fieldMarkerPoint.GetPosition();

            if (Vector3.DistanceSquared(targetPos, currentPos) <= 9 && MarkingController.Instance()->FieldMarkers[(int)fieldMarkerPoint].Active)
                continue;

            MarkingController.Instance()->PlaceFieldMarkerLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + counter * 5));

            counter++;
        }
    }

    private static bool IsFlagMarkerValid()
    {
        if (!GameState.IsFlagMarkerSet)
            return false;

        var flagMarker = GameState.FlagMarker;
        if (flagMarker.TerritoryId == 0 || flagMarker.MapId == 0 || flagMarker.TerritoryId != GameState.TerritoryType)
            return false;

        return true;
    }

    private class Config : ModuleConfig
    {
        public bool ConstantlyUpdate;
    }
}
