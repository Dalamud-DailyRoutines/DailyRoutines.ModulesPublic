using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace DailyRoutines.Modules;

public unsafe class AutoAetherialReduction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAetherialReductionTitle"),
        Description = GetLoc("AutoAetherialReductionDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    private static unsafe AtkUnitBase* PurifyItemSelector => (AtkUnitBase*)DService.Gui.GetAddonByName("PurifyItemSelector");
    private static unsafe AtkUnitBase* PurifyResult => (AtkUnitBase*)DService.Gui.GetAddonByName("PurifyResult");

    private static readonly InventoryType[] BackpackInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };
        Overlay    ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PurifyResult",       OnAddon);
        
        if (IsAddonAndNodesReady(PurifyItemSelector)) OnAddonList(AddonEvent.PostSetup, null);
        if (IsAddonAndNodesReady(PurifyResult)) OnAddon(AddonEvent.PostSetup, null);
        
        GameResourceManager.AddToBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");
    }

    public override void Uninit()
    {
        GameResourceManager.RemoveFromBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");
        
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
    }

    public override void OverlayUI()
    {
        var addon = PurifyItemSelector;
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(LightSkyBlue, GetLoc("AutoAetherialReductionTitle"));

        ImGui.Separator();

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                TaskHelper.Enqueue(StartAetherialReduction, "开始精选");
        }
            
        ImGui.SameLine();
        if (ImGui.Button(GetLoc("Stop"))) 
            TaskHelper.Abort();
    }

    private bool? StartAetherialReduction()
    {
        if (!Throttler.Throttle("AutoAetherialReduction")) return false;
        if (OccupiedInEvent) return false;
        if (!IsAddonAndNodesReady(PurifyItemSelector)) return false;

        if (IsEnvironmentBlockingOperation()) return false;
        
        var itemAmount = PurifyItemSelector->UldManager.NodeList[3]->GetAsAtkComponentList()->ListLength;
        if (itemAmount == 0)
        {
            TaskHelper.Abort();
            return true;
        }
        
        SendEvent(AgentId.Purify, 0, 12, 0);
        TaskHelper.Enqueue(StartAetherialReduction);
        return true;
    }

    private bool IsEnvironmentBlockingOperation()
    {
        if (IsInventoryFull(BackpackInventories))
        {
            TaskHelper.Abort();
            return true;
        }

        if (DService.Condition[ConditionFlag.Mounted] ||
            DService.Condition[ConditionFlag.InCombat] ||
            DService.Condition[ConditionFlag.Occupied39] ||
            DService.Condition[ConditionFlag.Casting])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
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

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!Throttler.Throttle("AutoAetherialReduction")) return;
        if (!IsAddonAndNodesReady(PurifyResult)) return;
        
        if (TaskHelper.IsBusy)
        {
            Callback(PurifyResult, true, 0, 0);
        }
    }
}
