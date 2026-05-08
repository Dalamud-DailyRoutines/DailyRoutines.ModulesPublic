using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    private void DrawWindow()
    {
        if (!isOpen)
            return;

        ImGui.SetNextWindowSize(new(WINDOW_DEFAULT_WIDTH, WINDOW_DEFAULT_HEIGHT), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new(WINDOW_MIN_WIDTH, WINDOW_MIN_HEIGHT),
            new(WINDOW_MAX_SIZE, WINDOW_MAX_SIZE));

        if (requestFocusNextOpen)
        {
            ImGui.SetNextWindowFocus();
            requestFocusNextOpen = false;
        }

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var framePadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f));
        using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
        using var childRounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f);
        using var grabRounding = ImRaii.PushStyle(ImGuiStyleVar.GrabRounding, 6f);
        using var childBorderSize = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 0f);
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f));

        using var header = ImRaii.PushColor(ImGuiCol.Header, new Vector4(1.00f, 0.46f, 0.72f, 0.26f));
        using var headerHovered = ImRaii.PushColor(ImGuiCol.HeaderHovered, new Vector4(1.00f, 0.58f, 0.80f, 0.42f));
        using var headerActive = ImRaii.PushColor(ImGuiCol.HeaderActive, new Vector4(1.00f, 0.36f, 0.66f, 0.52f));
        using var button = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.30f, 0.20f, 0.27f, 0.94f));
        using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.48f, 0.24f, 0.40f, 1.00f));
        using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.82f, 0.30f, 0.58f, 1.00f));
        using var frameBg = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.13f, 0.09f, 0.12f, 0.90f));
        using var frameBgHovered = ImRaii.PushColor(ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.12f, 0.18f, 0.98f));
        using var frameBgActive = ImRaii.PushColor(ImGuiCol.FrameBgActive, new Vector4(0.34f, 0.16f, 0.28f, 1.00f));
        using var windowBg = ImRaii.PushColor(ImGuiCol.WindowBg, new Vector4(0.025f, 0.025f, 0.030f, 0.96f));
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.035f, 0.035f, 0.045f, 0.88f));
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.030f, 0.040f, 0.98f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.18f, 0.13f, 0.18f, 0.70f));

        if (!ImGui.Begin($"{Lang.Get("UnifiedGlamourManager-Title")}###UnifiedGlamourManager", ref isOpen, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        ImGui.SetWindowFontScale(WINDOW_FONT_SCALE);

        DrawTopBar();
        DrawMainLayout();
        DrawConfirmPopups();

        ImGui.End();
    }

    private static void SectionTitle(string text)
    {
        ImGui.TextColored(ACCENT_COLOR, text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void RedTip(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ERROR_COLOR))
            ImGui.TextWrapped(text);
    }

    private void DrawConfirmPopups()
    {
        if (requestClearFavoritesConfirm)
        {
            ImGui.OpenPopup($"{Lang.Get("UnifiedGlamourManager-ClearFavoritesConfirmTitle")}###ClearFavoritesConfirm");
            requestClearFavoritesConfirm = false;
        }

        if (requestRestoreItemConfirm)
        {
            ImGui.OpenPopup($"{Lang.Get("UnifiedGlamourManager-RestoreItemConfirmTitle")}###RestoreItemConfirm");
            requestRestoreItemConfirm = false;
        }

        DrawClearFavoritesPopup();
        DrawRestoreItemPopup();
    }

    private void DrawClearFavoritesPopup()
    {
        var isOpenPopup = true;
        using var popup = ImRaii.PopupModal(
            $"{Lang.Get("UnifiedGlamourManager-ClearFavoritesConfirmTitle")}###ClearFavoritesConfirm",
            ref isOpenPopup,
            ImGuiWindowFlags.AlwaysAutoResize);

        if (!popup)
            return;

        ImGui.TextColored(ERROR_COLOR, Lang.Get("UnifiedGlamourManager-ClearFavoritesConfirmText"));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ClearFavoritesConfirmHelp"));
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmClear"), new(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
        {
            config.Favorites.Clear();
            SaveConfig();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Cancel"), new(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawRestoreItemPopup()
    {
        var isOpenPopup = true;
        using var popup = ImRaii.PopupModal(
            $"{Lang.Get("UnifiedGlamourManager-RestoreItemConfirmTitle")}###RestoreItemConfirm",
            ref isOpenPopup,
            ImGuiWindowFlags.AlwaysAutoResize);

        if (!popup)
            return;

        var item = selectedItem;
        if (item == null)
        {
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSelectedItemPopup"));
        }
        else
        {
            ImGui.TextColored(
                ERROR_COLOR,
                item.IsSetPart
                    ? Lang.Get("UnifiedGlamourManager-RestoreSetConfirmText")
                    : Lang.Get("UnifiedGlamourManager-RestoreItemConfirmText"));

            ImGui.TextWrapped(item.Name);

            if (item.IsSetPart)
                ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-RestoreSetPartHelp"));

            ImGui.Spacing();

            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmRestore"), new(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
            {
                RestoreSelectedPrismBoxItem(item);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
        }

        if (ImGui.Button(Lang.Get("Cancel"), new(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawTopBar()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, ITEM_SPACING_Y));
        using var child = ImRaii.Child("##TopBar", new Vector2(0f, TOP_BAR_HEIGHT), true, ImGuiWindowFlags.NoScrollbar);
        if (!child)
            return;

        var availableWidth = ImGui.GetContentRegionAvail().X;
        using (ImRaii.Group())
        {
            ImGui.TextColored(ACCENT_COLOR, Lang.Get("UnifiedGlamourManager-Title"));
            ImGui.SameLine();
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Subtitle"));
            ImGui.SameLine();

            var previewOnlyCount = items.Count(x => x.PreviewOnly);
            var statText = Lang.Get("UnifiedGlamourManager-Stat", prismBoxItemCount, cabinetItemCount, previewOnlyCount, items.Count);
            var statWidth = ImGui.CalcTextSize(statText).X;
            var statX = availableWidth - statWidth - 14f;
            if (statX > ImGui.GetCursorPosX())
                ImGui.SetCursorPosX(statX);

            ImGui.TextColored(ERROR_COLOR, statText);
            ImGui.Spacing();

            var rowY = ImGui.GetCursorPosY();
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ReadRefresh"), new(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
                StartRefreshAll();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-RecordPreview"), new(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
                RecordOpenedPreviewSources();

            ImGui.SameLine();

            var clearButtonWidth = TOP_BAR_CLEAR_BUTTON_WIDTH;
            var favoriteCountText = GetLoadedFavoriteCount().ToString();
            var favoriteGroupWidth = ImGui.CalcTextSize(Lang.Get("UnifiedGlamourManager-FavoriteCount")).X +
                                     ImGui.CalcTextSize(favoriteCountText).X +
                                     clearButtonWidth +
                                     40f;
            var searchWidth = MathF.Max(SEARCH_MIN_WIDTH, MathF.Min(SEARCH_MAX_WIDTH, availableWidth - TOP_BAR_RESERVED_WIDTH));

            ImGui.SetNextItemWidth(searchWidth);
            if (ImGui.InputTextWithHint("##Search", Lang.Get("UnifiedGlamourManager-SearchHint"), ref searchText, SEARCH_INPUT_MAX_LENGTH))
                MarkFilteredItemsDirty();

            ImGui.SameLine();
            if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-CurrentSlotOnly"), ref filterByCurrentPlateSlot))
                MarkFilteredItemsDirty(clearPlateSlotCache: true);

            ImGui.SameLine();

            if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-ShowRetainerPreview"), ref config.ShowRetainerPreview))
                SaveConfig();

            ImGui.SameLine();

            if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-ShowInventoryPreview"), ref config.ShowInventoryPreview))
                SaveConfig();

            ImGui.SameLine();

            var favoriteX = availableWidth - favoriteGroupWidth - 8f;
            if (favoriteX <= 0f)
                return;

            ImGui.SetCursorPos(new(favoriteX, rowY));
            using (ImRaii.Group())
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-FavoriteCount"));
                ImGui.SameLine(0f, 4f);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(WARNING_COLOR, favoriteCountText);
                ImGui.SameLine(0f, 10f);

                using (ImRaii.Disabled(config.Favorites.Count == 0))
                {
                    if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ClearFavorites"), new(clearButtonWidth, CONTROL_HEIGHT)))
                        requestClearFavoritesConfirm = true;
                }
            }
        }
    }

    private void DrawMainLayout()
    {
        var mainHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, MAIN_LAYOUT_MIN_HEIGHT);
        var tableFlags = ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.SizingStretchProp;

        using var table = ImRaii.Table("##UnifiedMainTable", 3, tableFlags, new(0f, mainHeight));
        if (!table)
            return;

        ImGui.TableSetupColumn(Lang.Get("UnifiedGlamourManager-FilterColumn"), ImGuiTableColumnFlags.WidthFixed, LEFT_PANEL_WIDTH);
        ImGui.TableSetupColumn(Lang.Get("UnifiedGlamourManager-ItemListColumn"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Lang.Get("UnifiedGlamourManager-SelectedColumn"), ImGuiTableColumnFlags.WidthFixed, RIGHT_PANEL_WIDTH);

        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        DrawSidebar();

        ImGui.TableSetColumnIndex(1);
        DrawItemList();

        ImGui.TableSetColumnIndex(2);
        DrawSelectedPanel();
    }

    private void DrawSidebar()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var child = ImRaii.Child("##FilterPanel", new Vector2(0f, 0f), true);
        if (!child)
            return;

        DrawFilterSection();
        DrawUsageSection();
    }

    private void DrawFilterSection()
    {
        SectionTitle(Lang.Get("UnifiedGlamourManager-FilterSection"));

        DrawSourceFilter();
        DrawSortFilter();
        DrawLevelFilter();
        DrawJobFilter();
        DrawSetRelationFilter();
        DrawResetFilterButton();
    }

    private void DrawSourceFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Source"));
        DrawSourceButtons();
        ImGui.Spacing();
    }

    private void DrawSortFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Sort"));

        var sortIndex = (int)sortMode;
        if (DrawCombo("##SortMode", SORT_MODE_NAMES, ref sortIndex))
        {
            sortMode = (SortMode)sortIndex;
            MarkFilteredItemsDirty();
        }

        ImGui.Spacing();
    }

    private void DrawLevelFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-EquipLevel"));
        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-EnableLevelRange"), ref enableLevelFilter))
            MarkFilteredItemsDirty();

        using (ImRaii.Disabled(!enableLevelFilter))
            DrawLevelRangeInputs();

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LevelRange", minEquipLevel, maxEquipLevel));
        ImGui.Spacing();
    }

    private void DrawLevelRangeInputs()
    {
        var inputWidth = (ImGui.GetContentRegionAvail().X - 10f) * 0.5f;

        ImGui.SetNextItemWidth(inputWidth);
        var oldMinLevel = minEquipLevel;
        var oldMaxLevel = maxEquipLevel;

        ImGui.InputInt("##MinLevel", ref minEquipLevel);

        ImGui.SameLine();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputInt("##MaxLevel", ref maxEquipLevel);

        minEquipLevel = Math.Clamp(minEquipLevel, DEFAULT_MIN_EQUIP_LEVEL, MAX_EQUIP_LEVEL_INPUT);
        maxEquipLevel = Math.Clamp(maxEquipLevel, DEFAULT_MIN_EQUIP_LEVEL, MAX_EQUIP_LEVEL_INPUT);

        if (minEquipLevel > maxEquipLevel)
            (minEquipLevel, maxEquipLevel) = (maxEquipLevel, minEquipLevel);

        if (oldMinLevel != minEquipLevel || oldMaxLevel != maxEquipLevel)
            MarkFilteredItemsDirty();
    }

    private void DrawJobFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Job"));
        if (DrawCombo("##JobFilter", JOB_FILTER_NAMES, ref selectedJobFilterIndex))
            MarkFilteredItemsDirty(clearJobCache: true);

        ImGui.Spacing();
    }

    private void DrawSetRelationFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SetDisplay"));

        var setIndex = Array.IndexOf(SET_RELATION_FILTER_VALUES, setRelationFilter);
        if (setIndex < 0)
            setIndex = 0;

        if (DrawCombo("##SetRelationFilter", SET_RELATION_FILTER_NAMES, ref setIndex))
        {
            setRelationFilter = SET_RELATION_FILTER_VALUES[setIndex];
            MarkFilteredItemsDirty();
        }

        ImGui.Spacing();
    }

    private void DrawResetFilterButton()
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ResetFilter"), new(-1f, CONTROL_HEIGHT)))
            ResetFilters();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void ResetFilters()
    {
        sourceFilter = SourceFilter.All;
        sortMode = SortMode.FavoriteThenNameAsc;
        setRelationFilter = SetRelationFilter.All;
        enableLevelFilter = false;
        minEquipLevel = DEFAULT_MIN_EQUIP_LEVEL;
        maxEquipLevel = DEFAULT_MAX_EQUIP_LEVEL;
        selectedJobFilterIndex = 0;
        searchText = string.Empty;
        filterByCurrentPlateSlot = true;
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
    }

    private void DrawSourceButtons()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var halfWidth = (width - 6f) * 0.5f;
        var buttonSize = new Vector2(halfWidth, CONTROL_HEIGHT);
        var hasInventoryPreview = HasPreviewSource(PREVIEW_SOURCE_INVENTORY) ||
                                  HasPreviewSource(PREVIEW_SOURCE_SADDLEBAG_LEGACY) ||
                                  HasPreviewSource(PREVIEW_SOURCE_ARMORY_LEGACY);
        var hasRetainerPreview = HasPreviewSource(PREVIEW_SOURCE_RETAINER);

        DrawSourceButtonRow(
            (Lang.Get("UnifiedGlamourManager-Source-All"), SourceFilter.All, false),
            (Lang.Get("UnifiedGlamourManager-Source-Favorite"), SourceFilter.Favorite, false),
            buttonSize);

        DrawSourceButtonRow(
            (Lang.Get("UnifiedGlamourManager-Source-PrismBox"), SourceFilter.PrismBox, false),
            (Lang.Get("UnifiedGlamourManager-Source-Cabinet"), SourceFilter.Cabinet, false),
            buttonSize);

        DrawSourceButtonRow(
            (Lang.Get("UnifiedGlamourManager-Source-RetainerPreview"), SourceFilter.RetainerPreview, !hasRetainerPreview),
            (Lang.Get("UnifiedGlamourManager-Source-InventoryPreview"), SourceFilter.InventoryPreview, !hasInventoryPreview),
            buttonSize);
    }

    private void DrawSourceButtonRow(
        (string Label, SourceFilter Value, bool Disabled) left,
        (string Label, SourceFilter Value, bool Disabled) right,
        Vector2 buttonSize)
    {
        DrawSourceButton(left.Label, left.Value, buttonSize, left.Disabled);
        ImGui.SameLine();
        DrawSourceButton(right.Label, right.Value, buttonSize, right.Disabled);
    }

    private void DrawSourceButton(string label, SourceFilter value, Vector2 size, bool disabled = false)
    {
        var active = sourceFilter == value;

        using (ImRaii.Disabled(disabled))
        using (ImRaii.PushColor(ImGuiCol.Button, ACCENT_COLOR, active))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ACCENT_SOFT_COLOR, active))
        {
            if (ImGui.Button($"{label}##Source{value}", size))
            {
                sourceFilter = value;
                MarkFilteredItemsDirty();
            }
        }
    }

    private bool HasPreviewSource(string source)
        => config.PreviewItems.Any(x => x.Source == source);

    private void DrawUsageSection()
    {
        SectionTitle(Lang.Get("UnifiedGlamourManager-Usage"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-CabinetAndPrism"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Inventory"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Retainer"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-PreviewOnly"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Apply"));
    }

    private static bool DrawCombo(string id, string[] items, ref int index)
    {
        if (items.Length == 0)
            return false;

        index = Math.Clamp(index, 0, items.Length - 1);
        var changed = false;

        ImGui.SetNextItemWidth(-1f);
        using var combo = ImRaii.Combo(id, items[index]);
        if (!combo)
            return false;

        for (var i = 0; i < items.Length; i++)
        {
            var selected = i == index;
            if (ImGui.Selectable(items[i], selected))
            {
                index = i;
                changed = true;
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        return changed;
    }

    private void DrawItemList()
    {
        EnsureFilteredItems();

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var listPanel = ImRaii.Child("##ListPanel", new Vector2(0f, 0f), true);
        if (!listPanel)
            return;

        DrawItemListHeader(filteredItems.Count);

        ImGui.Separator();

        if (isRefreshingItems)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Lang.Get("Loading"));
            return;
        }

        if (filteredItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoResult"));
            return;
        }

        using var itemList = ImRaii.Child("##UnifiedItemList", new Vector2(0f, 0f), false);
        if (!itemList)
            return;

        if (useGridView)
            DrawItemGrid(filteredItems);
        else
            DrawItemCardsVirtualized(filteredItems);
    }

    private void DrawItemListHeader(int count)
    {
        ImGui.TextColored(ACCENT_SOFT_COLOR, Lang.Get("UnifiedGlamourManager-ItemList"));
        ImGui.SameLine();
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ResultCount", count));
        ImGui.SameLine();
        var slotColor = IsPlateEditorReady()
            ? STAR_ON_COLOR
            : ERROR_COLOR;
        ImGui.TextColored(slotColor, Lang.Get("UnifiedGlamourManager-CurrentSlotValue", GetCurrentPlateSlotNameForUI()));

        var x = ImGui.GetContentRegionAvail().X - VIEW_MODE_BUTTON_WIDTH * 2f - GRID_ICON_PADDING;
        if (x > 0f)
            ImGui.SameLine(x);

        DrawViewModeButton(Lang.Get("UnifiedGlamourManager-ListView") + "##ViewList", !useGridView, () => useGridView = false);

        ImGui.SameLine();

        DrawViewModeButton(Lang.Get("UnifiedGlamourManager-GridView") + "##ViewGrid", useGridView, () => useGridView = true);
    }

    private static void DrawViewModeButton(string label, bool active, Action onClick)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.70f, 0.10f, 0.25f, 0.92f), active);

        if (ImGui.Button(label, new Vector2(VIEW_MODE_BUTTON_WIDTH, VIEW_MODE_BUTTON_HEIGHT)))
            onClick();
    }

    private void EnsureFilteredItems()
    {
        UpdateCurrentSlotFilterCache();

        if (!filteredItemsDirty)
            return;

        filteredItems.Clear();
        filteredItems.AddRange(ApplySort(items.Where(PassFilter)));
        filteredItemsDirty = false;
    }

    private void MarkFilteredItemsDirty(bool clearJobCache = false, bool clearPlateSlotCache = false)
    {
        filteredItemsDirty = true;

        if (clearJobCache)
            jobFilterCache.Clear();

        if (clearPlateSlotCache)
            plateSlotFilterCache.Clear();
    }

    private void UpdateCurrentSlotFilterCache()
    {
        if (!filterByCurrentPlateSlot)
            return;

        var slot = GetCurrentPlateSlotIndex();
        if (slot == lastFilterPlateSlot)
            return;

        lastFilterPlateSlot = slot;
        plateSlotFilterCache.Clear();
        MarkFilteredItemsDirty();
    }

    private void DrawItemCardsVirtualized(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0)
            return;

        var rowHeight = CARD_MIN_HEIGHT + ITEM_SPACING_Y;
        var startCursorPos = ImGui.GetCursorPos();
        var scrollY = ImGui.GetScrollY();
        var visibleHeight = ImGui.GetWindowHeight();
        var firstIndex = Math.Max(0, (int)MathF.Floor(scrollY / rowHeight) - VIRTUALIZED_LIST_BUFFER_ROWS);
        var lastIndex = Math.Min(filtered.Count - 1, (int)MathF.Ceiling((scrollY + visibleHeight) / rowHeight) + VIRTUALIZED_LIST_BUFFER_ROWS);

        if (firstIndex > 0)
            ImGui.Dummy(new Vector2(0f, firstIndex * rowHeight));

        for (var i = firstIndex; i <= lastIndex; i++)
            DrawItemCard(filtered[i]);

        var drawnHeight = (lastIndex + 1) * rowHeight;
        var totalHeight = filtered.Count * rowHeight;
        var remainingHeight = totalHeight - drawnHeight;
        if (remainingHeight > 0f)
            ImGui.Dummy(new Vector2(0f, remainingHeight));

        if (ImGui.GetCursorPosY() < startCursorPos.Y + totalHeight)
            ImGui.SetCursorPosY(startCursorPos.Y + totalHeight);
    }

    private void DrawItemCard(UnifiedItem item)
    {
        var favorite = IsFavorite(item.ItemID);
        var selected = selectedItem != null && IsSameSelectableItem(selectedItem, item);
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var borderColor = selected
            ? ACCENT_SOFT_COLOR
            : favorite
                ? FAVORITE_BORDER_COLOR
                : new Vector4(0.58f, 0.36f, 0.52f, 0.70f);

        using var id = ImRaii.PushId($"{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}_{item.PreviewSourceKey}_{item.PreviewOwnerName}");
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, borderColor);
        using var child = ImRaii.Child("##ItemCard", new Vector2(cardWidth, CARD_MIN_HEIGHT), true, ImGuiWindowFlags.NoScrollbar);

        if (!child)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = new Vector2(min.X + ImGui.GetWindowSize().X, min.Y + ImGui.GetWindowSize().Y);
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var bgColor = GetCardBackgroundColor(item, selected, favorite, hovered);

        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bgColor), CARD_ROUNDING);

        if (selected)
            drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(ACCENT_SOFT_COLOR), CARD_ROUNDING, (ImDrawFlags)0, CARD_BORDER_THICKNESS_SELECTED);
        else if (favorite)
            drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(FAVORITE_BORDER_COLOR), CARD_ROUNDING, (ImDrawFlags)0, CARD_BORDER_THICKNESS_FAVORITE);
        else if (hovered)
            drawList.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(ACCENT_COLOR), CARD_ROUNDING, (ImDrawFlags)0, CARD_BORDER_THICKNESS_HOVERED);

        ImGui.SetCursorPos(new Vector2(12f, 12f));

        DrawFavoriteButton(item, favorite);

        ImGui.SameLine(0f, 10f);

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.50f, item.PreviewOnly))
            DrawItemIcon(item.IconID, ICON_SIZE_LIST);

        ImGui.SameLine(0f, 10f);

        DrawItemCardInfo(item, selected, favorite);
        HandleItemCardInput(item, hovered);
        ImGui.Dummy(new Vector2(0f, ITEM_SPACING_Y));
    }

    private Vector4 GetCardBackgroundColor(UnifiedItem item, bool selected, bool favorite, bool hovered)
    {
        return selected
            ? hovered ? SELECTED_HOVER_COLOR : SELECTED_COLOR
            : item.PreviewOnly
                ? new Vector4(0.07f, 0.06f, 0.07f, hovered ? 0.72f : 0.52f)
                : favorite
                    ? hovered ? FAVORITE_HOVER_COLOR : FAVORITE_COLOR
                    : hovered ? HOVER_COLOR : NORMAL_CARD_COLOR;
    }

    private void DrawFavoriteButton(UnifiedItem item, bool favorite)
    {
        using var button = ImRaii.PushColor(ImGuiCol.Button, favorite ? new Vector4(1.00f, 0.90f, 0.50f, 0.96f) : new Vector4(0.30f, 0.25f, 0.30f, 0.92f));
        using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, favorite ? new Vector4(1.00f, 0.94f, 0.62f, 1.00f) : new Vector4(0.46f, 0.24f, 0.40f, 1.00f));
        using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, favorite ? new Vector4(1.00f, 0.82f, 0.32f, 1.00f) : new Vector4(0.72f, 0.26f, 0.54f, 1.00f));
        using var text = ImRaii.PushColor(ImGuiCol.Text, favorite ? STAR_ON_COLOR : STAR_OFF_COLOR);

        if (ImGui.Button(favorite ? FAVORITE_ICON_ON : FAVORITE_ICON_OFF, new Vector2(CONTROL_HEIGHT, ICON_SIZE_LIST)))
            ToggleFavorite(item);
    }

    private void DrawItemCardInfo(UnifiedItem item, bool selected, bool favorite)
    {
        using var group = ImRaii.Group();
        var titleColor = GetItemTitleColor(item, selected, favorite);

        using (ImRaii.PushColor(ImGuiCol.Text, titleColor))
            ImGui.TextUnformatted(item.Name);

        DrawItemBadges(item);
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));

        if (item.IsSetContainer)
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SetContainerTip"));
    }

    private Vector4 GetItemTitleColor(UnifiedItem item, bool selected, bool favorite)
    {
        return item.PreviewOnly
            ? MUTED_COLOR
            : selected
                ? ACCENT_SOFT_COLOR
                : favorite
                    ? new Vector4(1.00f, 0.88f, 0.96f, 1f)
                    : item.IsSetPart
                        ? new Vector4(1.00f, 0.74f, 0.88f, 1f)
                        : Vector4.One;
    }

    private void DrawItemBadges(UnifiedItem item)
    {
        if (item.PreviewOnly)
            ImGui.TextColored(MUTED_COLOR, Lang.Get("UnifiedGlamourManager-PreviewOnlySource", GetSourceLabel(item)));
    }

    private void DrawItemGrid(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0)
            return;

        var availableWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X);
        var columns = Math.Max(1, (int)MathF.Floor((availableWidth + GRID_CELL_SPACING) / (GRID_MIN_CELL_SIZE + GRID_CELL_SPACING)));
        var cellSize = MathF.Floor((availableWidth - GRID_CELL_SPACING * (columns - 1)) / columns);
        cellSize = Math.Clamp(cellSize, GRID_MIN_CELL_SIZE, GRID_MAX_CELL_SIZE);

        var iconSize = MathF.Max(GRID_ICON_MIN_SIZE, cellSize - GRID_ICON_PADDING);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var rows = (filtered.Count + columns - 1) / columns;
        var rowHeight = cellSize + GRID_CELL_SPACING;
        var totalHeight = rows * rowHeight;
        var scrollY = ImGui.GetScrollY();
        var visibleHeight = ImGui.GetWindowHeight();
        var firstVisibleRow = Math.Max(0, (int)MathF.Floor(scrollY / rowHeight) - VIRTUALIZED_GRID_BUFFER_ROWS);
        var lastVisibleRow = Math.Min(rows - 1, (int)MathF.Ceiling((scrollY + visibleHeight) / rowHeight) + VIRTUALIZED_GRID_BUFFER_ROWS);

        ImGui.Dummy(new Vector2(0f, totalHeight));

        for (var row = firstVisibleRow; row <= lastVisibleRow; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index < 0 || index >= filtered.Count)
                    continue;

                var item = filtered[index];
                var pos = new Vector2(start.X + col * (cellSize + GRID_CELL_SPACING), start.Y + row * rowHeight);
                ImGui.SetCursorScreenPos(pos);

                DrawGridCell(item, index, pos, cellSize, iconSize, drawList);
            }
        }

        ImGui.SetCursorScreenPos(start + new Vector2(0f, totalHeight));
    }

    private void DrawGridCell(UnifiedItem item, int index, Vector2 pos, float cellSize, float iconSize, ImDrawListPtr drawList)
    {
        using var id = ImRaii.PushId($"Grid_{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}_{item.PreviewSourceKey}_{item.PreviewOwnerName}_{index}");

        ImGui.InvisibleButton("##GridCell", new Vector2(cellSize, cellSize));

        var hovered = ImGui.IsItemHovered();
        var selected = selectedItem != null && IsSameSelectableItem(selectedItem, item);
        var favorite = IsFavorite(item.ItemID);
        var borderColor = GetGridBorderColor(item, selected, favorite);
        var bgColor = GetGridBackgroundColor(item, hovered);

        drawList.AddRectFilled(pos, pos + new Vector2(cellSize, cellSize), ImGui.ColorConvertFloat4ToU32(bgColor), GRID_CELL_ROUNDING);
        drawList.AddRect(
            pos,
            pos + new Vector2(cellSize, cellSize),
            ImGui.ColorConvertFloat4ToU32(borderColor),
            GRID_CELL_ROUNDING,
            (ImDrawFlags)0,
            selected ? 2.2f : CARD_BORDER_THICKNESS_HOVERED);

        var iconPos = pos + new Vector2((cellSize - iconSize) * 0.5f, (cellSize - iconSize) * 0.5f);
        ImGui.SetCursorScreenPos(iconPos);

        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.48f, item.PreviewOnly))
            DrawItemIcon(item.IconID, iconSize);

        if (favorite)
            drawList.AddText(pos + new Vector2(cellSize - 17f, 2f), ImGui.ColorConvertFloat4ToU32(STAR_ON_COLOR), FAVORITE_ICON_ON);

        if (!hovered)
            return;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            selectedItem = item;

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !item.PreviewOnly)
            SelectAndApplyItem(item);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ToggleFavorite(item);

        DrawGridTooltip(item);
    }

    private Vector4 GetGridBackgroundColor(UnifiedItem item, bool hovered)
    {
        return item.PreviewOnly
            ? new Vector4(0.07f, 0.06f, 0.07f, hovered ? 0.76f : 0.56f)
            : hovered ? HOVER_COLOR : NORMAL_CARD_COLOR;
    }

    private Vector4 GetGridBorderColor(UnifiedItem item, bool selected, bool favorite)
    {
        return selected
            ? ACCENT_SOFT_COLOR
            : favorite
                ? FAVORITE_BORDER_COLOR
                : item.PreviewOnly
                    ? new Vector4(0.48f, 0.42f, 0.48f, 0.60f)
                    : new Vector4(0.76f, 0.34f, 0.62f, 0.78f);
    }

    private void DrawGridTooltip(UnifiedItem item)
    {
        using var tooltip = ImRaii.Tooltip();

        ImGui.TextColored(item.PreviewOnly ? MUTED_COLOR : ACCENT_SOFT_COLOR, item.Name);
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SourceValue", GetSourceLabel(item)));

        if (item.IsSetPart)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-PartValue", item.SetPartLabel));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ParentSetValue", item.ParentSetName));
        }

        if (item.PreviewOnly)
        {
            ImGui.Separator();
            ImGui.TextColored(WARNING_COLOR, Lang.Get("UnifiedGlamourManager-PreviewOnlyCannotApply"));

            if (item.PreviewUpdatedAt > 0)
                ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-UpdatedAt", FormatUnixTime(item.PreviewUpdatedAt)));
        }
        else
        {
            ImGui.Separator();
            ImGui.TextColored(ACCENT_SOFT_COLOR, Lang.Get("UnifiedGlamourManager-GridTooltipHelp"));
        }
    }

    private static bool IsSameSelectableItem(UnifiedItem a, UnifiedItem b)
    {
        return a.ItemID == b.ItemID &&
               a.PrismBoxIndex == b.PrismBoxIndex &&
               a.IsSetPart == b.IsSetPart &&
               a.ParentSetItemID == b.ParentSetItemID &&
               a.PreviewOnly == b.PreviewOnly &&
               a.PreviewSourceKey == b.PreviewSourceKey &&
               a.PreviewOwnerName == b.PreviewOwnerName;
    }

    private void HandleItemCardInput(UnifiedItem item, bool hovered)
    {
        if (!hovered || ImGui.IsAnyItemActive())
            return;

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !item.PreviewOnly)
            SelectAndApplyItem(item);
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            selectedItem = item;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ToggleFavorite(item);
    }

    private void SelectAndApplyItem(UnifiedItem item)
    {
        selectedItem = item;
        if (item.PreviewOnly)
            return;

        ApplySelectedItemToCurrentPlateSlot(item);
    }

    private readonly Dictionary<uint, IconTextureCacheEntry> iconTextureCache = [];
    private readonly LinkedList<uint> iconTextureCacheOrder = [];

    private void DrawItemIcon(uint iconID, float size)
    {
        if (iconID == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        try
        {
            var texture = GetCachedIconTexture(iconID);
            if (texture == null)
            {
                ImGui.Dummy(new Vector2(size, size));
                return;
            }

            ImGui.Image(texture.Handle, new Vector2(size, size));
        }
        catch
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    private IDalamudTextureWrap? GetCachedIconTexture(uint iconID)
    {
        if (iconTextureCache.TryGetValue(iconID, out var entry))
        {
            TouchIconCacheEntry(entry);
            return entry.Texture.GetWrapOrDefault();
        }

        if (iconLoadCountThisFrame >= ICON_LOADS_PER_FRAME)
            return null;

        var node = iconTextureCacheOrder.AddFirst(iconID);
        iconTextureCache[iconID] = new(DService.Instance().Texture.GetFromGameIcon(new GameIconLookup(iconID)), node);
        iconLoadCountThisFrame++;

        TrimIconTextureCache();
        return iconTextureCache[iconID].Texture.GetWrapOrDefault();
    }

    private void TouchIconCacheEntry(IconTextureCacheEntry entry)
    {
        iconTextureCacheOrder.Remove(entry.Node);
        iconTextureCacheOrder.AddFirst(entry.Node);
    }

    private void TrimIconTextureCache()
    {
        while (iconTextureCache.Count > ICON_TEXTURE_CACHE_LIMIT && iconTextureCacheOrder.Last != null)
        {
            var iconID = iconTextureCacheOrder.Last.Value;
            iconTextureCacheOrder.RemoveLast();
            iconTextureCache.Remove(iconID);
        }
    }

    private void ClearIconCache()
    {
        iconTextureCache.Clear();
        iconTextureCacheOrder.Clear();
    }

    private void DrawSelectedPanel()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var child = ImRaii.Child("##SelectedPanel", new Vector2(0f, 0f), true);
        if (!child)
            return;

        SectionTitle(Lang.Get("UnifiedGlamourManager-SelectedSection"));

        if (selectedItem == null)
        {
            DrawEmptySelectedPanel();
            return;
        }

        var item = selectedItem;

        DrawSelectedItemHeader(item);
        DrawSelectedTargetSlot();
        DrawSelectedDetails(item);
        DrawSelectedActions(item);
    }

    private void DrawEmptySelectedPanel()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSelectedItem"));
        ImGui.Spacing();
        RedTip(Lang.Get("UnifiedGlamourManager-ApplyHelp"));
    }

    private void DrawSelectedItemHeader(UnifiedItem item)
    {
        DrawItemIcon(item.IconID, ICON_SIZE_SELECTED);
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.TextColored(CYAN_COLOR, item.Name);
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SourceValue", GetSourceLabel(item)));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawSelectedTargetSlot()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-TargetSlot"));
        ImGui.TextColored(CYAN_COLOR, GetCurrentPlateSlotNameForUI());
        ImGui.Spacing();
    }

    private void DrawSelectedDetails(UnifiedItem item)
    {
        DrawPreviewRecordInfo(item);
        DrawSetPartInfo(item);
        DrawSetContainerInfo(item);
    }

    private void DrawPreviewRecordInfo(UnifiedItem item)
    {
        if (!item.PreviewOnly)
            return;

        ImGui.TextColored(MUTED_COLOR, Lang.Get("UnifiedGlamourManager-PreviewRecord"));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SourceValue", GetSourceLabel(item)));

        if (item.PreviewUpdatedAt > 0)
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-UpdatedAt", FormatUnixTime(item.PreviewUpdatedAt)));

        ImGui.Spacing();
    }

    private void DrawSetPartInfo(UnifiedItem item)
    {
        if (!item.IsSetPart)
            return;

        ImGui.TextColored(CYAN_COLOR, Lang.Get("UnifiedGlamourManager-SetPart"));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-PartValue", item.SetPartLabel));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ParentSetValue", item.ParentSetName));
        ImGui.Spacing();
    }

    private void DrawSetContainerInfo(UnifiedItem item)
    {
        if (!item.IsSetContainer)
            return;

        ImGui.TextColored(WARNING_COLOR, Lang.Get("UnifiedGlamourManager-SetContainer"));
        RedTip(Lang.Get("UnifiedGlamourManager-SetContainerApplyTip"));
        ImGui.Spacing();
    }

    private void DrawSelectedActions(UnifiedItem item)
    {
        ImGui.Separator();
        ImGui.Spacing();

        DrawApplyAction(item);
        DrawRestoreAction(item);
        DrawSelectedTips(item);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSelectedUtilityActions(item);
    }

    private void DrawApplyAction(UnifiedItem item)
    {
        var plateReady = IsPlateEditorReady();
        var canApply = plateReady && !item.PreviewOnly && item.CanUseInPlate;

        using (ImRaii.Disabled(!canApply))
        {
            var buttonText = item.PreviewOnly
                ? Lang.Get("UnifiedGlamourManager-PreviewOnlyCannotApply")
                : Lang.Get("UnifiedGlamourManager-ApplyToCurrentSlot");

            if (ImGui.Button(buttonText, new(-1f, CONTROL_HEIGHT)))
                ApplySelectedItemToCurrentPlateSlot(item);
        }

        if (!plateReady && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));
        else if (item.PreviewOnly && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PreviewOnlyCannotApply"));
    }

    private void DrawRestoreAction(UnifiedItem item)
    {
        ImGui.Spacing();

        var plateReady = IsPlateEditorReady();
        var canRestore = plateReady
                         && !item.PreviewOnly
                         && !item.IsSetPart
                         && !isRestoringItem
                         && item.InPrismBox;

        var restoreButtonText = GetRestoreButtonText(item);

        using (ImRaii.Disabled(!canRestore))
        {
            if (ImGui.Button(restoreButtonText, new(-1f, RESTORE_BUTTON_HEIGHT)))
                requestRestoreItemConfirm = true;
        }

        if (item.IsSetPart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-SetPartRestoreUnsupportedTip"));
        else if (!plateReady && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));
        else if (item.PreviewOnly && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PreviewOnlyCannotApply"));
        else if (!item.InPrismBox && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-NotInPrismBoxTip"));
    }

    private string GetRestoreButtonText(UnifiedItem item)
    {
        if (isRestoringItem)
            return Lang.Get("UnifiedGlamourManager-Restoring");

        if (item.IsSetPart)
            return Lang.Get("UnifiedGlamourManager-SetPartRestoreUnsupported");

        return Lang.Get("UnifiedGlamourManager-RestoreItem");
    }

    private void DrawSelectedTips(UnifiedItem item)
    {
        if (!IsPlateEditorReady())
            RedTip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));

        if (item.IsSetPart)
            RedTip(Lang.Get("UnifiedGlamourManager-SetPartRestoreUnsupportedTip"));

        if (item.PreviewOnly)
            RedTip(Lang.Get("UnifiedGlamourManager-PreviewOnlyTip"));
        else if (!item.InPrismBox)
            RedTip(Lang.Get("UnifiedGlamourManager-NotInPrismBoxTip"));
    }

    private void DrawSelectedUtilityActions(UnifiedItem item)
    {
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CopyName"), new(-1f, CONTROL_HEIGHT)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CancelSelection"), new(-1f, CONTROL_HEIGHT)))
            selectedItem = null;
    }
}
