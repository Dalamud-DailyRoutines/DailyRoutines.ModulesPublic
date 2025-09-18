using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

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

    private static TextNode       LableNode;
    private static TextButtonNode StartButtonNode;
    private static TextButtonNode StopButtonNode;
    
    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay    ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,   "PurifyItemSelector", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnAddonList);

    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        ClearNodes();
    }

    private bool StartReduction()
    {
        if (TaskHelper == null) return false;
        
        TaskHelper.Enqueue(StartAetherialReduction);
        return true;
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

    private void OnAddonList(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (PurifyItemSelector == null) return;

                if (LableNode == null)
                {
                    LableNode = new()
                    {
                        IsVisible     = true,
                        Position      = new(135, 8),
                        Size          = new(150, 28),
                        SeString      = $"{Info.Title}",
                        FontSize      = 14,
                        AlignmentType = AlignmentType.Right,
                        TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge
                    };
                    Service.AddonController.AttachNode(LableNode, PurifyItemSelector->RootNode);
                }

                if (StartButtonNode == null)
                {
                    StartButtonNode = new()
                    {
                        Position  = new(295, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        SeString  = GetLoc("Start"),
                        OnClick   = () => StartReduction()
                    };
                    Service.AddonController.AttachNode(StartButtonNode, PurifyItemSelector->RootNode);
                }

                StartButtonNode.IsEnabled = !TaskHelper.IsBusy;
                
                if (StopButtonNode == null)
                {
                    StopButtonNode = new()
                    {
                        Position  = new(400, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        SeString  = GetLoc("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    Service.AddonController.AttachNode(StopButtonNode, PurifyItemSelector->RootNode);
                }
                
                break;
            
            case AddonEvent.PreFinalize:
                ClearNodes();
                TaskHelper.Abort();
                break;
        }
    }
    
    private bool IsCurrentEnvironmentInvalid()
    {
        if (IsInventoryFull(PlayerInventories)        ||
            DService.Condition[ConditionFlag.Mounted] ||
            DService.Condition[ConditionFlag.InCombat])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }
    
    
    private static void ClearNodes()
    {
        Service.AddonController.DetachNode(LableNode);
        LableNode = null;
        
        Service.AddonController.DetachNode(StartButtonNode);
        StartButtonNode = null;
        
        Service.AddonController.DetachNode(StopButtonNode);
        StopButtonNode = null;
    }
    
    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.IsBusy")]
    public bool IsCurrentlyBusy => TaskHelper?.IsBusy ?? false;

    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.StartReduction")]
    public bool StartReductionIPC() => StartReduction();
}
