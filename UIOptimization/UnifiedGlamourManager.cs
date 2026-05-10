using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ImageHelper = OmenTools.OmenService.ImageHelper;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager : ModuleBase
{
    #region 模块

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private bool isOpen;
    private bool isPrismBoxOpen;
    private bool requestFocusNextOpen;
    private bool autoOpenedByPlateEditor;
    private string searchText = string.Empty;
    private SourceFilter sourceFilter = SourceFilter.All;
    private SortMode sortMode = SortMode.FavoriteThenNameAsc;
    private SetRelationFilter setRelationFilter = SetRelationFilter.All;
    private bool filterByCurrentPlateSlot = true;
    private bool enableLevelFilter;
    private int minEquipLevel = DEFAULT_MIN_EQUIP_LEVEL;
    private int maxEquipLevel = DEFAULT_MAX_EQUIP_LEVEL;
    private int selectedJobFilterIndex;
    private readonly List<UnifiedItem> items = [];
    private readonly List<UnifiedItem> filteredItems = [];
    private readonly HashSet<uint> favoriteItemIDs = [];
    private readonly Dictionary<ulong, bool> jobFilterCache = [];
    private readonly Dictionary<ulong, bool> plateSlotFilterCache = [];
    private bool useGridView = true;
    private int prismBoxItemCount;
    private int cabinetItemCount;
    private UnifiedItem? selectedItem;
    private bool requestClearFavoritesConfirm;
    private bool filteredItemsDirty = true;
    private bool isRefreshingItems;
    private uint lastFilterPlateSlot = uint.MaxValue;
    private int cachedLoadedFavoriteCount;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };

        Overlay = new(this)
        {
            IsOpen = false
        };

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, PRISM_BOX_ADDON_NAME, OnPrismBoxAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PRISM_BOX_ADDON_NAME, OnPrismBoxAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
    }

    protected override void Uninit()
    {
        CloseWindow();
        TaskHelper?.Abort();
        DService.Instance().AddonLifecycle.UnregisterListener(OnPrismBoxAddon, OnPlateEditorAddon);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Open")))
            OpenWindow(false);

        ImGui.SameLine();

        using (ImRaii.Disabled(isRefreshingItems))
        {
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-Refresh")))
                StartRefreshAll();
        }

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LoadedStatus", items.Count, GetLoadedFavoriteCount()));
    }

    protected override void OverlayPreDraw()
    {
        if (isOpen && autoOpenedByPlateEditor && !IsPlateEditorReady())
            CloseWindow();

        if (Overlay != null && Overlay.IsOpen != isOpen)
            Overlay.IsOpen = isOpen;
    }

    protected override void OverlayUI()
    {
        if (isOpen)
            DrawWindow();
    }

    private void OnPrismBoxAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                isPrismBoxOpen = true;
                break;

            case AddonEvent.PreFinalize:
                isPrismBoxOpen = false;
                if (autoOpenedByPlateEditor)
                    CloseWindow();
                break;
        }
    }

    private void OnPlateEditorAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup when isPrismBoxOpen:
                OpenWindow(true);
                break;

            case AddonEvent.PreFinalize:
                if (autoOpenedByPlateEditor)
                    CloseWindow();
                break;
        }
    }

    private void OpenWindow(bool openedByPlateEditor)
    {
        isOpen = true;
        autoOpenedByPlateEditor = openedByPlateEditor;
        requestFocusNextOpen = true;

        if (Overlay != null)
            Overlay.IsOpen = true;

        StartRefreshAll();
    }

    private void CloseWindow()
    {
        isOpen = false;
        autoOpenedByPlateEditor = false;

        if (Overlay != null)
            Overlay.IsOpen = false;
    }

    private static bool TryGetLoadedMirageManager(out MirageManager* manager)
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    #endregion

    #region 数据

    private sealed class Config : ModuleConfig
    {
        public List<SavedItem> Favorites = [];
    }

    private void SaveConfig()
    {
        NormalizeConfig();
        config.Save(this);
        RefreshFavoriteCountCache();
        MarkFilteredItemsDirty();
    }

    private void NormalizeConfig()
    {
        config.Favorites ??= [];
        SyncFavoriteItemIDs();
    }

    private int GetLoadedFavoriteCount() => cachedLoadedFavoriteCount;

    private void RefreshFavoriteCountCache() =>
        cachedLoadedFavoriteCount = favoriteItemIDs.Count == 0 || items.Count == 0
            ? 0
            : items.Where(x => IsFavorite(x.ItemID)).Select(x => x.ItemID).Distinct().Count();

    private bool IsFavorite(uint itemID) =>
        favoriteItemIDs.Contains(itemID);

    private void ToggleFavorite(UnifiedItem item)
    {
        var existing = config.Favorites.FirstOrDefault(x => x.ItemID == item.ItemID);
        if (existing != null)
        {
            config.Favorites.Remove(existing);
        }
        else
        {
            config.Favorites.Add(new()
            {
                ItemID  = item.ItemID,
                Name    = item.Name,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        SaveConfig();
    }

    private void SyncFavoriteItemIDs()
    {
        favoriteItemIDs.Clear();

        foreach (var favorite in config.Favorites)
        {
            if (favorite.ItemID != 0)
                favoriteItemIDs.Add(favorite.ItemID);
        }
    }

    private void StartRefreshAll(UnifiedItem? reselectItem = null)
    {
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };
        TaskHelper.Abort();

        isRefreshingItems = true;
        items.Clear();
        filteredItems.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount = 0;
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);

        TaskHelper.Enqueue(LoadPrismBoxItems, nameof(LoadPrismBoxItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(LoadCabinetItems, nameof(LoadCabinetItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(
            () =>
            {
                MergeItems();
                RefreshFavoriteCountCache();

                if (reselectItem != null)
                    selectedItem = items.FirstOrDefault(x => IsSameSelectableItem(x, reselectItem));

                isRefreshingItems = false;
                MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
            },
            nameof(MergeItems));
    }

    private void LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager)) return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0) continue;

            var itemID = ItemUtil.GetBaseId(rawItemID).ItemId;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem(
                itemRow.Value,
                name,
                ItemSource.PrismBox,
                itemID,
                rawItemID,
                i,
                cabinetID: 0,
                stain0ID: manager->PrismBoxStain0Ids[(int)i],
                stain1ID: manager->PrismBoxStain1Ids[(int)i],
                manager);

            count++;
        }

        prismBoxItemCount = count;
    }

    private void LoadCabinetItems()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null) return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetID)) continue;

            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem(itemRow.Value, name, ItemSource.Cabinet, itemID, itemID, prismBoxIndex: 0, cabinetID);
            count++;
        }

        cabinetItemCount = count;
    }

    private void AddStoredItem(
        ItemSheet item,
        string name,
        ItemSource source,
        uint itemID,
        uint rawItemID,
        uint prismBoxIndex,
        uint cabinetID,
        uint stain0ID = 0,
        uint stain1ID = 0,
        MirageManager* manager = null)
    {
        var setParts = GetSetParts(itemID);

        items.Add(CreateUnifiedItem(
            itemID,
            rawItemID,
            name,
            item,
            source,
            prismBoxIndex,
            cabinetID,
            stain0ID,
            stain1ID,
            isSetContainer: setParts.Count > 0));

        foreach (var setPart in setParts)
        {
            if (source != ItemSource.PrismBox || manager->IsSetSlotUnlocked(prismBoxIndex, setPart.SlotIndex))
                AddSetPartItem(setPart, rawItemID, prismBoxIndex, cabinetID, itemID, name, source);
        }
    }

    private static GameCabinet* GetLoadedCabinet()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return null;

        var cabinet = &uiState->Cabinet;
        return cabinet->IsCabinetLoaded() ? cabinet : null;
    }

    private static List<SetPartInfo> GetSetParts(uint setItemID)
    {
        if (!LuminaGetter.TryGetRow<MirageStoreSetItem>(setItemID, out var row)) return [];

        List<SetPartInfo> parts = [];
        AddSetPart(parts, row.MainHand.RowId, 0);
        AddSetPart(parts, row.OffHand.RowId, 1);
        AddSetPart(parts, row.Head.RowId, 2);
        AddSetPart(parts, row.Body.RowId, 3);
        AddSetPart(parts, row.Hands.RowId, 4);
        AddSetPart(parts, row.Legs.RowId, 5);
        AddSetPart(parts, row.Feet.RowId, 6);
        AddSetPart(parts, row.Earrings.RowId, 7);
        AddSetPart(parts, row.Necklace.RowId, 8);
        AddSetPart(parts, row.Bracelets.RowId, 9);
        AddSetPart(parts, row.Ring.RowId, 10);
        return parts;
    }

    private static void AddSetPart(List<SetPartInfo> parts, uint itemID, int slotIndex)
    {
        if (itemID == 0) return;

        if (!LuminaGetter.TryGetRow<ItemSheet>(itemID, out var itemRow) ||
            !TryGetItemName(itemRow, out _) ||
            !IsEquipSlotCategoryCompatibleWithPlateSlot(itemRow.EquipSlotCategory.Value, (uint)slotIndex))
            return;

        var label = GetNativeItemCategoryName(itemRow);
        if (!parts.Any(x => x.ItemID == itemID && x.PartLabel == label))
            parts.Add(new(itemID, label, slotIndex));
    }

    private void AddSetPartItem(
        SetPartInfo setPart,
        uint rawItemID,
        uint prismBoxIndex,
        uint cabinetID,
        uint parentItemID,
        string parentName,
        ItemSource source)
    {
        if (!LuminaGetter.TryGetRow<ItemSheet>(setPart.ItemID, out var partRow) ||
            !TryGetItemName(partRow, out var partName))
            return;

        items.Add(CreateUnifiedItem(
            setPart.ItemID,
            rawItemID,
            $"{partName} ({parentName} / {setPart.PartLabel})",
            partRow,
            source,
            prismBoxIndex,
            cabinetID,
            stain0ID: 0,
            stain1ID: 0,
            isSetContainer: false,
            isSetPart: true,
            parentSetItemID: parentItemID,
            parentSetName: parentName,
            setPartLabel: setPart.PartLabel));
    }

    private static string GetNativeItemCategoryName(ItemSheet item)
    {
        var categoryName = item.ItemUICategory.Value.Name.ExtractText();
        return string.IsNullOrWhiteSpace(categoryName)
            ? item.Name.ExtractText()
            : categoryName;
    }

    private void MergeItems()
    {
        var merged = items
            .GroupBy(static item => item.IsSetPart
                ? $"set:{item.ParentSetItemID}:{item.PrismBoxIndex}:{item.ItemID}"
                : $"item:{item.ItemID}")
            .Select(MergeItemGroup)
            .ToList();

        items.Clear();
        items.AddRange(merged);
    }

    private static UnifiedItem MergeItemGroup(IGrouping<string, UnifiedItem> group)
    {
        var first = group.First();
        if (first.IsSetPart) return first;

        var prism = group.FirstOrDefault(x => x.InPrismBox);
        var cabinet = group.FirstOrDefault(x => x.InCabinet);

        first.InPrismBox = prism != null;
        first.InCabinet = cabinet != null;

        if (prism != null)
        {
            first.RawItemID = prism.RawItemID;
            first.PrismBoxIndex = prism.PrismBoxIndex;
            first.Stain0ID = prism.Stain0ID;
            first.Stain1ID = prism.Stain1ID;
            first.IconID = prism.IconID;
            first.EquipSlotCategoryRowID = prism.EquipSlotCategoryRowID;
            first.ClassJobCategoryRowID = prism.ClassJobCategoryRowID;
            first.LevelEquip = prism.LevelEquip;
            first.IsSetContainer = prism.IsSetContainer;
        }

        if (cabinet != null)
        {
            first.CabinetID = cabinet.CabinetID;
            first.IconID = first.IconID == 0 ? cabinet.IconID : first.IconID;
            first.EquipSlotCategoryRowID = first.EquipSlotCategoryRowID == 0 ? cabinet.EquipSlotCategoryRowID : first.EquipSlotCategoryRowID;
            first.ClassJobCategoryRowID = first.ClassJobCategoryRowID == 0 ? cabinet.ClassJobCategoryRowID : first.ClassJobCategoryRowID;
            first.LevelEquip = first.LevelEquip == 0 ? cabinet.LevelEquip : first.LevelEquip;
            first.IsSetContainer |= cabinet.IsSetContainer;
        }

        return first;
    }

    private static UnifiedItem CreateUnifiedItem(
        uint itemID,
        uint rawItemID,
        string name,
        ItemSheet row,
        ItemSource source,
        uint prismBoxIndex,
        uint cabinetID,
        uint stain0ID,
        uint stain1ID,
        bool isSetContainer,
        bool isSetPart = false,
        uint parentSetItemID = 0,
        string parentSetName = "",
        string setPartLabel = "") =>
        new()
        {
            ItemID                    = itemID,
            RawItemID                 = rawItemID,
            PrismBoxIndex             = prismBoxIndex,
            CabinetID                 = cabinetID,
            Name                      = name,
            Stain0ID                  = stain0ID,
            Stain1ID                  = stain1ID,
            EquipSlotCategoryRowID    = row.EquipSlotCategory.RowId,
            ClassJobCategoryRowID     = row.ClassJobCategory.RowId,
            IconID                    = row.Icon,
            LevelEquip                = (uint)row.LevelEquip,
            InPrismBox                = source == ItemSource.PrismBox,
            InCabinet                 = source == ItemSource.Cabinet,
            IsSetContainer            = isSetContainer,
            IsSetPart                 = isSetPart,
            ParentSetItemID           = parentSetItemID,
            ParentSetName             = parentSetName,
            SetPartLabel              = setPartLabel
        };

    private static bool TryGetItemName(ItemSheet item, out string name)
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    private void ApplySelectedItemToCurrentPlateSlot(UnifiedItem item)
    {
        if (!item.CanUseInPlate || !IsPlateEditorReady()) return;

        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null) return;

        var selectedSlot = agent->Data->SelectedItemIndex;
        if (filterByCurrentPlateSlot && !CanItemUseInPlateSlot(item, selectedSlot)) return;

        try
        {
            if (item.InPrismBox)
                ApplyPrismBoxItem(agent, item);
            else if (item.InCabinet)
                ApplyCabinetItem(agent, item);
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
        }
    }

    private void ApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (TryApplyPrismBoxItem(agent, item))
            QueueApplyRetry(item, ItemSource.PrismBox);
    }

    private bool TryApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (!TryGetLoadedMirageManager(out var manager)) return false;
        if (item.PrismBoxIndex >= PRISM_BOX_CAPACITY) return false;

        var rawItemID = manager->PrismBoxItemIds[(int)item.PrismBoxIndex];
        if (rawItemID == 0) return false;

        var expectedItemID = item is { IsSetPart: true, ParentSetItemID: not 0 }
            ? item.ParentSetItemID
            : item.ItemID;
        if (ItemUtil.GetBaseId(rawItemID).ItemId != expectedItemID) return false;

        var itemID = item.IsSetPart
            ? item.ItemID
            : item.RawItemID != 0
                ? item.RawItemID
                : item.ItemID;
        var stain0 = item.IsSetPart ? (byte)0 : (byte)item.Stain0ID;
        var stain1 = item.IsSetPart ? (byte)0 : (byte)item.Stain1ID;

        agent->SetSelectedItemData(ItemSource.PrismBox, item.PrismBoxIndex, itemID, stain0, stain1);
        MarkPlateSelectionDirty(agent);
        return true;
    }

    private void ApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (TryApplyCabinetItem(agent, item))
            QueueApplyRetry(item, ItemSource.Cabinet);
    }

    private bool TryApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null) return false;
        if (item.CabinetID == 0 || !cabinet->IsItemInCabinet(item.CabinetID)) return false;

        agent->SetSelectedItemData(ItemSource.Cabinet, item.CabinetID, item.ItemID, 0, 0);
        MarkPlateSelectionDirty(agent);
        return true;
    }

    private void QueueApplyRetry(UnifiedItem item, ItemSource source)
    {
        var itemID = item.ItemID;
        var prismBoxIndex = item.PrismBoxIndex;
        var cabinetID = item.CabinetID;
        var isSetPart = item.IsSetPart;
        var parentSetItemID = item.ParentSetItemID;

        DService.Instance().Framework.RunOnTick(
            () =>
            {
                var retryAgent = AgentMiragePrismMiragePlate.Instance();
                if (retryAgent == null || retryAgent->Data == null) return;

                var retryItem = items.FirstOrDefault(x =>
                    x.ItemID == itemID &&
                    x.IsSetPart == isSetPart &&
                    x.ParentSetItemID == parentSetItemID &&
                    x.CanUseInPlate &&
                    (source == ItemSource.PrismBox
                        ? x.InPrismBox && x.PrismBoxIndex == prismBoxIndex
                        : x.InCabinet && x.CabinetID == cabinetID));

                if (retryItem == null) return;

                if (source == ItemSource.PrismBox)
                    TryApplyPrismBoxItem(retryAgent, retryItem);
                else
                    TryApplyCabinetItem(retryAgent, retryItem);
            },
            TimeSpan.FromMilliseconds(APPLY_RETRY_DELAY_MS));
    }

    private static void MarkPlateSelectionDirty(AgentMiragePrismMiragePlate* agent)
    {
        if (agent == null || agent->Data == null) return;

        agent->Data->HasChanges = true;
        agent->CharaView.IsUpdatePending = true;
    }

    #endregion

    #region 筛选

    private bool PassFilter(UnifiedItem item)
    {
        if (filterByCurrentPlateSlot && !CanItemUseInCurrentPlateSlot(item)) return false;
        if (enableLevelFilter && (item.LevelEquip < minEquipLevel || item.LevelEquip > maxEquipLevel)) return false;
        if (sourceFilter == SourceFilter.PrismBox && !item.InPrismBox) return false;
        if (sourceFilter == SourceFilter.Cabinet && !item.InCabinet) return false;
        if (sourceFilter == SourceFilter.Favorite && !IsFavorite(item.ItemID)) return false;
        if (setRelationFilter == SetRelationFilter.SetRelatedOnly && !item.IsSetContainer && !item.IsSetPart) return false;
        if (setRelationFilter == SetRelationFilter.NonSetOnly && (item.IsSetContainer || item.IsSetPart)) return false;
        if (!PassJobFilter(item)) return false;
        if (string.IsNullOrWhiteSpace(searchText)) return true;

        var text = searchText.Trim();
        return item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               item.ParentSetName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               item.SetPartLabel.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private bool PassJobFilter(UnifiedItem item)
    {
        if (selectedJobFilterIndex <= 0 || selectedJobFilterIndex >= JobFilterClassJobIDs.Length) return true;

        var cacheKey = ((ulong)(uint)selectedJobFilterIndex << 32) | item.ClassJobCategoryRowID;
        if (jobFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var classJobIDs = JobFilterClassJobIDs[selectedJobFilterIndex];
        var result = classJobIDs.Length == 0 ||
                     classJobIDs.Any(id => ClassJobCategory.IsClassJobInCategory(id, item.ClassJobCategoryRowID));
        jobFilterCache[cacheKey] = result;
        return result;
    }

    private bool CanItemUseInCurrentPlateSlot(UnifiedItem item)
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null) return true;

        var selectedSlot = agent->Data->SelectedItemIndex;
        var cacheKey = ((ulong)selectedSlot << 32) | item.EquipSlotCategoryRowID;
        if (plateSlotFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = CanItemUseInPlateSlot(item, selectedSlot);
        plateSlotFilterCache[cacheKey] = result;
        return result;
    }

    private static uint GetCurrentPlateSlotIndex()
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        return agent == null || agent->Data == null
            ? uint.MaxValue
            : agent->Data->SelectedItemIndex;
    }

    private static bool CanItemUseInPlateSlot(UnifiedItem item, uint selectedSlot)
    {
        if (!LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategoryRowID, out var categoryRow)) return false;
        return selectedSlot >= PlateSlotDefinitions.Length || IsEquipSlotCategoryCompatibleWithPlateSlot(categoryRow, selectedSlot);
    }

    private string GetCurrentPlateSlotNameForUI()
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        return agent == null || agent->Data == null
            ? Lang.Get("UnifiedGlamourManager-PlateNotOpen")
            : GetPlateSlotName(agent->Data->SelectedItemIndex);
    }

    private static string GetPlateSlotName(uint selectedSlot) =>
        selectedSlot < PlateSlotDefinitions.Length
            ? Lang.Get(PlateSlotDefinitions[selectedSlot].LangKey)
            : Lang.Get("UnifiedGlamourManager-Slot-Unknown", selectedSlot);

    private static bool IsPlateEditorReady() =>
        MiragePrismMiragePlate->IsAddonAndNodesReady();

    private IEnumerable<UnifiedItem> ApplySort(IEnumerable<UnifiedItem> source) =>
        sortMode switch
        {
            SortMode.NameAsc => source.OrderBy(x => x.Name),
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

    private static string GetSourceLabel(UnifiedItem item)
    {
        List<string> labels = [];

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

    #endregion

    #region 界面

    private void DrawWindow()
    {
        if (!isOpen) return;

        ImGui.SetNextWindowSize(new(WINDOW_DEFAULT_WIDTH, WINDOW_DEFAULT_HEIGHT), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new(WINDOW_MIN_WIDTH, WINDOW_MIN_HEIGHT),
            new(WINDOW_MAX_SIZE, WINDOW_MAX_SIZE));

        if (requestFocusNextOpen)
        {
            ImGui.SetNextWindowFocus();
            requestFocusNextOpen = false;
        }

        using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y))
                                   .Push(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f))
                                   .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f))
                                   .Push(ImGuiStyleVar.ChildRounding, 8f)
                                   .Push(ImGuiStyleVar.FrameRounding, 6f)
                                   .Push(ImGuiStyleVar.GrabRounding, 6f)
                                   .Push(ImGuiStyleVar.ChildBorderSize, 0f)
                                   .Push(ImGuiStyleVar.CellPadding, new Vector2(8f, 6f));
        using var colors = ImRaii.PushColor(ImGuiCol.WindowBg, WINDOW_BG_COLOR)
                                   .Push(ImGuiCol.ChildBg, PANEL_BG_COLOR)
                                   .Push(ImGuiCol.PopupBg, POPUP_BG_COLOR)
                                   .Push(ImGuiCol.Border, BUTTON_ACCENT_COLOR)
                                   .Push(ImGuiCol.Button, BUTTON_ACCENT_COLOR)
                                   .Push(ImGuiCol.ButtonHovered, BUTTON_HOVERED_COLOR)
                                   .Push(ImGuiCol.ButtonActive, BUTTON_ACTIVE_COLOR)
                                   .Push(ImGuiCol.FrameBg, FRAME_BG_COLOR)
                                   .Push(ImGuiCol.FrameBgHovered, FRAME_BG_HOVERED_COLOR)
                                   .Push(ImGuiCol.FrameBgActive, FRAME_BG_ACTIVE_COLOR)
                                   .Push(ImGuiCol.Header, BUTTON_ACCENT_COLOR)
                                   .Push(ImGuiCol.HeaderHovered, BUTTON_HOVERED_COLOR)
                                   .Push(ImGuiCol.HeaderActive, BUTTON_ACTIVE_COLOR)
                                   .Push(ImGuiCol.CheckMark, GOLD_COLOR);

        if (ImGui.Begin($"{Lang.Get("UnifiedGlamourManager-Title")}###UnifiedGlamourManager", ref isOpen, ImGuiWindowFlags.NoScrollbar))
        {
            DrawTopBar();
            DrawMainLayout();
            DrawConfirmPopups();
        }

        ImGui.End();
    }

    private static void SectionTitle(string text)
    {
        ImGui.TextColored(TITLE_COLOR, text);
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

        var isOpenPopup = true;
        using var popup = ImRaii.PopupModal(
            $"{Lang.Get("UnifiedGlamourManager-ClearFavoritesConfirmTitle")}###ClearFavoritesConfirm",
            ref isOpenPopup,
            ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

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
            ImGui.CloseCurrentPopup();
    }

    private void DrawTopBar()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, ITEM_SPACING_Y));
        using var child = ImRaii.Child("##TopBar", new Vector2(0f, TOP_BAR_HEIGHT), true, ImGuiWindowFlags.NoScrollbar);
        if (!child) return;

        ImGui.TextColored(TITLE_COLOR, Lang.Get("UnifiedGlamourManager-Title"));
        ImGui.SameLine();
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Subtitle"));
        ImGui.SameLine();
        ImGui.TextColored(SOFT_ACCENT_COLOR, Lang.Get("UnifiedGlamourManager-Stat", prismBoxItemCount, cabinetItemCount, items.Count));

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ReadRefresh"), new(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
            StartRefreshAll();

        ImGui.SameLine();

        var searchWidth = Math.Clamp(ImGui.GetContentRegionAvail().X - TOP_BAR_BUTTON_WIDTH - 60f, SEARCH_MIN_WIDTH, SEARCH_MAX_WIDTH);
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##Search", Lang.Get("UnifiedGlamourManager-SearchHint"), ref searchText, SEARCH_INPUT_MAX_LENGTH))
            MarkFilteredItemsDirty();

        ImGui.SameLine();

        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-CurrentSlotOnly"), ref filterByCurrentPlateSlot))
            MarkFilteredItemsDirty(clearPlateSlotCache: true);

        ImGui.SameLine(0f, 12f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-FavoriteCount"));
        ImGui.SameLine(0f, 4f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(GOLD_COLOR, GetLoadedFavoriteCount().ToString());
        ImGui.SameLine(0f, 10f);

        var clearFavoritesText = Lang.Get("UnifiedGlamourManager-ClearFavorites");
        var clearButtonWidth = MathF.Max(TOP_BAR_CLEAR_BUTTON_WIDTH, ImGui.CalcTextSize(clearFavoritesText).X + 24f);
        using (ImRaii.Disabled(config.Favorites.Count == 0))
        {
            if (ImGui.Button(clearFavoritesText, new(clearButtonWidth, CONTROL_HEIGHT)))
                requestClearFavoritesConfirm = true;
        }
    }

    private void DrawMainLayout()
    {
        var mainHeight = MathF.Max(ImGui.GetContentRegionAvail().Y, MAIN_LAYOUT_MIN_HEIGHT);
        var tableFlags = ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.SizingStretchProp;

        using var table = ImRaii.Table("##UnifiedMainTable", 3, tableFlags, new(0f, mainHeight));
        if (!table) return;

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
        if (!child) return;

        SectionTitle(Lang.Get("UnifiedGlamourManager-FilterSection"));
        DrawSourceFilter();
        DrawSortFilter();
        DrawLevelFilter();
        DrawJobFilter();
        DrawSetRelationFilter();
        DrawResetFilterButton();

        SectionTitle(Lang.Get("UnifiedGlamourManager-Usage"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-CabinetAndPrism"));
        ImGui.BulletText(Lang.Get("UnifiedGlamourManager-Usage-Apply"));
    }

    private void DrawSourceFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Source"));

        var width = ImGui.GetContentRegionAvail().X;
        var buttonSize = new Vector2((width - 6f) * 0.5f, CONTROL_HEIGHT);

        for (var i = 0; i < SourceFilters.Length; i++)
        {
            if (i % 2 == 1)
                ImGui.SameLine();

            var (filter, langKey) = SourceFilters[i];
            using (ImRaii.PushColor(ImGuiCol.Button, BUTTON_ACTIVE_COLOR, sourceFilter == filter))
            {
                if (ImGui.Button($"{Lang.Get(langKey)}##Source{filter}", buttonSize))
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
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Sort"));

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
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-EquipLevel"));
        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-EnableLevelRange"), ref enableLevelFilter))
            MarkFilteredItemsDirty();

        using (ImRaii.Disabled(!enableLevelFilter))
        {
            var inputWidth = (ImGui.GetContentRegionAvail().X - 10f) * 0.5f;
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

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LevelRange", minEquipLevel, maxEquipLevel));
        ImGui.Spacing();
    }

    private void DrawJobFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Job"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##JobFilter", ref selectedJobFilterIndex, JobFilterNames, JobFilterNames.Length))
            MarkFilteredItemsDirty(clearJobCache: true);

        ImGui.Spacing();
    }

    private void DrawSetRelationFilter()
    {
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SetDisplay"));

        var setIndex = Math.Clamp((int)setRelationFilter, 0, SetRelationFilterNames.Length - 1);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##SetRelationFilter", ref setIndex, SetRelationFilterNames, SetRelationFilterNames.Length))
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

    private void DrawItemList()
    {
        EnsureFilteredItems();

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var listPanel = ImRaii.Child("##ListPanel", new Vector2(0f, 0f), true);
        if (!listPanel) return;

        ImGui.TextColored(TITLE_COLOR, Lang.Get("UnifiedGlamourManager-ItemList"));
        ImGui.SameLine();
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ResultCount", filteredItems.Count));
        ImGui.SameLine();
        ImGui.TextColored(
            IsPlateEditorReady() ? SOFT_ACCENT_COLOR : ERROR_COLOR,
            Lang.Get("UnifiedGlamourManager-CurrentSlotValue", GetCurrentPlateSlotNameForUI()));

        var x = ImGui.GetContentRegionAvail().X - VIEW_MODE_BUTTON_WIDTH * 2f - GRID_ICON_PADDING;
        if (x > 0f)
            ImGui.SameLine(x);

        DrawViewModeButton(Lang.Get("UnifiedGlamourManager-ListView") + "##ViewList", !useGridView, () => useGridView = false);
        ImGui.SameLine();
        DrawViewModeButton(Lang.Get("UnifiedGlamourManager-GridView") + "##ViewGrid", useGridView, () => useGridView = true);
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
        if (!itemList) return;

        if (useGridView)
            DrawItemGrid(filteredItems);
        else
            DrawItemCardsVirtualized(filteredItems);
    }

    private static void DrawViewModeButton(string label, bool active, System.Action onClick)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, BUTTON_ACCENT_COLOR, active);

        if (ImGui.Button(label, new Vector2(VIEW_MODE_BUTTON_WIDTH, VIEW_MODE_BUTTON_HEIGHT)))
            onClick();
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

        if (clearJobCache)
            jobFilterCache.Clear();

        if (clearPlateSlotCache)
            plateSlotFilterCache.Clear();
    }

    private void UpdateCurrentSlotFilterCache()
    {
        if (!filterByCurrentPlateSlot) return;

        var slot = GetCurrentPlateSlotIndex();
        if (slot == lastFilterPlateSlot) return;

        lastFilterPlateSlot = slot;
        plateSlotFilterCache.Clear();
        MarkFilteredItemsDirty();
    }

    private void DrawItemCardsVirtualized(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0) return;

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

        using var id = ImRaii.PushId($"{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}");
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, NORMAL_CARD_COLOR);
        using var border = ImRaii.PushColor(ImGuiCol.Border, GetCardBorderColor(selected, favorite));
        using var child = ImRaii.Child("##ItemCard", new Vector2(cardWidth, CARD_MIN_HEIGHT), true, ImGuiWindowFlags.NoScrollbar);
        if (!child) return;

        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        DrawItemBackground(drawList, min, max, selected, favorite, hovered, CARD_ROUNDING);

        ImGui.SetCursorPos(new Vector2(12f, 12f));
        DrawFavoriteButton(item, favorite);
        ImGui.SameLine(0f, 10f);
        DrawItemIcon(item.IconID, ICON_SIZE_LIST);
        ImGui.SameLine(0f, 10f);
        DrawItemCardInfo(item, selected, favorite);

        if (hovered && !ImGui.IsAnyItemActive())
            HandleItemClick(item);

        ImGui.Dummy(new Vector2(0f, ITEM_SPACING_Y));
    }

    private static Vector4 GetCardBackgroundColor(bool selected, bool favorite, bool hovered)
    {
        if (favorite)
            return hovered ? FAVORITE_CARD_HOVER_COLOR : FAVORITE_CARD_COLOR;

        if (selected)
            return SELECTED_COLOR;

        return hovered ? NORMAL_CARD_HOVER_COLOR : NORMAL_CARD_COLOR;
    }

    private static Vector4 GetCardBorderColor(bool selected, bool favorite) =>
        selected
            ? SELECTED_BORDER_COLOR
            : favorite
                ? GOLD_COLOR
                : MUTED_BORDER_COLOR;

    private void DrawFavoriteButton(UnifiedItem item, bool favorite)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, NORMAL_CARD_COLOR)
                                  .Push(ImGuiCol.ButtonHovered, FRAME_BG_COLOR)
                                  .Push(ImGuiCol.ButtonActive, BUTTON_ACCENT_COLOR)
                                  .Push(ImGuiCol.Text, favorite ? GOLD_COLOR : STAR_OFF_COLOR);

        if (ImGui.Button(favorite ? FAVORITE_ICON_ON : FAVORITE_ICON_OFF, new Vector2(CONTROL_HEIGHT, ICON_SIZE_LIST)))
            ToggleFavorite(item);
    }

    private void DrawItemCardInfo(UnifiedItem item, bool selected, bool favorite)
    {
        using var group = ImRaii.Group();

        var titleColor = selected
            ? SELECTED_BORDER_COLOR
            : favorite
                ? KnownColor.White.ToVector4()
                : item.IsSetPart
                    ? SOFT_ACCENT_COLOR
                    : KnownColor.White.ToVector4();

        using (ImRaii.PushColor(ImGuiCol.Text, titleColor))
            ImGui.TextUnformatted(item.Name);

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));

        if (item.IsSetContainer)
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SetContainerTip"));
    }

    private void DrawItemGrid(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0) return;

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
        using var id = ImRaii.PushId($"Grid_{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}_{index}");

        ImGui.InvisibleButton("##GridCell", new Vector2(cellSize, cellSize));

        var hovered = ImGui.IsItemHovered();
        var selected = selectedItem != null && IsSameSelectableItem(selectedItem, item);
        var favorite = IsFavorite(item.ItemID);
        DrawItemBackground(drawList, pos, pos + new Vector2(cellSize, cellSize), selected, favorite, hovered, GRID_CELL_ROUNDING);

        var iconPos = pos + new Vector2((cellSize - iconSize) * 0.5f, (cellSize - iconSize) * 0.5f);
        ImGui.SetCursorScreenPos(iconPos);
        DrawItemIcon(item.IconID, iconSize);

        if (favorite)
            drawList.AddText(pos + new Vector2(cellSize - 17f, 2f), ImGui.ColorConvertFloat4ToU32(GOLD_COLOR), FAVORITE_ICON_ON);

        if (!hovered) return;

        HandleItemClick(item);
        DrawGridTooltip(item);
    }

    private void HandleItemClick(UnifiedItem item)
    {
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectedItem = item;
            ApplySelectedItemToCurrentPlateSlot(item);
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ToggleFavorite(item);
    }

    private static void DrawItemBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool favorite,
        bool hovered,
        float rounding)
    {
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(GetCardBackgroundColor(selected, favorite, hovered)), rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(GetCardBorderColor(selected, favorite)),
            rounding,
            (ImDrawFlags)0,
            selected ? 2.2f : CARD_BORDER_THICKNESS);
    }

    private void DrawGridTooltip(UnifiedItem item)
    {
        using var tooltip = ImRaii.Tooltip();

        ImGui.TextColored(TITLE_COLOR, item.Name);
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SourceValue", GetSourceLabel(item)));

        if (item.IsSetPart)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-PartValue", item.SetPartLabel));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ParentSetValue", item.ParentSetName));
        }

        ImGui.Separator();
        ImGui.TextColored(SOFT_ACCENT_COLOR, Lang.Get("UnifiedGlamourManager-GridTooltipHelp"));
    }

    private static bool IsSameSelectableItem(UnifiedItem a, UnifiedItem b) =>
        a.ItemID == b.ItemID &&
        a.PrismBoxIndex == b.PrismBoxIndex &&
        a.IsSetPart == b.IsSetPart &&
        a.ParentSetItemID == b.ParentSetItemID;

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

    private void DrawSelectedPanel()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PANEL_PADDING_X, PANEL_PADDING_Y));
        using var child = ImRaii.Child("##SelectedPanel", new Vector2(0f, 0f), true);
        if (!child) return;

        SectionTitle(Lang.Get("UnifiedGlamourManager-SelectedSection"));

        if (selectedItem == null)
        {
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSelectedItem"));
            ImGui.Spacing();
            RedTip(Lang.Get("UnifiedGlamourManager-ApplyHelp"));
            return;
        }

        var item = selectedItem;
        DrawSelectedItemHeader(item);

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-TargetSlot"));
        ImGui.TextColored(SOFT_ACCENT_COLOR, GetCurrentPlateSlotNameForUI());
        ImGui.Spacing();

        if (item.IsSetPart)
        {
            ImGui.TextColored(SOFT_ACCENT_COLOR, Lang.Get("UnifiedGlamourManager-SetPart"));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-PartValue", item.SetPartLabel));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-ParentSetValue", item.ParentSetName));
            ImGui.Spacing();
        }

        if (item.IsSetContainer)
        {
            ImGui.TextColored(GOLD_COLOR, Lang.Get("UnifiedGlamourManager-SetContainer"));
            RedTip(Lang.Get("UnifiedGlamourManager-SetContainerApplyTip"));
            ImGui.Spacing();
        }

        DrawSelectedActions(item);
    }

    private void DrawSelectedItemHeader(UnifiedItem item)
    {
        DrawItemIcon(item.IconID, ICON_SIZE_SELECTED);
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.TextColored(TITLE_COLOR, item.Name);
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-Level", item.LevelEquip));
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-SourceValue", GetSourceLabel(item)));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawSelectedActions(UnifiedItem item)
    {
        ImGui.Separator();
        ImGui.Spacing();

        var plateReady = IsPlateEditorReady();
        var canApply = plateReady && item.CanUseInPlate;

        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button(Lang.Get("UnifiedGlamourManager-ApplyToCurrentSlot"), new(-1f, CONTROL_HEIGHT)))
                ApplySelectedItemToCurrentPlateSlot(item);
        }

        if (!plateReady && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));

        if (!plateReady)
            RedTip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CopyName"), new(-1f, CONTROL_HEIGHT)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(Lang.Get("UnifiedGlamourManager-CancelSelection"), new(-1f, CONTROL_HEIGHT)))
            selectedItem = null;
    }

    #endregion

    #region 预定义

    private enum SourceFilter
    {
        All,
        PrismBox,
        Cabinet,
        Favorite
    }

    private enum SortMode
    {
        FavoriteThenNameAsc,
        NameAsc,
        NameDesc,
        LevelAsc,
        LevelDesc
    }

    private enum SetRelationFilter
    {
        All,
        SetRelatedOnly,
        NonSetOnly
    }

    private sealed class UnifiedItem
    {
        public uint ItemID { get; set; }
        public uint RawItemID { get; set; }
        public uint PrismBoxIndex { get; set; }
        public uint CabinetID { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint Stain0ID { get; set; }
        public uint Stain1ID { get; set; }
        public uint EquipSlotCategoryRowID { get; set; }
        public uint ClassJobCategoryRowID { get; set; }
        public uint IconID { get; set; }
        public uint LevelEquip { get; set; }
        public bool InPrismBox { get; set; }
        public bool InCabinet { get; set; }
        public bool IsSetContainer { get; set; }
        public bool IsSetPart { get; set; }
        public uint ParentSetItemID { get; set; }
        public string ParentSetName { get; set; } = string.Empty;
        public string SetPartLabel { get; set; } = string.Empty;

        public bool CanUseInPlate => InPrismBox || InCabinet;
    }

    private sealed class SavedItem
    {
        public uint ItemID { get; set; }
        public string Name { get; set; } = string.Empty;
        public long AddedAt { get; set; }
    }

    private readonly record struct SetPartInfo(uint ItemID, string PartLabel, int SlotIndex);

    private readonly record struct PlateSlotDefinition(string LangKey, Func<EquipSlotCategory, bool> CanUse);

    private const string PRISM_BOX_ADDON_NAME = nameof(MiragePrismPrismBox);
    private const string PLATE_EDITOR_ADDON_NAME = nameof(MiragePrismMiragePlate);
    private const string FAVORITE_ICON_ON = "★";
    private const string FAVORITE_ICON_OFF = "☆";

    private const int TASK_TIMEOUT_MS = 30_000;
    private const int REFRESH_STEP_DELAY_MS = 1;
    private const int APPLY_RETRY_DELAY_MS = 50;
    private const int DEFAULT_MIN_EQUIP_LEVEL = 1;
    private const int DEFAULT_MAX_EQUIP_LEVEL = 100;
    private const int MAX_EQUIP_LEVEL_INPUT = 999;

    private const uint PRISM_BOX_CAPACITY = 800;

    private const float WINDOW_DEFAULT_WIDTH = 1420f;
    private const float WINDOW_DEFAULT_HEIGHT = 860f;
    private const float WINDOW_MIN_WIDTH = 1120f;
    private const float WINDOW_MIN_HEIGHT = 680f;
    private const float WINDOW_MAX_SIZE = 9999f;
    private const float LEFT_PANEL_WIDTH = 292f;
    private const float RIGHT_PANEL_WIDTH = 352f;
    private const float TOP_BAR_HEIGHT = 82f;
    private const float TOP_BAR_BUTTON_WIDTH = 112f;
    private const float TOP_BAR_CLEAR_BUTTON_WIDTH = 88f;
    private const float SEARCH_MIN_WIDTH = 240f;
    private const float SEARCH_MAX_WIDTH = 320f;
    private const float MAIN_LAYOUT_MIN_HEIGHT = 420f;
    private const float PANEL_PADDING_X = 12f;
    private const float PANEL_PADDING_Y = 10f;
    private const float POPUP_BUTTON_WIDTH = 132f;
    private const float ICON_SIZE_LIST = 54f;
    private const float ICON_SIZE_SELECTED = 82f;
    private const float CARD_MIN_HEIGHT = 88f;
    private const float ITEM_SPACING_Y = 8f;
    private const float CARD_ROUNDING = 8f;
    private const float CARD_BORDER_THICKNESS = 1.2f;
    private const float CONTROL_HEIGHT = 36f;
    private const float VIEW_MODE_BUTTON_WIDTH = 72f;
    private const float VIEW_MODE_BUTTON_HEIGHT = 30f;
    private const float GRID_MIN_CELL_SIZE = 58f;
    private const float GRID_MAX_CELL_SIZE = 68f;
    private const float GRID_CELL_SPACING = 4f;
    private const float GRID_ICON_MIN_SIZE = 42f;
    private const float GRID_ICON_PADDING = 10f;
    private const float GRID_CELL_ROUNDING = 6f;
    private const int VIRTUALIZED_LIST_BUFFER_ROWS = 3;
    private const int VIRTUALIZED_GRID_BUFFER_ROWS = 2;
    private const int SEARCH_INPUT_MAX_LENGTH = 128;

    private static readonly (SourceFilter Filter, string LangKey)[] SourceFilters =
    [
        (SourceFilter.All, "UnifiedGlamourManager-Source-All"),
        (SourceFilter.Favorite, "UnifiedGlamourManager-Source-Favorite"),
        (SourceFilter.PrismBox, "UnifiedGlamourManager-Source-PrismBox"),
        (SourceFilter.Cabinet, "UnifiedGlamourManager-Source-Cabinet")
    ];

    private static readonly uint[][] JobFilterClassJobIDs =
    [
        [],
        [1, 19],
        [3, 21],
        [32],
        [37],
        [6, 24],
        [28],
        [33],
        [40],
        [2, 20],
        [4, 22],
        [29, 30],
        [34],
        [39],
        [41],
        [5, 23],
        [31],
        [38],
        [7, 25],
        [26, 27],
        [35],
        [36],
        [42],
        [8, 9, 10, 11, 12, 13, 14, 15],
        [16, 17, 18]
    ];

    private static readonly string[] JobFilterNames = CreateJobFilterNames();

    private static readonly PlateSlotDefinition[] PlateSlotDefinitions =
    [
        new("UnifiedGlamourManager-Slot-MainHand", static x => x.MainHand != 0),
        new("UnifiedGlamourManager-Slot-OffHand", static x => x.OffHand != 0 && x.MainHand == 0),
        new("UnifiedGlamourManager-Slot-Head", static x => x.Head != 0),
        new("UnifiedGlamourManager-Slot-Body", static x => x.Body != 0),
        new("UnifiedGlamourManager-Slot-Hands", static x => x.Gloves != 0),
        new("UnifiedGlamourManager-Slot-Legs", static x => x.Legs != 0),
        new("UnifiedGlamourManager-Slot-Feet", static x => x.Feet != 0),
        new("UnifiedGlamourManager-Slot-Earrings", static x => x.Ears != 0),
        new("UnifiedGlamourManager-Slot-Necklace", static x => x.Neck != 0),
        new("UnifiedGlamourManager-Slot-Bracelets", static x => x.Wrists != 0),
        new("UnifiedGlamourManager-Slot-LeftRing", static x => x.FingerL != 0 || x.FingerR != 0),
        new("UnifiedGlamourManager-Slot-RightRing", static x => x.FingerL != 0 || x.FingerR != 0)
    ];

    private static readonly string[] SortModeNames =
    [
        Lang.Get("UnifiedGlamourManager-Sort-FavoriteThenNameAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-NameAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-NameDesc"),
        Lang.Get("UnifiedGlamourManager-Sort-LevelAsc"),
        Lang.Get("UnifiedGlamourManager-Sort-LevelDesc")
    ];

    private static readonly string[] SetRelationFilterNames =
    [
        Lang.Get("UnifiedGlamourManager-SetFilter-All"),
        Lang.Get("UnifiedGlamourManager-SetFilter-SetRelatedOnly"),
        Lang.Get("UnifiedGlamourManager-SetFilter-NonSetOnly")
    ];

    private static readonly Vector4 TITLE_COLOR = KnownColor.HotPink.ToVector4();
    private static readonly Vector4 SELECTED_COLOR = KnownColor.MediumVioletRed.ToVector4() with { W = 0.65f };
    private static readonly Vector4 BUTTON_ACCENT_COLOR = KnownColor.PaleVioletRed.ToVector4() with { W = 0.4f };
    private static readonly Vector4 BUTTON_HOVERED_COLOR = KnownColor.MediumVioletRed.ToVector4() with { W = 0.72f };
    private static readonly Vector4 BUTTON_ACTIVE_COLOR = KnownColor.HotPink.ToVector4() with { W = 0.78f };
    private static readonly Vector4 SOFT_ACCENT_COLOR = KnownColor.Plum.ToVector4();
    private static readonly Vector4 GOLD_COLOR = KnownColor.Gold.ToVector4();
    private static readonly Vector4 ERROR_COLOR = KnownColor.Crimson.ToVector4();
    private static readonly Vector4 WINDOW_BG_COLOR = KnownColor.Black.ToVector4() with { W = 0.84f };
    private static readonly Vector4 PANEL_BG_COLOR = KnownColor.Black.ToVector4() with { W = 0.48f };
    private static readonly Vector4 POPUP_BG_COLOR = KnownColor.Black.ToVector4() with { W = 0.92f };
    private static readonly Vector4 FRAME_BG_COLOR = KnownColor.DimGray.ToVector4() with { W = 0.48f };
    private static readonly Vector4 FRAME_BG_HOVERED_COLOR = KnownColor.MediumPurple.ToVector4() with { W = 0.38f };
    private static readonly Vector4 FRAME_BG_ACTIVE_COLOR = KnownColor.MediumVioletRed.ToVector4() with { W = 0.50f };
    private static readonly Vector4 NORMAL_CARD_COLOR = KnownColor.Black.ToVector4() with { W = 0.34f };
    private static readonly Vector4 NORMAL_CARD_HOVER_COLOR = KnownColor.Maroon.ToVector4() with { W = 0.26f };
    private static readonly Vector4 FAVORITE_CARD_COLOR = KnownColor.Gold.ToVector4() with { W = 0.4f };
    private static readonly Vector4 FAVORITE_CARD_HOVER_COLOR = KnownColor.Goldenrod.ToVector4() with { W = 0.68f };
    private static readonly Vector4 SELECTED_BORDER_COLOR = KnownColor.Khaki.ToVector4();
    private static readonly Vector4 MUTED_BORDER_COLOR = KnownColor.DarkGray.ToVector4();
    private static readonly Vector4 STAR_OFF_COLOR = KnownColor.Gray.ToVector4();

    private static string[] CreateJobFilterNames()
    {
        var names = new string[JobFilterClassJobIDs.Length];
        names[0] = Lang.Get("UnifiedGlamourManager-JobFilter-AllJobs");

        for (var i = 1; i < JobFilterClassJobIDs.Length; i++)
            names[i] = string.Join(" / ", JobFilterClassJobIDs[i].Select(LuminaWrapper.GetJobName));

        return names;
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);

    #endregion
}
