using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.Expansion.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;
using EventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoShowFrontlineKillCount : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoShowFrontlineKillCountTitle"),
        Description = Lang.Get("AutoShowFrontlineKillCountDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private uint lastKillCount;
    private uint preview = 1;

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "PvPFrontlineGauge", OnAddon);
        IClientState.Instance().TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

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

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.Frontline) return;

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null) return;

        var director = eventFramework->DirectorModule.ActiveContentDirector;
        if (director == null) return;

        var killCount = (uint)((FrontlineDirector*)director)->KillCount;
        if (lastKillCount == killCount) return;

        DisplayKillCount(killCount);
        lastKillCount = killCount;
    }

    private void OnZoneChanged
    (
        uint u
    ) =>
        lastKillCount = 0;

    private static void DisplayKillCount
    (
        uint killCount
    )
    {
        if (Streak != null)
        {
            Streak->IsVisible = false;
            Streak->Close(true);
        }

        UIModule.Instance()->ShowStreak
        (
            (int)killCount,
            killCount <= 2 ?
                1 :
                2
        );
    }
}
