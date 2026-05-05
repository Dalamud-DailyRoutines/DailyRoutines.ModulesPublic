using System.Numerics;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 侧边栏

    private void DrawSidebar()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        using var child = ImRaii.Child("##FilterPanel", new Vector2(0f, 0f), true);
        if (!child)
            return;

        DrawFilterSection();
        DrawUsageSection();
    }

    #endregion

    #region 筛选

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
            sortMode = (SortMode)sortIndex;

        ImGui.Spacing();
    }

    private void DrawLevelFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-EquipLevel"));
        ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-EnableLevelRange"), ref enableLevelFilter);

        using (ImRaii.Disabled(!enableLevelFilter))
            DrawLevelRangeInputs();

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LevelRange", minEquipLevel, maxEquipLevel));
        ImGui.Spacing();
    }

    private void DrawLevelRangeInputs()
    {
        var inputWidth = (ImGui.GetContentRegionAvail().X - 10f) * 0.5f;

        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputInt("##MinLevel", ref minEquipLevel);

        ImGui.SameLine();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputInt("##MaxLevel", ref maxEquipLevel);

        minEquipLevel = Math.Clamp(minEquipLevel, 1, 999);
        maxEquipLevel = Math.Clamp(maxEquipLevel, 1, 999);

        if (minEquipLevel > maxEquipLevel)
            (minEquipLevel, maxEquipLevel) = (maxEquipLevel, minEquipLevel);
    }

    private void DrawJobFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Job"));
        DrawCombo("##JobFilter", JOB_FILTER_NAMES, ref selectedJobFilterIndex);
        ImGui.Spacing();
    }

    private void DrawSetRelationFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SetDisplay"));

        var setIndex = Array.IndexOf(SET_RELATION_FILTER_VALUES, setRelationFilter);
        if (setIndex < 0)
            setIndex = 0;

        if (DrawCombo("##SetRelationFilter", SET_RELATION_FILTER_NAMES, ref setIndex))
            setRelationFilter = SET_RELATION_FILTER_VALUES[setIndex];

        ImGui.Spacing();
    }

    private void DrawResetFilterButton()
    {
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ResetFilter"), new Vector2(-1f, CONTROL_HEIGHT)))
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
        minEquipLevel = 1;
        maxEquipLevel = 100;
        selectedJobFilterIndex = 0;
        searchText = string.Empty;
        filterByCurrentPlateSlot = true;
    }

    #endregion

    #region 来源筛选

    private void DrawSourceButtons()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var halfWidth = (width - 6f) * 0.5f;
        var buttonSize = new Vector2(halfWidth, CONTROL_HEIGHT);
        var hasInventoryPreview = HasPreviewSource("InventoryPreview") ||
                                  HasPreviewSource("SaddleBagPreview") ||
                                  HasPreviewSource("ArmoryPreview");
        var hasRetainerPreview = HasPreviewSource("RetainerPreview");

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
                sourceFilter = value;
        }
    }

    private bool HasPreviewSource(string source)
        => config.PreviewItems.Any(x => x.Source == source);

    #endregion

    #region 使用说明

    private void DrawUsageSection()
    {
        SectionTitle(Lang.Get("UnifiedGlamourManager-Usage"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-CabinetAndPrism"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Inventory"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Retainer"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-PreviewOnly"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Apply"));
    }

    #endregion

    #region 组件

    private static bool DrawCombo(string id, string[] items, ref int index)
    {
        if (items.Length == 0)
            return false;

        index = Math.Clamp(index, 0, items.Length - 1);
        var changed = false;

        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo(id, items[index]))
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

        ImGui.EndCombo();
        return changed;
    }

    #endregion
}
