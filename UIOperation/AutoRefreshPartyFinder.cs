using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Numerics;
using System.Timers;
using Timer = System.Timers.Timer;

namespace DailyRoutines.Modules;

public class AutoRefreshPartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoRefreshPartyFinderTitle"),
        Description = GetLoc("AutoRefreshPartyFinderDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private static Config ModuleConfig = null!;

    private static Vector2 WindowPos = new(512); // 防止在屏幕外直接不渲染了

    private static Timer? PFRefreshTimer;
    private static int Cooldown;

    public override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Overlay ??= new Overlay(this);

        PFRefreshTimer ??= new Timer(1_000);
        PFRefreshTimer.AutoReset = true;
        PFRefreshTimer.Elapsed += OnRefreshTimer;
        Cooldown = ModuleConfig.RefreshInterval;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup", OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroupDetail", OnAddonLFGD);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnAddonLFGD);

        if (LookingForGroup != null) 
            OnAddonPF(AddonEvent.PostSetup, null);
    }

    public override unsafe void OverlayUI()
    {
        if (!IsAddonAndNodesReady(LookingForGroup))
        {
            Overlay.IsOpen = false;
            return;
        }

        var refreshButton = LookingForGroup->GetComponentButtonById(47)->OwnerNode;
        if (refreshButton == null) return;

        ImGui.SetWindowPos(WindowPos);

        using (ImRaii.Group())
        {
            ImGui.SetNextItemWidth(50f * GlobalFontScale);
            if (ImGui.InputInt("###RefreshIntervalInput", ref ModuleConfig.RefreshInterval, 0, 0))
                ModuleConfig.RefreshInterval = Math.Max(5, ModuleConfig.RefreshInterval);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                SaveConfig(ModuleConfig);

                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
            }

            ImGui.SameLine();
            ImGui.Text(GetLoc("AutoRefreshPartyFinder-RefreshInterval", Cooldown));

            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("AutoRefreshPartyFinder-OnlyInactive"), ref ModuleConfig.OnlyInactive))
                SaveConfig(ModuleConfig);
        }

        var framePadding = ImGui.GetStyle().FramePadding;
        WindowPos = new Vector2(refreshButton->ScreenX - ImGui.GetItemRectSize().X - (4 * framePadding.X),
                                refreshButton->ScreenY - framePadding.Y);
    }

    // 招募
    private void OnAddonPF(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
                Overlay.IsOpen = true;
                break;
            case AddonEvent.PostRefresh when ModuleConfig.OnlyInactive:
                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
                break;
            case AddonEvent.PreFinalize:
                PFRefreshTimer.Stop();
                Overlay.IsOpen = false;
                break;
        }
    }

    // 招募详情
    private void OnAddonLFGD(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                PFRefreshTimer.Stop();
                Overlay.IsOpen = false;
                break;
            case AddonEvent.PreFinalize:
                Cooldown = ModuleConfig.RefreshInterval;
                PFRefreshTimer.Restart();
                Overlay.IsOpen = true;
                break;
        }
    }

    private unsafe void OnRefreshTimer(object? sender, ElapsedEventArgs e)
    {
        if (!IsAddonAndNodesReady(LookingForGroup) || IsAddonAndNodesReady(LookingForGroupDetail))
        {
            PFRefreshTimer.Stop();
            Overlay.IsOpen = false;
            return;
        }

        if (Cooldown > 1)
        {
            Cooldown--;
            return;
        }

        Cooldown = ModuleConfig.RefreshInterval;
        SendEvent(AgentId.LookingForGroup, 1, 17);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonPF);
        DService.AddonLifecycle.UnregisterListener(OnAddonLFGD);

        if (PFRefreshTimer != null)
        {
            PFRefreshTimer.Elapsed -= OnRefreshTimer;
            PFRefreshTimer.Stop();
            PFRefreshTimer.Dispose();
        }
        PFRefreshTimer = null;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public int RefreshInterval = 10; // 秒
        public bool OnlyInactive = true;
    }
}
