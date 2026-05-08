using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 刷新

    private void StartRefreshAll(UnifiedItem? reselectItem = null)
    {
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };
        TaskHelper.Abort();

        isRefreshingItems = true;
        items.Clear();
        filteredItems.Clear();
        ownedConcreteItemIDs.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount = 0;
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);

        TaskHelper.Enqueue(() => RebuildOwnedConcreteItemIndex(), "刷新收藏柜索引");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => LoadPrismBoxItems(), "读取投影台");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => LoadCabinetItems(), "读取收藏柜");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(
            () =>
            {
                MergeItems();
                LoadPreviewItems();

                if (reselectItem != null)
                    ReselectItem(reselectItem);

                isRefreshingItems = false;
                MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
            },
            "合并装备列表");
    }

    private void ReselectItem(UnifiedItem item)
    {
        selectedItem = items.FirstOrDefault(x =>
            x.ItemID == item.ItemID &&
            x.PrismBoxIndex == item.PrismBoxIndex &&
            x.IsSetPart == item.IsSetPart &&
            x.ParentSetItemID == item.ParentSetItemID);
    }

    private void RebuildOwnedConcreteItemIndex()
    {
        ownedConcreteItemIDs.Clear();

        AddOwnedPrismBoxItemIDs();
        AddOwnedCabinetItemIDs();
    }

    private void AddOwnedPrismBoxItemIDs()
    {
        if (!TryGetLoadedMirageManager(out var manager))
            return;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
            if (itemID > MIN_VALID_ITEM_ID)
                ownedConcreteItemIDs.Add(itemID);
        }
    }

    private void AddOwnedCabinetItemIDs()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var itemID = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetRow.RowId))
                continue;

            ownedConcreteItemIDs.Add(itemID);
        }
    }

    #endregion

    #region 投影台

    private void LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager))
            return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var setParts = GetSetParts(itemID);
            items.Add(CreateUnifiedItem(
                itemID,
                rawItemID,
                name,
                itemRow.Value,
                inPrismBox: true,
                inCabinet: false,
                prismBoxIndex: i,
                cabinetID: 0,
                stain0ID: manager->PrismBoxStain0Ids[(int)i],
                stain1ID: manager->PrismBoxStain1Ids[(int)i],
                isSetContainer: setParts.Count > 0));

            count++;
            AddPrismBoxSetParts(manager, setParts, rawItemID, i, itemID, name);
        }

        prismBoxItemCount = count;
    }

    private void AddPrismBoxSetParts(
        MirageManager* manager,
        List<SetPartInfo> setParts,
        uint rawItemID,
        uint prismBoxIndex,
        uint parentItemID,
        string parentName)
    {
        foreach (var setPart in setParts)
        {
            if (!manager->IsSetSlotUnlocked(prismBoxIndex, setPart.SlotIndex))
                continue;

            AddSetPartItem(
                setPart,
                rawItemID,
                prismBoxIndex,
                cabinetID: 0,
                parentItemID,
                parentName,
                inPrismBox: true,
                inCabinet: false);
        }
    }

    #endregion

    #region 收藏柜

    private void LoadCabinetItems()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetID))
                continue;

            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var setParts = GetSetParts(itemID);
            items.Add(CreateUnifiedItem(
                itemID,
                itemID,
                name,
                itemRow.Value,
                inPrismBox: false,
                inCabinet: true,
                prismBoxIndex: 0,
                cabinetID,
                stain0ID: 0,
                stain1ID: 0,
                isSetContainer: setParts.Count > 0));

            count++;
            AddCabinetSetParts(setParts, itemID, cabinetID, name);
        }

        cabinetItemCount = count;
    }

    private void AddCabinetSetParts(
        List<SetPartInfo> setParts,
        uint parentItemID,
        uint cabinetID,
        string parentName)
    {
        foreach (var setPart in setParts)
        {
            AddSetPartItem(
                setPart,
                rawItemID: parentItemID,
                prismBoxIndex: 0,
                cabinetID,
                parentItemID,
                parentName,
                inPrismBox: false,
                inCabinet: true);
        }
    }

    private static GameCabinet* GetLoadedCabinet()
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return null;

        var cabinet = &uiState->Cabinet;
        return cabinet->IsCabinetLoaded() ? cabinet : null;
    }

    #endregion

    #region 套装部件

    private static List<SetPartInfo> GetSetParts(uint setItemID)
    {
        if (!LuminaGetter.TryGetRow<MirageStoreSetItem>(setItemID, out var row))
            return [];

        List<SetPartInfo> parts = [];

        AddSetPart(parts, row.MainHand.RowId, 0);
        AddSetPart(parts, row.OffHand.RowId, 1);
        AddSetPart(parts, row.Head.RowId, 2);
        AddSetPart(parts, row.Body.RowId, 3);
        AddSetPart(parts, row.Hands.RowId, 4);
        AddSetPart(parts, row.Legs.RowId, 5);
        AddSetPart(parts, row.Feet.RowId, 6);
        AddSetPart(parts, row.Earrings.RowId, 7);
        AddSetPart(parts, row.Necklace.RowId, 8);
        AddSetPart(parts, row.Bracelets.RowId, 9);
        AddSetPart(parts, row.Ring.RowId, 10);

        return parts;
    }

    private static void AddSetPart(
        List<SetPartInfo> parts,
        uint itemID,
        int slotIndex)
    {
        if (itemID <= MIN_VALID_ITEM_ID)
            return;

        if (!LuminaGetter.TryGetRow<ItemSheet>(itemID, out var itemRow) ||
            !TryGetItemName(itemRow, out _))
            return;

        if (!CanItemRowUseSetPartLabel(itemRow, slotIndex))
            return;

        var label = GetNativeItemCategoryName(itemRow);
        if (parts.Any(x => x.ItemID == itemID && x.PartLabel == label))
            return;

        parts.Add(new SetPartInfo(itemID, label, slotIndex));
    }

    private void AddSetPartItem(
        SetPartInfo setPart,
        uint rawItemID,
        uint prismBoxIndex,
        uint cabinetID,
        uint parentItemID,
        string parentName,
        bool inPrismBox,
        bool inCabinet)
    {
        if (!LuminaGetter.TryGetRow<ItemSheet>(setPart.ItemID, out var partRow) ||
            !TryGetItemName(partRow, out var partName))
            return;

        items.Add(CreateUnifiedItem(
            setPart.ItemID,
            rawItemID,
            $"{partName}（{parentName} / {setPart.PartLabel}）",
            partRow,
            inPrismBox,
            inCabinet,
            prismBoxIndex,
            cabinetID,
            stain0ID: 0,
            stain1ID: 0,
            isSetContainer: false,
            isSetPart: true,
            parentSetItemID: parentItemID,
            parentSetName: parentName,
            setPartLabel: setPart.PartLabel));
    }


    private static string GetNativeItemCategoryName(ItemSheet item)
    {
        var categoryName = item.ItemUICategory.Value.Name.ExtractText();
        return string.IsNullOrWhiteSpace(categoryName)
            ? item.Name.ExtractText()
            : categoryName;
    }

    private static bool CanItemRowUseSetPartLabel(ItemSheet item, int slotIndex)
    {
        var category = item.EquipSlotCategory.Value;
        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == slotIndex);

        return !definition.Equals(default(PlateSlotDefinition)) && definition.CanEquip(category);
    }

    #endregion

    #region 合并

    private void MergeItems()
    {
        var merged = items
            .GroupBy(GetMergeKey)
            .Select(MergeItemGroup)
            .ToList();

        items.Clear();
        items.AddRange(merged);
    }

    private static UnifiedItem MergeItemGroup(IGrouping<string, UnifiedItem> group)
    {
        var first = group.First();
        if (first.IsSetPart)
            return first;

        var prism = group.FirstOrDefault(x => x.InPrismBox);
        var cabinet = group.FirstOrDefault(x => x.InCabinet);

        first.InPrismBox = prism != null;
        first.InCabinet = cabinet != null;

        if (prism != null)
            MergePrismBoxData(first, prism);

        if (cabinet != null)
            MergeCabinetData(first, cabinet);

        return first;
    }

    private static void MergePrismBoxData(UnifiedItem target, UnifiedItem prism)
    {
        target.RawItemID = prism.RawItemID;
        target.PrismBoxIndex = prism.PrismBoxIndex;
        target.Stain0ID = prism.Stain0ID;
        target.Stain1ID = prism.Stain1ID;
        target.IconID = prism.IconID;
        target.EquipSlotCategoryRowID = prism.EquipSlotCategoryRowID;
        target.ClassJobCategoryRowID = prism.ClassJobCategoryRowID;
        target.ModelMain = prism.ModelMain;
        target.LevelEquip = prism.LevelEquip;
        target.IsSetContainer = prism.IsSetContainer;
    }

    private static void MergeCabinetData(UnifiedItem target, UnifiedItem cabinet)
    {
        target.CabinetID = cabinet.CabinetID;
        target.IconID = target.IconID == 0 ? cabinet.IconID : target.IconID;
        target.EquipSlotCategoryRowID = target.EquipSlotCategoryRowID == 0 ? cabinet.EquipSlotCategoryRowID : target.EquipSlotCategoryRowID;
        target.ClassJobCategoryRowID = target.ClassJobCategoryRowID == 0 ? cabinet.ClassJobCategoryRowID : target.ClassJobCategoryRowID;
        target.ModelMain = target.ModelMain == 0 ? cabinet.ModelMain : target.ModelMain;
        target.LevelEquip = target.LevelEquip == 0 ? cabinet.LevelEquip : target.LevelEquip;
        target.IsSetContainer |= cabinet.IsSetContainer;
    }

    private static string GetMergeKey(UnifiedItem item)
        => item.IsSetPart
            ? $"set:{item.ParentSetItemID}:{item.PrismBoxIndex}:{item.ItemID}"
            : $"item:{item.ItemID}";

    #endregion

    #region 构造

    private static UnifiedItem CreateUnifiedItem(
        uint itemID,
        uint rawItemID,
        string name,
        ItemSheet row,
        bool inPrismBox,
        bool inCabinet,
        uint prismBoxIndex,
        uint cabinetID,
        uint stain0ID,
        uint stain1ID,
        bool isSetContainer,
        bool isSetPart = false,
        uint parentSetItemID = 0,
        string parentSetName = "",
        string setPartLabel = "")
        => new()
        {
            ItemID = itemID,
            RawItemID = rawItemID,
            PrismBoxIndex = prismBoxIndex,
            CabinetID = cabinetID,
            Name = name,
            Stain0ID = stain0ID,
            Stain1ID = stain1ID,
            ModelMain = row.ModelMain,
            EquipSlotCategoryRowID = row.EquipSlotCategory.RowId,
            ClassJobCategoryRowID = row.ClassJobCategory.RowId,
            IconID = row.Icon,
            LevelEquip = (uint)row.LevelEquip,
            InPrismBox = inPrismBox,
            InCabinet = inCabinet,
            IsSetContainer = isSetContainer,
            IsSetPart = isSetPart,
            ParentSetItemID = parentSetItemID,
            ParentSetName = parentSetName,
            SetPartLabel = setPartLabel
        };

    private static bool TryGetItemName(ItemSheet item, out string name)
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    #endregion

    #region 常量

    private const uint PRISM_BOX_CAPACITY = 800;

    #endregion
}
