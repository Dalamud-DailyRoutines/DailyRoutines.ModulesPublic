using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading.TaskHelper;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private readonly List<UnifiedItem> items = [];
    private          bool              isRefreshingItems;

    private StoredItemSource lastLoadedStorageSources;
    private bool             hasReadPrismBoxItems;
    private bool             hasReadCabinetItems;

    private bool         pendingStorageRefresh;
    private UnifiedItem? pendingReselectItem;
    private TaskHelper?  refreshTaskHelper;

    private void EnsureStoredItemsLoaded()
    {
        if (!IClientState.Instance().IsLoggedIn)
            return;

        var loadedSources = StoredItemSource.None;
        if (TryGetLoadedMirageManager(out _))
            loadedSources |= StoredItemSource.PrismBox;
        if (GetLoadedCabinet() != null)
            loadedSources |= StoredItemSource.Cabinet;

        UpdateStoredItemSources(loadedSources);
    }

    private void UpdateStoredItemSources
    (
        StoredItemSource loadedSources
    )
    {
        if ((lastLoadedStorageSources & ~loadedSources) != StoredItemSource.None)
        {
            pendingReselectItem ??= selectedItem;
            refreshTaskHelper?.Abort();
            ClearStoredItems();
            pendingStorageRefresh = true;
        }

        var newlyLoaded          = (loadedSources & ~lastLoadedStorageSources) != StoredItemSource.None;
        lastLoadedStorageSources = loadedSources;
        if (isRefreshingItems && refreshTaskHelper?.IsBusy != true)
        {
            isRefreshingItems    = false;
            hasReadPrismBoxItems = false;
            hasReadCabinetItems  = false;
            InvalidateOwnedEquipment();
            pendingStorageRefresh = true;
        }

        if (loadedSources != StoredItemSource.None &&
            (newlyLoaded || (pendingStorageRefresh && refreshTaskHelper?.IsBusy != true)))
            StartRefreshAll();
    }

    private void ClearStoredItems()
    {
        isRefreshingItems    = false;
        hasReadPrismBoxItems = false;
        hasReadCabinetItems  = false;
        items.Clear();
        filteredItems.Clear();
        selectedItem = null;
        InvalidateOwnedEquipment();
        presetEditorItemsDirty = true;
        MarkFilteredItemsDirty(true, true);
    }

    private static bool TryGetLoadedMirageManager
    (
        out MirageManager* manager
    )
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    private static bool TryGetReadyPlateEditor
    (
        out AgentMiragePrismMiragePlate* agent
    )
    {
        agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null                          &&
               agent->IsAgentActive()                 &&
               agent->Data                    != null &&
               agent->Data->SelectedItemIndex < PlateSlotDefinitions.Length;
    }

    // 数据刷新
    private void StartRefreshAll
    (
        UnifiedItem? reselectItem = null
    )
    {
        if (!IClientState.Instance().IsLoggedIn)
            return;

        pendingStorageRefresh = true;
        pendingReselectItem   = reselectItem ?? pendingReselectItem ?? selectedItem;
        refreshTaskHelper ??= new()
        {
            TimeoutMS = TASK_TIMEOUT_MS
        };
        if (refreshTaskHelper.IsBusy)
            return;

        var prismLoaded   = TryGetLoadedMirageManager(out _);
        var cabinetLoaded = GetLoadedCabinet() != null;
        if (!prismLoaded && !cabinetLoaded)
        {
            lastLoadedStorageSources = StoredItemSource.None;
            ClearStoredItems();
            return;
        }

        pendingStorageRefresh    = false;
        var itemToReselect       = pendingReselectItem;
        pendingReselectItem      = null;
        lastLoadedStorageSources = StoredItemSource.None;
        if (prismLoaded)
            lastLoadedStorageSources |= StoredItemSource.PrismBox;
        if (cabinetLoaded)
            lastLoadedStorageSources |= StoredItemSource.Cabinet;

        ClearStoredItems();
        isRefreshingItems = true;
        refreshTaskHelper.Enqueue(() => ReadStoredSource(ItemSource.PrismBox), nameof(LoadPrismBoxItems));
        refreshTaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        refreshTaskHelper.Enqueue(() => ReadStoredSource(ItemSource.Cabinet), nameof(LoadCabinetItems));
        refreshTaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        refreshTaskHelper.Enqueue
        (
            () =>
            {
                try
                {
                    MergeItems();
                    selectedItem = itemToReselect != null ?
                                       items.FirstOrDefault(x => x == itemToReselect) :
                                       null;
                }
                catch (Exception ex)
                {
                    DLog.Error("UnifiedGlamourManager refresh merge failed", ex);
                    selectedItem = null;
                }
                finally
                {
                    isRefreshingItems      = false;
                    presetEditorItemsDirty = true;
                    InvalidateOwnedEquipment();
                    MarkFilteredItemsDirty(true, true);
                }
            },
            nameof(MergeItems)
        );
    }

    private void ReadStoredSource
    (
        ItemSource itemSource
    )
    {
        var isPrismBox = itemSource == ItemSource.PrismBox;
        if (isPrismBox ?
                !TryGetLoadedMirageManager(out _) :
                GetLoadedCabinet() == null)
        {
            return;
        }

        try
        {
            if (isPrismBox)
            {
                LoadPrismBoxItems();
                hasReadPrismBoxItems = true;
            }
            else
            {
                LoadCabinetItems();
                hasReadCabinetItems = true;
            }
        }
        catch (Exception ex)
        {
            if (isPrismBox)
                hasReadPrismBoxItems = false;
            else
                hasReadCabinetItems = false;
            DLog.Error($"UnifiedGlamourManager refresh step failed: {itemSource}", ex);
        }
    }

    // 投影台/收藏柜数据读取
    private void LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager)) return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0) continue;

            var itemID  = ItemUtil.GetBaseId(rawItemID).ItemId;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem
            (
                itemRow.Value,
                name,
                ItemSource.PrismBox,
                itemID,
                rawItemID,
                i,
                0,
                manager->PrismBoxStain0Ids[(int)i],
                manager->PrismBoxStain1Ids[(int)i],
                manager
            );

        }

    }

    private void LoadCabinetItems()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null) return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID    = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetID)) continue;

            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem(itemRow.Value, name, ItemSource.Cabinet, itemID, itemID, 0, cabinetID);
        }
    }

    private void AddStoredItem
    (
        ItemSheet      item,
        string         name,
        ItemSource     source,
        uint           itemID,
        uint           rawItemID,
        uint           prismBoxIndex,
        uint           cabinetID,
        uint           stain0ID = 0,
        uint           stain1ID = 0,
        MirageManager* manager  = null
    )
    {
        var setParts = GetSetParts(itemID);

        items.Add
        (
            UnifiedItem.Create
            (
                itemID,
                rawItemID,
                name,
                item,
                source,
                prismBoxIndex,
                cabinetID,
                stain0ID,
                stain1ID,
                setParts.Count > 0
            )
        );

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
        return cabinet->IsCabinetLoaded() ?
                   cabinet :
                   null;
    }

    private static bool TryGetItemName
    (
        ItemSheet  item,
        out string name
    )
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    private static List<PlateItemInfo> GetCurrentPlateItems()
    {
        if (!TryGetReadyPlateEditor(out var agent)) return [];

        List<PlateItemInfo> items = [];

        var currentItems = agent->Data->CurrentItems;

        for (var i = 0; i < currentItems.Length; i++)
        {
            var item = currentItems[i];
            if (item.ItemId == 0) continue;

            items.Add
            (
                new
                (
                    (uint)i,
                    item.ItemId,
                    item.StainIds[0],
                    item.StainIds[1]
                )
            );
        }

        return items;
    }

    private static List<PlateItemInfo> GetInspectPlateItems()
    {
        if (!InventoryType.Examine.TryGetItems
            (
                x => x.ItemId != 0 && PlateSlots.Contains(x.Slot),
                out var inspectItems
            ))
            return [];

        List<PlateItemInfo> items = [];

        foreach (var item in inspectItems)
        {
            var itemID = item.GlamourId != 0 ?
                             item.GlamourId :
                             item.ItemId;
            var plateSlot = (uint)Array.IndexOf(PlateSlots, item.Slot);
            if (itemID == 0) continue;

            items.Add
            (
                new
                (
                    plateSlot,
                    itemID,
                    item.Stains[0],
                    item.Stains[1]
                )
            );
        }

        return items;
    }

    private static string GetRaceName
    (
        byte race,
        byte sex
    ) =>
        !LuminaGetter.TryGetRow(race, out Race raceRow) ? Lang.Get("Unknown")
        : sex == FEMALE_SEX                             ? raceRow.Feminine.ToString()    ?? Lang.Get("Unknown")
                                                          : raceRow.Masculine.ToString() ?? Lang.Get("Unknown");

    private static string GetSexName
    (
        byte sex
    ) =>
        sex == FEMALE_SEX ?
            LuminaWrapper.GetAddonText(15609) :
            LuminaWrapper.GetAddonText(15608);

    private static string GetDefaultPresetTitle
    (
        PresetSource source
    ) =>
        source switch
        {
            PresetSource.Self        => $"{Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")}-{LuminaWrapper.GetAddonText(3991)}",
            PresetSource.OtherPlayer => $"{Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")}-{LuminaWrapper.GetAddonText(7979)}",
            _                        => Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")
        };
}
