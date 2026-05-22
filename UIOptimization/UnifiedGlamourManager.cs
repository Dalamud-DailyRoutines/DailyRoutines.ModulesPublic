using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet  = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ImageHelper  = OmenTools.OmenService.ImageHelper;
using ItemSheet    = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic;

public unsafe class UnifiedGlamourManager : ModuleBase
{
    #region 模块信息

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    #endregion

    #region 状态

    private Config config                       = null!;
    private string searchText                   = string.Empty;
    private SourceFilter sourceFilter           = SourceFilter.All;
    private SortMode sortMode                   = SortMode.FavoriteThenNameAsc;
    private SetRelationFilter setRelationFilter = SetRelationFilter.All;
    private bool filterByCurrentPlateSlot       = true;
    private bool enableLevelFilter;
    private int minEquipLevel = DEFAULT_MIN_EQUIP_LEVEL;
    private int maxEquipLevel = DEFAULT_MAX_EQUIP_LEVEL;
    private int selectedJobFilterIndex;
    private readonly List<UnifiedItem> items                      = [];
    private readonly List<UnifiedItem> filteredItems              = [];
    private readonly HashSet<uint> favoriteItemIDs                = [];
    private readonly Dictionary<ulong, bool> jobFilterCache       = [];
    private readonly Dictionary<ulong, bool> plateSlotFilterCache = [];
    private bool useGridView                                      = true;
    private int prismBoxItemCount;
    private int cabinetItemCount;
    private UnifiedItem? selectedItem;
    private bool requestClearFavoritesConfirm;
    private bool filteredItemsDirty = true;
    private bool isRefreshingItems;
    private uint lastFilterPlateSlot = uint.MaxValue;
    private int cachedLoadedFavoriteCount;
    private int StoredItemCount => prismBoxItemCount + cabinetItemCount;

    #endregion

    #region 生命周期

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();
        TaskHelper ??= new()
        {
            TimeoutMS = TASK_TIMEOUT_MS
        };

        Overlay ??= new(this);

        Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName = $"{Info.Title}###UnifiedGlamourManagerOverlay";

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
    }

    protected override void Uninit()
    {
        TaskHelper?.Abort();
        DService.Instance().AddonLifecycle.UnregisterListener(OnPlateEditorAddon);
    }

    protected override void OverlayPreDraw()
    {
        if (Overlay?.IsOpen == true &&
            (!TryGetReadyPlateEditor(out var agent) ||
            agent->Data->OpenMode != MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW))
        {
            Overlay.IsOpen = false;
            return;
        }

        var minSize = new Vector2(
            ImGui.GetFrameHeight() * 28f,
            ImGui.GetTextLineHeightWithSpacing() * 30f);

        ImGui.SetNextWindowSizeConstraints(minSize, ImGui.GetMainViewport().WorkSize);
    }

    protected override void OverlayUI()
    {
        DrawTopBar();
        DrawMainLayout();
        DrawConfirmPopups();
    }

    private void OnPlateEditorAddon(AddonEvent type, AddonArgs args)
    {
        if (Overlay == null) return;

        switch (type)
        {
            case AddonEvent.PostSetup:
                if (TryGetReadyPlateEditor(out var agent) &&
                    agent->Data->OpenMode == MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW)
                {
                    Overlay.IsOpen = true;
                    StartRefreshAll();
                }
                break;

            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                break;
        }
    }

    private static bool TryGetLoadedMirageManager(out MirageManager* manager)
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    private static bool TryGetReadyPlateEditor(out AgentMiragePrismMiragePlate* agent)
    {
        agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null &&
               agent->IsAgentActive() &&
               agent->Data != null &&
               agent->Data->SelectedItemIndex < PlateSlotDefinitions.Length;
    }

    #endregion

    #region 配置 & 收藏

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
        config.Favorites = config.Favorites
                                  .Where(static x => x.ItemID != 0 && LuminaGetter.TryGetRow<ItemSheet>(x.ItemID, out _))
                                  .Select(static x =>
                                  {
                                      var name = x.Name ?? string.Empty;
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

    #endregion

    #region 数据刷新 & 读取 & 合并
    //数据刷新
    private void StartRefreshAll(UnifiedItem? reselectItem = null)
    {
        var itemToReselect = reselectItem ?? selectedItem;

        TaskHelper ??= new()
        {
            TimeoutMS = TASK_TIMEOUT_MS
        };
        TaskHelper.Abort();

        isRefreshingItems = true;
        items.Clear();
        filteredItems.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount  = 0;
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

    //投影台/收藏柜数据读取
    private void LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager)) return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count     = 0;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0) continue;

            var itemID  = ItemUtil.GetBaseId(rawItemID).ItemId;
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
        var count     = 0;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID    = cabinetRow.Item.RowId;
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
        uint stain0ID          = 0,
        uint stain1ID          = 0,
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

    private static bool TryGetItemName(ItemSheet item, out string name)
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    //套装与合并
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
            partName,
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

        var prism   = group.FirstOrDefault(x => x.InPrismBox);
        var cabinet = group.FirstOrDefault(x => x.InCabinet);

        first.InPrismBox = prism != null;
        first.InCabinet  = cabinet != null;

        if (prism != null)
        {
            first.RawItemID              = prism.RawItemID;
            first.PrismBoxIndex          = prism.PrismBoxIndex;
            first.Stain0ID               = prism.Stain0ID;
            first.Stain1ID               = prism.Stain1ID;
            first.IconID                 = prism.IconID;
            first.EquipSlotCategoryRowID = prism.EquipSlotCategoryRowID;
            first.ClassJobCategoryRowID  = prism.ClassJobCategoryRowID;
            first.LevelEquip             = prism.LevelEquip;
            first.IsSetContainer         = prism.IsSetContainer;
        }

        if (cabinet != null)
        {
            first.CabinetID              = cabinet.CabinetID;
            first.IconID                 = first.IconID == 0 ? cabinet.IconID : first.IconID;
            first.EquipSlotCategoryRowID = first.EquipSlotCategoryRowID == 0 ? cabinet.EquipSlotCategoryRowID : first.EquipSlotCategoryRowID;
            first.ClassJobCategoryRowID  = first.ClassJobCategoryRowID == 0 ? cabinet.ClassJobCategoryRowID : first.ClassJobCategoryRowID;
            first.LevelEquip             = first.LevelEquip == 0 ? cabinet.LevelEquip : first.LevelEquip;
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
        bool isSetPart       = false,
        uint parentSetItemID = 0,
        string parentSetName = "",
        string setPartLabel  = "") =>
        new()
        {
            ItemID                 = itemID,
            RawItemID              = rawItemID,
            PrismBoxIndex          = prismBoxIndex,
            CabinetID              = cabinetID,
            Name                   = name,
            Stain0ID               = stain0ID,
            Stain1ID               = stain1ID,
            EquipSlotCategoryRowID = row.EquipSlotCategory.RowId,
            ClassJobCategoryRowID  = row.ClassJobCategory.RowId,
            IconID                 = row.Icon,
            LevelEquip             = (uint)row.LevelEquip,
            InPrismBox             = source == ItemSource.PrismBox,
            InCabinet              = source == ItemSource.Cabinet,
            IsSetContainer         = isSetContainer,
            IsSetPart              = isSetPart,
            ParentSetItemID        = parentSetItemID,
            ParentSetName          = parentSetName,
            SetPartLabel           = setPartLabel
        };

    #endregion

    #region 应用功能
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
            DLog.Warning("UnifiedGlamourManager apply failed", ex);
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
        var itemID          = item.ItemID;
        var prismBoxIndex   = item.PrismBoxIndex;
        var cabinetID       = item.CabinetID;
        var isSetPart       = item.IsSetPart;
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

        agent->Data->HasChanges          = true;
        agent->CharaView.IsUpdatePending = true;
    }

    #endregion

    #region 筛选 & 排序 & 部位判断

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
        var result      = classJobIDs.Length == 0 ||
                     classJobIDs.Any(id => ClassJobCategory.IsClassJobInCategory(id, item.ClassJobCategoryRowID));
        jobFilterCache[cacheKey] = result;
        return result;
    }

    private bool CanItemUseInCurrentPlateSlot(UnifiedItem item)
    {
        if (!TryGetReadyPlateEditor(out var agent)) return true;

        var selectedSlot = agent->Data->SelectedItemIndex;
        var cacheKey     = ((ulong)selectedSlot << 32) | item.EquipSlotCategoryRowID;
        if (plateSlotFilterCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result                     = CanItemUseInPlateSlot(item, selectedSlot);
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
            : LuminaWrapper.GetAddonText(4764);

    private static string GetPlateSlotName(uint selectedSlot) =>
        selectedSlot < PlateSlotDefinitions.Length
            ? LuminaWrapper.GetAddonText(PlateSlotDefinitions[selectedSlot].AddonTextID)
            : $"{Lang.Get("Unknown")} {selectedSlot}";

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
            labels.Add(LuminaWrapper.GetAddonText(11910));

        if (item.InCabinet)
            labels.Add(LuminaWrapper.GetAddonText(12216));

        if (item.IsSetPart || item.IsSetContainer)
            labels.Add(LuminaWrapper.GetAddonText(15624));

        return labels.Count == 0
            ? Lang.Get("Unknown")
            : string.Join(" / ", labels);
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
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
    }
    
    #endregion

    #region UI - 通用

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

    private static void DrawItemBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool favorite,
        bool hovered)
    {
        var rounding = ImGui.GetStyle().FrameRounding;
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(GetCardBackgroundColor(selected, favorite, hovered)), rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(GetCardBorderColor(selected, favorite)),
            rounding,
            (ImDrawFlags)0,
            selected ? 2f * ImGuiHelpers.GlobalScale : 1f * ImGuiHelpers.GlobalScale);
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

    #endregion

    #region UI - 顶部栏 & 主布局 & 侧边栏

    private void DrawTopBar()
    {
        using var child = ImRaii.Child("##TopBar", new Vector2(0f, ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2f), true, ImGuiWindowFlags.NoScrollbar);
        if (!child) return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(11910)}: {prismBoxItemCount} / {LuminaWrapper.GetAddonText(12216)}: {cabinetItemCount} / {LuminaWrapper.GetAddonText(929)}: {StoredItemCount}");

        if (ImGui.Button(Lang.Get("Refresh")))
            StartRefreshAll();
    
        ImGui.SameLine();

        var searchWidth = MathF.Min(
            ImGui.GetContentRegionAvail().X * 0.35f,
            ImGui.GetContentRegionAvail().X);
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

        var tableFlags = ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.SizingStretchProp |
                         ImGuiTableFlags.Resizable;

        using var table = ImRaii.Table("##UnifiedMainTable", 3, tableFlags, contentSize);
        if (!table) return;

        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(14370), ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn(Lang.Get("UnifiedGlamourManager-FilteredResult"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(2154), ImGuiTableColumnFlags.WidthStretch, 0.7f);

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

        SectionTitle(LuminaWrapper.GetAddonText(14370));
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
        if (ImGui.Checkbox(Lang.Get("UnifiedGlamourManager-EnableLevelRange"), ref enableLevelFilter))
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
            MarkFilteredItemsDirty(clearJobCache: true);

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

    #endregion

    #region UI - 物品列表 & 卡片 & 网格

    private void DrawItemList()
    {
        EnsureFilteredItems();

        using var listPanel = ImRaii.Child("##ListPanel", Vector2.Zero, true);
        if (!listPanel) return;

        ImGui.TextColored(TITLE_COLOR, Lang.Get("UnifiedGlamourManager-FilteredResult"));

        ImGui.Separator();

        var tabSize = new Vector2(
            (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f,
            ImGui.GetFrameHeight());

        foreach (var (label, gridView) in new[]
        {
            (Lang.Get("List"), false),
            (Lang.Get("Icon"), true)
        })
        {
            var active = useGridView == gridView;
            var pos = ImGui.GetCursorScreenPos();

            if (ImGui.InvisibleButton($"##ViewMode{gridView}", tabSize))
                useGridView = gridView;

            ImGui.GetWindowDrawList().AddText(
                pos + new Vector2((tabSize.X - ImGui.CalcTextSize(label).X) * 0.5f, (tabSize.Y - ImGui.CalcTextSize(label).Y) * 0.5f),
                ImGui.GetColorU32(active ? BUTTON_ACTIVE_COLOR : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]),
                label);

            if (active)
                ImGui.GetWindowDrawList().AddLine(
                    pos + new Vector2(tabSize.X * 0.25f, tabSize.Y - 1f),
                    pos + new Vector2(tabSize.X * 0.75f, tabSize.Y - 1f),
                    ImGui.GetColorU32(BUTTON_ACTIVE_COLOR),
                    2f);

            if (!gridView)
                ImGui.SameLine();
        }
        
        ImGui.Separator();
        ImGui.Spacing();

        var showFavoriteFooter = sourceFilter == SourceFilter.Favorite;
        var footerHeight       = showFavoriteFooter ? ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y: 0f;

        using (var itemList = ImRaii.Child(
                "##UnifiedItemList",
                showFavoriteFooter ? new Vector2(0f, -footerHeight) : Vector2.Zero,
                false))
        {
            if (isRefreshingItems)
                ImGui.TextDisabled(Lang.Get("Loading"));
            else if (filteredItems.Count == 0)
                ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-NoSearchResult"));
            else if (useGridView)
                DrawItemGrid(filteredItems);
            else
                DrawItemCardsVirtualized(filteredItems);
        }

        if (showFavoriteFooter)
        {
            ImGui.Separator();
            ImGui.TextColored(GOLD_COLOR, $"{Lang.Get("UnifiedGlamourManager-FilteredFavoriteCount")}: {filteredItems.Select(static x => x.ItemID).Distinct().Count()}");

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

        var rowHeight      = MathF.Max(ImGui.GetFrameHeight() * 1.6f + ImGui.GetStyle().WindowPadding.Y * 2f, ImGui.GetTextLineHeightWithSpacing() * 2f + ImGui.GetStyle().WindowPadding.Y * 2f) + ImGui.GetStyle().ItemSpacing.Y;
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
        var totalHeight     = filtered.Count * rowHeight;
        var remainingHeight = totalHeight - drawnHeight;
        if (remainingHeight > 0f)
            ImGui.Dummy(new Vector2(0f, remainingHeight));

        if (ImGui.GetCursorPosY() < startCursorPos.Y + totalHeight)
            ImGui.SetCursorPosY(startCursorPos.Y + totalHeight);
    }

    private void DrawItemCard(UnifiedItem item)
    {
        var favorite  = IsFavorite(item.ItemID);
        var selected  = selectedItem != null && IsSameSelectableItem(selectedItem, item);
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var iconSize  = ImGui.GetFrameHeight() * 1.6f;

        using var id      = ImRaii.PushId($"{item.ItemID}_{item.PrismBoxIndex}_{item.IsSetPart}_{item.ParentSetItemID}");
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, NORMAL_CARD_COLOR);
        using var border  = ImRaii.PushColor(ImGuiCol.Border, GetCardBorderColor(selected, favorite));
        using var child   = ImRaii.Child("##ItemCard", new Vector2(cardWidth, MathF.Max(ImGui.GetFrameHeight() * 1.6f + ImGui.GetStyle().WindowPadding.Y * 2f, ImGui.GetTextLineHeightWithSpacing() * 2f + ImGui.GetStyle().WindowPadding.Y * 2f)), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
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

        ImGui.Dummy(new Vector2(0f, ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawFavoriteButton(UnifiedItem item, bool favorite, float iconSize)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, NORMAL_CARD_COLOR)
                                  .Push(ImGuiCol.ButtonHovered, FRAME_BG_COLOR)
                                  .Push(ImGuiCol.ButtonActive, BUTTON_ACCENT_COLOR)
                                  .Push(ImGuiCol.Text, favorite ? GOLD_COLOR : STAR_OFF_COLOR);

        if (ImGui.Button(favorite ? FAVORITE_ICON_ON : FAVORITE_ICON_OFF, new Vector2(ImGui.GetFrameHeight(), iconSize)))
            ToggleFavorite(item);
    }

    private void DrawItemCardInfo(UnifiedItem item, bool selected, bool favorite)
    {
        using var group = ImRaii.Group();

        var titleColor = selected ? SELECTED_BORDER_COLOR :
            favorite || !item.IsSetPart ? KnownColor.White.ToVector4() : SOFT_ACCENT_COLOR;

        using (ImRaii.PushColor(ImGuiCol.Text, titleColor))
            ImGui.TextUnformatted(item.Name);

        //加上了当前衣服的染色情况
        LuminaGetter.TryGetRow<Stain>(item.Stain0ID, out var stain0);
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15970)}: {stain0.Name.ExtractText()}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(stain0.Color.ToVector4().Z, stain0.Color.ToVector4().Y, stain0.Color.ToVector4().X, 1f), "■");
        ImGui.SameLine();
        LuminaGetter.TryGetRow<Stain>(item.Stain1ID, out var stain1);
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15971)}: {stain1.Name.ExtractText()}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(stain1.Color.ToVector4().Z, stain1.Color.ToVector4().Y, stain1.Color.ToVector4().X, 1f), "■");
    }

    private void DrawItemGrid(IReadOnlyList<UnifiedItem> filtered)
    {
        if (filtered.Count == 0) return;

        var spacing        = ImGui.GetStyle().ItemSpacing.X;
        var minCellSize    = ImGui.GetFrameHeight() * 1.8f;
        var maxCellSize    = ImGui.GetFrameHeight() * 2.3f;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var columns        = Math.Max(1, (int)MathF.Floor((availableWidth + spacing) / (minCellSize + spacing)));
        var cellSize       = MathF.Floor((availableWidth - spacing * (columns - 1)) / columns);
        cellSize           = Math.Clamp(cellSize, minCellSize, maxCellSize);

        var iconSize        = MathF.Max(ImGui.GetFrameHeight() * 1.3f, cellSize - ImGui.GetStyle().FramePadding.X * 2f);
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
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index < 0 || index >= filtered.Count)
                    continue;

                var item = filtered[index];
                var pos  = new Vector2(start.X + col * (cellSize + spacing), start.Y + row * rowHeight);
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

        var hovered  = ImGui.IsItemHovered();
        var selected = selectedItem != null && IsSameSelectableItem(selectedItem, item);
        var favorite = IsFavorite(item.ItemID);
        DrawItemBackground(drawList, pos, pos + new Vector2(cellSize, cellSize), selected, favorite, hovered);

        var halfDiff = (cellSize - iconSize) * 0.5f;
        ImGui.SetCursorScreenPos(pos + new Vector2(halfDiff, halfDiff));
        DrawItemIcon(item.IconID, iconSize);

        if (favorite)
        {
            var starSize = ImGui.CalcTextSize(FAVORITE_ICON_ON);
            drawList.AddText(
                pos + new Vector2(cellSize - starSize.X - ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().FramePadding.Y),
                ImGui.ColorConvertFloat4ToU32(GOLD_COLOR),
                FAVORITE_ICON_ON);
        }

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

    private void DrawGridTooltip(UnifiedItem item)
    {
        using var tooltip = ImRaii.Tooltip();

        ImGui.TextColored(TITLE_COLOR, item.Name);

        //加上了当前衣服的染色情况
        LuminaGetter.TryGetRow<Stain>(item.Stain0ID, out var stain0);
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15970)}: {stain0.Name.ExtractText()}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(stain0.Color.ToVector4().Z, stain0.Color.ToVector4().Y, stain0.Color.ToVector4().X, 1f), "■");

        LuminaGetter.TryGetRow<Stain>(item.Stain1ID, out var stain1);
        ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15971)}: {stain1.Name.ExtractText()}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(stain1.Color.ToVector4().Z, stain1.Color.ToVector4().Y, stain1.Color.ToVector4().X, 1f), "■");

        ImGui.Separator();
        //增加一个网格的收藏提示
        ImGui.TextDisabled(Lang.Get("UnifiedGlamourManager-RightClickFavoriteHint"));

    }

    private static bool IsSameSelectableItem(UnifiedItem a, UnifiedItem b) =>
        a.ItemID == b.ItemID &&
        a.PrismBoxIndex == b.PrismBoxIndex &&
        a.IsSetPart == b.IsSetPart &&
        a.ParentSetItemID == b.ParentSetItemID;

    #endregion

    #region UI - 当前选中物品 & 弹窗
    private void DrawSelectedPanel()
    {
        using var child = ImRaii.Child("##SelectedPanel", Vector2.Zero, true);
        if (!child) return;

        SectionTitle(LuminaWrapper.GetAddonText(2154));

        if (selectedItem == null)
        {
            ImGui.TextDisabled(LuminaWrapper.GetAddonText(4764));
            return;
        }

        var item = selectedItem;
        DrawSelectedItemHeader(item);

        ImGui.TextColored(SOFT_ACCENT_COLOR, $"{Lang.Get("UnifiedGlamourManager-GlamourTargetSlot")}: ");
        if (LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategoryRowID, out var category))
        {
            var availableSlots = PlateSlotDefinitions
                .Where(x => x.CanUse(category))
                .Select(x => LuminaWrapper.GetAddonText(x.AddonTextID))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();

            ImGui.TextDisabled(availableSlots.Length > 0
                ? string.Join(" / ", availableSlots)
                : Lang.Get("Unknown"));
        }

        ImGui.Spacing();

        if (item.IsSetPart)
        {
            ImGui.TextColored(SOFT_ACCENT_COLOR, $"{LuminaWrapper.GetAddonText(15624)}: ");
            ImGui.TextDisabled(item.ParentSetName);
            ImGui.Spacing();
        }

        if (item.IsSetContainer)
        {
            RedTip(LuminaWrapper.GetAddonText(15624));
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        //原来的SelectAction()
        if (ImGui.Button(LuminaWrapper.GetAddonText(159), new Vector2(-1f, 0f)))
            ImGui.SetClipboardText(item.Name);

        if (ImGui.Button(LuminaWrapper.GetAddonText(102590), new Vector2(-1f, 0f)))
            selectedItem = null;
    }

    private void DrawSelectedItemHeader(UnifiedItem item)
    {
        DrawItemIcon(item.IconID, ImGui.GetFrameHeight());
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.TextColored(SOFT_ACCENT_COLOR, item.Name);
            ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(335)}: {item.LevelEquip}");
            ImGui.TextDisabled($"{Lang.Get("UnifiedGlamourManager-GlamourSource")}: {GetSourceLabel(item)}");

            //加上了当前衣服的染色情况
            LuminaGetter.TryGetRow<Stain>(item.Stain0ID, out var stain0);
            ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15970)}: {stain0.Name.ExtractText()}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(stain0.Color.ToVector4().Z, stain0.Color.ToVector4().Y, stain0.Color.ToVector4().X, 1f), "■");

            LuminaGetter.TryGetRow<Stain>(item.Stain1ID, out var stain1);
            ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(15971)}: {stain1.Name.ExtractText()}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(stain1.Color.ToVector4().Z, stain1.Color.ToVector4().Y, stain1.Color.ToVector4().X, 1f), "■");
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

        var popupOpen   = true;
        using var popup = ImRaii.PopupModal(
            $"{Lang.Get("Clear")}###ClearFavoritesConfirm",
            ref popupOpen);
        if (!popup) return;

        ImGui.TextColored(ERROR_COLOR, Lang.Get("UnifiedGlamourManager-ClearFavoriteConfirm"));
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

    #endregion

    #region 枚举 & 模型

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
        public string SetPartLabel { get; set; }  = string.Empty;

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

    #endregion

    #region 常量

    private const string PLATE_EDITOR_ADDON_NAME            = nameof(MiragePrismMiragePlate);
    private const uint MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW    = 0;
    private const string FAVORITE_ICON_ON                   = "★";
    private const string FAVORITE_ICON_OFF                  = "☆";

    private const int TASK_TIMEOUT_MS            = 30_000;
    private const int REFRESH_STEP_DELAY_MS      = 1;
    private const int APPLY_RETRY_DELAY_MS       = 50;
    private const int DEFAULT_MIN_EQUIP_LEVEL    = 1;
    private const int DEFAULT_MAX_EQUIP_LEVEL    = 100;
    private const int MAX_EQUIP_LEVEL_INPUT      = 999;

    private const uint PRISM_BOX_CAPACITY = 800;
    
    private const int VIRTUALIZED_LIST_BUFFER_ROWS = 3;
    private const int VIRTUALIZED_GRID_BUFFER_ROWS = 2;

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

    private static string[] CreateSetRelationFilterNames() =>
    [
        Lang.Get("All"),
        Lang.Get("UnifiedGlamourManager-SetRelatedOnly"),
        Lang.Get("UnifiedGlamourManager-NonSetOnly")
    ];

    private static readonly Vector4 TITLE_COLOR                = KnownColor.HotPink.ToVector4();
    private static readonly Vector4 SELECTED_COLOR             = KnownColor.MediumVioletRed.ToVector4() with { W = 0.65f };
    private static readonly Vector4 BUTTON_ACCENT_COLOR        = KnownColor.PaleVioletRed.ToVector4() with { W = 0.4f };
    private static readonly Vector4 BUTTON_ACTIVE_COLOR        = KnownColor.HotPink.ToVector4() with { W = 0.78f };
    private static readonly Vector4 SOFT_ACCENT_COLOR          = KnownColor.Plum.ToVector4();
    private static readonly Vector4 GOLD_COLOR                 = KnownColor.Gold.ToVector4();
    private static readonly Vector4 ERROR_COLOR                = KnownColor.Crimson.ToVector4();
    private static readonly Vector4 FRAME_BG_COLOR             = KnownColor.DimGray.ToVector4() with { W = 0.48f };
    private static readonly Vector4 NORMAL_CARD_COLOR          = KnownColor.Black.ToVector4() with { W = 0.34f };
    private static readonly Vector4 NORMAL_CARD_HOVER_COLOR    = KnownColor.Maroon.ToVector4() with { W = 0.26f };
    private static readonly Vector4 FAVORITE_CARD_COLOR        = KnownColor.Gold.ToVector4() with { W = 0.4f };
    private static readonly Vector4 FAVORITE_CARD_HOVER_COLOR  = KnownColor.Goldenrod.ToVector4() with { W = 0.68f };
    private static readonly Vector4 SELECTED_BORDER_COLOR      = KnownColor.Khaki.ToVector4();
    private static readonly Vector4 MUTED_BORDER_COLOR         = KnownColor.DarkGray.ToVector4();
    private static readonly Vector4 STAR_OFF_COLOR             = KnownColor.Gray.ToVector4();

    private static string GetSourceFilterLabel(SourceFilter filter) =>
        filter switch
        {
            SourceFilter.Favorite => Lang.Get("UnifiedGlamourManager-MyFavorites"),
            SourceFilter.PrismBox => LuminaWrapper.GetAddonText(11910),
            SourceFilter.Cabinet  => LuminaWrapper.GetAddonText(12216),
            _                     => Lang.Get("All")
        };

    private static string[] CreateSortModeNames()
    {
        var nameAsc   = $"{Lang.Get("Name")} ({Lang.Get("Ascending")})";
        var nameDesc  = $"{Lang.Get("Name")} ({Lang.Get("Descending")})";
        var levelAsc  = $"{LuminaWrapper.GetAddonText(335)} ({Lang.Get("Ascending")})";
        var levelDesc = $"{LuminaWrapper.GetAddonText(335)} ({Lang.Get("Descending")})";

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
        names[0]  = Lang.Get("All");

        for (var i = 1; i < JobFilterClassJobIDs.Length; i++)
            names[i] = string.Join(" / ", JobFilterClassJobIDs[i].Select(LuminaWrapper.GetJobName));

        return names;
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);

    #endregion
}