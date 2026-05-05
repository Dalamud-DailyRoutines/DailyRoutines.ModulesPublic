using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using ItemSheet = Lumina.Excel.Sheets.Item;
using OmenTools.Dalamud;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 自动记录

    private void RecordOpenedPreviewSources()
    {
        var changed = TryRecordInventoryPreview();
        changed |= TryRecordRetainerPreview();

        if (!changed)
            return;

        SaveModuleConfig();

        if (isOpen)
            RefreshAll();
    }

    private bool TryRecordInventoryPreview()
    {
        var snapshot = ScanPreviewInventoryItems(GetInventoryPreviewContainerTypes()).ToList();
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
        var snapshot = ScanPreviewInventoryItems(GetRetainerPreviewContainerTypes()).ToList();
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

    #endregion

    #region 雇员识别

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

    #endregion

    #region 快照

    private List<CachedPreviewItem> BuildCachedPreviewItems(
        IReadOnlyList<PreviewScanItem> snapshot,
        string source,
        string owner,
        string sourceKey,
        long updatedAt)
    {
        var itemSheet = LuminaGetter.Get<ItemSheet>();
        List<CachedPreviewItem> result = [];

        foreach (var scanItem in snapshot)
        {
            var itemRow = itemSheet.GetRowOrDefault(scanItem.ItemID);
            if (itemRow == null || !IsGlamourPreviewCandidate(itemRow.Value))
                continue;

            var name = itemRow.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new CachedPreviewItem
            {
                ItemID = scanItem.ItemID,
                Name = name,
                IconID = itemRow.Value.Icon,
                LevelEquip = (uint)itemRow.Value.LevelEquip,
                EquipSlotCategoryRowID = itemRow.Value.EquipSlotCategory.RowId,
                ClassJobCategoryRowID = itemRow.Value.ClassJobCategory.RowId,
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
        if (item.RowId <= 1)
            return false;

        var category = item.EquipSlotCategory.Value;
        return category.MainHand != 0 ||
               category.OffHand != 0 ||
               category.Head != 0 ||
               category.Body != 0 ||
               category.Gloves != 0 ||
               category.Legs != 0 ||
               category.Feet != 0 ||
               category.Ears != 0 ||
               category.Neck != 0 ||
               category.Wrists != 0 ||
               category.FingerL != 0 ||
               category.FingerR != 0;
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

    #endregion

    #region 容器扫描

    private static IEnumerable<InventoryType> GetInventoryPreviewContainerTypes() => INVENTORY_PREVIEW_CONTAINER_TYPES;

    private static IEnumerable<InventoryType> GetRetainerPreviewContainerTypes() => RETAINER_PREVIEW_CONTAINER_TYPES;

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

                var itemID = rawItemID % 1_000_000;
                if (itemID <= 1)
                    continue;

                result.Add(new PreviewScanItem(itemID, globalSlot + slot, sourceLabel));
            }

            globalSlot += INVENTORY_CONTAINER_SLOT_OFFSET;
        }

        return result;
    }

    #endregion

    #region 预览载入

    private void LoadPreviewItems()
    {
        if (config.PreviewItems.Count == 0)
            return;

        foreach (var preview in config.PreviewItems)
        {
            if (preview.ItemID <= 1)
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

    #endregion

    #region 常量

    private const int INVENTORY_CONTAINER_SCAN_SIZE = 120;
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

    #endregion
}
