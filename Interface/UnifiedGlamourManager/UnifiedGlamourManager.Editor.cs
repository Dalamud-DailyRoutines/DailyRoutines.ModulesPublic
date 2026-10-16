using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading.TaskHelper;
using ItemSheet = Lumina.Excel.Sheets.Item;

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

    private readonly List<PresetItemCandidate>                  presetEditorItems     = [];
    private readonly Dictionary<int, List<PresetItemCandidate>> presetEditorSlotItems = [];

    private TaskHelper? tryOnTaskHelper;
    private bool        focusPresetTitle;
    private bool        enablePresetLivePreview;
    private long        nextPreviewAt;
    private bool        isPreviewPending;
    private long        previewVersion;
    private bool        wasTryOnVisible;

    private void InvalidatePresetPreview()
    {
        tryOnTaskHelper?.Abort();
        previewVersion++;
        isPreviewPending = false;
    }

    private void QueuePresetPreview()
    {
        InvalidatePresetPreview();
        isPreviewPending = enablePresetLivePreview && isPresetEditorOpen && editingPreset != null &&
                           originalEditingPreset == selectedPreset;
        nextPreviewAt = Environment.TickCount64 + 300;
    }

    private void UpdatePresetLivePreview()
    {
        if (!isPresetEditorOpen || editingPreset == null || !enablePresetLivePreview || originalEditingPreset != selectedPreset)
            return;

        if (isPreviewPending && Environment.TickCount64 >= nextPreviewAt && !TaskHelper.IsBusy &&
            tryOnTaskHelper?.IsBusy != true && CanTryOnPreset())
        {
            var generation   = previewVersion;
            isPreviewPending = false;
            StartTryOnPreset
            (
                editingPreset,
                () => isPresetEditorOpen && enablePresetLivePreview && originalEditingPreset == selectedPreset && generation == previewVersion
            );
        }

        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            return;

        var addon = manager->GetAddonByName(nameof(Tryon));
        if (addon == null || !addon->IsVisible)
        {
            wasTryOnVisible = false;
            return;
        }

        if (wasTryOnVisible)
            return;

        wasTryOnVisible = true;
        var position    = ImGui.GetWindowPos();
        var size        = ImGui.GetWindowSize();
        var viewport    = ImGui.GetMainViewport();
        var width       = addon->GetScaledWidth(true);
        var height      = addon->GetScaledHeight(true);
        var x           = position.X + size.X + (8 * GlobalUIScale);
        if (x + width > viewport.WorkPos.X + viewport.WorkSize.X)
            x = position.X - width - (8 * GlobalUIScale);
        x = Math.Clamp
        (
            x,
            viewport.WorkPos.X,
            Math.Max(viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - width)
        );
        var y = Math.Clamp
        (
            position.Y,
            viewport.WorkPos.Y,
            Math.Max(viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - height)
        );
        if (addon->X != (short)x || addon->Y != (short)y)
            addon->SetPosition((short)x, (short)y);
    }

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
        bool        preserveDraft = false
    )
    {
        if (preserveDraft && isPresetEditorOpen && HasPresetChanges())
            return;

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

        var row     = LuminaGetter.GetRow<ItemSheet>(itemID).GetValueOrDefault();
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

    private void RefreshPresetEditorItems()
    {
        if (!presetEditorItemsDirty)
            return;

        presetEditorItemsDirty = false;
        presetEditorItems.Clear();
        var owned  = GetOwnedEquipment(out _);
        var search = presetEditorSearchText.Trim();
        if (!presetEditorSlotItems.TryGetValue(editingSlotIndex, out var candidates))
        {
            candidates = [];
            foreach (var row in LuminaGetter.Get<ItemSheet>())
            {
                if (row.RowId == 0 || row.ModelMain == 0 ||
                    !LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) ||
                    !IsEquipSlotCategoryCompatibleWithPlateSlot(category, (uint)editingSlotIndex) ||
                    !TryGetItemName(row, out var name))
                {
                    continue;
                }

                candidates.Add(new(row.RowId, name));
            }

            candidates.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
            presetEditorSlotItems[editingSlotIndex] = candidates;
        }

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
        var row   = LuminaGetter.GetRow<ItemSheet>(ItemUtil.GetBaseId(entry.ItemID).ItemId).GetValueOrDefault();
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

    private static uint GetBlockedEquipmentSlots
    (
        uint itemID
    )
    {
        if (!LuminaGetter.TryGetRow<ItemSheet>(ItemUtil.GetBaseId(itemID).ItemId, out var item) ||
            !LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategory.RowId, out var category))
        {
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

        return mask;
    }

    private static void NormalizePresetEquipment
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

        preset.Items = kept.OrderBy(x => x.SlotIndex).ToList();
    }

    private static void FillEmptyPresetSlots
    (
        PlatePreset preset
    )
    {
        for (var slot = 0U; slot < EmperorItemIDs.Length; slot++)
        {
            if (EmperorItemIDs[slot] == 0 || preset.Items.Any(x => x.SlotIndex == slot && x.ItemID != 0))
                continue;

            preset.Items.RemoveAll(x => x.SlotIndex == slot);
            preset.Items.Add(new(slot, EmperorItemIDs[slot], 0, 0));
        }
    }
}
