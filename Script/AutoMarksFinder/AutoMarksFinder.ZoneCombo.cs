using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class ScanZoneCombo : LuminaComboBase<TerritoryType>
    {
        private readonly Config config;

        public ScanZoneCombo(Config config) : base("AutoMarksFinder.ScanZones", null)
        {
            this.config = config;
            Searcher = new
            (
                config.ScanPoints.Where(pair => pair.Value.Count > 0)
                      .Select(pair => LuminaGetter.GetRowOrDefault<TerritoryType>(pair.Key))
                      .Where(zone => zone.RowId > 0),
                [zone => zone.ExtractPlaceName(), zone => $"{zone.ExVersion.RowId + 2}.0", zone => zone.RowId.ToString()],
                resultLimit: int.MaxValue
            );
        }

        public override uint SelectedID { get; set; }

        public override HashSet<uint> SelectedIDs
        {
            get => config.SelectedZones;
            set => config.SelectedZones = value;
        }

        protected override string GetPreviewText(ComboSelectionMode mode) =>
            Lang.Get("AutoMarksFinder-SelectedZonePreview", new Dictionary<string, object> { ["count"] = SelectedIDs.Count });

        protected override int GetTableColumnCount() => 3;

        protected override void SetupColumns(ComboSelectionMode mode)
        {
            ImGui.TableSetupColumn("###Selected",                          ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableSetupColumn(Lang.Get("Zone"),                       ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-ScanPoints"), ImGuiTableColumnFlags.WidthFixed, 100 * GlobalUIScale);
        }

        protected override void DrawHeaders() => ImGui.TableHeadersRow();

        protected override uint? GetGroupID(TerritoryType item) => item.ExVersion.RowId;

        protected override bool DrawGroupHeader(uint groupID, IReadOnlyList<TerritoryType> items, ComboSelectionMode mode)
        {
            var groupZones    = Searcher.Data.Where(zone => zone.ExVersion.RowId == groupID).Select(zone => zone.RowId).ToArray();
            var selectedCount = groupZones.Count(SelectedIDs.Contains);
            var selected      = selectedCount == groupZones.Length;
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn();
            using var group   = ImRaii.PushId($"Version_{groupID}");
            var       changed = ImGui.Checkbox("###Version", ref selected);
            ImGui.TableNextColumn();
            ImGui.TableHeader($"{LuminaWrapper.GetExVersionName(groupID)} ({selectedCount}/{groupZones.Length})");
            changed |= ImGui.IsItemClicked();
            ImGui.TableNextColumn();
            ImGui.TableHeader($"{groupZones.Sum(zone => config.ScanPoints[zone].Count)}");
            if (!changed)
                return false;

            if (selectedCount == groupZones.Length)
                SelectedIDs.ExceptWith(groupZones);
            else
                SelectedIDs.UnionWith(groupZones);

            return true;
        }

        protected override bool DrawDataColumns(TerritoryType item, ComboSelectionMode mode, bool isSelected)
        {
            ImGui.TableNextColumn();
            var changed = ImGui.Selectable(item.ExtractPlaceName(), isSelected, ImGuiSelectableFlags.DontClosePopups | ImGuiSelectableFlags.SpanAllColumns);
            ImGui.TableNextColumn();
            var points = config.ScanPoints[item.RowId];
            ImGui.TextUnformatted($"{points.Count}");

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                foreach (var point in points)
                    ImGui.TextUnformatted($"{point.Position:F0}");
            }

            return changed;
        }
    }
}
