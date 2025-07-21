using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAetherialReduction : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAetherialReductionTitle"),
        Description = GetLoc("AutoAetherialReductionDescription"),
        Category    = ModuleCategories.UIOperation,
        Author      = ["YLCHEN"]
    };
    
    public bool IsReducing => TaskHelper?.IsBusy ?? false;
    
    private static readonly InventoryType[] Inventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    protected override void Init()
    {
        TaskHelper ??= new TaskHelper();
        Overlay    ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnAddonList);
        if (IsAddonAndNodesReady(PurifyItemSelector)) 
            OnAddonList(AddonEvent.PostSetup, null);

        GameResourceManager.AddToBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");
    }

    protected override void OverlayUI()
    {
        var addon = PurifyItemSelector;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }
        if (!IsAddonAndNodesReady(addon)) return;

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

    protected override void Uninit()
    {
        GameResourceManager.RemoveFromBlacklist(typeof(AutoAetherialReduction), "chara/action/normal/item_action.tmb");

        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        
        base.Uninit();
    }

    private bool? StartAetherialReduction()
    {
        if (IsCurrentEnvironmentInvalid()) return true;
        
        var agent = AgentPurify.Instance();
        if (agent == null || agent->ReducibleItems.Count == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        var manager = InventoryManager.Instance();
        if (manager == null) return false;
        
        if (OccupiedInEvent) return false;

        var firstItem = agent->ReducibleItems.First;
        if (firstItem == null)
        {
            TaskHelper.Abort();
            return true;
        }

        var inventoryItem = manager->GetInventorySlot(firstItem->Inventory, firstItem->Slot);
        if (inventoryItem == null)
        {
            TaskHelper.Abort();
            return true;
        }
        
        agent->ReduceItem(inventoryItem);

        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(StartAetherialReduction);
        return true;
    }

    private bool IsCurrentEnvironmentInvalid()
    {
        if (IsInventoryFull(Inventories)               ||
            DService.Condition[ConditionFlag.Mounted]  ||
            DService.Condition[ConditionFlag.InCombat])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }
    
    public bool StartReduction()
    {
        if (TaskHelper == null) return false;
        TaskHelper.Enqueue(StartAetherialReduction, "开始精选");
        return true;
    }

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

        if (type == AddonEvent.PreFinalize)
            TaskHelper.Abort();
    }
    
    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.IsBusy")]
    public bool IsCurrentlyBusy => IsReducing;

    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.StartReduction")]
    public bool StartReductionIPC() => StartReduction();
}
