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
        ImGui.SetNextWindowSizeConstraints(new Vector2(1120f, 680f), new Vector2(9999f, 9999f));

        if (requestFocusNextOpen)
        {
            ImGui.SetNextWindowFocus();
            requestFocusNextOpen = false;
        }

        using var style = PushUnifiedStyle();
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

    private static IDisposable PushUnifiedStyle()
    {
        return new DisposableGroup
        (
            ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f)),
            ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f)),
            ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f)),
            ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f),
            ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f),
            ImRaii.PushStyle(ImGuiStyleVar.GrabRounding, 6f),
            ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 0f),
            ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f)),

            ImRaii.PushColor(ImGuiCol.Header, new Vector4(1.00f, 0.46f, 0.72f, 0.26f)),
            ImRaii.PushColor(ImGuiCol.HeaderHovered, new Vector4(1.00f, 0.58f, 0.80f, 0.42f)),
            ImRaii.PushColor(ImGuiCol.HeaderActive, new Vector4(1.00f, 0.36f, 0.66f, 0.52f)),
            ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.30f, 0.20f, 0.27f, 0.94f)),
            ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.48f, 0.24f, 0.40f, 1.00f)),
            ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.82f, 0.30f, 0.58f, 1.00f)),
            ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.13f, 0.09f, 0.12f, 0.90f)),
            ImRaii.PushColor(ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.12f, 0.18f, 0.98f)),
            ImRaii.PushColor(ImGuiCol.FrameBgActive, new Vector4(0.34f, 0.16f, 0.28f, 1.00f)),

            ImRaii.PushColor(ImGuiCol.WindowBg, new Vector4(0.025f, 0.025f, 0.030f, 0.96f)),
            ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.035f, 0.035f, 0.045f, 0.88f)),
            ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.030f, 0.040f, 0.98f)),
            ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.18f, 0.13f, 0.18f, 0.70f))
        );
    }

    private static void DrawSmallStat(string label, string value, Vector4 color)
    {
        using (ImRaii.Group())
        {
            ImGui.TextDisabled(label);
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(color, value);
        }
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

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmClear"), new Vector2(132f, CONTROL_HEIGHT)))
        {
            config.Favorites.Clear();
            SaveModuleConfig();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Cancel"), new Vector2(132f, CONTROL_HEIGHT)))
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

            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ConfirmRestore"), new Vector2(132f, CONTROL_HEIGHT)))
            {
                RestoreSelectedPrismBoxItem(item);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
        }

        if (ImGui.Button(Lang.Get("Cancel"), new Vector2(132f, CONTROL_HEIGHT)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    #endregion

    #region 顶栏

    private void DrawTopBar()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 8f));
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
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ReadRefresh"), new Vector2(112f, CONTROL_HEIGHT)))
                RefreshAll();

            ImGui.SameLine();
            
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-RecordPreview"), new Vector2(112f, CONTROL_HEIGHT)))
                RecordOpenedPreviewSources();

            ImGui.SameLine();


            var clearButtonWidth = 88f;
            var favoriteCountText = GetLoadedFavoriteCount().ToString();
            var favoriteGroupWidth = ImGui.CalcTextSize(Lang.Get("UnifiedGlamourManager-FavoriteCount")).X +
                                     ImGui.CalcTextSize(favoriteCountText).X +
                                     clearButtonWidth +
                                     40f;
            var searchWidth = MathF.Max(240f, MathF.Min(520f, availableWidth - 900f));

            ImGui.SetNextItemWidth(searchWidth);
            ImGui.InputTextWithHint("##Search", Lang.Get("UnifiedGlamourManager-SearchHint"), ref searchText, 128);
            ImGui.SameLine();
            ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-CurrentSlotOnly"), ref filterByCurrentPlateSlot);
            ImGui.SameLine();

            if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-ShowRetainerPreview"), ref config.ShowRetainerPreview))
                SaveModuleConfig();

            ImGui.SameLine();

            if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-ShowInventoryPreview"), ref config.ShowInventoryPreview))
                SaveModuleConfig();

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
        var mainHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, 420f);
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
