using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 主列表

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

    #endregion

    #region 列表头

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

    #endregion

    #region 卡片列表

    #region 筛选缓存

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

    #endregion

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

    #endregion

    #region 网格列表

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

    #endregion

    #region 交互

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

    #endregion

    #region 图标

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

    #endregion
}
