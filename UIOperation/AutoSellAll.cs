using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Windows.Forms;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoSellAll : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoSellAllTitle"),
        Description = GetLoc("AutoSellAllDescription"),
        Category = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["AutoRetainerWork"],
        Author = ["Yangdoubao"],
    };

    private static int currentErrorCount;
    private const int MaxErrorCount = 5;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 5000, AbortOnTimeout = true };
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        base.Uninit();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: { } item } ||
            !args.AddonName.StartsWith("Inventory") ||
            !LuminaGetter.TryGetRow<Item>(item.ItemId, out var itemData) ||
            itemData.IsUntradable)
            return;
        args.AddMenuItem(new SellMenu(item.ItemId, item.IsHq, item.Quantity).Get());
    }

    private void ExecuteSellTask(uint itemID, bool itemHq, int itemAmount, string taskName)
    {
        TaskHelper.Abort();
        currentErrorCount = 0;
        TaskHelper.Enqueue(() =>
        {
            if (AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer) == null)
            {
                NotificationError("请先与雇员互动");
                return true; 
            }
            TaskHelper.Enqueue(() => SellLoop(itemID, itemHq), "启动出售循环");
            return true;
        }, "开始出售所有");
    }

   
    private (InventoryType, ushort) FindItemInInventory(uint itemID, bool isHq)
    {
        foreach (var invType in PlayerInventories)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(invType);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId != itemID) continue;
                if (item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != isHq) continue;
                return (invType, (ushort)item->Slot);
            }
        }
        return (InventoryType.Invalid, 0);
    }


    private bool? SellLoop(uint itemID, bool isHq)
    {

        if (currentErrorCount >= MaxErrorCount)
        {
            NotificationError($"出售失败次数过多 ({currentErrorCount}次)，已停止。");
            return true; 
        }
        var (inv, slot) = FindItemInInventory(itemID, isHq);
        if (inv == InventoryType.Invalid)
        {
            NotificationSuccess("物品已全部售出或不在背包中。");
            return true;
        }

        TaskHelper.Enqueue(() =>
        {
            var contextAgent = AgentInventoryContext.Instance();
            if (contextAgent == null) return false;
            OpenInventoryItemContext(inv, slot);
            return true;
        }, "打开出售窗口");
        TaskHelper.Enqueue(HandleSell,"点击出售");
        
        TaskHelper.DelayNext(300, "出售后延迟");

        TaskHelper.Enqueue(() => SellLoop(itemID, isHq), "下一轮出售");

        return true;
    }
    private bool? HandleSell()
    {
        if (IsAddonAndNodesReady(InfosOm.ContextMenu))
        {
            var text = LuminaWrapper.GetAddonText(99);
            if (ClickContextMenu(text))
            return true;
        }
        else
        {
            TaskHelper.DelayNext(10);
            TaskHelper.Enqueue(() =>
            {
                HandleSell();
            });
        }
        return false;
    }
    private class SellMenu(uint itemID, bool isItemHq, int itemCount) : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("AutoSellAll");
        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            ModuleManager.GetModule<AutoSellAll>().ExecuteSellTask(itemID, isItemHq, itemCount, "右键菜单出售");
    }
}
