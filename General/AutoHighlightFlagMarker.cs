using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoHighlightFlagMarker : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoHighlightFlagMarkerTitle"),
        Description = GetLoc("AutoHighlightFlagMarkerDescription"),
        Category = ModuleCategories.General,
        ModulesConflict = ["MultiTargetTracker"],
    };

    private static readonly CompSig SetFlagMarkerSig = new("E8 ?? ?? ?? ?? 48 8B 06 48 8B CE FF 50 ?? 84 C0 74 ?? 48 8B 8B");
    private delegate void SetFlagMarkerDelegate(AgentMap* agent, uint zoneID, uint mapID, float worldX, float worldZ, uint iconID = 60561);
    private static Hook<SetFlagMarkerDelegate>? SetFlagMarkerHook;

    private static readonly CompSig AgentMapReceiveEventSig = new("40 53 56 57 41 56 48 83 EC ?? 8B BC 24");
    private static Hook<AgentReceiveEventDelegate>? AgentMapReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeLimitMS = 15_000 };

        SetFlagMarkerHook ??= SetFlagMarkerSig.GetHook<SetFlagMarkerDelegate>(SetFlagMarkerDetour);
        SetFlagMarkerHook.Enable();

        AgentMapReceiveEventHook ??= AgentMapReceiveEventSig.GetHook<AgentReceiveEventDelegate>(AgentMapReceiveEventDetour);
        AgentMapReceiveEventHook.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Register(OnUpdate, throttleMS: 1_000);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoHighlightFlagMarker-ConstantlyUpdate"), ref ModuleConfig.ConstantlyUpdate))
            ModuleConfig.Save(this);
    }

    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        base.Uninit();
    }

    private void SetFlagMarkerDetour(AgentMap* agent, uint zoneID, uint mapID, float worldX, float worldZ, uint iconID = 60561)
    {
        SetFlagMarkerHook.Original(agent, zoneID, mapID, worldX, worldZ, iconID);
        if (mapID != DService.ClientState.MapId || iconID != 60561) return;

        EnqueuePlaceFieldMarkers();
    }

    private AtkValue* AgentMapReceiveEventDetour(
        AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        var ret = AgentMapReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        if (eventKind == 0 && valueCount > 0 && values->Int == 10)
            EnqueuePlaceFieldMarkers();

        return ret;
    }

    private void OnZoneChanged(ushort obj)
    {
        EnqueuePlaceFieldMarkers();
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
        if (!ModuleConfig.ConstantlyUpdate) return;
        if (!IsFlagMarkerValid())
        {
            ClearMarkers();
            return;
        }

        EnqueuePlaceFieldMarkers();
    }

    private void EnqueuePlaceFieldMarkers()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() => DService.ObjectTable.LocalPlayer != null && !DService.Condition[ConditionFlag.BetweenAreas]);

        TaskHelper.Enqueue(() =>
        {
            if (IsFlagMarkerValid()) return;
            TaskHelper.Abort();
        });

        TaskHelper.Enqueue(() =>
        {
            var agent    = AgentMap.Instance();
            var flagPos  = new Vector2(agent->FlagMapMarkers[0].XFloat, agent->FlagMapMarkers[0].YFloat);
            var currentY = DService.ObjectTable.LocalPlayer?.Position.Y ?? 0;
            var counter  = 0;

            foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
            {
                FieldMarkerHelper.PlaceLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + (counter * 5)), true);
                counter++;
            }
        });
    }

    private static bool IsFlagMarkerValid()
    {
        var agent = AgentMap.Instance();
        if (agent == null) return false;

        if (agent->FlagMarkerCount == 0) return false;
        if (agent->FlagMapMarkers[0].TerritoryId == 0 || agent->FlagMapMarkers[0].MapId == 0) return false;
        if (agent->FlagMapMarkers[0].MapId != DService.ClientState.MapId) return false;

        return true;
    }

    private class Config : ModuleConfiguration
    {
        public bool ConstantlyUpdate;
    }
}
