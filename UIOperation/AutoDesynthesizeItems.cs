using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoDesynthesizeItems : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoDesynthesizeItemsTitle"),
        Description = GetLoc("AutoDesynthesizeItemsDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private static bool ConfigSkipWhenHQ;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { AbortOnTimeout = true, TimeLimitMS = 10000, ShowDebug = false };
        Overlay ??= new Overlay(this);

        AddConfig("SkipWhenHQ", ConfigSkipWhenHQ);
        ConfigSkipWhenHQ = GetConfig<bool>("SkipWhenHQ");

        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "SalvageDialog", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
    }

    public override void OverlayUI()
    {
        var addon = (AtkUnitBase*)DService.Gui.GetAddonByName("SalvageItemSelector");
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoDesynthesizeItemsTitle"));

        ImGui.Separator();

        ImGui.BeginDisabled(TaskHelper.IsBusy);
        if (ImGui.Checkbox(Lang.Get("AutoDesynthesizeItems-SkipHQ"), ref ConfigSkipWhenHQ))
            UpdateConfig("SkipWhenHQ", ConfigSkipWhenHQ);

        if (ImGui.Button(Lang.Get("Start"))) StartDesynthesize();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Stop"))) TaskHelper.Abort();
    }

    private void OnAddonList(AddonEvent type, AddonArgs args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AddonSalvageDialog*)SalvageDialog;
        if (addon == null) return;

        addon->BulkDesynthCheckboxNode->ClickAddonCheckBox(SalvageDialog, 3);
        addon->DesynthesizeButton->ClickAddonButton(SalvageDialog);
    }

    private bool? StartDesynthesize()
    {
        if (OccupiedInEvent) return false;
        if (TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var addon) &&
            IsAddonAndNodesReady(addon))
        {
            var itemAmount = addon->AtkValues[9].Int;
            if (itemAmount == 0)
            {
                TaskHelper.Abort();
                return true;
            }

            for (var i = 0; i < itemAmount; i++)
            {
                var itemName = MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[(i * 8) + 14].String.Value);
                if (ConfigSkipWhenHQ)
                {
                    if (itemName.Contains('')) // HQ 符号
                        continue;
                }

                var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Salvage);
                if (agent == null) return false;

                SendEvent(agent, 0, 12, i);

                TaskHelper.DelayNext(1500);
                TaskHelper.Enqueue(StartDesynthesize);
                return true;
            }
        }

        return false;
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        base.Uninit();
    }
}
