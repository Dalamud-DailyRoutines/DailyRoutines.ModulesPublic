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

    private void RefreshAll()
    {
        items.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount = 0;

        RebuildOwnedConcreteItemIndex();
        LoadPrismBoxItems();
        LoadCabinetItems();
        MergeItems();
        LoadPreviewItems();
    }

    private void RebuildOwnedConcreteItemIndex()
    {
        ownedConcreteItemIDs.Clear();

        AddOwnedPrismBoxItemIDs();
        AddOwnedCabinetItemIDs();
    }

    private void AddOwnedPrismBoxItemIDs()
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
            return;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % 1_000_000;
            if (itemID > 1)
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
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxRequested || !manager->PrismBoxLoaded)
            return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % 1_000_000;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var setParts = GetSetParts(itemSheet, itemID);
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
            AddPrismBoxSetParts(manager, itemSheet, setParts, rawItemID, i, itemID, name);
        }

        prismBoxItemCount = count;
    }

    private void AddPrismBoxSetParts(
        MirageManager* manager,
        Lumina.Excel.ExcelSheet<ItemSheet> itemSheet,
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
                itemSheet,
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

            var setParts = GetSetParts(itemSheet, itemID);
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
            AddCabinetSetParts(itemSheet, setParts, itemID, cabinetID, name);
        }

        cabinetItemCount = count;
    }

    private void AddCabinetSetParts(
        Lumina.Excel.ExcelSheet<ItemSheet> itemSheet,
        List<SetPartInfo> setParts,
        uint parentItemID,
        uint cabinetID,
        string parentName)
    {
        foreach (var setPart in setParts)
        {
            AddSetPartItem(
                itemSheet,
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

    private static List<SetPartInfo> GetSetParts(Lumina.Excel.ExcelSheet<ItemSheet> itemSheet, uint setItemID)
    {
        var row = LuminaGetter.Get<MirageStoreSetItem>().GetRowOrDefault(setItemID);
        if (row == null)
            return [];

        List<SetPartInfo> parts = [];

        AddSetPart(itemSheet, parts, row.Value.MainHand.RowId, 0);
        AddSetPart(itemSheet, parts, row.Value.OffHand.RowId, 1);
        AddSetPart(itemSheet, parts, row.Value.Head.RowId, 2);
        AddSetPart(itemSheet, parts, row.Value.Body.RowId, 3);
        AddSetPart(itemSheet, parts, row.Value.Hands.RowId, 4);
        AddSetPart(itemSheet, parts, row.Value.Legs.RowId, 5);
        AddSetPart(itemSheet, parts, row.Value.Feet.RowId, 6);
        AddSetPart(itemSheet, parts, row.Value.Earrings.RowId, 7);
        AddSetPart(itemSheet, parts, row.Value.Necklace.RowId, 8);
        AddSetPart(itemSheet, parts, row.Value.Bracelets.RowId, 9);
        AddSetPart(itemSheet, parts, row.Value.Ring.RowId, 10);

        return parts;
    }

    private static void AddSetPart(
        Lumina.Excel.ExcelSheet<ItemSheet> itemSheet,
        List<SetPartInfo> parts,
        uint itemID,
        int slotIndex)
    {
        if (itemID <= 1)
            return;

        var itemRow = itemSheet.GetRowOrDefault(itemID);
        if (itemRow == null || !TryGetItemName(itemRow.Value, out _))
            return;

        if (!CanItemRowUseSetPartLabel(itemRow.Value, slotIndex))
            return;

        var label = GetNativeItemCategoryName(itemRow.Value);
        if (parts.Any(x => x.ItemID == itemID && x.PartLabel == label))
            return;

        parts.Add(new SetPartInfo(itemID, label, slotIndex));
    }

    private void AddSetPartItem(
        Lumina.Excel.ExcelSheet<ItemSheet> itemSheet,
        SetPartInfo setPart,
        uint rawItemID,
        uint prismBoxIndex,
        uint cabinetID,
        uint parentItemID,
        string parentName,
        bool inPrismBox,
        bool inCabinet)
    {
        var partRow = itemSheet.GetRowOrDefault(setPart.ItemID);
        if (partRow == null || !TryGetItemName(partRow.Value, out var partName))
            return;

        items.Add(CreateUnifiedItem(
            setPart.ItemID,
            rawItemID,
            $"{partName}（{parentName} / {setPart.PartLabel}）",
            partRow.Value,
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
