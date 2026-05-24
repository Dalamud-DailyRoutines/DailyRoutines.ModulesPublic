using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private readonly List<UnifiedItem>       filteredItems        = [];
    private readonly HashSet<uint>           favoriteItemIDs      = [];
    private readonly Dictionary<ulong, bool> jobFilterCache       = [];
    private readonly Dictionary<ulong, bool> plateSlotFilterCache = [];

    private string            searchText               = string.Empty;
    private SourceFilter      sourceFilter             = SourceFilter.All;
    private SortMode          sortMode                 = SortMode.FavoriteThenNameAsc;
    private SetRelationFilter setRelationFilter        = SetRelationFilter.All;
    private bool              filterByCurrentPlateSlot = true;
    private bool              enableLevelFilter;
    private int               minEquipLevel = DEFAULT_MIN_EQUIP_LEVEL;
    private int               maxEquipLevel = DEFAULT_MAX_EQUIP_LEVEL;
    private int               selectedJobFilterIndex;
    private bool              requestClearFavoritesConfirm;
    private bool              filteredItemsDirty  = true;
    private uint              lastFilterPlateSlot = uint.MaxValue;
    private UnifiedItem?      selectedItem;

    private bool PassFilter(UnifiedItem item)
    {
        if (filterByCurrentPlateSlot && !CanItemUseInCurrentPlateSlot(item)) return false;
        if (enableLevelFilter        && (item.LevelEquip < minEquipLevel || item.LevelEquip > maxEquipLevel)) return false;

        switch (sourceFilter)
        {
            case SourceFilter.PrismBox when !item.InPrismBox:
            case SourceFilter.Cabinet when !item.InCabinet:
            case SourceFilter.Favorite when !IsFavorite(item.ItemID):
                return false;
        }

        switch (setRelationFilter)
        {
            case SetRelationFilter.SetRelatedOnly when item is { IsSetContainer: false, IsSetPart: false }:
            case SetRelationFilter.NonSetOnly when item.IsSetContainer || item.IsSetPart:
                return false;
        }

        if (!PassJobFilter(item)) return false;
        if (string.IsNullOrWhiteSpace(searchText)) return true;

        var text = searchText.Trim();
        return item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)          ||
               item.ParentSetName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               item.SetPartLabel.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private bool PassJobFilter(UnifiedItem item)
    {
        if (selectedJobFilterIndex <= 0 || selectedJobFilterIndex >= JobFilterClassJobIDs.Length) return true;

        var cacheKey = ((ulong)(uint)selectedJobFilterIndex << 32) | item.ClassJobCategoryRowID;
        if (jobFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var classJobIDs = JobFilterClassJobIDs[selectedJobFilterIndex];
        var result = item.IsCompatibleWithJobs(classJobIDs);
        jobFilterCache[cacheKey] = result;
        return result;
    }

    private bool CanItemUseInCurrentPlateSlot(UnifiedItem item)
    {
        if (!TryGetReadyPlateEditor(out var agent)) return true;

        var selectedSlot = agent->Data->SelectedItemIndex;
        var cacheKey     = ((ulong)selectedSlot << 32) | item.EquipSlotCategoryRowID;
        if (plateSlotFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = item.IsCompatibleWithSlot(selectedSlot);
        plateSlotFilterCache[cacheKey] = result;
        return result;
    }

    private IEnumerable<UnifiedItem> ApplySort(IEnumerable<UnifiedItem> source) =>
        sortMode switch
        {
            SortMode.NameAsc  => source.OrderBy(x => x.Name),
            SortMode.NameDesc => source.OrderByDescending(x => x.Name),
            SortMode.LevelAsc => source
                                 .OrderBy(x => x.LevelEquip)
                                 .ThenBy(x => x.Name),
            SortMode.LevelDesc => source
                                  .OrderByDescending(x => x.LevelEquip)
                                  .ThenBy(x => x.Name),
            _ => source
                 .OrderByDescending(x => IsFavorite(x.ItemID))
                 .ThenByDescending(x => x.InPrismBox)
                 .ThenByDescending(x => x.InCabinet)
                 .ThenByDescending(x => x.IsSetPart)
                 .ThenBy(x => x.ParentSetName)
                 .ThenBy(x => x.Name)
        };

    private void UpdateCurrentSlotFilterCache()
    {
        if (!filterByCurrentPlateSlot) return;

        var slot = TryGetReadyPlateEditor(out var agent)
                       ? agent->Data->SelectedItemIndex
                       : uint.MaxValue;
        if (slot == lastFilterPlateSlot) return;

        lastFilterPlateSlot = slot;
        plateSlotFilterCache.Clear();
        MarkFilteredItemsDirty();
    }

    private void EnsureFilteredItems()
    {
        UpdateCurrentSlotFilterCache();
        if (!filteredItemsDirty) return;

        filteredItems.Clear();
        filteredItems.AddRange(ApplySort(items.Where(PassFilter)));
        filteredItemsDirty = false;
    }

    private void MarkFilteredItemsDirty(bool clearJobCache = false, bool clearPlateSlotCache = false)
    {
        filteredItemsDirty = true;
        if (clearJobCache) jobFilterCache.Clear();
        if (clearPlateSlotCache) plateSlotFilterCache.Clear();
    }

    private void ResetFilters()
    {
        sourceFilter             = SourceFilter.All;
        sortMode                 = SortMode.FavoriteThenNameAsc;
        setRelationFilter        = SetRelationFilter.All;
        enableLevelFilter        = false;
        minEquipLevel            = DEFAULT_MIN_EQUIP_LEVEL;
        maxEquipLevel            = DEFAULT_MAX_EQUIP_LEVEL;
        selectedJobFilterIndex   = 0;
        searchText               = string.Empty;
        filterByCurrentPlateSlot = true;
        MarkFilteredItemsDirty(true, true);
    }
}
