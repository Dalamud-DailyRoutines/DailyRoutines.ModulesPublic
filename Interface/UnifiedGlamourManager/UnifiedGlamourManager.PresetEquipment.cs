using System.Numerics;
using System.Text;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal partial class UnifiedGlamourManager
{
    private readonly Dictionary<uint, uint> blockedSlotsCache = [];

    private static PlatePreset CopyPreset
    (
        PlatePreset preset
    ) =>
        new()
        {
            Title     = preset.Title,
            Note      = preset.Note,
            Race      = preset.Race,
            Sex       = preset.Sex,
            CreatedAt = preset.CreatedAt,
            Items     = preset.Items.ToList()
        };

    private static string GetPresetSlotName
    (
        uint slot
    ) =>
        slot < PlateSlotAddonTextIDs.Length ?
            LuminaWrapper.GetAddonText(PlateSlotAddonTextIDs[slot]) :
            Lang.Get("Unknown");

    private void NormalizePresetWithNotice
    (
        PlatePreset preset
    )
    {
        var removed = NormalizePresetEquipment(preset);
        if (removed.Count == 0)
            return;

        var builder = new StringBuilder
        (
            Lang.Get
            (
                "UnifiedGlamourManager-Editor-RemovedEquipment",
                new Dictionary<string, object>
                {
                    ["presetName"] = preset.Title
                }
            )
        );

        foreach (var entry in removed)
        {
            builder.AppendLine()
                   .Append("  \ue06f ")
                   .Append(GetPresetSlotName(entry.SlotIndex))
                   .Append(": ")
                   .Append(LuminaWrapper.GetItemName(entry.ItemID));
        }

        NotifyHelper.Instance().NotificationWarning(builder.ToString());
    }

    private uint GetBlockedEquipmentSlots
    (
        uint itemID
    )
    {
        itemID = ItemUtil.GetBaseId(itemID).ItemId;
        if (blockedSlotsCache.TryGetValue(itemID, out var cached))
            return cached;

        if (!LuminaGetter.TryGetRow<Item>(itemID, out var item) ||
            !LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategory.RowId, out var category))
        {
            blockedSlotsCache[itemID] = 0;
            return 0;
        }

        var mask = 0U;

        for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
        {
            if (PlateSlotDefinitions[slot].IsBlocked(category))
                mask |= 1u << slot;
        }

        blockedSlotsCache[itemID] = mask;
        return mask;
    }

    private List<PlateItemInfo> NormalizePresetEquipment
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

    private void FillEmptyPresetSlots
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
