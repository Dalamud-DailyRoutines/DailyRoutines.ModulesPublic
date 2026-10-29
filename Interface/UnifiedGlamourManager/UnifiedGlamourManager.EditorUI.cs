using System.Numerics;
using Dalamud.Utility;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private bool HasPresetChanges()
    {
        if (editingPreset == null || editingPresetBaseline == null)
            return false;

        if (editingPreset.Title.Trim() != editingPresetBaseline.Title.Trim() ||
            editingPreset.Note.Trim() != editingPresetBaseline.Note.Trim() ||
            editingPreset.Items.Count != editingPresetBaseline.Items.Count)
        {
            return true;
        }

        foreach (var entry in editingPreset.Items)
        {
            if (!editingPresetBaseline.Items.Contains(entry))
                return true;
        }

        return false;
    }

    private void SelectPresetEditorSlot
    (
        int slot,
        int channel
    )
    {
        editingSlotIndex       = slot;
        editingStainIndex      = channel;
        presetEditorSearchText = string.Empty;
        presetEditorItemsDirty = true;
    }

    private void DrawPresetDetailActions()
    {
        if (selectedPreset == null)
            return;

        using var table = ImRaii.Table("##PresetDetailActions", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table)
            return;

        var activeDraft = isPresetEditorOpen && originalEditingPreset == selectedPreset ?
                              editingPreset :
                              null;
        var switching = isPresetEditorOpen && originalEditingPreset != selectedPreset && HasPresetChanges();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (ImRaii.Disabled(switching || TaskHelper.IsBusy || !CanTryOnPreset()))
        {
            if (ImGui.Button(LuminaWrapper.GetAddonText(2426), new Vector2(-1f, 0f)))
                StartTryOnPreset(activeDraft ?? selectedPreset);
        }

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(switching || TaskHelper.IsBusy || isRefreshingItems ||
                               (activeDraft != null && HasPresetChanges()) || !TryGetReadyPlateEditor(out var agent) ||
                               agent->Data->OpenMode != 0))
        {
            if (ImGui.Button(Lang.Get("Apply"), new Vector2(-1f, 0f)))
            {
                InvalidatePresetPreview();
                TaskHelper.Enqueue(StartApplyPreset);
            }
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.FileExport, Lang.Get("Export"), new Vector2(-1f, 0f)))
            ExportToClipboard(CopyPreset(activeDraft ?? selectedPreset));

        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("HoldCtrlToDelete"), new Vector2(-1f, 0f)) &&
            ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
        {
            config.Presets.Remove(selectedPreset);
            selectedPreset = null;
            if (!switching)
                EndPresetEditing();
            else
                selectedPreset = originalEditingPreset;
            SaveConfig();
        }
    }

    private void DrawPresetEditor()
    {
        if (selectedPreset == null)
            return;

        if (originalEditingPreset != selectedPreset || !isPresetEditorOpen)
        {
            if (isPresetEditorOpen && HasPresetChanges())
            {
                ImGui.TextWrapped(Lang.Get("UnifiedGlamourManager-Editor-SwitchWarning"));
                var target = selectedPreset;
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(editingPreset.Title)))
                {
                    if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Editor-SaveSwitch")))
                    {
                        SaveEditedPreset(false);
                        selectedPreset = target;
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Editor-DiscardSwitch")))
                    BeginPresetEditing(target);

                return;
            }

            BeginPresetEditing(selectedPreset);
        }

        var draft      = editingPreset;
        var ownedItems = GetOwnedEquipment(out var ownershipComplete);
        if (focusPresetTitle)
            ImGui.SetNextItemOpen(true);
        if (ImGui.CollapsingHeader($"{LuminaWrapper.GetAddonText(4534)} / {Lang.Get("Note")}"))
        {
            if (focusPresetTitle)
            {
                ImGui.SetKeyboardFocusHere();
                focusPresetTitle = false;
            }

            ImGui.InputText(LuminaWrapper.GetAddonText(4534), ref draft.Title, 40);
            ImGui.InputText(Lang.Get("Note"), ref draft.Note, 120);
        }

        var remaining       = ImGui.GetContentRegionAvail().Y;
        var lineHeight      = ImGui.GetFrameHeightWithSpacing();
        var candidateHeight = Math.Clamp(remaining * 0.18f, 70 * GlobalUIScale, 120 * GlobalUIScale);
        var rowsHeight      = Math.Max
        (
            80 * GlobalUIScale,
            remaining - candidateHeight - (lineHeight * 3) - (ImGui.GetStyle().ItemSpacing.Y * 3)
        );
        using (var table = ImRaii.Table
               (
                   "##InlineEditorSlots",
                   4,
                   ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                   new Vector2(0, rowsHeight)
               ))
        {
            if (table)
            {
                ImGui.TableSetupColumn
                (
                    Lang.Get("UnifiedGlamourManager-GlamourTargetSlot"),
                    ImGuiTableColumnFlags.WidthFixed,
                    60 * GlobalUIScale
                );
                ImGui.TableSetupColumn
                (
                    LuminaWrapper.GetAddonText(6942),
                    ImGuiTableColumnFlags.WidthStretch,
                    1.5f
                );
                ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(15970));
                ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(15971));
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                for (var slot = 0; slot < PlateSlotDefinitions.Length; slot++)
                {
                    using var id  = ImRaii.PushId(slot);
                    var entry     = draft.Items.FirstOrDefault(x => x.SlotIndex == (uint)slot);
                    var covering  =
                        draft.Items.FirstOrDefault(x => (GetBlockedEquipmentSlots(x.ItemID) & (1u << slot)) != 0);
                    var row       = LuminaGetter.GetRow<Item>(ItemUtil.GetBaseId(entry.ItemID).ItemId)
                                                .GetValueOrDefault();
                    var itemLabel = Lang.Get("None");
                    if (entry.ItemID != 0)
                    {
                        itemLabel = row.Name.ExtractText();
                    }
                    else if (covering.ItemID != 0)
                    {
                        itemLabel =
                            $"{Lang.Get("UnifiedGlamourManager-Editor-CoveredSlot")} {LuminaWrapper.GetItemName(covering.ItemID)}";
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(LuminaWrapper.GetAddonText(PlateSlotAddonTextIDs[slot]));

                    ImGui.TableNextColumn();
                    if (entry.ItemID != 0)
                    {
                        DrawPresetItemIcon(row.Icon, entry.ItemID, ownedItems, ownershipComplete);
                        ImGui.SameLine();
                    }

                    if (ImGui.Selectable($"{itemLabel}##Item", editingSlotIndex == slot && editingStainIndex == -1))
                        SelectPresetEditorSlot(slot, -1);

                    HandlePresetItemContextMenu(entry.ItemID);

                    for (var channel = 0; channel < 2; channel++)
                    {
                        ImGui.TableNextColumn();
                        var dye = channel == 0 ?
                                      entry.Stain0ID :
                                      entry.Stain1ID;
                        using var disabled = ImRaii.Disabled(row.DyeCount <= channel);
                        var label = Lang.Get("UnifiedGlamourManager-Editor-NotDyeable");
                        if (row.DyeCount > channel)
                        {
                            var stain = LuminaGetter.GetRow<Stain>(dye).GetValueOrDefault();
                            label = dye == 0 ?
                                        Lang.Get("None") :
                                        stain.Name.ExtractText();
                            if (dye != 0)
                            {
                                DrawStainColor(stain);
                                ImGui.SameLine();
                            }
                        }

                        if (ImGui.Selectable
                            (
                                $"{label}##Dye{channel}",
                                editingSlotIndex == slot && editingStainIndex == channel
                            ))
                        {
                            SelectPresetEditorSlot(slot, channel);
                        }
                    }
                }
            }
        }

        ImGui.TextUnformatted
        (
            $"{LuminaWrapper.GetAddonText(PlateSlotAddonTextIDs[editingSlotIndex])} / " +
            (editingStainIndex < 0 ?
                 LuminaWrapper.GetAddonText(6942) :
                 LuminaWrapper.GetAddonText
                 (
                     editingStainIndex == 0 ?
                         15970U :
                         15971U
                 ))
        );
        ImGui.SameLine();
        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-Editor-OwnedOnly"), ref onlyOwnedPresetItems))
            presetEditorItemsDirty = true;
        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(Lang.Get("Refresh")))
                StartRefreshAll();
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint
            (
                "##InlineEditorSearch",
                Lang.Get("Search"),
                ref presetEditorSearchText,
                160
            ))
        {
            presetEditorItemsDirty = true;
        }

        using (var candidates = ImRaii.Child("##InlineCandidates", new Vector2(0, candidateHeight), true))
        {
            if (candidates)
            {
                if (editingStainIndex < 0)
                {
                    if (ImGui.Selectable(Lang.Get("UnifiedGlamourManager-Editor-ClearSlot")))
                        SetPresetEditorItem(0);
                    RefreshPresetEditorItems();
                    if (presetEditorSlotItems.Count == 0)
                        ImGui.TextDisabled(Lang.Get("Loading"));
                    var clipper = new ImGuiListClipper();
                    clipper.Begin(presetEditorItems.Count);
                    while (clipper.Step())
                    {
                        for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                        {
                            var item = presetEditorItems[index];
                            if (ImGui.Selectable($"{item.Name} ({item.ItemID})##{item.ItemID}"))
                                SetPresetEditorItem(item.ItemID);
                            HandlePresetItemContextMenu(item.ItemID);
                        }
                    }

                    clipper.End();
                }
                else
                {
                    if (ImGui.Selectable(Lang.Get("None")))
                        SetPresetEditorDye(0);
                    LoadPresetEditorStains();
                    var search = presetEditorSearchText.Trim();
                    foreach (var (stain, name) in presetEditorStains)
                    {
                        if (search.Length > 0 &&
                            !name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                            stain.RowId.ToString() != search)
                        {
                            continue;
                        }

                        DrawStainColor(stain);
                        ImGui.SameLine();
                        if (ImGui.Selectable($"{name}##{stain.RowId}"))
                            SetPresetEditorDye((byte)stain.RowId);
                    }
                }
            }
        }

        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-Editor-LivePreview"), ref enablePresetLivePreview))
            QueuePresetPreview();

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(draft.Title)))
        {
            if (ImGui.Button(Lang.Get("Save")))
                SaveEditedPreset(false);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Editor-SaveAs")))
                SaveEditedPreset(true);
        }

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Cancel")))
            BeginPresetEditing(selectedPreset);

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Editor-FillEmpty")) && isPresetEditorOpen)
        {
            FillEmptyPresetSlots(editingPreset);
            NormalizePresetEquipment(editingPreset);
            QueuePresetPreview();
        }

        DrawPresetItemSearchMenu();
        UpdatePresetLivePreview();
    }

    private void DrawPresetItemIcon
    (
        uint          iconID,
        uint          itemID,
        HashSet<uint> owned,
        bool          complete
    )
    {
        DrawItemIcon(iconID, ImGui.GetFrameHeight());
        HandlePresetItemContextMenu(itemID);
        if (owned.Contains(ItemUtil.GetBaseId(itemID).ItemId))
            return;

        var min    = ImGui.GetItemRectMin();
        var max    = ImGui.GetItemRectMax();
        var radius = 5 * GlobalUIScale;
        var center = new Vector2(max.X - radius, min.Y + radius);
        var draw   = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(ImGuiCol.WindowBg));
        if (complete)
        {
            var offset = new Vector2(radius * 0.5f);
            draw.AddLine
            (
                center - offset,
                center + offset,
                ImGui.GetColorU32(ImGuiColors.DalamudRed),
                2 * GlobalUIScale
            );
            draw.AddLine
            (
                center + new Vector2(-offset.X, offset.Y),
                center + new Vector2(offset.X, -offset.Y),
                ImGui.GetColorU32(ImGuiColors.DalamudRed),
                2 * GlobalUIScale
            );
        }
        else
        {
            var textSize = ImGui.CalcTextSize("?");
            draw.AddText(center - (textSize / 2), ImGui.GetColorU32(ImGuiColors.DalamudYellow), "?");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Lang.Get(complete ?
                                          "UnifiedGlamourManager-Editor-NotOwnedHint" :
                                          "UnifiedGlamourManager-Editor-OwnershipUnknownHint"));
        }
    }
}
