using System;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoShowFrontlineKillCount : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoShowFrontlineKillCountTitle"),
        Description = GetLoc("AutoShowFrontlineKillCountDescription"),
        Category    = ModuleCategories.Combat
    };
    
    private static uint LastKillCount;

    private static uint Preview = 1;
    
    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "PvPFrontlineGauge", OnAddon);
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        
        if (IsAddonAndNodesReady(PvPFrontlineGauge))
        {
            try
            {
                LastKillCount = PvPFrontlineGauge->AtkValues[6].UInt;
            }
            catch
            {
                // ignored
            }
        }
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, GetLoc("Preview"));

        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(GetLoc("Confirm")))
                DisplayKillCount(Preview);
            
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            if (ImGui.InputUInt("###PreviewInput", ref Preview, 1, 1))
                Preview = Math.Clamp(Preview, 1, 99);
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (PvPFrontlineGauge == null) return;
        if (!Throttler.Throttle("AutoShowFrontlineKillCount-OnUpdate", 100)) return;

        var killCount = 0U;
        
        try
        {
            killCount = PvPFrontlineGauge->AtkValues[6].UInt;
        }
        catch
        {
            killCount = LastKillCount;
        }
        
        if (LastKillCount != killCount)
        {
            DisplayKillCount(killCount);
            LastKillCount = killCount;
        }
    }

    private static void DisplayKillCount(uint killCount)
    {
        if (TryGetAddonByName("_Streak", out var addon))
        {
            addon->IsVisible = false;
            addon->Close(true);
        }
        
        UIModule.Instance()->ShowStreak((int)killCount, killCount <= 2 ? 1 : 2);
    }
    
    private static void OnZoneChanged(ushort obj) => 
        LastKillCount = 0;

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        LastKillCount = 0;
    }
}
