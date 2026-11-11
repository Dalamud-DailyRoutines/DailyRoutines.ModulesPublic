using DailyRoutines.Extensions;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class UnifiedGlamourManager
{
    private PresetEditor presetEditor;

    private sealed class PresetEditor
    (
        UnifiedGlamourManager owner
    ) : IDisposable
    {
        private          PlatePreset?                                 baseline;
        private          IEnumerator<Item>?                           itemRows;
        private readonly List<(PresetItemCandidate Item, uint Slots)> itemIndex = [];

        private long nextPreviewAt;
        private bool wasTryOnVisible;

        public PlatePreset? OriginalPreset { get; private set; }
        public PlatePreset? Draft          { get; private set; }
        public bool         IsOpen         { get; private set; }

        public int    SlotIndex      { get; private set; }
        public int    StainIndex     { get; private set; } = -1;
        public string SearchText     { get; set; }         = string.Empty;
        public bool   OnlyOwnedItems { get; set; }
        public bool   ItemsDirty     { get; set; }
        public bool   FocusTitle     { get; set; }

        public bool EnableLivePreview { get; set; }
        public bool IsPreviewPending  { get; set; }

        public List<(Stain Stain, string Name)>           Stains    { get; } = [];
        public List<PresetItemCandidate>                  Items     { get; } = [];
        public Dictionary<int, List<PresetItemCandidate>> SlotItems { get; } = [];

        public void Begin
        (
            PlatePreset preset
        )
        {
            OriginalPreset = preset;
            Draft          = CopyPreset(preset);
            owner.NormalizePresetEquipment(Draft);
            baseline   = CopyPreset(Draft);
            IsOpen     = true;
            SlotIndex  = 0;
            StainIndex = -1;
            SearchText = string.Empty;
            ItemsDirty = true;
            QueuePreview();
        }

        public void End()
        {
            IsPreviewPending = false;
            wasTryOnVisible  = false;
            IsOpen           = false;
            Draft            = null;
            baseline         = null;
            OriginalPreset   = null;
            FocusTitle       = false;
        }

        public void SelectSlot
        (
            int slot,
            int channel
        )
        {
            SlotIndex  = slot;
            StainIndex = channel;
            SearchText = string.Empty;
            ItemsDirty = true;
        }

        public bool HasChanges()
        {
            if (Draft == null || baseline == null)
                return false;

            if (Draft.Title.Trim() != baseline.Title.Trim() ||
                Draft.Note.Trim()  != baseline.Note.Trim()  ||
                Draft.Items.Count  != baseline.Items.Count)
                return true;

            foreach (var entry in Draft.Items)
            {
                if (!baseline.Items.Contains(entry))
                    return true;
            }

            return false;
        }

        public void SetItem
        (
            uint itemID
        )
        {
            if (Draft == null)
                return;

            var previous = Draft.Items.FirstOrDefault(x => x.SlotIndex == (uint)SlotIndex);
            Draft.Items.RemoveAll(x => x.SlotIndex == (uint)SlotIndex);
            if (itemID == 0)
            {
                QueuePreview();
                return;
            }

            var row     = LuminaGetter.GetRow<Item>(itemID).GetValueOrDefault();
            var blocked = owner.GetBlockedEquipmentSlots(itemID);

            Draft.Items.RemoveAll
            (x => (blocked                                    & (1u << (int)x.SlotIndex)) != 0 ||
                  (owner.GetBlockedEquipmentSlots(x.ItemID) & (1u << SlotIndex))        != 0
            );

            Draft.Items.Add
            (
                new
                (
                    (uint)SlotIndex,
                    itemID,
                    row.DyeCount > 0 ?
                        previous.Stain0ID :
                        (byte)0,
                    row.DyeCount > 1 ?
                        previous.Stain1ID :
                        (byte)0
                )
            );
            QueuePreview();
        }

        public void SetDye
        (
            byte dye
        )
        {
            if (Draft == null)
                return;

            var entry = Draft.Items.FirstOrDefault(x => x.SlotIndex == (uint)SlotIndex);
            var row   = LuminaGetter.GetRow<Item>(ItemUtil.GetBaseId(entry.ItemID).ItemId).GetValueOrDefault();
            if (entry.ItemID == 0 || row.DyeCount <= StainIndex)
                return;

            Draft.Items.RemoveAll(x => x.SlotIndex == (uint)SlotIndex);
            Draft.Items.Add
            (
                StainIndex == 0 ?
                    entry with { Stain0ID = dye } :
                    entry with { Stain1ID = dye }
            );
            QueuePreview();
        }

        public void LoadStains()
        {
            if (Stains.Count > 0)
                return;

            foreach (var stain in LuminaGetter.Get<Stain>())
            {
                if (stain.RowId == 0 || stain.RowId > byte.MaxValue)
                    continue;

                var name = stain.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    Stains.Add((stain, name));
            }
        }

        public void RefreshItems()
        {
            if (!ItemsDirty)
                return;

            ItemsDirty = false;
            Items.Clear();

            var owned  = owner.GetOwnedEquipment(out _);
            var search = SearchText.Trim();

            if (SlotItems.Count == 0)
            {
                itemRows ??= LuminaGetter.Get<Item>().GetEnumerator();

                for (var index = 0; index < ITEM_INDEX_BATCH_SIZE; index++)
                {
                    if (!itemRows.MoveNext())
                    {
                        itemRows.Dispose();
                        itemRows = null;

                        itemIndex.Sort((left, right) => string.Compare(left.Item.Name, right.Item.Name, StringComparison.CurrentCulture));

                        for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
                        {
                            SlotItems[slot] = itemIndex.Where(x => (x.Slots & (1u << slot)) != 0)
                                                       .Select(x => x.Item)
                                                       .ToList();
                        }

                        itemIndex.Clear();
                        break;
                    }

                    var row = itemRows.Current;
                    if (row.RowId     == 0 ||
                        row.ModelMain == 0 ||
                        !LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) ||
                        !TryGetItemName(row, out var name))
                        continue;

                    var slots = 0U;

                    for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
                    {
                        if (PlateSlotDefinitions[slot].CanUse(category))
                            slots |= 1u << slot;
                    }

                    if (slots != 0)
                        itemIndex.Add((new(row.RowId, name), slots));
                }

                if (SlotItems.Count == 0)
                {
                    ItemsDirty = true;
                    return;
                }
            }

            var candidates = SlotItems[SlotIndex];

            Items.AddRange
            (
                candidates.Where
                (row => (!OnlyOwnedItems || owned.Contains(row.ItemID)) &&
                        (search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                )
            );
        }

        public void Save
        (
            bool saveAs
        )
        {
            if (Draft == null || OriginalPreset == null || string.IsNullOrWhiteSpace(Draft.Title))
                return;

            owner.NormalizePresetEquipment(Draft);

            if (saveAs || !owner.config.Presets.Contains(OriginalPreset))
            {
                var copy = CopyPreset(Draft);
                copy.Title     = copy.Title.Trim();
                copy.Note      = copy.Note.Trim();
                copy.CreatedAt = DateTime.Now;
                owner.config.Presets.Add(copy);
                owner.selectedPreset = copy;
            }
            else
            {
                OriginalPreset.Title = Draft.Title.Trim();
                OriginalPreset.Note  = Draft.Note.Trim();
                OriginalPreset.Items = Draft.Items.ToList();
                owner.selectedPreset = OriginalPreset;
            }

            owner.SaveConfig();
            End();
        }

        public void QueuePreview()
        {
            IsPreviewPending = EnableLivePreview && IsOpen && Draft != null && OriginalPreset == owner.selectedPreset;
            nextPreviewAt    = Environment.TickCount64 + 300;
        }

        public void UpdateLivePreview()
        {
            if (!IsOpen || Draft == null || !EnableLivePreview || OriginalPreset != owner.selectedPreset)
                return;

            if (IsPreviewPending                         &&
                Environment.TickCount64 >= nextPreviewAt &&
                !owner.TaskHelper.IsBusy                 &&
                !ICondition.Instance().IsOccupiedInEvent)
            {
                IsPreviewPending = false;
                owner.StartTryOnPreset(Draft);
            }

            var addon = Tryon;
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

        public void Dispose()
        {
            EnableLivePreview = false;

            itemRows?.Dispose();
            itemRows = null;

            itemIndex.Clear();
        }
    }
}
