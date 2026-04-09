using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoGlamourDresser : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGlamourDresserTitle"),
        Description = Lang.Get("AutoGlamourDresserDescription"),
        Category    = ModuleCategory.UIOperation
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeoutMS = 5_000 };
        config =   Config.Load(this) ?? new();

        LogMessageManager.Instance().RegPost(OnReceiveLogMessage);

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.RegisterListener(AddonEvent.PreSetup,    "MiragePrismPrismBox",         OnMiragePrismPrismBox);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismPrismSetConvert",  OnAddonMiragePrismPrismSetConvert);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MiragePrismPrismSetConvert",  OnAddonMiragePrismPrismSetConvert);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   "MiragePrismPrismSetConvertC", OnAddonMiragePrismPrismSetConvertC);

        if (MiragePrismPrismSetConvert->IsAddonAndNodesReady())
            OnAddonMiragePrismPrismSetConvert(AddonEvent.PostRefresh, null);

        if (MiragePrismPrismSetConvertC->IsAddonAndNodesReady())
            OnAddonMiragePrismPrismSetConvertC(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit()
    {
        LogMessageManager.Instance().Unreg(OnReceiveLogMessage);

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.UnregisterListener(OnMiragePrismPrismBox, OnAddonMiragePrismPrismSetConvert, OnAddonMiragePrismPrismSetConvertC);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoGlamourDresser-AutoSwitchJobCategory"), ref config.AutoSwitchJobCategory))
            config.Save(this);

        ImGui.NewLine();
        
        ImGui.TextUnformatted(Lang.Get("Operation"));

        using (ImRaii.PushIndent())
        using (ImRaii.Disabled(TaskHelper.IsBusy || !MiragePrismPrismBox->IsAddonAndNodesReady()))
        {
            if (ImGui.Button(Lang.Get("AutoGlamourDresser-RemoveDuplicateItems")))
                StartRestoreOperation(FilterDuplicateItems);

            if (ImGui.Button(Lang.Get("AutoGlamourDresser-RemoveDuplicateModels")))
                StartRestoreOperation(FilterDuplicateModelItems);

            if (ImGui.Button(Lang.Get("AutoGlamourDresser-RemoveArmoireItems")))
                StartRestoreOperation(FilterArmoireItems);

            if (ImGui.Button(Lang.Get("AutoGlamourDresser-RemoveSetItems")))
                StartAttireItems();

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("AutoGlamourDresser-OnlyRemoveSetItemsWhenComplete"), ref config.OnlyRemoveSetItemsWhenComplete))
                    config.Save(this);

                ImGui.SameLine();
                if (ImGui.Checkbox(Lang.Get("AutoGlamourDresser-SkipItemsWithStain"), ref config.SkipItemsWithStain))
                    config.Save(this);
            }
        }

        using (ImRaii.PushIndent())
        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            ImGui.Spacing();
            
            if (ImGui.Button(Lang.Get("Stop")))
                TaskHelper.Abort();
        }
    }
    
    private void OnMiragePrismPrismBox(AddonEvent type, AddonArgs? args)
    {
        if (!config.AutoSwitchJobCategory) return;

        var addon = (AddonMiragePrismPrismBox*)MiragePrismPrismBox;
        if (addon == null) return;

        addon->Param = (int)LocalPlayerState.ClassJob;
    }

    private void StartRestoreOperation(Func<List<PrismBoxItemInfo>, List<uint>> filter)
    {
        TaskHelper.Abort();
        QueueRestoreOperation(filter);
    }

    private void QueueRestoreOperation(Func<List<PrismBoxItemInfo>, List<uint>> filter)
    {
        if (!TryGetLoadedMirageManager(out _)) return;

        var itemIndexes = filter(GetPrismBoxItems());
        if (itemIndexes.Count == 0) return;

        EnqueueRestoreItems(itemIndexes);
        TaskHelper.Enqueue(() => QueueRestoreOperation(filter));
    }

    private static List<uint> FilterDuplicateItems(List<PrismBoxItemInfo> items)
    {
        List<uint>    itemIndexesToRestore = [];
        HashSet<uint> uniqueItemIDs        = [];

        foreach (var item in items)
        {
            if (!uniqueItemIDs.Add(item.ItemID))
                itemIndexesToRestore.Add(item.Index);
        }

        return itemIndexesToRestore;
    }

    private static List<uint> FilterDuplicateModelItems(List<PrismBoxItemInfo> items)
    {
        List<uint>      itemIndexesToRestore = [];
        HashSet<uint>   uniqueItemIDs        = [];
        HashSet<string> uniqueModels         = [];

        foreach (var item in items)
        {
            if (!item.IsGlamourModelValid) continue;

            var modelKey = $"{item.ModelMain}_{item.EquipSlotCategoryRowID}";
            if (!uniqueItemIDs.Add(item.ItemID) || !uniqueModels.Add(modelKey))
                itemIndexesToRestore.Add(item.Index);
        }

        return itemIndexesToRestore;
    }

    private static List<uint> FilterArmoireItems(List<PrismBoxItemInfo> items) =>
        items.Where(item => ArmoireAvailableItems.Contains(item.ItemID))
             .Select(item => item.Index)
             .ToList();

    private void StartAttireItems()
    {
        TaskHelper.Abort();
        RestoreItemsFromPrismBox();
    }

    private void RestoreItemsFromPrismBox()
    {
        if (!TryGetLoadedMirageManager(out _)) return;

        var items = GetPrismBoxItems();
        if (items.Count == 0) return;

        Dictionary<uint, PrismBoxItemInfo> itemsByID = [];
        foreach (var item in items)
            itemsByID.TryAdd(item.ItemID, item);

        Dictionary<MirageItemSet, List<uint>> validItemSets = [];

        foreach (var mirageItemSet in MirageItemSets)
        {
            if (!mirageItemSet.TryGetRestorableIndexes(itemsByID, config.OnlyRemoveSetItemsWhenComplete, config.SkipItemsWithStain, out var itemIndexes))
                continue;

            validItemSets[mirageItemSet] = itemIndexes;
        }

        if (validItemSets.Count == 0) return;

        foreach (var (mirageItemSet, itemIndexes) in validItemSets)
        {
            EnqueueRestoreItems(itemIndexes, true);
            TaskHelper.Enqueue
            (() => NotifyHelper.Instance().Chat
                 (Lang.GetSe("AutoGlamourDresser-SetItemsMessage", new ItemPayload(mirageItemSet.Set.ID), itemIndexes.Count, mirageItemSet.SetItems.Count))
            );
        }

        TaskHelper.Enqueue(RestoreItemsFromPrismBox);
    }

    private void OnReceiveLogMessage(uint logMessageID, LogMessageQueueItem values)
    {
        if (logMessageID != 4280) return;
        TaskHelper.Abort();
    }

    private void OnAddonMiragePrismPrismSetConvert(AddonEvent type, AddonArgs? args)
    {
        if (MiragePrismPrismSetConvert == null) return;
        FillMiragePrismBoxSet();
    }

    private static void OnAddonMiragePrismPrismSetConvertC(AddonEvent type, AddonArgs? args)
    {
        if (MiragePrismPrismSetConvertC == null) return;
        MiragePrismPrismSetConvertC->Callback(0);
    }

    private void FillMiragePrismBoxSet()
    {
        if (!MiragePrismPrismSetConvert->IsAddonAndNodesReady() || TaskHelper.IsBusy) return;

        var slotCount = MiragePrismPrismSetConvert->AtkValues[20].UInt;
        if (slotCount == 0) return;

        List<int> slotsToFill = [];

        for (var i = 0; i < slotCount; i++)
        {
            var inventoryType = MiragePrismPrismSetConvert->AtkValues[25 + i * 7].UInt;
            if (inventoryType != 9999) continue;

            var unkParam0 = MiragePrismPrismSetConvert->AtkValues[26 + i * 7].UInt;
            var unkParam1 = MiragePrismPrismSetConvert->AtkValues[27 + i * 7].UInt;
            if (unkParam0 == unkParam1 && unkParam1 == 0) continue;

            slotsToFill.Add(i);
        }

        if (slotsToFill.Count == 0) return;

        foreach (var slotIndex in slotsToFill)
        {
            var index = slotIndex;
            TaskHelper.Enqueue
            (() =>
                {
                    if (!ContextIconMenu->IsAddonAndNodesReady())
                    {
                        MiragePrismPrismSetConvert->Callback(13, index);
                        return false;
                    }

                    ContextIconMenu->Callback(0, 0, 1021003u, 0u, 0);
                    return true;
                }
            );
        }

        TaskHelper.DelayNext(100);
        TaskHelper.Enqueue(() => AgentId.MiragePrismPrismSetConvert.SendEvent(1, 14));
    }

    private static bool TryGetLoadedMirageManager(out MirageManager* instance)
    {
        instance = MirageManager.Instance();
        return instance != null && instance->PrismBoxRequested && instance->PrismBoxLoaded;
    }

    private static List<PrismBoxItemInfo> GetPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var instance))
            return [];

        List<PrismBoxItemInfo> items = [];

        for (var i = 0U; i < 800; i++)
        {
            var rawItemID = instance->PrismBoxItemIds[(int)i];
            if (rawItemID == 0) continue;

            var itemID   = rawItemID % 100_0000;
            var stain0ID = instance->PrismBoxStain0Ids[(int)i];
            var stain1ID = instance->PrismBoxStain1Ids[(int)i];

            if (LuminaGetter.TryGetRow(itemID, out Item itemRow))
            {
                items.Add
                (
                    new
                    (
                        i,
                        rawItemID,
                        itemID,
                        stain0ID,
                        stain1ID,
                        itemRow.IsGlamorous,
                        itemRow.ModelMain,
                        itemRow.EquipSlotCategory.RowId
                    )
                );
                continue;
            }

            items.Add(new(i, rawItemID, itemID, stain0ID, stain1ID, false, 0, 0));
        }

        return items;
    }

    private void EnqueueRestoreItems(IEnumerable<uint> itemIndexes, bool abortWhenInventoryFull = false)
    {
        if (!TryGetLoadedMirageManager(out var instance)) return;

        foreach (var itemIndex in itemIndexes)
        {
            if (abortWhenInventoryFull)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (!Inventories.Player.IsFull()) return;
                        TaskHelper.Abort();
                    }
                );
            }

            TaskHelper.Enqueue(() => instance->RestorePrismBoxItem(itemIndex));
        }
    }

    private class Config : ModuleConfig
    {
        public bool AutoSwitchJobCategory  = true;
        public bool OnlyRemoveSetItemsWhenComplete = true;
        public bool SkipItemsWithStain     = true;
    }

    private readonly record struct PrismBoxItemInfo
    (
        uint  Index,
        uint  RawItemID,
        uint  ItemID,
        uint  Stain0ID,
        uint  Stain1ID,
        bool  IsGlamorous,
        ulong ModelMain,
        uint  EquipSlotCategoryRowID
    )
    {
        public bool IsGlamourModelValid =>
            IsGlamorous                 &&
            ModelMain              != 0 &&
            EquipSlotCategoryRowID != 0;

        public bool IsStained =>
            Stain0ID != 0 || Stain1ID != 0;
    }

    private sealed record MirageItemSet
    (
        (uint ID, string Name)       Set,
        List<(uint ID, string Name)> SetItems
    )
    {
        public static MirageItemSet? Parse(MirageStoreSetItem row)
        {
            if (!LuminaGetter.TryGetRow<Item>(row.RowId, out var setItemRow)) return null;

            var setName = setItemRow.Name.ToString();
            if (string.IsNullOrWhiteSpace(setName)) return null;

            List<uint> setItemsID =
            [
                row.Body.RowId, 
                row.Bracelets.RowId, 
                row.Earrings.RowId, 
                row.Feet.RowId, 
                row.Hands.RowId,
                row.Head.RowId, 
                row.Legs.RowId,
                row.Necklace.RowId,
                row.Ring.RowId,
                row.MainHand.RowId,
                row.OffHand.RowId
            ];

            var filteredItems = setItemsID
                                .Where(x => x > 1 && LuminaGetter.TryGetRow<Item>(x, out _))
                                .Select(x => (x, LuminaGetter.GetRow<Item>(x)!.Value.Name.ToString()))
                                .ToList();
            if (filteredItems.Count == 0) return null;

            return new(new(row.RowId, setName), filteredItems);
        }

        public bool TryGetRestorableIndexes
        (
            IReadOnlyDictionary<uint, PrismBoxItemInfo> itemsByID,
            bool                                        onlyRemoveWhenComplete,
            bool                                        skipItemsWithStain,
            out List<uint>                              itemIndexes
        )
        {
            itemIndexes = [];

            foreach (var setItem in SetItems)
            {
                if (!itemsByID.TryGetValue(setItem.ID, out var prismBoxItem))
                {
                    if (onlyRemoveWhenComplete)
                        return false;

                    continue;
                }

                if (skipItemsWithStain && prismBoxItem.IsStained)
                {
                    if (onlyRemoveWhenComplete)
                        return false;

                    continue;
                }

                itemIndexes.Add(prismBoxItem.Index);
            }

            return itemIndexes.Count > 0 && (!onlyRemoveWhenComplete || itemIndexes.Count == SetItems.Count);
        }
    }
    
    #region 常量

    private static readonly FrozenSet<uint> ArmoireAvailableItems =
        LuminaGetter.Get<Cabinet>()
                    .Where(x => x.Item.RowId > 0)
                    .Select(x => x.Item.RowId)
                    .ToFrozenSet();

    private static readonly FrozenSet<MirageItemSet> MirageItemSets =
        LuminaGetter.Get<MirageStoreSetItem>()
                    .Select(MirageItemSet.Parse)
                    .Where(x => x != null)
                    .OfType<MirageItemSet>()
                    .ToFrozenSet();

    #endregion
}
