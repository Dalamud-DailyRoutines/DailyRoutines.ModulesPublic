using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class FastRetainerStore : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastRetainerStoreTitle"),
        Description = Lang.Get("FastRetainerStoreDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["YLCHEN"]
    };

    private ItemMoveMenu menuItem = null!;

    protected override void Init()
    {
        TaskHelper ??= new();

        menuItem = new(this);
        ContextMenuManager.Instance().Reg(menuItem);
    }

    protected override void Uninit() =>
        ContextMenuManager.Instance().Unreg(menuItem);

    private void ExecuteMoveAll
    (
        uint itemID,
        bool isHQ,
        bool isCollectable,
        bool storeToRetainer
    )
    {
        if (TaskHelper.IsBusy) return;

        var sourceInvs = storeToRetainer ?
                             Inventories.Player :
                             Inventories.Retainer;
        var targetInvs = storeToRetainer ?
                             Inventories.Retainer :
                             Inventories.Player;

        TaskHelper.Enqueue
        (
            () =>
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

                        manager->MoveItemSlot
                        (
                            sourceInv,
                            (ushort)slot->Slot,
                            targetSlot.Inventory,
                            (ushort)targetSlot.Slot,
                            true
                        );
                    }
                }

                return true;
            },
            storeToRetainer ?
                "存入雇员" :
                "取出到背包"
        );
    }

    private static bool IsSameItem
    (
        InventoryItem* slot,
        uint           itemID,
        bool           isHQ,
        bool           isCollectable
    )
    {
        var rawID = slot->GetBaseItemId();
        if (rawID == 0) return false;

        var currentIsHQ          = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var currentIsCollectable = slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);

        return rawID                == itemID &&
               currentIsHQ          == isHQ   &&
               currentIsCollectable == isCollectable;
    }

    private static bool TryFindTargetSlot
    (
        FrozenSet<InventoryType>                targetInvs,
        uint                                    itemID,
        bool                                    isHQ,
        bool                                    isCollectable,
        out (InventoryType Inventory, int Slot) targetSlot
    )
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

    private sealed class ItemMoveMenu
    (
        FastRetainerStore module
    ) : ContextMenuEntry
    {
        public override string Identifier =>
            nameof(FastRetainerStore);

        public override ContextMenuItem? Create
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.TargetInventoryItem is not { } item || args.AddonName is not { } addonName)
                return null;

            var itemID = item.GetBaseItemId();
            if (!LuminaGetter.TryGetRow<Item>(itemID, out _)) return null;

            var playerOpen   = IsPlayerInventoryOpen();
            var retainerOpen = IsRetainerInventoryOpen();
            if (!playerOpen || !retainerOpen) return null;

            var isHQ          = item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var isCollectable = item.Flags.HasFlag(InventoryItem.ItemFlags.Collectable);

            if (PlayerAddonNames.Contains(addonName))
            {
                if (!TryFindTargetSlot(Inventories.Retainer, itemID, isHQ, isCollectable, out _))
                    return null;

                return CreateItem(true);
            }

            if (RetainerAddonNames.Contains(addonName))
            {
                if (!TryFindTargetSlot(Inventories.Player, itemID, isHQ, isCollectable, out _))
                    return null;

                return CreateItem(false);
            }

            return null;

            ContextMenuItem CreateItem
            (
                bool storeToRetainer
            ) =>
                new()
                {
                    Name = Lang.Get
                    (
                        storeToRetainer ?
                            "FastRetainerStore-SaveAll" :
                            "FastRetainerStore-RetrieveAll"
                    ),
                    OnClicked = _ => module.ExecuteMoveAll(itemID, isHQ, isCollectable, storeToRetainer)
                };

            bool IsRetainerInventoryOpen() =>
                InventoryRetainer->IsAddonAndNodesReady() ||
                InventoryRetainerLarge->IsAddonAndNodesReady();

            bool IsPlayerInventoryOpen() =>
                Inventory->IsAddonAndNodesReady()      ||
                InventoryLarge->IsAddonAndNodesReady() ||
                InventoryExpansion->IsAddonAndNodesReady();
        }
    }

    #region 常量

    private static readonly string[] PlayerAddonNames =
    [
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion"
    ];

    private static readonly string[] RetainerAddonNames =
    [
        "InventoryRetainer",
        "InventoryRetainerLarge"
    ];

    #endregion
}
