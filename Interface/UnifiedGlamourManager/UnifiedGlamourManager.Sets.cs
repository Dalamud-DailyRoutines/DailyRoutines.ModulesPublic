using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic;

public partial class UnifiedGlamourManager
{
    private readonly Dictionary<uint, List<SetPartInfo>> setPartsCache = [];

    private List<SetPartInfo> GetSetParts
    (
        uint setItemID
    )
    {
        if (setPartsCache.TryGetValue(setItemID, out var cached))
            return cached;

        List<SetPartInfo> parts = [];
        setPartsCache.Add(setItemID, parts);
        if (!LuminaGetter.TryGetRow<MirageStoreSetItem>(setItemID, out var row))
            return parts;

        AddSetPart(parts, row.MainHand.RowId,  0);
        AddSetPart(parts, row.OffHand.RowId,   1);
        AddSetPart(parts, row.Head.RowId,      2);
        AddSetPart(parts, row.Body.RowId,      3);
        AddSetPart(parts, row.Hands.RowId,     4);
        AddSetPart(parts, row.Legs.RowId,      5);
        AddSetPart(parts, row.Feet.RowId,      6);
        AddSetPart(parts, row.Earrings.RowId,  7);
        AddSetPart(parts, row.Necklace.RowId,  8);
        AddSetPart(parts, row.Bracelets.RowId, 9);
        AddSetPart(parts, row.Ring.RowId,      10);
        return parts;
    }

    private static void AddSetPart
    (
        List<SetPartInfo> parts,
        uint              itemID,
        int               slotIndex
    )
    {
        if (itemID == 0) return;

        if (!LuminaGetter.TryGetRow<ItemSheet>(itemID, out var itemRow)                                      ||
            !TryGetItemName(itemRow, out _)                                                                  ||
            !LuminaGetter.TryGetRow<EquipSlotCategory>(itemRow.EquipSlotCategory.RowId, out var categoryRow) ||
            !IsEquipSlotCategoryCompatibleWithPlateSlot(categoryRow, (uint)slotIndex))
            return;

        var label = GetNativeItemCategoryName(itemRow);
        if (!parts.Any(x => x.ItemID == itemID && x.PartLabel == label))
            parts.Add(new(itemID, label, slotIndex));
    }

    private static void AddSetPartItem
    (
        List<UnifiedItem> target,
        SetPartInfo       setPart,
        uint              rawItemID,
        uint              prismBoxIndex,
        uint              cabinetID,
        uint              parentItemID,
        string            parentName,
        ItemSource        source
    )
    {
        if (!LuminaGetter.TryGetRow<ItemSheet>(setPart.ItemID, out var partRow) ||
            !TryGetItemName(partRow, out var partName))
            return;

        target.Add
        (
            UnifiedItem.Create
            (
                setPart.ItemID,
                rawItemID,
                partName,
                partRow,
                source,
                prismBoxIndex,
                cabinetID,
                0,
                0,
                false,
                true,
                parentItemID,
                parentName,
                setPart.PartLabel
            )
        );
    }

    private static string GetNativeItemCategoryName
    (
        ItemSheet item
    )
    {
        var categoryName = item.ItemUICategory.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(categoryName) ?
                   item.Name.ExtractText() :
                   categoryName;
    }

    private static void MergeItems
    (
        List<UnifiedItem> target
    )
    {
        Dictionary<(uint ItemID, uint ParentSetItemID, uint PrismBoxIndex, uint CabinetID), UnifiedItem> merged = new(target.Count);

        foreach (var item in target)
        {
            if (!merged.TryAdd(item.Identity, item))
                merged[item.Identity].MergeWith(item);
        }

        target.Clear();
        target.AddRange(merged.Values);
    }
}
