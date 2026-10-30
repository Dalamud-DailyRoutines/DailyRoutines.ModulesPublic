using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private PlatePreset? originalEditingPreset;
    private PlatePreset? editingPreset;
    private PlatePreset? editingPresetBaseline;
    private bool         isPresetEditorOpen;

    private int    editingSlotIndex;
    private int    editingStainIndex      = -1;
    private string presetEditorSearchText = string.Empty;
    private bool   onlyOwnedPresetItems;
    private bool   presetEditorItemsDirty;

    private          IEnumerator<Item>                            presetItemRows;
    private readonly List<(PresetItemCandidate Item, uint Slots)> presetItemIndex    = [];
    private readonly List<(Stain Stain, string Name)>             presetEditorStains = [];

    private readonly List<PresetItemCandidate>                  presetEditorItems     = [];
    private readonly Dictionary<int, List<PresetItemCandidate>> presetEditorSlotItems = [];

    private bool focusPresetTitle;

    private static PlatePreset CopyPreset
    (
        PlatePreset preset
    ) => new()
    {
        Title     = preset.Title,
        Note      = preset.Note,
        Race      = preset.Race,
        Sex       = preset.Sex,
        CreatedAt = preset.CreatedAt,
        Items     = preset.Items.ToList()
    };

    private void BeginPresetEditing
    (
        PlatePreset preset
    )
    {
        originalEditingPreset = preset;
        editingPreset         = CopyPreset(preset);
        NormalizePresetEquipment(editingPreset);
        editingPresetBaseline  = CopyPreset(editingPreset);
        isPresetEditorOpen     = true;
        editingSlotIndex       = 0;
        editingStainIndex      = -1;
        presetEditorSearchText = string.Empty;
        presetEditorItemsDirty = true;
        QueuePresetPreview();
    }

    private void EndPresetEditing()
    {
        InvalidatePresetPreview();
        isPresetEditorOpen    = false;
        editingPreset         = null;
        editingPresetBaseline = null;
        originalEditingPreset = null;
        focusPresetTitle      = false;
    }

    private void SelectPreset
    (
        PlatePreset preset,
        bool        preserveDraft = true
    )
    {
        if (!preserveDraft)
            EndPresetEditing();

        if (selectedPreset == preset)
            return;

        selectedPreset = preset;
        QueuePresetPreview();
    }

    private void SetPresetEditorItem
    (
        uint itemID
    )
    {
        if (editingPreset == null)
            return;

        var previous = editingPreset.Items.FirstOrDefault(x => x.SlotIndex == (uint)editingSlotIndex);
        editingPreset.Items.RemoveAll(x => x.SlotIndex == (uint)editingSlotIndex);
        if (itemID == 0)
        {
            QueuePresetPreview();
            return;
        }

        var row     = LuminaGetter.GetRow<Item>(itemID).GetValueOrDefault();
        var blocked = GetBlockedEquipmentSlots(itemID);
        editingPreset.Items.RemoveAll
        (x => (blocked & (1u << (int)x.SlotIndex)) != 0 ||
              (GetBlockedEquipmentSlots(x.ItemID) & (1u << editingSlotIndex)) != 0
        );
        editingPreset.Items.Add
        (
            new
            (
                (uint)editingSlotIndex,
                itemID,
                row.DyeCount > 0 ?
                    previous.Stain0ID :
                    (byte)0,
                row.DyeCount > 1 ?
                    previous.Stain1ID :
                    (byte)0
            )
        );
        QueuePresetPreview();
    }

    private void LoadPresetEditorStains()
    {
        if (presetEditorStains.Count > 0)
            return;

        foreach (var stain in LuminaGetter.Get<Stain>())
        {
            if (stain.RowId == 0 || stain.RowId > byte.MaxValue)
                continue;

            var name = stain.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                presetEditorStains.Add((stain, name));
        }
    }

    private void RefreshPresetEditorItems()
    {
        if (!presetEditorItemsDirty)
            return;

        presetEditorItemsDirty = false;
        presetEditorItems.Clear();
        var owned  = GetOwnedEquipment(out _);
        var search = presetEditorSearchText.Trim();

        if (presetEditorSlotItems.Count == 0)
        {
            presetItemRows ??= LuminaGetter.Get<Item>().GetEnumerator();

            for (var index = 0; index < 4096; index++)
            {
                if (!presetItemRows.MoveNext())
                {
                    presetItemRows.Dispose();
                    presetItemRows = null;
                    presetItemIndex.Sort((left, right) => string.Compare(left.Item.Name, right.Item.Name, StringComparison.CurrentCulture));

                    for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
                    {
                        presetEditorSlotItems[slot] = presetItemIndex.Where(x => (x.Slots & (1u << slot)) != 0)
                                                                     .Select(x => x.Item)
                                                                     .ToList();
                    }

                    presetItemIndex.Clear();
                    break;
                }

                var row = presetItemRows.Current;
                if (row.RowId == 0 || row.ModelMain == 0 ||
                    !LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) ||
                    !TryGetItemName(row, out var name))
                {
                    continue;
                }

                var slots = 0U;

                for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
                {
                    if (IsEquipSlotCategoryCompatibleWithPlateSlot(category, (uint)slot))
                        slots |= 1u << slot;
                }

                if (slots != 0)
                    presetItemIndex.Add((new(row.RowId, name), slots));
            }

            if (presetEditorSlotItems.Count == 0)
            {
                presetEditorItemsDirty = true;
                return;
            }
        }

        var candidates = presetEditorSlotItems[editingSlotIndex];

        presetEditorItems.AddRange
        (
            candidates.Where(row => (!onlyOwnedPresetItems || owned.Contains(row.ItemID)) &&
                                    (search.Length == 0 ||
                                     row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     row.ItemID.ToString() == search))
        );
    }

    private void SaveEditedPreset
    (
        bool saveAs
    )
    {
        if (editingPreset == null || originalEditingPreset == null || string.IsNullOrWhiteSpace(editingPreset.Title))
            return;

        NormalizePresetEquipment(editingPreset);
        if (saveAs || !config.Presets.Contains(originalEditingPreset))
        {
            var copy       = CopyPreset(editingPreset);
            copy.Title     = copy.Title.Trim();
            copy.Note      = copy.Note.Trim();
            copy.CreatedAt = DateTime.Now;
            config.Presets.Add(copy);
            selectedPreset = copy;
        }
        else
        {
            originalEditingPreset.Title = editingPreset.Title.Trim();
            originalEditingPreset.Note  = editingPreset.Note.Trim();
            originalEditingPreset.Items = editingPreset.Items.ToList();
            selectedPreset              = originalEditingPreset;
        }

        SaveConfig();
        EndPresetEditing();
    }

    private void SetPresetEditorDye
    (
        byte dye
    )
    {
        if (editingPreset == null)
            return;

        var entry = editingPreset.Items.FirstOrDefault(x => x.SlotIndex == (uint)editingSlotIndex);
        var row   = LuminaGetter.GetRow<Item>(ItemUtil.GetBaseId(entry.ItemID).ItemId).GetValueOrDefault();
        if (entry.ItemID == 0 || row.DyeCount <= editingStainIndex)
            return;

        editingPreset.Items.RemoveAll(x => x.SlotIndex == (uint)editingSlotIndex);
        editingPreset.Items.Add
        (
            editingStainIndex == 0 ?
                entry with { Stain0ID = dye } :
                entry with { Stain1ID = dye }
        );
        QueuePresetPreview();
    }
}
