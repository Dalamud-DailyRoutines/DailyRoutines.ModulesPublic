using System.Numerics;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 当前选择

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

    #endregion

    #region 物品信息

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

    #endregion

    #region 操作

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

            if (ImGui.Button(buttonText, new Vector2(-1f, CONTROL_HEIGHT)))
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
            if (ImGui.Button(restoreButtonText, new Vector2(-1f, RESTORE_BUTTON_HEIGHT)))
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
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CopyName"), new Vector2(-1f, CONTROL_HEIGHT)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CancelSelection"), new Vector2(-1f, CONTROL_HEIGHT)))
            selectedItem = null;
    }

    #endregion
}
