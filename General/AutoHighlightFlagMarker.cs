// TODO: 闪现问题比较严重, 目前未知原因, 先注释掉
/*
using System;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightFlagMarker : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("AutoHighlightFlagMarkerTitle"),
        Description     = GetLoc("AutoHighlightFlagMarkerDescription"),
        Category        = ModuleCategories.General,
        ModulesConflict = ["MultiTargetTracker"],
    };

    private delegate        void SetFlagMarkerDelegate(AgentMap* agent, uint zoneID, uint mapID, float worldX, float worldZ, uint iconID = 60561);
    private static          Hook<SetFlagMarkerDelegate>? SetFlagMarkerHook;

    private static Hook<AgentReceiveEventDelegate>? AgentMapReceiveEventHook;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeLimitMS = 15_000 };

        SetFlagMarkerHook ??= DService.Hook.HookFromAddress<SetFlagMarkerDelegate>(
            GetMemberFuncByName(typeof(AgentMap.MemberFunctionPointers), "SetFlagMapMarker"),
            SetFlagMarkerDetour);
        SetFlagMarkerHook.Enable();

        AgentMapReceiveEventHook ??= DService.Hook.HookFromAddress<AgentReceiveEventDelegate>(
            GetVFuncByName(AgentMap.Instance()->VirtualTable, "ReceiveEvent"),
            AgentMapReceiveEventDetour);
        AgentMapReceiveEventHook.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Register(OnUpdate, throttleMS: 3000);
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
    }

    private void SetFlagMarkerDetour(AgentMap* agent, uint zoneID, uint mapID, float worldX, float worldZ, uint iconID = 60561)
    {
        SetFlagMarkerHook.Original(agent, zoneID, mapID, worldX, worldZ, iconID);
        if (mapID != DService.ClientState.MapId || iconID != 60561) return;

        OnZoneChanged(0);
    }

    private AtkValue* AgentMapReceiveEventDetour(AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        var ret = AgentMapReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        if (eventKind == 0 && valueCount > 0 && values->Int == 10)
            OnZoneChanged(0);

        return ret;
    }

    private void OnZoneChanged(ushort obj)
    {
        if (!IsFlagMarkerValid()) return;
        
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

            var counter = 0;
            foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
            {
                var targetPos  = flagPos.ToVector3(currentY - 2 + (counter * 5));
                var currentPos = FieldMarkerHelper.GetLocalPosition(fieldMarkerPoint);
                if (Vector3.DistanceSquared(targetPos, currentPos) <= 9) continue;

                FieldMarkerHelper.PlaceLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + (counter * 5)), true);
                counter++;
            }
        });
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
        
        if (TaskHelper.IsBusy) return;

        var counter = 0;
        foreach (var fieldMarkerPoint in Enum.GetValues<FieldMarkerPoint>())
        {
            var agent    = AgentMap.Instance();
            var flagPos  = new Vector2(agent->FlagMapMarkers[0].XFloat, agent->FlagMapMarkers[0].YFloat);
            var currentY = DService.ObjectTable.LocalPlayer?.Position.Y ?? 0;
                
            var targetPos  = flagPos.ToVector3(currentY - 2 + (counter * 5));
            var currentPos = FieldMarkerHelper.GetLocalPosition(fieldMarkerPoint);
                
            if (Vector3.DistanceSquared(targetPos, currentPos) <= 9 && MarkingController.Instance()->FieldMarkers[(int)fieldMarkerPoint].Active) 
                continue;
                    
            FieldMarkerHelper.PlaceLocal(fieldMarkerPoint, flagPos.ToVector3(currentY - 2 + (counter * 5)), true);
                
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

    private class Config : ModuleConfiguration
    {
        public bool ConstantlyUpdate;
    }
}
*/
