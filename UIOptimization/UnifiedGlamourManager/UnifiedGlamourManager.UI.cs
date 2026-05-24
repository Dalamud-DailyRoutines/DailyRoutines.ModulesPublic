using System.Numerics;
using Dalamud.Interface.Utility;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    protected override void OverlayPreDraw()
    {
        if (Overlay?.IsOpen == true &&
            (!TryGetReadyPlateEditor(out var agent) ||
             agent->Data->OpenMode != MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW))
        {
            Overlay.IsOpen = false;
            return;
        }

        var minSize = new Vector2
        (
            ImGui.GetFrameHeight()               * 28f,
            ImGui.GetTextLineHeightWithSpacing() * 30f
        );

        ImGui.SetNextWindowSizeConstraints(minSize, ImGui.GetMainViewport().WorkSize);
    }

    protected override void OverlayUI()
    {
        DrawTopBar();
        DrawMainLayout();
        DrawConfirmPopups();
    }

    private static void DrawSectionTitle(string text)
    {
        ImGui.TextColored(TitleColor, text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawItemIcon(uint iconID, float size)
    {
        if (iconID == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        var texture = ImageHelper.GetGameIcon(iconID);
        if (texture != null)
            ImGui.Image(texture.Handle, new Vector2(size, size));
        else
            ImGui.Dummy(new Vector2(size, size));
    }

    private static void DrawItemBackground
    (
        ImDrawListPtr drawList,
        Vector2       min,
        Vector2       max,
        bool          selected,
        bool          favorite,
        bool          hovered
    )
    {
        var rounding = ImGui.GetStyle().FrameRounding;
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(GetCardBackgroundColor(selected, favorite, hovered)), rounding);
        drawList.AddRect
        (
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(GetCardBorderColor(selected, favorite)),
            rounding,
            0,
            selected ? 2f * ImGuiHelpers.GlobalScale : 1f * ImGuiHelpers.GlobalScale
        );
    }

    private void DrawTopBar()
    {
        using var child = ImRaii.Child
        (
            "##TopBar",
            new Vector2(0f, ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().WindowPadding.Y * 2f)),
            true,
            ImGuiWindowFlags.NoScrollbar
        );
        if (!child) return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled
        (
            $"{LuminaWrapper.GetAddonText(11910)}: {prismBoxItemCount} / {LuminaWrapper.GetAddonText(12216)}: {cabinetItemCount} / {LuminaWrapper.GetAddonText(929)}: {StoredItemCount}"
        );

        if (ImGui.Button(Lang.Get("Refresh")))
            StartRefreshAll();

        ImGui.SameLine();

        var searchWidth = MathF.Min
        (
            ImGui.GetContentRegionAvail().X * 0.35f,
            ImGui.GetContentRegionAvail().X
        );
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##Search", Lang.Get("Search"), ref searchText))
            MarkFilteredItemsDirty();

        ImGui.SameLine();

        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-CurrentTargetSlotOnly", string.Empty).TrimEnd(':', ' '), ref filterByCurrentPlateSlot))
            MarkFilteredItemsDirty(clearPlateSlotCache: true);
    }

    private void DrawMainLayout()
    {
        var contentSize = ImGui.GetContentRegionAvail();
        if (contentSize.X <= 0f || contentSize.Y <= 0f) return;

        var tableFlags = ImGuiTableFlags.BordersInnerV     |
                         ImGuiTableFlags.SizingStretchProp |
                         ImGuiTableFlags.Resizable;

        using var table = ImRaii.Table("##UnifiedMainTable", 3, tableFlags, contentSize);
        if (!table) return;

        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(14370),                ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn(Lang.Get("UnifiedGlamourManager-FilteredResult"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(2154),                 ImGuiTableColumnFlags.WidthStretch, 0.7f);

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
        using var child = ImRaii.Child("##FilterPanel", Vector2.Zero, true);
        if (!child) return;

        DrawSectionTitle(LuminaWrapper.GetAddonText(14370));
        DrawSourceFilter();
        DrawSortFilter();
        DrawLevelFilter();
        DrawJobFilter();
        DrawSetRelationFilter();
        DrawResetFilterButton();
    }

    private void DrawSourceFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-GlamourSource"));

        var width       = ImGui.GetContentRegionAvail().X;
        var buttonWidth = MathF.Max(1f, (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
        var buttonSize  = new Vector2(buttonWidth, 0f);

        for (var i = 0; i < SourceFilters.Length; i++)
        {
            if (i % 2 == 1)
                ImGui.SameLine();

            var filter = SourceFilters[i];

            using (ImRaii.PushColor(ImGuiCol.Button, ButtonActiveColor, sourceFilter == filter))
            {
                if (ImGui.Button($"{GetSourceFilterLabel(filter)}##Source{filter}", buttonSize))
                {
                    sourceFilter = filter;
                    MarkFilteredItemsDirty();
                }
            }
        }

        ImGui.Spacing();
    }

    private void DrawSortFilter()
    {
        ImGui.TextDisabled(LuminaWrapper.GetAddonText(12170));

        var sortIndex = (int)sortMode;
        ImGui.SetNextItemWidth(-1f);

        if (ImGui.Combo("##SortMode", ref sortIndex, SortModeNames, SortModeNames.Length))
        {
            sortMode = (SortMode)sortIndex;
            MarkFilteredItemsDirty();
        }

        ImGui.Spacing();
    }

    private void DrawLevelFilter()
    {
        ImGui.TextDisabled(LuminaWrapper.GetAddonText(7873));
        if (ImGui.Checkbox(Lang.Get("Enable"), ref enableLevelFilter))
            MarkFilteredItemsDirty();

        using (ImRaii.Disabled(!enableLevelFilter))
        {
            var inputWidth  = MathF.Max(1f, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
            var oldMinLevel = minEquipLevel;
            var oldMaxLevel = maxEquipLevel;

            ImGui.SetNextItemWidth(inputWidth);
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

        ImGui.Spacing();
    }

    private void DrawJobFilter()
    {
        ImGui.TextDisabled(LuminaWrapper.GetAddonText(294));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##JobFilter", ref selectedJobFilterIndex, JobFilterNames, JobFilterNames.Length))
            MarkFilteredItemsDirty(true);

        ImGui.Spacing();
    }

    private void DrawSetRelationFilter()
    {
        ImGui.TextDisabled(LuminaWrapper.GetAddonText(15624));

        var names    = CreateSetRelationFilterNames();
        var setIndex = Math.Clamp((int)setRelationFilter, 0, names.Length - 1);
        ImGui.SetNextItemWidth(-1f);

        if (ImGui.Combo("##SetRelationFilter", ref setIndex, names, names.Length))
        {
            setRelationFilter = (SetRelationFilter)setIndex;
            MarkFilteredItemsDirty();
        }

        ImGui.Spacing();
    }

    private void DrawResetFilterButton()
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(LuminaWrapper.GetAddonText(329), new Vector2(-1f, 0f)))
            ResetFilters();
    }

    private void DrawItemList()
    {
        EnsureFilteredItems();

        using var listPanel = ImRaii.Child("##ListPanel", Vector2.Zero, true);
        if (!listPanel) return;

        ImGui.TextColored(TitleColor, Lang.Get("UnifiedGlamourManager-FilteredResult"));

        ImGui.Separator();

        var tabSize = new Vector2
        (
            (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f,
            ImGui.GetFrameHeight()
        );

        foreach (var (label, gridView) in new[]
                 {
                     (Lang.Get("List"), false),
                     (Lang.Get("Icon"), true)
                 })
        {
            var active = config.UseGridView == gridView;
            var pos    = ImGui.GetCursorScreenPos();

            if (ImGui.InvisibleButton($"##ViewMode{gridView}", tabSize))
                config.UseGridView = gridView;

            ImGui.GetWindowDrawList().AddText
            (
                pos + new Vector2((tabSize.X - ImGui.CalcTextSize(label).X) * 0.5f, (tabSize.Y - ImGui.CalcTextSize(label).Y) * 0.5f),
                ImGui.GetColorU32(active ? ButtonActiveColor : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
                label
            );

            if (active)
            {
                ImGui.GetWindowDrawList().AddLine
                (
                    pos + new Vector2(tabSize.X * 0.25f, tabSize.Y - 1f),
                    pos + new Vector2(tabSize.X * 0.75f, tabSize.Y - 1f),
                    ImGui.GetColorU32(ButtonActiveColor),
                    2f
                );
            }

            if (!gridView)
                ImGui.SameLine();
        }

        ImGui.Separator();
        ImGui.Spacing();

        var showFavoriteFooter = sourceFilter == SourceFilter.Favorite;
        var footerHeight       = showFavoriteFooter ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y : 0f;

        using (ImRaii.Child
               (
                   "##UnifiedItemList",
                   showFavoriteFooter ? new Vector2(0f, -footerHeight) : Vector2.Zero,
                   false
               ))
        {
            if (isRefreshingItems)
                ImGui.TextDisabled(Lang.Get("Loading"));
            else if (filteredItems.Count == 0)
                ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSearchResult"));
            else if (config.UseGridView)
                DrawItemGrid(filteredItems);
            else
                DrawItemCardsVirtualized(filteredItems);
        }

        if (showFavoriteFooter)
        {
            ImGui.Separator();
            ImGui.TextColored
                (GoldColor, $"{Lang.Get("UnifiedGlamourManager-FilteredFavoriteCount")}: {filteredItems.Select(static x => x.ItemID).Distinct().Count()}");

            using (ImRaii.Disabled(filteredItems.Count == 0))
            {
                if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ClearFavorites"), new Vector2(-1f, 0f)))
                    requestClearFavoritesConfirm = true;
            }
        }
    }

    private void DrawItemCardsVirtualized(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0) return;

        var rowHeight = CardHeight + ImGui.GetStyle().ItemSpacing.Y;
        var startCursorPos = ImGui.GetCursorPos();
        var scrollY        = ImGui.GetScrollY();
        var visibleHeight  = ImGui.GetWindowHeight();
        var firstIndex     = Math.Max(0, (int)MathF.Floor(scrollY / rowHeight) - VIRTUALIZED_LIST_BUFFER_ROWS);
        var lastIndex      = Math.Min(filtered.Count - 1, (int)MathF.Ceiling((scrollY + visibleHeight) / rowHeight) + VIRTUALIZED_LIST_BUFFER_ROWS);

        if (firstIndex > 0)
            ImGui.Dummy(new Vector2(0f, firstIndex * rowHeight));

        for (var i = firstIndex; i <= lastIndex; i++)
            DrawItemCard(filtered[i]);

        var drawnHeight     = (lastIndex + 1) * rowHeight;
        var totalHeight     = filtered.Count  * rowHeight;
        var remainingHeight = totalHeight - drawnHeight;
        if (remainingHeight > 0f)
            ImGui.Dummy(new Vector2(0f, remainingHeight));

        if (ImGui.GetCursorPosY() < startCursorPos.Y + totalHeight)
            ImGui.SetCursorPosY(startCursorPos.Y + totalHeight);
    }

    private void DrawItemCard(UnifiedItem item)
    {
        var favorite  = IsFavorite(item.ItemID);
        var selected  = selectedItem == item;
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var iconSize  = ImGui.GetFrameHeight() * 1.6f;

        using var id      = ImRaii.PushId($"{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}");
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, NormalCardColor);
        using var border  = ImRaii.PushColor(ImGuiCol.Border,  GetCardBorderColor(selected, favorite));
        using var child = ImRaii.Child
        (
            "##ItemCard",
            new Vector2(cardWidth, CardHeight),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        );
        if (!child) return;

        var hovered  = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var drawList = ImGui.GetWindowDrawList();
        var min      = ImGui.GetWindowPos();
        var max      = min + ImGui.GetWindowSize();
        DrawItemBackground(drawList, min, max, selected, favorite, hovered);

        ImGui.SetCursorPos(ImGui.GetStyle().WindowPadding);
        DrawFavoriteButton(item, favorite, iconSize);
        ImGui.SameLine();
        DrawItemIcon(item.IconID, iconSize);
        ImGui.SameLine();
        DrawItemCardInfo(item, selected, favorite);

        if (hovered && !ImGui.IsAnyItemActive())
            HandleItemClick(item);

        ImGui.Dummy(new(0f, ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawFavoriteButton(UnifiedItem item, bool favorite, float iconSize)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, NormalCardColor)
                                 .Push(ImGuiCol.ButtonHovered, FrameBGColor)
                                 .Push(ImGuiCol.ButtonActive,  ButtonAccentColor)
                                 .Push(ImGuiCol.Text,          favorite ? GoldColor : StarOffColor);

        if (ImGui.Button(favorite ? FAVORITE_ICON_ON : FAVORITE_ICON_OFF, new Vector2(ImGui.GetFrameHeight(), iconSize)))
            ToggleFavorite(item);
    }

    private static void DrawItemCardInfo(UnifiedItem item, bool selected, bool favorite)
    {
        using var group = ImRaii.Group();

        var titleColor = selected                    ? SelectedBorderColor :
                         favorite || !item.IsSetPart ? KnownColor.White.ToVector4() : SoftAccentColor;

        using (ImRaii.PushColor(ImGuiCol.Text, titleColor))
            ImGui.TextUnformatted(item.Name);

        DrawItemStains(item, true);
    }

    private void DrawItemGrid(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0) return;

        var spacing        = ImGui.GetStyle().ItemSpacing.X;
        var minCellSize    = ImGui.GetFrameHeight() * 1.8f;
        var maxCellSize    = ImGui.GetFrameHeight() * 2.3f;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columns        = Math.Max(1, (int)MathF.Floor((availableWidth + spacing) / (minCellSize + spacing)));
        var cellSize       = MathF.Floor((availableWidth - (spacing                  * (columns     - 1))) / columns);
        cellSize = Math.Clamp(cellSize, minCellSize, maxCellSize);

        var iconSize        = MathF.Max(ImGui.GetFrameHeight() * 1.3f, cellSize - (ImGui.GetStyle().FramePadding.X * 2f));
        var start           = ImGui.GetCursorScreenPos();
        var drawList        = ImGui.GetWindowDrawList();
        var rows            = (filtered.Count + columns - 1) / columns;
        var rowHeight       = cellSize + spacing;
        var totalHeight     = rows * rowHeight;
        var scrollY         = ImGui.GetScrollY();
        var visibleHeight   = ImGui.GetWindowHeight();
        var firstVisibleRow = Math.Max(0, (int)MathF.Floor(scrollY / rowHeight) - VIRTUALIZED_GRID_BUFFER_ROWS);
        var lastVisibleRow  = Math.Min(rows - 1, (int)MathF.Ceiling((scrollY + visibleHeight) / rowHeight) + VIRTUALIZED_GRID_BUFFER_ROWS);

        ImGui.Dummy(new Vector2(0f, totalHeight));

        for (var row = firstVisibleRow; row <= lastVisibleRow; row++)
        for (var col = 0; col < columns; col++)
        {
            var index = (row * columns) + col;
            if (index < 0 || index >= filtered.Count)
                continue;

            var item = filtered[index];
            var pos  = new Vector2(start.X + (col * (cellSize + spacing)), start.Y + (row * rowHeight));
            ImGui.SetCursorScreenPos(pos);
            DrawGridCell(item, index, pos, cellSize, iconSize, drawList);
        }

        ImGui.SetCursorScreenPos(start + new Vector2(0f, totalHeight));
    }

    private void DrawGridCell(UnifiedItem item, int index, Vector2 pos, float cellSize, float iconSize, ImDrawListPtr drawList)
    {
        using var id = ImRaii.PushId($"Grid_{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}_{index}");

        ImGui.InvisibleButton("##GridCell", new Vector2(cellSize, cellSize));

        var hovered  = ImGui.IsItemHovered();
        var selected = selectedItem == item;
        var favorite = IsFavorite(item.ItemID);
        DrawItemBackground(drawList, pos, pos + new Vector2(cellSize, cellSize), selected, favorite, hovered);

        var halfDiff = (cellSize - iconSize) * 0.5f;
        ImGui.SetCursorScreenPos(pos + new Vector2(halfDiff, halfDiff));
        DrawItemIcon(item.IconID, iconSize);

        if (favorite)
        {
            var starSize = ImGui.CalcTextSize(FAVORITE_ICON_ON);
            drawList.AddText
            (
                pos + new Vector2(cellSize - starSize.X - ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y),
                ImGui.ColorConvertFloat4ToU32(GoldColor),
                FAVORITE_ICON_ON
            );
        }

        if (!hovered) return;

        HandleItemClick(item);
        DrawGridTooltip(item);
    }

    private void DrawSelectedPanel()
    {
        using var child = ImRaii.Child("##SelectedPanel", Vector2.Zero, true);
        if (!child) return;

        DrawSectionTitle(LuminaWrapper.GetAddonText(2154));

        if (selectedItem == null)
        {
            ImGui.TextDisabled(LuminaWrapper.GetAddonText(4764));
            return;
        }

        var item = selectedItem;
        DrawSelectedItemHeader(item);

        ImGui.TextColored(SoftAccentColor, $"{Lang.Get("UnifiedGlamourManager-GlamourTargetSlot")}: ");

        ImGui.TextDisabled(item.AvailableSlotsLabel);

        ImGui.Spacing();

        if (item.IsSetPart)
        {
            ImGui.TextColored(SoftAccentColor, $"{LuminaWrapper.GetAddonText(15624)}: ");
            ImGui.TextDisabled(item.ParentSetName);

            ImGui.Spacing();
        }

        if (item.IsSetContainer)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                ImGui.TextWrapped(LuminaWrapper.GetAddonText(15624));

            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(LuminaWrapper.GetAddonText(159), new Vector2(-1f, 0f)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(LuminaWrapper.GetAddonText(102590), new Vector2(-1f, 0f)))
            selectedItem = null;
    }

    private static void DrawSelectedItemHeader(UnifiedItem item)
    {
        DrawItemIcon(item.IconID, ImGui.GetFrameHeight());
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.TextColored(SoftAccentColor, item.Name);
            ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(335)}: {item.LevelEquip}");
            ImGui.TextDisabled($"{Lang.Get("UnifiedGlamourManager-GlamourSource")}: {item.SourceLabel}");

            DrawItemStains(item);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawConfirmPopups()
    {
        if (requestClearFavoritesConfirm)
        {
            ImGui.OpenPopup($"{Lang.Get("Clear")}###ClearFavoritesConfirm");
            requestClearFavoritesConfirm = false;
        }

        var popupOpen = true;
        using var popup = ImRaii.PopupModal
        (
            $"{Lang.Get("Clear")}###ClearFavoritesConfirm",
            ref popupOpen
        );
        if (!popup) return;

        ImGui.TextColored(ErrorColor, Lang.Get("UnifiedGlamourManager-ClearFavoriteConfirm"));
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("Confirm")))
        {
            var ids = filteredItems.Select(static x => x.ItemID).ToHashSet();
            config.Favorites.RemoveAll(x => ids.Contains(x.ItemID));
            SaveConfig();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Cancel")))
            ImGui.CloseCurrentPopup();
    }

    private static void DrawGridTooltip(UnifiedItem item)
    {
        using var tooltip = ImRaii.Tooltip();

        ImGui.TextColored(TitleColor, item.Name);

        DrawItemStains(item);

        ImGui.Separator();
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-RightClickFavoriteHint"));
    }

    #region 工具

    private static Vector4 GetCardBackgroundColor(bool selected, bool favorite, bool hovered)
    {
        if (favorite)
            return hovered ? FavoriteCardHoverColor : FavoriteCardColor;

        if (selected)
            return SelectedColor;

        return hovered ? NormalCardHoverColor : NormalCardColor;
    }

    private static Vector4 GetCardBorderColor(bool selected, bool favorite) =>
        selected
            ? SelectedBorderColor
            : favorite
                ? GoldColor
                : MutedBorderColor;



    private static string GetSourceFilterLabel(SourceFilter filter) =>
        filter switch
        {
            SourceFilter.Favorite => Lang.Get("Favorite"),
            SourceFilter.PrismBox => LuminaWrapper.GetAddonText(11910),
            SourceFilter.Cabinet  => LuminaWrapper.GetAddonText(12216),
            _                     => Lang.Get("All")
        };

    private void HandleItemClick(UnifiedItem item)
    {
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectedItem = item;
            ApplySelectedItemToCurrentPlateSlot(item);
        }

        if (config.UseGridView && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ToggleFavorite(item);
    }

    private static float CardHeight => MathF.Max
    (
        (ImGui.GetFrameHeight() * 1.6f) + (ImGui.GetStyle().WindowPadding.Y * 2f),
        (ImGui.GetTextLineHeightWithSpacing() * 2f) + (ImGui.GetStyle().WindowPadding.Y * 2f)
    );

    private static void DrawStainLabel(uint labelAddonID, Stain stain)
    {
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(labelAddonID)}: {stain.Name.ExtractText()}");
        ImGui.SameLine();
        ImGui.TextColored(stain.Color.ReverseToVector4().WithAlpha(1f), "■");
    }

    private static void DrawItemStains(UnifiedItem item, bool horizontal = false)
    {
        if (item.Stain0 is { } s0)
        {
            DrawStainLabel(15970, s0);
            if (horizontal && item.Stain1 != null)
                ImGui.SameLine();
        }

        if (item.Stain1 is { } s1)
            DrawStainLabel(15971, s1);
    }

    #endregion
}
