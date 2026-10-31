using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using CabinetEx = FFXIVClientStructs.Expansion.FFXIV.Client.Game.UI.Cabinet;
using ItemFinderEx = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Misc.ItemFinderModule;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    private readonly (uint RawItemID, uint Stain0ID, uint Stain1ID)[] prismBoxStains = new (uint, uint, uint)[PRISM_BOX_CAPACITY];

    private readonly List<UnifiedItem> items = [];

    private bool isRefreshingItems;
    private bool isStoredItemsRefreshPending;
    private uint storedItemsSession;
    private bool isPrismBoxCacheAvailable;
    private bool isCabinetCacheAvailable;

    private static bool TryGetStorageFinder
    (
        out ItemFinderEx* finder
    )
    {
        finder = null;
        if (!GameState.IsLoggedIn ||
            ICondition.Instance().IsBetweenAreas)
            return false;

        var contentID = LocalPlayerState.ContentID;
        finder = (ItemFinderEx*)ItemFinderModule.Instance();
        return finder                     != null      &&
               contentID                  != 0         &&
               finder->CharacterContentId == contentID &&
               !finder->IsCacheResetPending;
    }

    private (bool PrismBox, bool Cabinet) GetStorageStatus() =>
        TryGetStorageFinder(out _) ?
            (isPrismBoxCacheAvailable, isCabinetCacheAvailable) :
            (false, false);

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

    private void StartRefreshAll()
    {
        if (IsDisposed ||
            !GameState.IsLoggedIn)
            return;

        if (isRefreshingItems)
        {
            isStoredItemsRefreshPending = true;
            return;
        }

        isRefreshingItems = true;
        var session = storedItemsSession;
        IFramework.Instance().RunOnTick(() => RefreshStoredItems(session, STORAGE_REFRESH_ATTEMPTS), delayTicks: 1);
    }

    private void RefreshStoredItems
    (
        uint                                                                  session,
        int                                                                   remainingAttempts,
        (GameCabinet.CabinetState Base, GameCabinet.CabinetState Additional)? previousCabinetState = null
    )
    {
        if (session != storedItemsSession)
            return;

        var                                                                   retry            = false;
        (GameCabinet.CabinetState Base, GameCabinet.CabinetState Additional)? nextCabinetState = null;

        try
        {
            if (IsDisposed ||
                !GameState.IsLoggedIn)
                return;

            if (!TryGetStorageFinder(out var finder))
            {
                retry = true;
                return;
            }

            var cabinet = (CabinetEx*)&UIState.Instance()->Cabinet;

            var cabinetState = (cabinet->State, cabinet->AdditionalItemsState);

            if (!isStoredItemsRefreshPending         &&
                previousCabinetState == cabinetState &&
                (cabinet->State                == GameCabinet.CabinetState.Requested ||
                 cabinet->AdditionalItemsState == GameCabinet.CabinetState.Requested))
            {
                retry = true;
                return;
            }

            if (cabinet->State == GameCabinet.CabinetState.Invalid)
                cabinet->RequestBaseItems();
            if (cabinet->AdditionalItemsState == GameCabinet.CabinetState.Invalid)
                cabinet->RequestAdditionalItems();

            isStoredItemsRefreshPending = false;
            nextCabinetState            = (cabinet->State, cabinet->AdditionalItemsState);

            finder->SyncGlamourDresserCache();
            finder->SyncCabinetCache();

            List<UnifiedItem> refreshedItems = [];
            LoadPrismBoxItems(finder, refreshedItems);
            LoadCabinetItems(finder, refreshedItems);
            MergeItems(refreshedItems);
            selectedItem = selectedItem == null ?
                               null :
                               refreshedItems.FirstOrDefault(x => x == selectedItem);
            items.Clear();
            items.AddRange(refreshedItems);
            isPrismBoxCacheAvailable = finder->IsGlamourDresserCached ||
                                       finder->GlamourDresserItemIds.ContainsAnyExcept(0u);
            isCabinetCacheAvailable = cabinet->State == GameCabinet.CabinetState.Loaded &&
                                      (cabinet->AdditionalItemsState == GameCabinet.CabinetState.Loaded ||
                                       finder->CabinetItemUnlockBits.ContainsAnyExcept(0u));
            MarkFilteredItemsDirty(true, true);
            retry = cabinet->State                == GameCabinet.CabinetState.Requested ||
                    cabinet->AdditionalItemsState == GameCabinet.CabinetState.Requested;
        }
        catch (Exception ex)
        {
            DLog.Error("UnifiedGlamourManager refresh failed", ex);
        }
        finally
        {
            isRefreshingItems = retry && remainingAttempts > 1;
            if (isRefreshingItems)
                IFramework.Instance().RunOnTick
                (
                    () => RefreshStoredItems(session, remainingAttempts - 1, nextCabinetState ?? previousCabinetState),
                    TimeSpan.FromMilliseconds(STORAGE_REFRESH_INTERVAL_MS)
                );
        }
    }

    private void LoadPrismBoxItems
    (
        ItemFinderEx*     finder,
        List<UnifiedItem> target
    )
    {
        var liveLoaded = TryGetLoadedMirageManager(out var manager);
        var itemSheet  = LuminaGetter.Get<ItemSheet>();

        for (var index = 0; index < finder->GlamourDresserItemIds.Length; index++)
        {
            var     rawItemID = finder->GlamourDresserItemIds[index];
            ref var stains    = ref prismBoxStains[index];
            if (stains.RawItemID != rawItemID)
                stains = (rawItemID, 0, 0);

            if (rawItemID == 0)
                continue;

            var itemID  = ItemUtil.GetBaseId(rawItemID).ItemId;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null ||
                !TryGetItemName(itemRow.Value, out var name))
                continue;

            if (liveLoaded &&
                manager->PrismBoxItemIds[index] == rawItemID)
                stains = (rawItemID, manager->PrismBoxStain0Ids[index], manager->PrismBoxStain1Ids[index]);

            var missingSlots = finder->GlamourDresserItemSetUnlockBits[index];

            AddStoredItem
            (
                target,
                itemRow.Value,
                name,
                ItemSource.PrismBox,
                itemID,
                rawItemID,
                (uint)index,
                0,
                stains.Stain0ID,
                stains.Stain1ID,
                missingSlots
            );
        }
    }

    private void LoadCabinetItems
    (
        ItemFinderEx*     finder,
        List<UnifiedItem> target
    )
    {
        var cabinet    = (CabinetEx*)&UIState.Instance()->Cabinet;
        var cachedBits = finder->CabinetItemUnlockBits;
        var itemSheet  = LuminaGetter.Get<ItemSheet>();

        foreach (var (cabinetID, itemID) in CabinetItems)
        {
            bool unlocked;

            if (cabinetID < CABINET_CACHE_FIRST_ROW_ID)
                unlocked = cabinet->State == GameCabinet.CabinetState.Loaded &&
                           cabinet->IsItemInCabinet(cabinetID);
            else
            {
                var index = cabinetID - CABINET_CACHE_FIRST_ROW_ID;
                var word  = (int)(index / 32);
                var mask  = 1u << (int)(index % 32);
                unlocked = word                      < cachedBits.Length &&
                           (cachedBits[word] & mask) != 0;
            }

            if (!unlocked)
                continue;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null ||
                !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem(target, itemRow.Value, name, ItemSource.Cabinet, itemID, itemID, 0, cabinetID);
        }

    }

    private void AddStoredItem
    (
        List<UnifiedItem> target,
        ItemSheet         item,
        string            name,
        ItemSource        source,
        uint              itemID,
        uint              rawItemID,
        uint              prismBoxIndex,
        uint              cabinetID,
        uint              stain0ID     = 0,
        uint              stain1ID     = 0,
        ushort            missingSlots = 0
    )
    {
        var setParts = GetSetParts(itemID);

        target.Add
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
            if ((missingSlots & (1 << setPart.SlotIndex)) == 0)
                AddSetPartItem(target, setPart, rawItemID, prismBoxIndex, cabinetID, itemID, name, source);
        }
    }

    private static GameCabinet* GetLoadedCabinet
    (
        uint cabinetID
    )
    {
        var uiState = UIState.Instance();
        if (uiState == null) return null;

        var cabinet = (CabinetEx*)&uiState->Cabinet;
        var state = cabinetID < CABINET_CACHE_FIRST_ROW_ID ?
                        cabinet->State :
                        cabinet->AdditionalItemsState;
        return state == GameCabinet.CabinetState.Loaded ?
                   (GameCabinet*)cabinet :
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
    )
    {
        if (!LuminaGetter.TryGetRow(race, out Race raceRow))
            return Lang.Get("Unknown");

        return sex == FEMALE_SEX ?
                   raceRow.Feminine.ToString() :
                   raceRow.Masculine.ToString();
    }

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

    private void ResetStoredItemsSession()
    {
        storedItemsSession++;
        TaskHelper?.Abort();
        isRefreshingItems           = false;
        isStoredItemsRefreshPending = false;
        isPrismBoxCacheAvailable    = false;
        isCabinetCacheAvailable     = false;
        Array.Clear(prismBoxStains);
        items.Clear();
        filteredItems.Clear();
        selectedItem = null;
        missingApplyItems.Clear();
        openMissingApplyItemsPopup = false;
        missingApplyItemsPopupOpen = false;
        MarkFilteredItemsDirty(true, true);
    }

    #region 常量

    private static readonly Dictionary<uint, uint> CabinetItems =
        LuminaGetter.Get<CabinetSheet>()
                    .Where(x => x.Item.RowId != 0)
                    .ToDictionary(x => x.RowId, x => x.Item.RowId);

    #endregion
}
