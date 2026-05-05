using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Frozen;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 筛选

    private bool PassFilter(UnifiedItem item)
    {
        if (filterByCurrentPlateSlot && !CanItemUseInCurrentPlateSlot(item))
            return false;

        if (!PassPreviewConfigFilter(item))
            return false;

        if (!PassSourceFilter(item))
            return false;

        if (enableLevelFilter && (item.LevelEquip < minEquipLevel || item.LevelEquip > maxEquipLevel))
            return false;

        if (!PassSetRelationFilter(item))
            return false;

        if (!PassJobFilter(item))
            return false;

        return PassSearchFilter(item);
    }

    private bool PassPreviewConfigFilter(UnifiedItem item)
    {
        if (!item.PreviewOnly)
            return true;

        return IsRetainerPreviewItem(item)
            ? config.ShowRetainerPreview
            : config.ShowInventoryPreview;
    }

    private bool PassSourceFilter(UnifiedItem item)
    {
        var saved = GetSaved(item.ItemID);
        return sourceFilter switch
        {
            SourceFilter.PrismBox         => item.InPrismBox,
            SourceFilter.Cabinet          => item.InCabinet,
            SourceFilter.Favorite         => saved != null,
            SourceFilter.InventoryPreview => IsInventoryPreviewItem(item),
            SourceFilter.RetainerPreview  => IsRetainerPreviewItem(item),
            _                             => true
        };
    }

    private bool PassSearchFilter(UnifiedItem item)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var text = searchText.Trim();
        return ContainsIgnoreCase(item.Name, text) ||
               ContainsIgnoreCase(item.ParentSetName, text) ||
               ContainsIgnoreCase(item.SetPartLabel, text) ||
               ContainsIgnoreCase(item.PreviewOwnerName, text) ||
               ContainsIgnoreCase(item.PreviewSourceName, text);
    }

    private bool PassSetRelationFilter(UnifiedItem item)
        => setRelationFilter switch
        {
            SetRelationFilter.SetRelatedOnly => item.IsSetContainer || item.IsSetPart,
            SetRelationFilter.NonSetOnly     => !item.IsSetContainer && !item.IsSetPart,
            _                                => true
        };

    private bool PassJobFilter(UnifiedItem item)
    {
        if (selectedJobFilterIndex <= 0 || selectedJobFilterIndex >= JOB_FILTER_OPTIONS.Length)
            return true;

        var option = JOB_FILTER_OPTIONS[selectedJobFilterIndex];
        if (option.Jobs.Length == 0)
            return true;

        var categoryRow = LuminaGetter.GetRowOrDefault<ClassJobCategory>(item.ClassJobCategoryRowID);
        if (categoryRow.RowId == 0)
            return false;

        return option.Jobs.Any(job => ReadJobCategoryFlag(categoryRow, job));
    }

    #endregion

    #region 投影模板槽位

    private bool CanItemUseInCurrentPlateSlot(UnifiedItem item)
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return true;

        return CanItemUseInPlateSlot(item, agent->Data->SelectedItemIndex);
    }

    private static bool CanItemUseInPlateSlot(UnifiedItem item, uint selectedSlot)
    {
        var categoryRow = LuminaGetter.GetRowOrDefault<EquipSlotCategory>(item.EquipSlotCategoryRowID);
        if (categoryRow.RowId == 0)
            return false;

        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == selectedSlot);
        return definition.Equals(default(PlateSlotDefinition)) || definition.CanEquip(categoryRow);
    }

    private string GetCurrentPlateSlotNameForUI()
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return Lang.Get("UnifiedGlamourManager-PlateNotOpen");

        return GetPlateSlotName(agent->Data->SelectedItemIndex);
    }

    private static string GetPlateSlotName(uint selectedSlot)
    {
        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == selectedSlot);
        return definition.Equals(default(PlateSlotDefinition))
            ? Lang.Get("UnifiedGlamourManager-Slot-Unknown", selectedSlot)
            : Lang.Get(definition.LangKey);
    }

    private static bool IsPlateEditorReady()
    {
        try
        {
            var addon = DService.Instance().GameGUI.GetAddonByName(PLATE_EDITOR_ADDON_NAME);
            return addon.Address != nint.Zero && ((AtkUnitBase*)addon.Address)->IsAddonAndNodesReady();
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 排序

    private IEnumerable<UnifiedItem> ApplySort(IEnumerable<UnifiedItem> source)
        => sortMode switch
        {
            SortMode.NameAsc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenBy(x => x.Name),
            SortMode.NameDesc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => x.Name),
            SortMode.LevelAsc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenBy(x => x.LevelEquip)
                .ThenBy(x => x.Name),
            SortMode.LevelDesc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => x.LevelEquip)
                .ThenBy(x => x.Name),
            _ => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => IsFavorite(x.ItemID))
                .ThenByDescending(x => x.InPrismBox)
                .ThenByDescending(x => x.InCabinet)
                .ThenByDescending(x => x.IsSetPart)
                .ThenBy(x => x.ParentSetName)
                .ThenBy(x => x.Name)
        };

    #endregion

    #region 来源显示

    private static string GetSourceLabel(UnifiedItem item)
    {
        List<string> labels = [];

        if (item.PreviewOnly)
            labels.Add(GetPreviewSourceLabel(item));

        if (item.InPrismBox)
            labels.Add(Lang.Get("UnifiedGlamourManager-PrismBox"));

        if (item.InCabinet)
            labels.Add(Lang.Get("UnifiedGlamourManager-Cabinet"));

        if (item.IsSetPart)
            labels.Add(Lang.Get("UnifiedGlamourManager-SetPart"));

        if (item.IsSetContainer)
            labels.Add(Lang.Get("UnifiedGlamourManager-SetContainer"));

        return labels.Count == 0
            ? Lang.Get("UnifiedGlamourManager-Unknown")
            : string.Join(" / ", labels);
    }

    private static string GetPreviewSourceLabel(UnifiedItem item)
    {
        if (!IsRetainerPreviewItem(item))
            return string.IsNullOrWhiteSpace(item.PreviewSourceName)
                ? Lang.Get("UnifiedGlamourManager-PreviewRecord")
                : item.PreviewSourceName;

        var owner = string.IsNullOrWhiteSpace(item.PreviewOwnerName)
            ? Lang.Get("UnifiedGlamourManager-UnknownRetainer")
            : item.PreviewOwnerName;

        return Lang.Get("UnifiedGlamourManager-RetainerSource", owner);
    }

    private static bool IsInventoryPreviewItem(UnifiedItem item)
        => item.PreviewOnly && !IsRetainerPreviewItem(item);

    private static bool IsRetainerPreviewItem(UnifiedItem item)
    {
        if (!item.PreviewOnly)
            return false;

        if (!string.IsNullOrWhiteSpace(item.PreviewSourceKey) &&
            item.PreviewSourceKey.StartsWith(RETAINER_RECORD_PREFIX, StringComparison.Ordinal))
            return true;

        return string.Equals(
            item.PreviewSourceName,
            Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer"),
            StringComparison.Ordinal);
    }

    #endregion

    #region 工具方法

    private static bool ContainsIgnoreCase(string value, string search)
        => !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool IsEquipSlotEnabled(EquipSlotCategory category, EquipSlotKind slot)
    => slot switch
    {
        EquipSlotKind.MainHand => category.MainHand != 0,
        EquipSlotKind.OffHand  => category.OffHand != 0,
        EquipSlotKind.Head     => category.Head != 0,
        EquipSlotKind.Body     => category.Body != 0,
        EquipSlotKind.Hands    => category.Gloves != 0,
        EquipSlotKind.Legs     => category.Legs != 0,
        EquipSlotKind.Feet     => category.Feet != 0,
        EquipSlotKind.Ears     => category.Ears != 0,
        EquipSlotKind.Neck     => category.Neck != 0,
        EquipSlotKind.Wrists   => category.Wrists != 0,
        EquipSlotKind.Ring     => category.FingerL != 0 || category.FingerR != 0,
        _                      => false
    };

    private static bool ReadJobCategoryFlag(ClassJobCategory category, JobKind job)
        => JobCategoryReaders.TryGetValue(job, out var reader) && reader(category);

    private static readonly FrozenDictionary<JobKind, Func<ClassJobCategory, bool>> JobCategoryReaders =
        Enum.GetValues<JobKind>()
            .Select(job => new
            {
                Job = job,
                Property = typeof(ClassJobCategory).GetProperty(job.ToString())
            })
            .Where(x => x.Property is { PropertyType: not null } && x.Property.PropertyType == typeof(bool))
            .ToFrozenDictionary(
                x => x.Job,
                x =>
                {
                    var property = x.Property!;
                    return new Func<ClassJobCategory, bool>(category => property.GetValue(category) is true);
                });
            
    private static byte SafeByte(uint value)
        => value > byte.MaxValue ? (byte)0 : (byte)value;

    #endregion
}
