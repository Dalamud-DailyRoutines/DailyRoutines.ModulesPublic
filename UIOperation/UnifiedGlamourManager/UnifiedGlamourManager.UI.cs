using System.Numerics;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region UI 主窗口

    private void DrawWindow()
    {
        if (!isOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(WINDOW_DEFAULT_WIDTH, WINDOW_DEFAULT_HEIGHT), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(WINDOW_MIN_WIDTH, WINDOW_MIN_HEIGHT),
            new Vector2(WINDOW_MAX_SIZE, WINDOW_MAX_SIZE));

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

    #endregion

    #region 样式

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

    #endregion

    #region 弹窗

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

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmClear"), new Vector2(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
        {
            config.Favorites.Clear();
            SaveConfig();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Cancel"), new Vector2(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
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

            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmRestore"), new Vector2(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
            {
                RestoreSelectedPrismBoxItem(item);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
        }

        if (ImGui.Button(Lang.Get("Cancel"), new Vector2(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    #endregion

    #region 顶栏

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
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ReadRefresh"), new Vector2(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
                StartRefreshAll();

            ImGui.SameLine();
            
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-RecordPreview"), new Vector2(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
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

            ImGui.SetCursorPos(new Vector2(favoriteX, rowY));
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
                    if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ClearFavorites"), new Vector2(clearButtonWidth, CONTROL_HEIGHT)))
                        requestClearFavoritesConfirm = true;
                }
            }
        }
    }

    #endregion

    #region 主布局

    private void DrawMainLayout()
    {
        var mainHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, MAIN_LAYOUT_MIN_HEIGHT);
        var tableFlags = ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.SizingStretchProp;

        using var table = ImRaii.Table("##UnifiedMainTable", 3, tableFlags, new Vector2(0f, mainHeight));
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

    #endregion
}
