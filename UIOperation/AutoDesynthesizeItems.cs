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
using DailyRoutines.Managers;

namespace DailyRoutines.Modules;

public unsafe class AutoDesynthesizeItems : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDesynthesizeItemsTitle"),
        Description = GetLoc("AutoDesynthesizeItemsDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static bool SkipWhenHQ;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };
        Overlay    ??= new Overlay(this);

        AddConfig(nameof(SkipWhenHQ), SkipWhenHQ);
        SkipWhenHQ = GetConfig<bool>(nameof(SkipWhenHQ));

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageDialog",       OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "SalvageItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnAddonList);
        if (IsAddonAndNodesReady(SalvageItemSelector)) 
            OnAddonList(AddonEvent.PostSetup, null);
        
        GameResourceManager.AddToBlacklist(typeof(AutoDesynthesizeItems), "chara/action/normal/item_action.tmb");
    }

    public override void OverlayUI()
    {
        var addon = SalvageItemSelector;
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(LightSkyBlue, GetLoc("AutoDesynthesizeItemsTitle"));

        ImGui.Separator();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Checkbox(GetLoc("AutoDesynthesizeItems-SkipHQ"), ref SkipWhenHQ))
                UpdateConfig("SkipWhenHQ", SkipWhenHQ);

            if (ImGui.Button(GetLoc("Start"))) 
                StartDesynthesize();
        }
        
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop"))) 
            TaskHelper.Abort();
    }

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen,
        };
        
        if (type == AddonEvent.PreFinalize)
            TaskHelper.Abort();
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoDesynthesizeItems", 100)) return;
        if (!IsAddonAndNodesReady(SalvageDialog)) return;

        Callback(SalvageDialog, true, 0, 0);
    }

    private bool? StartDesynthesize()
    {
        if (OccupiedInEvent) return false;
        if (!IsAddonAndNodesReady(SalvageItemSelector)) return false;
        
        var itemAmount = SalvageItemSelector->AtkValues[9].Int;
        if (itemAmount == 0)
        {
            TaskHelper.Abort();
            return true;
        }
        
        for (var i = 0; i < itemAmount; i++)
        {
            var itemName = MemoryHelper.ReadStringNullTerminated((nint)SalvageItemSelector->AtkValues[(i * 8) + 14].String.Value);
            if (SkipWhenHQ)
            {
                if (itemName.Contains('')) // HQ 符号
                    continue;
            }

            SendEvent(AgentId.Salvage, 0, 12, i);
            TaskHelper.Enqueue(StartDesynthesize);
            return true;
        }

        TaskHelper.Abort();
        return true;
    }

    public override void Uninit()
    {
        GameResourceManager.RemoveFromBlacklist(typeof(AutoDesynthesizeItems), "chara/action/normal/item_action.tmb");
        
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        base.Uninit();
    }
}
