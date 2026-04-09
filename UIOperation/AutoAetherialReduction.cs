using DailyRoutines.Common.Interface.Windows;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Data;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAetherialReduction : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAetherialReductionTitle"),
        Description = Lang.Get("AutoAetherialReductionDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["YLCHEN"]
    };
    
    private TextNode?       lableNode;
    private TextButtonNode? startButtonNode;
    private TextButtonNode? stopButtonNode;

    protected override void Init()
    {
        TaskHelper ??= new();
        Overlay    ??= new Overlay(this);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "PurifyItemSelector", OnAddonList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "PurifyItemSelector", OnAddonList);

    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonList);
        ClearNodes();
    }

    private bool StartReduction()
    {
        if (TaskHelper == null) return false;

        TaskHelper.Enqueue(StartAetherialReduction);
        return true;
    }

    private bool StartAetherialReduction()
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

        if (DService.Instance().Condition.IsOccupiedInEvent) return false;

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

                if (lableNode == null)
                {
                    lableNode = new()
                    {
                        IsVisible     = true,
                        Position      = new(135, 8),
                        Size          = new(150, 28),
                        String        = $"{Info.Title}",
                        FontSize      = 14,
                        AlignmentType = AlignmentType.Right,
                        TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge
                    };
                    lableNode.AttachNode(PurifyItemSelector->RootNode);
                }

                if (startButtonNode == null)
                {
                    startButtonNode = new()
                    {
                        Position  = new(295, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        String    = Lang.Get("Start"),
                        OnClick   = () => StartReduction()
                    };
                    startButtonNode.AttachNode(PurifyItemSelector->RootNode);
                }

                startButtonNode.IsEnabled = !TaskHelper.IsBusy;

                if (stopButtonNode == null)
                {
                    stopButtonNode = new()
                    {
                        Position  = new(400, 10),
                        Size      = new(100, 28),
                        IsVisible = true,
                        String    = Lang.Get("Stop"),
                        OnClick   = () => TaskHelper.Abort()
                    };
                    stopButtonNode.AttachNode(PurifyItemSelector->RootNode);
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
        if (Inventories.Player.IsFull() ||
            DService.Instance().Condition.Any(ConditionFlag.Mounted, ConditionFlag.InCombat))
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }
    
    private void ClearNodes()
    {
        lableNode?.Dispose();
        lableNode = null;

        startButtonNode?.Dispose();
        startButtonNode = null;

        stopButtonNode?.Dispose();
        stopButtonNode = null;
    }
    
    #region IPC

    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.IsBusy")]
    private bool IsCurrentlyBusy => TaskHelper?.IsBusy ?? false;
    
    [IPCProvider("DailyRoutines.Modules.AutoAetherialReduction.StartReduction")]
    private bool StartReductionIPC() => StartReduction();

    #endregion
}
