using System.Numerics;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private static readonly Dictionary<uint, uint> BlockedEquipmentSlots = [];

    private static string GetPresetSlotName
    (
        uint slot
    ) => slot < PlateSlotAddonTextIDs.Length ?
             LuminaWrapper.GetAddonText(PlateSlotAddonTextIDs[slot]) :
             Lang.Get("Unknown");

    private static void NormalizePresetWithNotice
    (
        PlatePreset preset
    )
    {
        var removed = NormalizePresetEquipment(preset);
        if (removed.Count == 0)
            return;

        var items = string.Join
        (
            ", ",
            removed.Select
            (x =>
                {
                    var name = LuminaWrapper.GetItemName(x.ItemID);
                    return $"{GetPresetSlotName(x.SlotIndex)}: {name} ({x.ItemID})";
                }
            )
        );
        NotifyHelper.Instance().NotificationWarning
        (
            Lang.Get
            (
                "UnifiedGlamourManager-Editor-RemovedEquipment",
                new Dictionary<string, object>
                {
                    ["presetName"] = preset.Title,
                    ["items"]      = items
                }
            )
        );
    }

    private static uint GetBlockedEquipmentSlots
    (
        uint itemID
    )
    {
        itemID = ItemUtil.GetBaseId(itemID).ItemId;
        if (BlockedEquipmentSlots.TryGetValue(itemID, out var cached))
            return cached;
        if (!LuminaGetter.TryGetRow<Item>(itemID, out var item) ||
            !LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategory.RowId, out var category))
        {
            BlockedEquipmentSlots[itemID] = 0;
            return 0;
        }

        ReadOnlySpan<int> flags = stackalloc int[]
        {
            category.MainHand,
            category.OffHand,
            category.Head,
            category.Body,
            category.Gloves,
            category.Legs,
            category.Feet,
            category.Ears,
            category.Neck,
            category.Wrists,
            category.FingerL,
            category.FingerR
        };
        var mask = 0U;

        for (var slot = 0; slot < flags.Length; slot++)
        {
            if (flags[slot] < 0)
                mask |= 1u << slot;
        }

        BlockedEquipmentSlots[itemID] = mask;
        return mask;
    }

    private static List<PlateItemInfo> NormalizePresetEquipment
    (
        PlatePreset preset
    )
    {
        var candidates = preset.Items
                               .Where(x => x.ItemID != 0 && x.SlotIndex < PlateSlots.Length)
                               .Select(entry => (Entry: entry, Blocked: GetBlockedEquipmentSlots(entry.ItemID)))
                               .OrderByDescending(x => BitOperations.PopCount(x.Blocked))
                               .ThenBy(x => x.Entry.SlotIndex)
                               .ToList();
        var kept     = new List<PlateItemInfo>();
        var blocked  = 0U;
        var occupied = 0U;

        foreach (var candidate in candidates)
        {
            var bit = 1u << (int)candidate.Entry.SlotIndex;
            if ((blocked & bit) != 0 || (occupied & bit) != 0 || (candidate.Blocked & occupied) != 0)
                continue;

            kept.Add(candidate.Entry);
            occupied |= bit;
            blocked  |= candidate.Blocked;
        }

        var removed = preset.Items.ToList();

        foreach (var entry in kept)
            removed.Remove(entry);

        preset.Items = kept.OrderBy(x => x.SlotIndex).ToList();
        return removed;
    }

    private static void FillEmptyPresetSlots
    (
        PlatePreset preset
    )
    {
        var blocked = preset.Items.Aggregate(0U, (mask, entry) => mask | GetBlockedEquipmentSlots(entry.ItemID));

        for (var slot = 0U; slot < EmperorItemIDs.Length; slot++)
        {
            if ((blocked & (1u << (int)slot)) != 0 || EmperorItemIDs[slot] == 0 || preset.Items.Any(x => x.SlotIndex == slot && x.ItemID != 0))
                continue;

            preset.Items.RemoveAll(x => x.SlotIndex == slot);
            preset.Items.Add(new(slot, EmperorItemIDs[slot], 0, 0));
        }
    }
}
