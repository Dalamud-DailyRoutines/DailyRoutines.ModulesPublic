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

public unsafe class UnifiedGlamourManager : ModuleBase
{
    #region 模块

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOperation,
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
        try
        {
            config = LoadConfig<Config>() ?? new Config();
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
        catch (Exception ex)
        {
            CleanupRuntimeResources();
            DLog.Error("UnifiedGlamourManager init failed", ex);
            throw;
        }
    }

    protected override void Uninit() => CleanupRuntimeResources();

    private void CleanupRuntimeResources()
    {
        SafeCleanup(CloseWindow, "close window");
        SafeCleanup(() => TaskHelper?.Abort(), "task cleanup");
        SafeCleanup(
            () => DService.Instance().AddonLifecycle.UnregisterListener(OnPrismBoxAddon, OnPlateEditorAddon),
            "addon listener cleanup");
    }

    private static void SafeCleanup(System.Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            DLog.Error($"UnifiedGlamourManager {name} failed during unload", ex);
        }
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button(Lang.Get("Open")))
            OpenWindow(false);

        ImGui.SameLine();

        using (ImRaii.Disabled(isRefreshingItems))
        {
            if (ImGui.Button(Lang.Get("Refresh")))
                StartRefreshAll();
        }

        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-LoadedStatus", items.Count, cachedLoadedFavoriteCount));
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

    private static bool TryGetReadyPlateEditor(out AgentMiragePrismMiragePlate* agent)
    {
        agent = null;

        var addon = MiragePrismMiragePlate;
        if (addon == null || !addon->IsAddonAndNodesReady()) return false;

        agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null &&
               agent->Data != null &&
               agent->Data->SelectedItemIndex < PlateSlotDefinitions.Length;
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
        base.SaveConfig(config);
        RefreshFavoriteCountCache();
        MarkFilteredItemsDirty();
    }

    private void NormalizeConfig()
    {
        config.Favorites ??= [];
        config.Favorites = config.Favorites
                                  .OfType<SavedItem>()
                                  .Where(static x => x.ItemID != 0 && LuminaGetter.TryGetRow<ItemSheet>(x.ItemID, out _))
                                  .Select(static x =>
                                  {
                                      var name = ToSingleLine(x.Name ?? string.Empty);
                                      if (string.IsNullOrWhiteSpace(name) &&
                                          LuminaGetter.TryGetRow<ItemSheet>(x.ItemID, out var itemRow) &&
                                          TryGetItemName(itemRow, out var itemName))
                                          name = itemName;

                                      return new SavedItem
                                      {
                                          ItemID  = x.ItemID,
                                          Name    = name,
                                          AddedAt = Math.Max(0, x.AddedAt)
                                      };
                                  })
                                  .GroupBy(static x => x.ItemID)
                                  .Select(static x => x.OrderByDescending(y => y.AddedAt).First())
                                  .OrderByDescending(static x => x.AddedAt)
                                  .ToList();
        favoriteItemIDs.Clear();
        favoriteItemIDs.UnionWith(config.Favorites.Select(static x => x.ItemID));
    }

    private void RefreshFavoriteCountCache() =>
        cachedLoadedFavoriteCount = items.Where(x => IsFavorite(x.ItemID)).Select(x => x.ItemID).Distinct().Count();

    private bool IsFavorite(uint itemID) =>
        favoriteItemIDs.Contains(itemID);

    private void ToggleFavorite(UnifiedItem item)
    {
        if (config.Favorites.RemoveAll(x => x.ItemID == item.ItemID) == 0)
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

    private void StartRefreshAll(UnifiedItem? reselectItem = null)
    {
        var itemToReselect = reselectItem ?? selectedItem;

        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };
        TaskHelper.Abort();

        isRefreshingItems = true;
        items.Clear();
        filteredItems.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount = 0;
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);

        TaskHelper.Enqueue(() => RunRefreshStep(LoadPrismBoxItems, nameof(LoadPrismBoxItems)), nameof(LoadPrismBoxItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => RunRefreshStep(LoadCabinetItems, nameof(LoadCabinetItems)), nameof(LoadCabinetItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(
            () =>
            {
                try
                {
                    MergeItems();
                    RefreshFavoriteCountCache();
                    selectedItem = itemToReselect != null
                        ? items.FirstOrDefault(x => IsSameSelectableItem(x, itemToReselect))
                        : null;
                }
                catch (Exception ex)
                {
                    DLog.Error("UnifiedGlamourManager refresh merge failed", ex);
                    selectedItem = null;
                }
                finally
                {
                    isRefreshingItems = false;
                    MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
                }
            },
            nameof(MergeItems));
    }

    private static void RunRefreshStep(System.Action action, string stepName)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            DLog.Error($"UnifiedGlamourManager refresh step failed: {stepName}", ex);
        }
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
            if (source != ItemSource.PrismBox || (manager != null && manager->IsSetSlotUnlocked(prismBoxIndex, setPart.SlotIndex)))
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
            !LuminaGetter.TryGetRow<EquipSlotCategory>(itemRow.EquipSlotCategory.RowId, out var categoryRow) ||
            !IsEquipSlotCategoryCompatibleWithPlateSlot(categoryRow, (uint)slotIndex))
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
        var categoryName = item.ItemUICategory.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(categoryName) ? item.Name.ExtractText() : categoryName;
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
        if (!item.CanUseInPlate || !TryGetReadyPlateEditor(out var agent)) return;

        var selectedSlot = agent->Data->SelectedItemIndex;
        if (!CanItemUseInPlateSlot(item, selectedSlot)) return;

        try
        {
            if (item.InPrismBox)
            {
                if (TryApplyPrismBoxItem(agent, item))
                    QueueApplyRetry(item, ItemSource.PrismBox);
            }
            else if (item.InCabinet)
            {
                if (TryApplyCabinetItem(agent, item))
                    QueueApplyRetry(item, ItemSource.Cabinet);
            }
        }
        catch (Exception ex)
        {
            DLog.Warning($"UnifiedGlamourManager apply failed: {ex}");
        }
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

        var itemID = !item.IsSetPart && item.RawItemID != 0 ? item.RawItemID : item.ItemID;
        var stain0 = (byte)(item.IsSetPart ? 0 : item.Stain0ID);
        var stain1 = (byte)(item.IsSetPart ? 0 : item.Stain1ID);

        agent->SetSelectedItemData(ItemSource.PrismBox, item.PrismBoxIndex, itemID, stain0, stain1);
        MarkPlateSelectionDirty(agent);
        return true;
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
                if (IsDisposed || !TryGetReadyPlateEditor(out var retryAgent)) return;

                var retryItem = items.FirstOrDefault(x =>
                    x.ItemID == itemID &&
                    x.IsSetPart == isSetPart &&
                    x.ParentSetItemID == parentSetItemID &&
                    x.CanUseInPlate &&
                    (source == ItemSource.PrismBox
                        ? x.InPrismBox && x.PrismBoxIndex == prismBoxIndex
                        : x.InCabinet && x.CabinetID == cabinetID));

                if (retryItem == null) return;
                if (!CanItemUseInPlateSlot(retryItem, retryAgent->Data->SelectedItemIndex)) return;

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
        if (!TryGetReadyPlateEditor(out var agent)) return true;

        var selectedSlot = agent->Data->SelectedItemIndex;
        var cacheKey = ((ulong)selectedSlot << 32) | item.EquipSlotCategoryRowID;
        if (plateSlotFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = CanItemUseInPlateSlot(item, selectedSlot);
        plateSlotFilterCache[cacheKey] = result;
        return result;
    }

    private static bool CanItemUseInPlateSlot(UnifiedItem item, uint selectedSlot)
    {
        if (!LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategoryRowID, out var categoryRow)) return false;
        return selectedSlot < PlateSlotDefinitions.Length && IsEquipSlotCategoryCompatibleWithPlateSlot(categoryRow, selectedSlot);
    }

    private string GetCurrentPlateSlotNameForUI() =>
        TryGetReadyPlateEditor(out var a)
            ? GetPlateSlotName(a->Data->SelectedItemIndex)
            : Lang.Get("UnifiedGlamourManager-PlateNotOpen");

    private static string GetPlateSlotName(uint selectedSlot) =>
        selectedSlot < PlateSlotDefinitions.Length
            ? GetAddonText(PlateSlotDefinitions[selectedSlot].AddonTextID, Lang.Get("Unknown"))
            : $"{Lang.Get("Unknown")} {GetSlotText()} {selectedSlot}";

    private static bool IsPlateEditorReady() => TryGetReadyPlateEditor(out _);

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
            labels.Add(GetPrismBoxText());

        if (item.InCabinet)
            labels.Add(GetCabinetText());

        if (item.IsSetPart || item.IsSetContainer)
            labels.Add(GetSetText());

        return labels.Count == 0
            ? Lang.Get("Unknown")
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

        if (ImGui.Begin($"{Lang.Get("UnifiedGlamourManagerTitle")}###UnifiedGlamourManager", ref isOpen, ImGuiWindowFlags.NoScrollbar))
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

        if (ImGui.Button(Lang.Get("Confirm"), new(POPUP_BUTTON_WIDTH, CONTROL_HEIGHT)))
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

        ImGui.TextColored(SOFT_ACCENT_COLOR, $"{GetPrismBoxText()} {prismBoxItemCount} / {GetCabinetText()} {cabinetItemCount} / {GetTotalText()} {items.Count}");

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("Refresh"), new(TOP_BAR_BUTTON_WIDTH, CONTROL_HEIGHT)))
            StartRefreshAll();

        ImGui.SameLine();

        var searchWidth = Math.Clamp(ImGui.GetContentRegionAvail().X - TOP_BAR_BUTTON_WIDTH - 60f, SEARCH_MIN_WIDTH, SEARCH_MAX_WIDTH);
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##Search", GetItemSearchHintText(), ref searchText, SEARCH_INPUT_MAX_LENGTH))
        {
            searchText = ToSingleLine(searchText);
            MarkFilteredItemsDirty();
        }

        ImGui.SameLine();

        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-CurrentSlotOnly"), ref filterByCurrentPlateSlot))
            MarkFilteredItemsDirty(clearPlateSlotCache: true);

        ImGui.SameLine(0f, 12f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Lang.Get("Favorite"));
        ImGui.SameLine(0f, 4f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(GOLD_COLOR, cachedLoadedFavoriteCount.ToString());
        ImGui.SameLine(0f, 10f);

        var clearFavoritesText = Lang.Get("Clear");
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

        ImGui.TableSetupColumn(GetFilterText(), ImGuiTableColumnFlags.WidthFixed, LEFT_PANEL_WIDTH);
        ImGui.TableSetupColumn(GetEquipmentSelectionText(), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Lang.Get("Current"), ImGuiTableColumnFlags.WidthFixed, RIGHT_PANEL_WIDTH);
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

        SectionTitle(GetFilterText());
        DrawSourceFilter();
        DrawSortFilter();
        DrawLevelFilter();
        DrawJobFilter();
        DrawSetRelationFilter();
        DrawResetFilterButton();
    }

    private void DrawSourceFilter()
    {
        ImGui.TextDisabled(GetSourceText());

        var width = ImGui.GetContentRegionAvail().X;
        var buttonSize = new Vector2((width - 6f) * 0.5f, CONTROL_HEIGHT);

        for (var i = 0; i < SourceFilters.Length; i++)
        {
            if (i % 2 == 1)
                ImGui.SameLine();

            var filter = SourceFilters[i];
            using (ImRaii.PushColor(ImGuiCol.Button, BUTTON_ACTIVE_COLOR, sourceFilter == filter))
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
        ImGui.TextDisabled(GetSortText());

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
        ImGui.TextDisabled(GetItemLevelText());
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
        ImGui.TextDisabled(GetClassJobText());
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

        if (ImGui.Button($"{Lang.Get("Reset")} {GetFilterText()}", new(-1f, CONTROL_HEIGHT)))
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

        ImGui.TextColored(TITLE_COLOR, GetEquipmentSelectionText());
        ImGui.SameLine();
        ImGui.TextDisabled(filteredItems.Count.ToString());
        ImGui.SameLine();
        ImGui.TextColored(
            IsPlateEditorReady() ? SOFT_ACCENT_COLOR : ERROR_COLOR,
            FormatLabelValue($"{Lang.Get("Current")} {GetSlotText()}", GetCurrentPlateSlotNameForUI()));

        var x = ImGui.GetContentRegionAvail().X - VIEW_MODE_BUTTON_WIDTH * 2f - GRID_ICON_PADDING;
        if (x > 0f)
            ImGui.SameLine(x);

        DrawViewModeButton(Lang.Get("List") + "##ViewList", !useGridView, () => useGridView = false);
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
        if (clearJobCache) jobFilterCache.Clear();
        if (clearPlateSlotCache) plateSlotFilterCache.Clear();
    }

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

        var titleColor = selected ? SELECTED_BORDER_COLOR :
            favorite || !item.IsSetPart ? KnownColor.White.ToVector4() : SOFT_ACCENT_COLOR;

        using (ImRaii.PushColor(ImGuiCol.Text, titleColor))
            ImGui.TextUnformatted(item.Name);

        ImGui.TextDisabled(FormatLabelValue(GetLevelText(), item.LevelEquip));

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

        var halfDiff = (cellSize - iconSize) * 0.5f;
        ImGui.SetCursorScreenPos(pos + new Vector2(halfDiff, halfDiff));
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
        ImGui.TextDisabled(FormatLabelValue(GetLevelText(), item.LevelEquip));
        ImGui.TextDisabled(FormatLabelValue(GetSourceText(), GetSourceLabel(item)));

        if (item.IsSetPart)
        {
            ImGui.Separator();
            ImGui.TextDisabled(FormatPartValue(item.SetPartLabel));
            ImGui.TextDisabled(FormatParentSetValue(item.ParentSetName));
        }

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

        SectionTitle(Lang.Get("Current"));

        if (selectedItem == null)
        {
            ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSelectedItem"));
            return;
        }

        var item = selectedItem;
        DrawSelectedItemHeader(item);

        ImGui.TextDisabled(GetTargetSlotText());
        ImGui.TextColored(SOFT_ACCENT_COLOR, GetCurrentPlateSlotNameForUI());
        ImGui.Spacing();

        if (item.IsSetPart)
        {
            ImGui.TextColored(SOFT_ACCENT_COLOR, GetSetText());
            ImGui.TextDisabled(FormatPartValue(item.SetPartLabel));
            ImGui.TextDisabled(FormatParentSetValue(item.ParentSetName));
            ImGui.Spacing();
        }

        if (item.IsSetContainer)
        {
            ImGui.TextColored(GOLD_COLOR, GetSetText());
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
            ImGui.TextDisabled(FormatLabelValue(GetLevelText(), item.LevelEquip));
            ImGui.TextDisabled(FormatLabelValue(GetSourceText(), GetSourceLabel(item)));
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
        var canApply = plateReady && item.CanUseInPlate && CanItemUseInCurrentPlateSlot(item);

        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button($"{Lang.Get("Apply")} {GetTargetSlotText()}", new(-1f, CONTROL_HEIGHT)))
                ApplySelectedItemToCurrentPlateSlot(item);
        }

        if (!plateReady && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));

        if (!plateReady)
            RedTip(Lang.Get("UnifiedGlamourManager-PlateRequiredTip"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(GetCopyItemNameText(), new(-1f, CONTROL_HEIGHT)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(GetClearSelectionText(), new(-1f, CONTROL_HEIGHT)))
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

        public bool CanUseInPlate => (InPrismBox || InCabinet) && !IsSetContainer;
    }

    private sealed class SavedItem
    {
        public uint ItemID { get; set; }
        public string Name { get; set; } = string.Empty;
        public long AddedAt { get; set; }
    }

    private readonly record struct SetPartInfo(uint ItemID, string PartLabel, int SlotIndex);

    private readonly record struct PlateSlotDefinition(uint AddonTextID, Func<EquipSlotCategory, bool> CanUse);

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

    private static readonly SourceFilter[] SourceFilters =
    [
        SourceFilter.All,
        SourceFilter.Favorite,
        SourceFilter.PrismBox,
        SourceFilter.Cabinet
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
        new(11960, static x => x.MainHand != 0),
        new(11961, static x => x.OffHand != 0 && x.MainHand == 0),
        new(11962, static x => x.Head != 0),
        new(11963, static x => x.Body != 0),
        new(11964, static x => x.Gloves != 0),
        new(11965, static x => x.Legs != 0),
        new(11966, static x => x.Feet != 0),
        new(11968, static x => x.Ears != 0),
        new(11967, static x => x.Neck != 0),
        new(11969, static x => x.Wrists != 0),
        new(750, static x => x.FingerL != 0 || x.FingerR != 0),
        new(749, static x => x.FingerL != 0 || x.FingerR != 0)
    ];

    private static readonly string[] SortModeNames = CreateSortModeNames();

    private static readonly string[] SetRelationFilterNames =
    [
        Lang.Get("All"),
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

    private static string GetAddonText(uint rowID, string fallback)
    {
        var text = LuminaWrapper.GetAddonText(rowID);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string FormatLabelValue(string label, object value) => $"{label}: {value}";
    private static string FormatPartValue(string value) => FormatLabelValue(GetPartText(), value);
    private static string FormatParentSetValue(string value) => FormatLabelValue(GetParentSetText(), value);

    private static string ToSingleLine(string text)
    {
        var carriageReturnIndex = text.IndexOf('\r');
        var lineFeedIndex = text.IndexOf('\n');
        var lineEndIndex = carriageReturnIndex < 0
            ? lineFeedIndex
            : lineFeedIndex < 0
                ? carriageReturnIndex
                : Math.Min(carriageReturnIndex, lineFeedIndex);

        return (lineEndIndex >= 0 ? text[..lineEndIndex] : text).Trim();
    }

    private static string GetClassJobText() => GetAddonText(294, Lang.Get("Job"));
    private static string GetLevelText() => GetAddonText(335, Lang.Get("Level"));
    private static string GetSourceText() => GetAddonText(8191, "Source");
    private static string GetFilterText() => GetAddonText(13125, "Filter");
    private static string GetSortText() => GetAddonText(12170, Lang.Get("Sort"));
    private static string GetItemLevelText() => GetAddonText(7873, "Item Level");
    private static string GetPartText()
    {
        var text = NormalizeAddonLabel(GetAddonText(2155, "Part"));
        var partIndex = text.IndexOf("部位", StringComparison.Ordinal);
        return partIndex >= 0 ? text[partIndex..] : text;
    }

    private static string GetParentSetText()
    {
        var affiliationText = NormalizeAddonLabel(GetAddonText(733, string.Empty));
        var freeCompanyText = NormalizeAddonLabel(GetAddonText(297, string.Empty));
        var belongsToText = !string.IsNullOrEmpty(freeCompanyText) && affiliationText.EndsWith(freeCompanyText, StringComparison.Ordinal)
            ? affiliationText[..^freeCompanyText.Length]
            : affiliationText;

        return string.IsNullOrEmpty(belongsToText)
            ? GetSetText()
            : $"{belongsToText}{GetSetText()}";
    }

    private static string NormalizeAddonLabel(string text) =>
        ToSingleLine(text).Trim().TrimEnd(':', '：');

    private static string GetSlotText() => GetPartText();
    private static string GetSetText() => GetAddonText(15624, GetAddonText(756, "Set"));
    private static string GetTargetText() => GetAddonText(1030, Lang.Get("Target"));
    private static string GetTargetSlotText() => $"{GetTargetText()}{GetSlotText()}";
    private static string GetTotalText() => GetAddonText(929, "Total");
    private static string GetPrismBoxText() => GetAddonText(11910, "Glamour Dresser");
    private static string GetCabinetText() => GetAddonText(12216, "Armoire");
    private static string GetEquipmentSelectionText() => GetAddonText(11920, "Equipment Selection");
    private static string GetItemSearchHintText() => ToSingleLine(GetAddonText(11933, Lang.Get("Search")));
    private static string GetCopyItemNameText() => GetAddonText(159, $"{Lang.Get("Copy")} {Lang.Get("Name")}");
    private static string GetClearSelectionText() => GetAddonText(102590, Lang.Get("Cancel"));
    private static string GetSourceFilterLabel(SourceFilter filter) =>
        filter switch
        {
            SourceFilter.Favorite => GetAddonText(8127, Lang.Get("Favorite")),
            SourceFilter.PrismBox => GetPrismBoxText(),
            SourceFilter.Cabinet  => GetCabinetText(),
            _                     => Lang.Get("All")
        };

    private static string[] CreateSortModeNames()
    {
        var nameAsc = $"{Lang.Get("Name")} {Lang.Get("Ascending")}";
        var nameDesc = $"{Lang.Get("Name")} {Lang.Get("Descending")}";
        var levelAsc = $"{GetLevelText()} {Lang.Get("Ascending")}";
        var levelDesc = $"{GetLevelText()} {Lang.Get("Descending")}";

        return
        [
            $"{Lang.Get("Favorite")} / {nameAsc}",
            nameAsc,
            nameDesc,
            levelAsc,
            levelDesc
        ];
    }

    private static string[] CreateJobFilterNames()
    {
        var names = new string[JobFilterClassJobIDs.Length];
        names[0] = Lang.Get("All");

        for (var i = 1; i < JobFilterClassJobIDs.Length; i++)
            names[i] = string.Join(" / ", JobFilterClassJobIDs[i].Select(LuminaWrapper.GetJobName));

        return names;
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);

    #endregion
}
