using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastRetainerStore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastRetainerStoreTitle"),
        Description = GetLoc("FastRetainerStoreDescription"),
        Category    = ModuleCategories.UIOperation,
        Author      = ["YLChen"]
    };
    
    private static readonly HashSet<string> PlayerAddonNames   = ["Inventory", "InventoryLarge", "InventoryExpansion"];
    private static readonly HashSet<string> RetainerAddonNames = ["InventoryRetainer", "InventoryRetainerLarge"];

    protected override void Init()
    {
        TaskHelper ??= new();

        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        base.Uninit();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args is not { MenuType: ContextMenuType.Inventory, Target: MenuTargetInventory { TargetItem: { } item }, AddonName: { } addonName })
            return;
        if (!LuminaGetter.TryGetRow<Item>(item.ItemId, out _)) return;

        var playerOpen   = IsPlayerInventoryOpen();
        var retainerOpen = IsRetainerInventoryOpen();
        if (!playerOpen || !retainerOpen) return;

        if (PlayerAddonNames.Contains(addonName))
        {
            if (TryFindTargetSlot(RetainerInventories, item.ItemId, item.IsHq, item.IsCollectable, out _))
                args.AddMenuItem(new ItemMoveMenu(item.ItemId, item.IsHq, item.IsCollectable, true).Get());
        }        
        else if (RetainerAddonNames.Contains(addonName))
        {
            if (TryFindTargetSlot(PlayerInventories, item.ItemId, item.IsHq, item.IsCollectable, out _))
                args.AddMenuItem(new ItemMoveMenu(item.ItemId, item.IsHq, item.IsCollectable, false).Get());
        }
        
        return;

        bool IsRetainerInventoryOpen()
            => IsAddonAndNodesReady(InventoryRetainer) ||
               IsAddonAndNodesReady(InventoryRetainerLarge);

        bool IsPlayerInventoryOpen()
            => IsAddonAndNodesReady(Inventory)      ||
               IsAddonAndNodesReady(InventoryLarge) ||
               IsAddonAndNodesReady(InventoryExpansion);
    }

    private void ExecuteMoveAll(uint itemID, bool isHQ, bool isCollectable, bool storeToRetainer)
    {
        if (TaskHelper.IsBusy) return;

        var sourceInvs = storeToRetainer ? PlayerInventories : RetainerInventories;
        var targetInvs = storeToRetainer ? RetainerInventories : PlayerInventories;

        TaskHelper.Enqueue(() =>
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return false;
            
            foreach (var sourceInv in sourceInvs)
            {
                var container = manager->GetInventoryContainer(sourceInv);
                if (container == null) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || !IsSameItem(slot, itemID, isHQ, isCollectable)) continue;

                    if (!TryFindTargetSlot(targetInvs, itemID, isHQ, isCollectable, out var targetSlot))
                        return true;

                    manager->MoveItemSlot(sourceInv, (ushort)slot->Slot, targetSlot.Inventory, (ushort)targetSlot.Slot, true);
                }
            }

            return true;
        }, storeToRetainer ? "存入雇员" : "取出到背包");
    }

    private static bool IsSameItem(InventoryItem* slot, uint itemID, bool isHQ, bool isCollectable)
    {
        var rawID = slot->GetItemId();
        if (rawID == 0) return false;

        var currentIsHQ          = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var currentIsCollectable = slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);

        var baseItemID = rawID;
        if (currentIsCollectable)
            baseItemID %= 500000;
        else if (currentIsHQ)
            baseItemID %= 1000000;

        return baseItemID == itemID && currentIsHQ == isHQ && currentIsCollectable == isCollectable;
    }

    private static bool TryFindTargetSlot(List<InventoryType> targetInvs, uint itemID, bool isHQ, bool isCollectable, 
                                          out (InventoryType Inventory, int Slot) targetSlot)
    {
        var manager = InventoryManager.Instance();
        
        if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemData))
        {
            targetSlot = (InventoryType.Invalid, -1);
            return false;
        }

        foreach (var invType in targetInvs)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || !IsSameItem(slot, itemID, isHQ, isCollectable)) continue;

                if (slot->Quantity < itemData.StackSize)
                {
                    targetSlot = (invType, slot->Slot);
                    return true;
                }
            }
        }

        foreach (var invType in targetInvs)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                if (slot->GetItemId() == 0)
                {
                    targetSlot = (invType, slot->Slot);
                    return true;
                }
            }
        }

        targetSlot = (InventoryType.Invalid, -1);
        return false;
    }

    private class ItemMoveMenu(uint ItemID, bool IsHQ, bool IsCollectable, bool IsStoreToRetainer) : MenuItemBase
    {
        public override    string Name         { get; protected set; } = GetLoc(IsStoreToRetainer ? "SaveAll" : "RetrieveAll");
        protected override bool   WithDRPrefix { get; set; }           = true;

        protected override void OnClicked(IMenuItemClickedArgs args) => 
            ModuleManager.GetModule<FastRetainerStore>().ExecuteMoveAll(ItemID, IsHQ, IsCollectable, IsStoreToRetainer);
    }
}
