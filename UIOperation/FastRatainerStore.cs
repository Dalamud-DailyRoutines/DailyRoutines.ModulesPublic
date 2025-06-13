using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class FastRatainerStore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "雇员背包快速存取",
        Description = "在玩家背包和雇员背包中右键物品显示存入/取出全部相同物品的选项",
        Category = ModuleCategories.UIOperation,
        Author = ["YLChen"],
    };

    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    private static readonly InventoryType[] RetainerInventories =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5
    ];

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 10_000 };
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    public override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        base.Uninit();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args is not { MenuType: ContextMenuType.Inventory, Target: MenuTargetInventory { TargetItem: { } item }, AddonName: { } addonName })
            return;

        if (!LuminaGetter.TryGetRow<Item>(item.ItemId, out _)) return;

        var playerOpen = IsPlayerInventoryOpen();
        var retainerOpen = IsRetainerInventoryOpen();
        if (!playerOpen || !retainerOpen) return;

        if (IsPlayerAddon(addonName))
            args.AddMenuItem(new ItemMoveMenu(item.ItemId, item.IsHq, item.IsCollectable, true).Get());
        else if (IsRetainerAddon(addonName))
            args.AddMenuItem(new ItemMoveMenu(item.ItemId, item.IsHq, item.IsCollectable, false).Get());
    }

    private static bool IsPlayerInventoryOpen()
        => IsAddonAndNodesReady(GetAddonByName("Inventory")) ||
           IsAddonAndNodesReady(GetAddonByName("InventoryLarge")) ||
           IsAddonAndNodesReady(GetAddonByName("InventoryExpansion"));

    private static bool IsRetainerInventoryOpen()
        => IsAddonAndNodesReady(GetAddonByName("InventoryRetainer")) ||
           IsAddonAndNodesReady(GetAddonByName("InventoryRetainerLarge"));

    private static bool IsPlayerAddon(string addonName)
        => addonName is "Inventory" or "InventoryLarge" or "InventoryExpansion";

    private static bool IsRetainerAddon(string addonName)
        => addonName is "InventoryRetainer" or "InventoryRetainerLarge";

    private void ExecuteMoveAll(uint itemId, bool isHQ, bool isCollectable, bool storeToRetainer)
    {
        if (TaskHelper.IsBusy) return;

        var sourceInvs = storeToRetainer ? PlayerInventories : RetainerInventories;
        var targetInvs = storeToRetainer ? RetainerInventories : PlayerInventories;

        TaskHelper.Enqueue(() =>
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var moveCount = 0;

            // 查找所有相同物品并移动
            foreach (var sourceInv in sourceInvs)
            {
                var container = manager->GetInventoryContainer(sourceInv);
                if (container == null) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || !IsSameItem(slot, itemId, isHQ, isCollectable)) continue;

                    // 寻找目标位置
                    var targetSlot = FindTargetSlot(manager, targetInvs, itemId, isHQ, isCollectable);
                    if (targetSlot.Inv == InventoryType.Invalid)
                    {
                        NotificationWarning("目标背包空间不足", Info.Title);
                        return true;
                    }

                    // 移动物品
                    var result = manager->MoveItemSlot(sourceInv, (ushort)slot->Slot, targetSlot.Inv, (ushort)targetSlot.Slot, 1);
                    if (result == 0)
                        moveCount++;
                    else
                        NotificationError($"物品移动失败，错误代码: {result}", Info.Title);
                }
            }

            if (moveCount > 0)
                NotificationSuccess($"成功移动 {moveCount} 个物品", Info.Title);
            else
                NotificationWarning("未找到可移动的相同物品", Info.Title);

            return true;
        }, storeToRetainer ? "存入雇员" : "取出到背包");
    }

    private static bool IsSameItem(InventoryItem* slot, uint itemId, bool isHQ, bool isCollectable)
    {
        var rawId = slot->GetItemId();
        if (rawId == 0) return false;

        var currentIsHQ = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var currentIsCollectable = slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);

        var baseItemId = rawId;
        if (currentIsCollectable)
            baseItemId -= 500000;
        else if (currentIsHQ)
            baseItemId -= 1000000;

        return baseItemId == itemId && currentIsHQ == isHQ && currentIsCollectable == isCollectable;
    }

    private static (InventoryType Inv, int Slot) FindTargetSlot(InventoryManager* manager, InventoryType[] targetInvs, uint itemId, bool isHQ, bool isCollectable)
    {
        if (!LuminaGetter.TryGetRow<Item>(itemId, out var itemData))
            return (InventoryType.Invalid, -1);

        // 先找可以堆叠的位置
        foreach (var invType in targetInvs)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || !IsSameItem(slot, itemId, isHQ, isCollectable)) continue;

                if (slot->Quantity < itemData.StackSize)
                    return (invType, slot->Slot);
            }
        }

        // 找空位置
        foreach (var invType in targetInvs)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                if (slot->GetItemId() == 0)
                    return (invType, slot->Slot);
            }
        }

        return (InventoryType.Invalid, -1);
    }

    private class ItemMoveMenu(uint ItemId, bool IsHQ, bool IsCollectable, bool IsStoreToRetainer) : MenuItemBase
    {
        public override string Name { get; protected set; } = IsStoreToRetainer ? "全部存入雇员" : "全部取出到背包";
        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            ModuleManager.GetModule<FastRatainerStore>().ExecuteMoveAll(ItemId, IsHQ, IsCollectable, IsStoreToRetainer);
        }
    }
}