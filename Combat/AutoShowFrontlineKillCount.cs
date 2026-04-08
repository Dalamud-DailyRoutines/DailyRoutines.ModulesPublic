using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoShowFrontlineKillCount : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoShowFrontlineKillCountTitle"),
        Description = Lang.Get("AutoShowFrontlineKillCountDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private uint lastKillCount;
    private uint preview = 1;

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "PvPFrontlineGauge", OnAddon);
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;

        if (PvPFrontlineGauge->IsAddonAndNodesReady())
        {
            try
            {
                lastKillCount = PvPFrontlineGauge->AtkValues[6].UInt;
            }
            catch
            {
                // ignored
            }
        }
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        lastKillCount = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Preview"));

        using (ImRaii.PushIndent())
        {
            if (ImGui.Button(Lang.Get("Confirm")))
                DisplayKillCount(preview);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f * GlobalUIScale);
            if (ImGui.InputUInt("###PreviewInput", ref preview, 1, 1))
                preview = Math.Clamp(preview, 1, 99);
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (PvPFrontlineGauge == null) return;
        if (!Throttler.Shared.Throttle("AutoShowFrontlineKillCount-OnUpdate", 100)) return;

        var killCount = 0U;

        try
        {
            killCount = PvPFrontlineGauge->AtkValues[6].UInt;
        }
        catch
        {
            killCount = lastKillCount;
        }

        if (lastKillCount != killCount)
        {
            DisplayKillCount(killCount);
            lastKillCount = killCount;
        }
    }
    
    private void OnZoneChanged(ushort obj) =>
        lastKillCount = 0;
    
    private static void DisplayKillCount(uint killCount)
    {
        if (AddonHelper.TryGetByName("_Streak", out var addon))
        {
            addon->IsVisible = false;
            addon->Close(true);
        }

        UIModule.Instance()->ShowStreak((int)killCount, killCount <= 2 ? 1 : 2);
    }
}
