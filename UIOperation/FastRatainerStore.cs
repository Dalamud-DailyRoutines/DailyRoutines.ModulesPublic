using DailyRoutines.Abstracts;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Lumina.Excel;

namespace DailyRoutines.Modules;

public unsafe class FastRatainerStore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "雇员背包快速存取",
        Description = "在玩家背包和雇员背包中右键物品显示存入/取出全部相同物品的选项",
        Category = ModuleCategories.UIOperation,
    };


    private enum InventoryOwner
    {
        Player,
        Retainer
    }

    private static readonly Dictionary<InventoryOwner, InventoryType[]> InventoryConfigs = new()
    {
        [InventoryOwner.Player] = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4],
        [InventoryOwner.Retainer] = [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5]
    };

    private static readonly Dictionary<InventoryOwner, string[]> AddonConfigs = new()
    {
        [InventoryOwner.Player] = ["Inventory", "InventoryLarge", "InventoryExpansion"],
        [InventoryOwner.Retainer] = ["InventoryRetainer", "InventoryRetainerLarge"]
    };

    private sealed record ItemInfo
    {
        public uint ItemId { get; init; }
        public bool IsHQ { get; init; }
        public bool IsCollectable { get; init; }
        public int Total { get; set; }
        public List<ItemLocation> Locations { get; init; } = [];
    }

    private sealed record ItemLocation(InventoryType Source, int Slot, int Quantity);
    private readonly record struct ItemIdentifier(uint ItemId, bool IsHQ, bool IsCollectable);
    private static ExcelSheet<Item>? CachedItemSheet;
    private static ExcelSheet<Item>? CachedItemSheetInstance => CachedItemSheet ??= DService.Data.GetExcelSheet<Item>();

    private bool IsInventoryOpen(InventoryOwner owner) 
        => AddonConfigs[owner].Any(name => IsAddonAndNodesReady(GetAddonByName(name)));

    private static bool IsAddonOfType(string addonName, InventoryOwner owner) 
        => AddonConfigs[owner].Contains(addonName);

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory) return;

        var invItem = ((MenuTargetInventory)args.Target).TargetItem!.Value;
        var itemId = invItem.ItemId;
        var isHQ = invItem.IsHq;
        var isCollectable = invItem.IsCollectable;

        if (CachedItemSheetInstance?.HasRow(itemId) != true) return;

        var hasPlayer = IsInventoryOpen(InventoryOwner.Player);
        var hasRetainer = IsInventoryOpen(InventoryOwner.Retainer);

        if (!hasPlayer || !hasRetainer) return;

        if (args.AddonName != null && IsAddonOfType(args.AddonName, InventoryOwner.Player))
            args.AddMenuItem(CreateMenuItem(itemId, isHQ, isCollectable, InventoryOwner.Player, "[DR] 全部存入雇员"));
        else if (args.AddonName != null && IsAddonOfType(args.AddonName, InventoryOwner.Retainer))
            args.AddMenuItem(CreateMenuItem(itemId, isHQ, isCollectable, InventoryOwner.Retainer, "[DR] 全部取出到背包"));
    }

    private MenuItem CreateMenuItem(uint itemId, bool isHQ, bool isCollectable, InventoryOwner sourceOwner, string menuText)
    {
        var menu = new MenuItem
        {
            Name = new SeString(new TextPayload(menuText))
        };

        var actionText = sourceOwner == InventoryOwner.Player ? "全部存入雇员" : "全部取出到背包";
        menu.OnClicked += _ => ExecuteMoveAllTask(itemId, isHQ, isCollectable, sourceOwner, actionText);

        return menu;
    }

    private void ExecuteMoveAllTask(uint itemId, bool isHQ, bool isCollectable, InventoryOwner sourceOwner, string taskName)
    {
        if (TaskHelper.IsBusy) return;

        TaskHelper.Enqueue(() =>
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var itemInfo = CollectSameItems(manager, sourceOwner, itemId, isHQ, isCollectable);
            if (itemInfo.Locations.Count == 0)
            {
                var action = sourceOwner == InventoryOwner.Player ? "存入" : "取出";
                NotificationWarning($"未找到可{action}的相同物品", Info.Title);
                return true;
            }

            ScheduleItemMoves(itemInfo, sourceOwner);
            return true;
        }, $"{taskName} - 准备阶段");
    }

    private void ScheduleItemMoves(ItemInfo itemInfo, InventoryOwner sourceOwner)
    {
        var targetOwner = sourceOwner == InventoryOwner.Player ? InventoryOwner.Retainer : InventoryOwner.Player;
        var targetAddons = AddonConfigs[targetOwner];
        var totalItems = itemInfo.Locations.Count;
        var successCount = 0;

        for (int i = 0; i < itemInfo.Locations.Count; i++)
        {
            var location = itemInfo.Locations[i];
            var currentIndex = i + 1; 
            var isLast = i == itemInfo.Locations.Count - 1;

            TaskHelper.Enqueue(() =>
            {
                var manager = InventoryManager.Instance();
                if (manager == null) return false;

                var target = FindInventorySlot(itemInfo.ItemId, location.Quantity, itemInfo.IsHQ, itemInfo.IsCollectable, targetOwner, targetAddons);
                if (target.Inv == InventoryType.Invalid)
                {
                    NotificationWarning("目标背包空间不足", Info.Title);
                    TaskHelper.Abort(); 
                    return false;
                }

                var result = manager->MoveItemSlot(location.Source, (ushort)location.Slot, target.Inv, (ushort)target.Slot, 1);
                if (result == 0)
                {
                    successCount++;
                    if (isLast)
                    {
                        var message = successCount == totalItems ? 
                            $"物品移动完成":
                            $"成功移动 {successCount}/{totalItems} 个物品";
                        NotificationSuccess(message, Info.Title);
                    }
                    return true;
                }
                else
                {
                    NotificationError($"物品移动失败，错误代码: {result} ({currentIndex}/{totalItems})", Info.Title);
                    return false;
                }
            }, $"移动物品 {currentIndex}/{totalItems}");

            if (!isLast)
                TaskHelper.DelayNext(100);
        }
    }

    private ItemInfo CollectSameItems(InventoryManager* manager, InventoryOwner sourceOwner, uint itemId, bool isHQ, bool isCollectable)
    {
        var itemInfo = new ItemInfo
        {
            ItemId = itemId,
            IsHQ = isHQ,
            IsCollectable = isCollectable,
            Total = 0
        };

        var targetIdentifier = new ItemIdentifier(itemId, isHQ, isCollectable);

        foreach (var invType in InventoryConfigs[sourceOwner])
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item == null) continue;

                var rawId = item->GetItemId();
                if (rawId == 0) continue;

                var itemIdentifier = ProcessItemId(rawId, item);

                if (itemIdentifier.Equals(targetIdentifier))
                {
                    var quantity = (int)item->GetQuantity();
                    itemInfo.Total += quantity;
                    itemInfo.Locations.Add(new ItemLocation(invType, item->Slot, quantity));
                }
            }
        }

        return itemInfo;
    }

    private (InventoryType Inv, int Slot) FindInventorySlot(uint itemId, int quantity, bool isHQ, bool isCollectable,
        InventoryOwner targetOwner, string[] addons)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return (InventoryType.Invalid, -1);

        if (CachedItemSheetInstance?.HasRow(itemId) != true) return (InventoryType.Invalid, -1);

        if (!IsInventoryOpen(targetOwner)) return (InventoryType.Invalid, -1);

        var row = CachedItemSheetInstance.GetRow(itemId);

        var stackSlot = FindStackableSlot(manager, itemId, quantity, isHQ, isCollectable, row, targetOwner);
        if (stackSlot.Inv != InventoryType.Invalid)
            return stackSlot;

        return FindEmptySlot(manager, targetOwner);
    }

    private (InventoryType Inv, int Slot) FindStackableSlot(InventoryManager* manager, 
        uint itemId, int quantity, bool isHQ, bool isCollectable, Item row, InventoryOwner targetOwner)
    {
        var targetIdentifier = new ItemIdentifier(itemId, isHQ, isCollectable);

        foreach (var invType in InventoryConfigs[targetOwner])
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;

                var rawId = item->GetItemId();
                if (rawId == 0) continue;

                var itemIdentifier = ProcessItemId(rawId, item);
                
                if (itemIdentifier.Equals(targetIdentifier) &&
                    (item->GetQuantity() + quantity) <= row.StackSize)
                    return (invType, item->Slot);
            }
        }
        
        return (InventoryType.Invalid, -1);
    }

    private (InventoryType Inv, int Slot) FindEmptySlot(InventoryManager* manager, InventoryOwner targetOwner)
    {
        foreach (var invType in InventoryConfigs[targetOwner])
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;

                if (item->GetItemId() == 0)
                    return (invType, item->Slot);
            }
        }

        return (InventoryType.Invalid, -1);
    }

    private static ItemIdentifier ProcessItemId(uint rawId, InventoryItem* item)
    {
        var isCollectable = item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);
        var isHQ = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        
        var itemId = rawId;
        if (isCollectable)
            itemId -= 500000;
        else if (isHQ)
            itemId -= 1000000;
            
        return new ItemIdentifier(itemId, isHQ, isCollectable);
    }

    public override void Uninit()
    {
        TaskHelper?.Abort();
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        
        base.Uninit();
    }
} 