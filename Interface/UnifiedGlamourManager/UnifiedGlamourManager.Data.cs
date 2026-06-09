using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private int StoredItemCount =>
        prismBoxItemCount + cabinetItemCount;

    private readonly List<UnifiedItem> items = [];
    private          int               prismBoxItemCount;
    private          int               cabinetItemCount;
    private          bool              isRefreshingItems;

    private static bool TryGetLoadedMirageManager(out MirageManager* manager)
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    private static bool TryGetReadyPlateEditor(out AgentMiragePrismMiragePlate* agent)
    {
        agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null                          &&
               agent->IsAgentActive()                 &&
               agent->Data                    != null &&
               agent->Data->SelectedItemIndex < PlateSlotDefinitions.Length;
    }

    // 数据刷新
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
        MarkFilteredItemsDirty(true, true);

        TaskHelper.Enqueue(() => RunRefreshStep(LoadPrismBoxItems, nameof(LoadPrismBoxItems)), nameof(LoadPrismBoxItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => RunRefreshStep(LoadCabinetItems, nameof(LoadCabinetItems)), nameof(LoadCabinetItems));
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue
        (
            () =>
            {
                try
                {
                    MergeItems();
                    selectedItem = itemToReselect != null
                                       ? items.FirstOrDefault(x => x == itemToReselect)
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
                    MarkFilteredItemsDirty(true, true);
                }
            },
            nameof(MergeItems)
        );
    }

    private static void RunRefreshStep(Action action, string stepName)
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

    // 投影台/收藏柜数据读取
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

            AddStoredItem(itemRow.Value, name, ItemSource.Cabinet, itemID, itemID, 0, cabinetID);
            count++;
        }

        cabinetItemCount = count;
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
        return cabinet->IsCabinetLoaded() ? cabinet : null;
    }

    private static bool TryGetItemName(ItemSheet item, out string name)
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }
}
