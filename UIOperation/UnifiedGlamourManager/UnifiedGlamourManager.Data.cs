using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    private sealed class Config : ModuleConfig
    {
        public List<SavedItem> Favorites = [];
        public List<CachedPreviewItem> PreviewItems = [];
        public bool ShowRetainerPreview = true;
        public bool ShowInventoryPreview = true;
    }

    private void SaveConfig()
    {
        NormalizeConfig();
        config.Save(this);
        MarkFilteredItemsDirty();
    }

    private void NormalizeConfig()
    {
        config.Favorites ??= [];
        config.PreviewItems ??= [];

        CleanPreviewCache();
        SyncFavoriteItemIDs();
    }

    private int GetLoadedFavoriteCount()
    {
        if (favoriteItemIDs.Count == 0 || items.Count == 0)
            return 0;

        return items
            .Where(x => IsFavorite(x.ItemID))
            .Select(x => x.ItemID)
            .Distinct()
            .Count();
    }

    private SavedItem? GetSaved(uint itemID)
        => config.Favorites.FirstOrDefault(x => x.ItemID == itemID);

    private bool IsFavorite(uint itemID)
        => favoriteItemIDs.Contains(itemID);

    private void ToggleFavorite(UnifiedItem item)
    {
        var existing = GetSaved(item.ItemID);
        if (existing != null)
        {
            config.Favorites.Remove(existing);
        }
        else
        {
            config.Favorites.Add(new()
            {
                ItemID = item.ItemID,
                Name = item.Name,
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
            if (favorite.ItemID > MIN_VALID_ITEM_ID)
                favoriteItemIDs.Add(favorite.ItemID);
        }
    }

    private void CleanPreviewCache()
    {
        NormalizeRetainerPreviewRecords();
        RemoveInvalidPreviewItems();
        RemoveDuplicatePreviewItems();
    }

    private void NormalizeRetainerPreviewRecords()
    {
        foreach (var item in config.PreviewItems.Where(x => x.Source == PREVIEW_SOURCE_RETAINER))
        {
            item.SourceKey = NormalizeSourceKey(item.SourceKey, item.Owner);

            if (string.IsNullOrWhiteSpace(item.Owner))
                item.Owner = Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");
        }
    }

    private void RemoveInvalidPreviewItems()
        => config.PreviewItems.RemoveAll(x => x.ItemID <= MIN_VALID_ITEM_ID);

    private void RemoveDuplicatePreviewItems()
    {
        config.PreviewItems = config.PreviewItems
            .GroupBy(GetPreviewCacheKey)
            .Select(x => x.OrderByDescending(y => y.UpdatedAt).First())
            .ToList();
    }

    private static string GetPreviewCacheKey(CachedPreviewItem item)
        => $"{item.Source}|{item.SourceKey}|{item.Owner}|{item.SlotIndex}|{item.ItemID}";

    private void StartRefreshAll(UnifiedItem? reselectItem = null)
    {
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };
        TaskHelper.Abort();

        isRefreshingItems = true;
        items.Clear();
        filteredItems.Clear();
        ownedConcreteItemIDs.Clear();
        prismBoxItemCount = 0;
        cabinetItemCount = 0;
        MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);

        TaskHelper.Enqueue(() => RebuildOwnedConcreteItemIndex(), "刷新收藏柜索引");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => LoadPrismBoxItems(), "读取投影台");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(() => LoadCabinetItems(), "读取收藏柜");
        TaskHelper.DelayNext(REFRESH_STEP_DELAY_MS);
        TaskHelper.Enqueue(
            () =>
            {
                MergeItems();
                LoadPreviewItems();

                if (reselectItem != null)
                    ReselectItem(reselectItem);

                isRefreshingItems = false;
                MarkFilteredItemsDirty(clearJobCache: true, clearPlateSlotCache: true);
            },
            "合并装备列表");
    }

    private void ReselectItem(UnifiedItem item)
    {
        selectedItem = items.FirstOrDefault(x =>
            x.ItemID == item.ItemID &&
            x.PrismBoxIndex == item.PrismBoxIndex &&
            x.IsSetPart == item.IsSetPart &&
            x.ParentSetItemID == item.ParentSetItemID);
    }

    private void RebuildOwnedConcreteItemIndex()
    {
        ownedConcreteItemIDs.Clear();

        AddOwnedPrismBoxItemIDs();
        AddOwnedCabinetItemIDs();
    }

    private void AddOwnedPrismBoxItemIDs()
    {
        if (!TryGetLoadedMirageManager(out var manager))
            return;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
            if (itemID > MIN_VALID_ITEM_ID)
                ownedConcreteItemIDs.Add(itemID);
        }
    }

    private void AddOwnedCabinetItemIDs()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var itemID = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetRow.RowId))
                continue;

            ownedConcreteItemIDs.Add(itemID);
        }
    }

    private void LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager))
            return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0)
                continue;

            var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var setParts = GetSetParts(itemID);
            items.Add(CreateUnifiedItem(
                itemID,
                rawItemID,
                name,
                itemRow.Value,
                inPrismBox: true,
                inCabinet: false,
                prismBoxIndex: i,
                cabinetID: 0,
                stain0ID: manager->PrismBoxStain0Ids[(int)i],
                stain1ID: manager->PrismBoxStain1Ids[(int)i],
                isSetContainer: setParts.Count > 0));

            count++;
            AddPrismBoxSetParts(manager, setParts, rawItemID, i, itemID, name);
        }

        prismBoxItemCount = count;
    }

    private void AddPrismBoxSetParts(
        MirageManager* manager,
        List<SetPartInfo> setParts,
        uint rawItemID,
        uint prismBoxIndex,
        uint parentItemID,
        string parentName)
    {
        foreach (var setPart in setParts)
        {
            if (!manager->IsSetSlotUnlocked(prismBoxIndex, setPart.SlotIndex))
                continue;

            AddSetPartItem(
                setPart,
                rawItemID,
                prismBoxIndex,
                cabinetID: 0,
                parentItemID,
                parentName,
                inPrismBox: true,
                inCabinet: false);
        }
    }

    private void LoadCabinetItems()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return;

        var itemSheet = LuminaGetter.Get<ItemSheet>();
        var count = 0;

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetID))
                continue;

            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var setParts = GetSetParts(itemID);
            items.Add(CreateUnifiedItem(
                itemID,
                itemID,
                name,
                itemRow.Value,
                inPrismBox: false,
                inCabinet: true,
                prismBoxIndex: 0,
                cabinetID,
                stain0ID: 0,
                stain1ID: 0,
                isSetContainer: setParts.Count > 0));

            count++;
            AddCabinetSetParts(setParts, itemID, cabinetID, name);
        }

        cabinetItemCount = count;
    }

    private void AddCabinetSetParts(
        List<SetPartInfo> setParts,
        uint parentItemID,
        uint cabinetID,
        string parentName)
    {
        foreach (var setPart in setParts)
        {
            AddSetPartItem(
                setPart,
                rawItemID: parentItemID,
                prismBoxIndex: 0,
                cabinetID,
                parentItemID,
                parentName,
                inPrismBox: false,
                inCabinet: true);
        }
    }

    private static GameCabinet* GetLoadedCabinet()
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return null;

        var cabinet = &uiState->Cabinet;
        return cabinet->IsCabinetLoaded() ? cabinet : null;
    }

    private static List<SetPartInfo> GetSetParts(uint setItemID)
    {
        if (!LuminaGetter.TryGetRow<MirageStoreSetItem>(setItemID, out var row))
            return [];

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

    private static void AddSetPart(
        List<SetPartInfo> parts,
        uint itemID,
        int slotIndex)
    {
        if (itemID <= MIN_VALID_ITEM_ID)
            return;

        if (!LuminaGetter.TryGetRow<ItemSheet>(itemID, out var itemRow) ||
            !TryGetItemName(itemRow, out _))
            return;

        if (!CanItemRowUseSetPartLabel(itemRow, slotIndex))
            return;

        var label = GetNativeItemCategoryName(itemRow);
        if (parts.Any(x => x.ItemID == itemID && x.PartLabel == label))
            return;

        parts.Add(new SetPartInfo(itemID, label, slotIndex));
    }

    private void AddSetPartItem(
        SetPartInfo setPart,
        uint rawItemID,
        uint prismBoxIndex,
        uint cabinetID,
        uint parentItemID,
        string parentName,
        bool inPrismBox,
        bool inCabinet)
    {
        if (!LuminaGetter.TryGetRow<ItemSheet>(setPart.ItemID, out var partRow) ||
            !TryGetItemName(partRow, out var partName))
            return;

        items.Add(CreateUnifiedItem(
            setPart.ItemID,
            rawItemID,
            $"{partName}（{parentName} / {setPart.PartLabel}）",
            partRow,
            inPrismBox,
            inCabinet,
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

    private static bool CanItemRowUseSetPartLabel(ItemSheet item, int slotIndex)
    {
        var category = item.EquipSlotCategory.Value;
        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == slotIndex);

        return !definition.Equals(default(PlateSlotDefinition)) && definition.CanEquip(category);
    }

    private void MergeItems()
    {
        var merged = items
            .GroupBy(GetMergeKey)
            .Select(MergeItemGroup)
            .ToList();

        items.Clear();
        items.AddRange(merged);
    }

    private static UnifiedItem MergeItemGroup(IGrouping<string, UnifiedItem> group)
    {
        var first = group.First();
        if (first.IsSetPart)
            return first;

        var prism = group.FirstOrDefault(x => x.InPrismBox);
        var cabinet = group.FirstOrDefault(x => x.InCabinet);

        first.InPrismBox = prism != null;
        first.InCabinet = cabinet != null;

        if (prism != null)
            MergePrismBoxData(first, prism);

        if (cabinet != null)
            MergeCabinetData(first, cabinet);

        return first;
    }

    private static void MergePrismBoxData(UnifiedItem target, UnifiedItem prism)
    {
        target.RawItemID = prism.RawItemID;
        target.PrismBoxIndex = prism.PrismBoxIndex;
        target.Stain0ID = prism.Stain0ID;
        target.Stain1ID = prism.Stain1ID;
        target.IconID = prism.IconID;
        target.EquipSlotCategoryRowID = prism.EquipSlotCategoryRowID;
        target.ClassJobCategoryRowID = prism.ClassJobCategoryRowID;
        target.ModelMain = prism.ModelMain;
        target.LevelEquip = prism.LevelEquip;
        target.IsSetContainer = prism.IsSetContainer;
    }

    private static void MergeCabinetData(UnifiedItem target, UnifiedItem cabinet)
    {
        target.CabinetID = cabinet.CabinetID;
        target.IconID = target.IconID == 0 ? cabinet.IconID : target.IconID;
        target.EquipSlotCategoryRowID = target.EquipSlotCategoryRowID == 0 ? cabinet.EquipSlotCategoryRowID : target.EquipSlotCategoryRowID;
        target.ClassJobCategoryRowID = target.ClassJobCategoryRowID == 0 ? cabinet.ClassJobCategoryRowID : target.ClassJobCategoryRowID;
        target.ModelMain = target.ModelMain == 0 ? cabinet.ModelMain : target.ModelMain;
        target.LevelEquip = target.LevelEquip == 0 ? cabinet.LevelEquip : target.LevelEquip;
        target.IsSetContainer |= cabinet.IsSetContainer;
    }

    private static string GetMergeKey(UnifiedItem item)
        => item.IsSetPart
            ? $"set:{item.ParentSetItemID}:{item.PrismBoxIndex}:{item.ItemID}"
            : $"item:{item.ItemID}";

    private static UnifiedItem CreateUnifiedItem(
        uint itemID,
        uint rawItemID,
        string name,
        ItemSheet row,
        bool inPrismBox,
        bool inCabinet,
        uint prismBoxIndex,
        uint cabinetID,
        uint stain0ID,
        uint stain1ID,
        bool isSetContainer,
        bool isSetPart = false,
        uint parentSetItemID = 0,
        string parentSetName = "",
        string setPartLabel = "")
        => new()
        {
            ItemID = itemID,
            RawItemID = rawItemID,
            PrismBoxIndex = prismBoxIndex,
            CabinetID = cabinetID,
            Name = name,
            Stain0ID = stain0ID,
            Stain1ID = stain1ID,
            ModelMain = row.ModelMain,
            EquipSlotCategoryRowID = row.EquipSlotCategory.RowId,
            ClassJobCategoryRowID = row.ClassJobCategory.RowId,
            IconID = row.Icon,
            LevelEquip = (uint)row.LevelEquip,
            InPrismBox = inPrismBox,
            InCabinet = inCabinet,
            IsSetContainer = isSetContainer,
            IsSetPart = isSetPart,
            ParentSetItemID = parentSetItemID,
            ParentSetName = parentSetName,
            SetPartLabel = setPartLabel
        };

    private static bool TryGetItemName(ItemSheet item, out string name)
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    private void RecordOpenedPreviewSources()
    {
        var changed = TryRecordInventoryPreview();
        changed |= TryRecordRetainerPreview();

        if (!changed)
            return;

        SaveConfig();

        if (isOpen)
            StartRefreshAll();
    }

    private bool TryRecordInventoryPreview()
    {
        var snapshot = ScanPreviewInventoryItems(INVENTORY_PREVIEW_CONTAINER_TYPES).ToList();
        var fingerprint = BuildSnapshotFingerprint(snapshot);

        if (string.IsNullOrEmpty(fingerprint) || fingerprint == lastInventorySnapshotFingerprint)
            return false;

        lastInventorySnapshotFingerprint = fingerprint;

        var cached = BuildCachedPreviewItems(
            snapshot,
            PREVIEW_SOURCE_INVENTORY,
            Lang.Get("UnifiedGlamourManager-PreviewSourceInventoryAll"),
            PREVIEW_SOURCE_KEY_INVENTORY,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (cached.Count == 0)
            return false;

        config.PreviewItems.RemoveAll(x =>
            x.Source is PREVIEW_SOURCE_INVENTORY or PREVIEW_SOURCE_SADDLEBAG_LEGACY or PREVIEW_SOURCE_ARMORY_LEGACY);

        config.PreviewItems.AddRange(cached);
        return true;
    }

    private bool TryRecordRetainerPreview()
    {
        var snapshot = ScanPreviewInventoryItems(RETAINER_PREVIEW_CONTAINER_TYPES).ToList();
        var fingerprint = BuildSnapshotFingerprint(snapshot);

        if (string.IsNullOrEmpty(fingerprint))
            return false;

        if (!TryGetCurrentRetainerIdentity(out var sourceKey, out var ownerName))
        {
            sourceKey = RETAINER_SOURCE_KEY_UNKNOWN;
            ownerName = Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");
        }

        if (fingerprint == lastRetainerSnapshotFingerprint && sourceKey == lastRetainerSourceKey)
            return false;

        lastRetainerSnapshotFingerprint = fingerprint;

        var cached = BuildCachedPreviewItems(
            snapshot,
            PREVIEW_SOURCE_RETAINER,
            ownerName,
            sourceKey,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (cached.Count == 0)
            return false;

        config.PreviewItems.RemoveAll(x =>
            x.Source == PREVIEW_SOURCE_RETAINER && NormalizeSourceKey(x.SourceKey, x.Owner) == sourceKey);

        config.PreviewItems.AddRange(cached);
        lastRetainerSourceKey = sourceKey;
        return true;
    }

    private bool TryGetCurrentRetainerIdentity(out string sourceKey, out string ownerName)
    {
        sourceKey = string.Empty;
        ownerName = string.Empty;

        try
        {
            var manager = RetainerManager.Instance();
            if (manager == null)
                return false;

            var active = manager->GetActiveRetainer();
            if (active != null && active->RetainerId != 0)
            {
                sourceKey = $"{RETAINER_SOURCE_KEY_PREFIX}{active->RetainerId}";
                ownerName = Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");
                return true;
            }

            var lastSelectedID = manager->LastSelectedRetainerId;
            if (lastSelectedID == 0)
                return false;

            sourceKey = $"{RETAINER_SOURCE_KEY_PREFIX}{lastSelectedID}";
            ownerName = Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");
            return true;
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
            return false;
        }
    }

    private static string NormalizeSourceKey(string? sourceKey, string? owner)
    {
        if (!string.IsNullOrWhiteSpace(sourceKey))
            return sourceKey;

        if (string.IsNullOrWhiteSpace(owner))
            return RETAINER_SOURCE_KEY_UNKNOWN;

        if (owner.StartsWith(RETAINER_RECORD_PREFIX, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(owner[RETAINER_RECORD_PREFIX.Length..], out var index) && index > 0)
            return $"{RETAINER_SOURCE_KEY_PREFIX}{index}";

        return $"{RETAINER_SOURCE_KEY_PREFIX}{owner}";
    }

    private List<CachedPreviewItem> BuildCachedPreviewItems(
        IReadOnlyList<PreviewScanItem> snapshot,
        string source,
        string owner,
        string sourceKey,
        long updatedAt)
    {
        List<CachedPreviewItem> result = [];

        foreach (var scanItem in snapshot)
        {
            if (!LuminaGetter.TryGetRow<ItemSheet>(scanItem.ItemID, out var itemRow) ||
                !IsGlamourPreviewCandidate(itemRow))
                continue;

            var name = itemRow.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new CachedPreviewItem
            {
                ItemID = scanItem.ItemID,
                Name = name,
                IconID = itemRow.Icon,
                LevelEquip = (uint)itemRow.LevelEquip,
                EquipSlotCategoryRowID = itemRow.EquipSlotCategory.RowId,
                ClassJobCategoryRowID = itemRow.ClassJobCategory.RowId,
                Source = source,
                Owner = owner,
                SourceKey = sourceKey,
                SourceLabel = string.IsNullOrWhiteSpace(scanItem.SourceLabel) ? owner : scanItem.SourceLabel,
                SlotIndex = scanItem.SlotIndex,
                UpdatedAt = updatedAt,
            });
        }

        return result;
    }

    private static bool IsGlamourPreviewCandidate(ItemSheet item)
    {
        if (item.RowId <= MIN_VALID_ITEM_ID)
            return false;

        var category = item.EquipSlotCategory.Value;
        return GLAMOUR_EQUIP_SLOT_KINDS.Any(slot => IsEquipSlotEnabled(category, slot));
    }

    private static string BuildSnapshotFingerprint(IReadOnlyList<PreviewScanItem> snapshot)
    {
        return snapshot.Count == 0
            ? string.Empty
            : string.Join("|", snapshot
                .OrderBy(x => x.SlotIndex)
                .ThenBy(x => x.ItemID)
                .Select(x => $"{x.SlotIndex}:{x.ItemID}"));
    }

    private static string GetInventoryContainerSourceLabel(InventoryType inventoryType)
    {
        var name = inventoryType.ToString();

        if (name.StartsWith("Retainer", StringComparison.OrdinalIgnoreCase))
            return Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer");

        if (name.StartsWith("Armory", StringComparison.OrdinalIgnoreCase))
            return Lang.Get("UnifiedGlamourManager-PreviewSourceArmory");

        if (name.Contains("SaddleBag", StringComparison.OrdinalIgnoreCase))
            return Lang.Get("UnifiedGlamourManager-PreviewSourceSaddleBag");

        if (name.StartsWith("Inventory", StringComparison.OrdinalIgnoreCase))
            return Lang.Get("UnifiedGlamourManager-PreviewSourceInventory");

        return Lang.Get("UnifiedGlamourManager-PreviewSourceInventoryAll");
    }

    private static IEnumerable<PreviewScanItem> ScanPreviewInventoryItems(IEnumerable<InventoryType> containerTypes)
    {
        List<PreviewScanItem> result = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return result;

        var globalSlot = 0;
        foreach (var inventoryType in containerTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null)
            {
                globalSlot += INVENTORY_CONTAINER_SLOT_OFFSET;
                continue;
            }

            var sourceLabel = GetInventoryContainerSourceLabel(inventoryType);
            for (var slot = 0; slot < container->Size; slot++)
            {
                var inventoryItem = container->GetInventorySlot(slot);
                if (inventoryItem == null)
                    continue;

                var rawItemID = inventoryItem->ItemId;
                if (rawItemID == 0)
                    continue;

                var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
                if (itemID <= MIN_VALID_ITEM_ID)
                    continue;

                result.Add(new PreviewScanItem(itemID, globalSlot + slot, sourceLabel));
            }

            globalSlot += INVENTORY_CONTAINER_SLOT_OFFSET;
        }

        return result;
    }

    private void LoadPreviewItems()
    {
        if (config.PreviewItems.Count == 0)
            return;

        foreach (var preview in config.PreviewItems)
        {
            if (preview.ItemID <= MIN_VALID_ITEM_ID)
                continue;

            items.Add(new UnifiedItem
            {
                ItemID = preview.ItemID,
                RawItemID = preview.ItemID,
                PrismBoxIndex = 0,
                CabinetID = 0,
                Name = preview.Name,
                Stain0ID = 0,
                Stain1ID = 0,
                ModelMain = 0,
                EquipSlotCategoryRowID = preview.EquipSlotCategoryRowID,
                ClassJobCategoryRowID = preview.ClassJobCategoryRowID,
                IconID = preview.IconID,
                LevelEquip = preview.LevelEquip,
                InPrismBox = false,
                InCabinet = false,
                PreviewOnly = true,
                PreviewSourceName = preview.Source == PREVIEW_SOURCE_RETAINER
                    ? Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer")
                    : !string.IsNullOrWhiteSpace(preview.SourceLabel)
                        ? preview.SourceLabel
                        : Lang.Get("UnifiedGlamourManager-PreviewSourceInventoryAll"),
                PreviewOwnerName = preview.Owner,
                PreviewSourceKey = preview.SourceKey,
                PreviewUpdatedAt = preview.UpdatedAt,
                IsSetContainer = false,
                IsSetPart = false,
                ParentSetItemID = 0,
                ParentSetName = string.Empty,
                SetPartLabel = string.Empty,
            });
        }
    }

    private static string FormatUnixTime(long unixSeconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
            return Lang.Get("UnifiedGlamourManager-Unknown");
        }
    }

    private void ApplySelectedItemToCurrentPlateSlot(UnifiedItem item)
    {
        if (item.PreviewOnly || !item.CanUseInPlate)
            return;

        if (!IsPlateEditorReady())
            return;

        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return;

        var selectedSlot = agent->Data->SelectedItemIndex;
        if (filterByCurrentPlateSlot && !CanItemUseInPlateSlot(item, selectedSlot))
            return;

        try
        {
            if (item.InPrismBox)
            {
                ApplyPrismBoxItem(agent, item);
                return;
            }

            if (item.InCabinet)
                ApplyCabinetItem(agent, item);
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
        }
    }

    private void ApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        TryApplyPrismBoxItem(agent, item);
    }

    private bool TryApplyPrismBoxItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (!TryGetLoadedMirageManager(out var manager))
            return false;

        if (item.PrismBoxIndex >= PRISM_BOX_CAPACITY)
            return false;

        var rawItemID = manager->PrismBoxItemIds[(int)item.PrismBoxIndex];
        if (!IsExpectedPrismBoxItem(item, rawItemID))
            return false;

        var itemID = item.IsSetPart
            ? item.ItemID
            : item.RawItemID != 0
                ? item.RawItemID
                : item.ItemID;

        var stain0 = item.IsSetPart ? (byte)0 : SafeByte(item.Stain0ID);
        var stain1 = item.IsSetPart ? (byte)0 : SafeByte(item.Stain1ID);

        agent->SetSelectedItemData(ItemSource.PrismBox, item.PrismBoxIndex, itemID, stain0, stain1);
        MarkPlateSelectionDirty(agent);

        return true;
    }

    private void ApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        if (!TryApplyCabinetItem(agent, item))
            return;

        var itemID = item.ItemID;
        var cabinetID = item.CabinetID;

        DService.Instance().Framework.RunOnTick(
            () =>
            {
                var retryAgent = AgentMiragePrismMiragePlate.Instance();
                if (retryAgent == null || retryAgent->Data == null)
                    return;

                var retryItem = items.FirstOrDefault(x =>
                    x.ItemID == itemID &&
                    x.CabinetID == cabinetID &&
                    x.InCabinet &&
                    x.CanUseInPlate &&
                    !x.PreviewOnly);

                if (retryItem == null)
                    return;

                TryApplyCabinetItem(retryAgent, retryItem);
            },
            TimeSpan.FromMilliseconds(CABINET_APPLY_RETRY_DELAY_MS));
    }

    private bool TryApplyCabinetItem(AgentMiragePrismMiragePlate* agent, UnifiedItem item)
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return false;

        if (item.CabinetID == 0 || !cabinet->IsItemInCabinet(item.CabinetID))
            return false;

        agent->SetSelectedItemData(ItemSource.Cabinet, item.CabinetID, item.ItemID, 0, 0);
        MarkPlateSelectionDirty(agent);

        return true;
    }

    private static void MarkPlateSelectionDirty(AgentMiragePrismMiragePlate* agent)
    {
        if (agent == null || agent->Data == null)
            return;

        agent->Data->HasChanges = true;
        agent->CharaView.IsUpdatePending = true;
    }

    private void RestoreSelectedPrismBoxItem(UnifiedItem item)
    {
        if (item.IsSetPart)
            return;

        if (!CanRestorePrismBoxItem(item))
            return;

        if (!TryGetLoadedMirageManager(out var manager))
            return;

        var rawItemID = manager->PrismBoxItemIds[(int)item.PrismBoxIndex];
        if (!IsExpectedPrismBoxItem(item, rawItemID))
            return;

        isRestoringItem = true;

        try
        {
            manager->RestorePrismBoxItem(item.PrismBoxIndex);
            StartRefreshAll(item);
        }
        catch (Exception ex)
        {
            DLog.Warning($"Failed: {ex}");
        }
        finally
        {
            isRestoringItem = false;
        }
    }

    private bool CanRestorePrismBoxItem(UnifiedItem item)
    {
        if (isRestoringItem || !item.InPrismBox || item.PreviewOnly || item.IsSetPart)
            return false;

        if (item.PrismBoxIndex >= PRISM_BOX_CAPACITY)
            return false;

        if (!TryGetLoadedMirageManager(out _))
            return false;

        return !Inventories.Player.IsFull();
    }

    private static bool IsExpectedPrismBoxItem(UnifiedItem item, uint rawItemID)
    {
        if (rawItemID == 0)
            return false;

        var itemID = rawItemID % ITEM_ID_NORMALIZE_MODULO;
        var expectedItemID = item is { IsSetPart: true, ParentSetItemID: not 0 }
            ? item.ParentSetItemID
            : item.ItemID;

        return itemID == expectedItemID;
    }

    #region 预定义

    private const int INVENTORY_CONTAINER_SLOT_OFFSET = 1_000;

    private const string PREVIEW_SOURCE_INVENTORY = "InventoryPreview";
    private const string PREVIEW_SOURCE_RETAINER = "RetainerPreview";
    private const string PREVIEW_SOURCE_SADDLEBAG_LEGACY = "SaddleBagPreview";
    private const string PREVIEW_SOURCE_ARMORY_LEGACY = "ArmoryPreview";
    private const string PREVIEW_SOURCE_KEY_INVENTORY = "inventory";
    private const string RETAINER_SOURCE_KEY_PREFIX = "retainer:";
    private const string RETAINER_SOURCE_KEY_UNKNOWN = "retainer:unknown";
    private const string RETAINER_RECORD_PREFIX = "retainer-record-";

    private static readonly InventoryType[] INVENTORY_PREVIEW_CONTAINER_TYPES =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RETAINER_PREVIEW_CONTAINER_TYPES =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private const uint PRISM_BOX_CAPACITY = 800;

    #endregion
}
