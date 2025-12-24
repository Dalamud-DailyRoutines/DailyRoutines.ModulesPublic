using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class MoreFlexibleMJIWorkdays : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MoreFlexibleMJIWorkdaysTitle"),
        Description = GetLoc("MoreFlexibleMJIWorkdaysDescription"),
        Category    = ModuleCategories.System
    };

    protected override void Init()
    {
        Overlay ??= new(this);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MJICraftSchedule", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MJICraftSchedule", OnAddon);
        if (IsAddonAndNodesReady(MJICraftSchedule))
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        var agent = AgentMJICraftSchedule.Instance();
        var addon = MJICraftSchedule;
        if (addon == null || agent == null || agent->Data == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var node = addon->GetNodeById(24);
        if (node == null) return;

        var nodeState = NodeState.Get((AtkResNode*)addon->WindowNode);
        ImGui.SetWindowPos(nodeState.Position with { Y = nodeState.Position.Y - ImGui.GetWindowSize().Y });

        if (agent->Data->NewRestCycles == 0)
            agent->Data->NewRestCycles = agent->Data->RestCycles;
        
        var restDays = DecodeRestDays(agent->Data->NewRestCycles);
        using (ImRaii.Group())
        {
            for (var i = 0; i < restDays.Count; i++)
            {
                var day = restDays[i];

                if (ImGui.Checkbox($"##Day{i}", ref day))
                {
                    restDays[i] = day;
                    
                    var newDays = EncodeRestDays(restDays);
                    agent->Data->RestCycles    = newDays;
                    agent->Data->NewRestCycles = newDays;
                    
                    var list = new List<int>();
                    for (var j = 0; j < restDays.Count; j++)
                    {
                        if (!restDays[j]) continue;
                        list.Add(j);
                    }

                    while (list.Count < 4)
                        list.Add(0);
                    
                    ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.MJISetRestCycles,   (uint)list[0], (uint)list[1], (uint)list[2], (uint)list[3]);
                    ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.MJIWorkshopRequest, agent->Data->CycleDisplayed);
                }
                
                switch (i)
                {
                    case 6:
                        ImGui.SameLine(0, 4f * GlobalFontScale);
                        ImGui.Text(LuminaWrapper.GetAddonText(15107));
                        break;
                    case 13:
                        ImGui.SameLine(0, 4f * GlobalFontScale);
                        ImGui.Text(LuminaWrapper.GetAddonText(15108));
                        break;
                }
                
                if (i != 6)
                    ImGui.SameLine();
            }
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    private static uint EncodeRestDays(List<bool> restDays)
    {
        if (restDays.Count != 14)
            throw new ArgumentException("休息日列表长度必须为 14");

        uint result = 0;

        for (var i = 0; i < 7; i++)
        {
            if (restDays[i])
                result |= (uint)(1 << i);
        }

        for (var i = 7; i < 14; i++)
        {
            if (restDays[i])
                result |= (uint)(1 << i);
        }

        return result;
    }

    private static List<bool> DecodeRestDays(uint value)
    {
        var restDays = new List<bool>(14);

        for (var i = 0; i < 14; i++) 
            restDays.Add(false);

        for (var i = 0; i < 14; i++) 
            restDays[i] = (value & (1u << i)) != 0;

        return restDays;
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);
}
