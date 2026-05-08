using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    private bool PassFilter(UnifiedItem item)
    {
        if (filterByCurrentPlateSlot && !CanItemUseInCurrentPlateSlot(item))
            return false;

        if (!PassPreviewConfigFilter(item))
            return false;

        if (!PassSourceFilter(item))
            return false;

        if (enableLevelFilter && (item.LevelEquip < minEquipLevel || item.LevelEquip > maxEquipLevel))
            return false;

        if (!PassSetRelationFilter(item))
            return false;

        if (!PassJobFilter(item))
            return false;

        return PassSearchFilter(item);
    }

    private bool PassPreviewConfigFilter(UnifiedItem item)
    {
        if (!item.PreviewOnly)
            return true;

        return IsRetainerPreviewItem(item)
            ? config.ShowRetainerPreview
            : config.ShowInventoryPreview;
    }

    private bool PassSourceFilter(UnifiedItem item)
    {
        var saved = GetSaved(item.ItemID);
        return sourceFilter switch
        {
            SourceFilter.PrismBox         => item.InPrismBox,
            SourceFilter.Cabinet          => item.InCabinet,
            SourceFilter.Favorite         => saved != null,
            SourceFilter.InventoryPreview => IsInventoryPreviewItem(item),
            SourceFilter.RetainerPreview  => IsRetainerPreviewItem(item),
            _                             => true
        };
    }

    private bool PassSearchFilter(UnifiedItem item)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        var text = searchText.Trim();
        return (!string.IsNullOrEmpty(item.Name) && item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(item.ParentSetName) && item.ParentSetName.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(item.SetPartLabel) && item.SetPartLabel.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(item.PreviewOwnerName) && item.PreviewOwnerName.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(item.PreviewSourceName) && item.PreviewSourceName.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private bool PassSetRelationFilter(UnifiedItem item)
        => setRelationFilter switch
        {
            SetRelationFilter.SetRelatedOnly => item.IsSetContainer || item.IsSetPart,
            SetRelationFilter.NonSetOnly     => !item.IsSetContainer && !item.IsSetPart,
            _                                => true
        };

    private bool PassJobFilter(UnifiedItem item)
    {
        if (selectedJobFilterIndex <= 0 || selectedJobFilterIndex >= JOB_FILTER_OPTIONS.Length)
            return true;

        var cacheKey = ((ulong)(uint)selectedJobFilterIndex << 32) | item.ClassJobCategoryRowID;
        if (jobFilterCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var option = JOB_FILTER_OPTIONS[selectedJobFilterIndex];
        if (option.Jobs.Length == 0)
            return true;

        if (!LuminaGetter.TryGetRow<ClassJobCategory>(item.ClassJobCategoryRowID, out var categoryRow))
            return false;

        var result = option.Jobs.Any(job => ReadJobCategoryFlag(categoryRow, job));
        jobFilterCache[cacheKey] = result;
        return result;
    }

    private bool CanItemUseInCurrentPlateSlot(UnifiedItem item)
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return true;

        var selectedSlot = agent->Data->SelectedItemIndex;
        var cacheKey = ((ulong)selectedSlot << 32) | item.EquipSlotCategoryRowID;
        if (plateSlotFilterCache.TryGetValue(cacheKey, out var cached))
            return cached;

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
        if (!LuminaGetter.TryGetRow<EquipSlotCategory>(item.EquipSlotCategoryRowID, out var categoryRow))
            return false;

        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == selectedSlot);
        return definition.Equals(default(PlateSlotDefinition)) || definition.CanEquip(categoryRow);
    }

    private string GetCurrentPlateSlotNameForUI()
    {
        var agent = AgentMiragePrismMiragePlate.Instance();
        if (agent == null || agent->Data == null)
            return Lang.Get("UnifiedGlamourManager-PlateNotOpen");

        return GetPlateSlotName(agent->Data->SelectedItemIndex);
    }

    private static string GetPlateSlotName(uint selectedSlot)
    {
        var definition = PLATE_SLOT_DEFINITIONS.FirstOrDefault(x => x.Index == selectedSlot);
        return definition.Equals(default(PlateSlotDefinition))
            ? Lang.Get("UnifiedGlamourManager-Slot-Unknown", selectedSlot)
            : Lang.Get(definition.LangKey);
    }

    private static bool IsPlateEditorReady()
        => MiragePrismMiragePlate->IsAddonAndNodesReady();

    private IEnumerable<UnifiedItem> ApplySort(IEnumerable<UnifiedItem> source)
        => sortMode switch
        {
            SortMode.NameAsc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenBy(x => x.Name),
            SortMode.NameDesc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => x.Name),
            SortMode.LevelAsc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenBy(x => x.LevelEquip)
                .ThenBy(x => x.Name),
            SortMode.LevelDesc => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => x.LevelEquip)
                .ThenBy(x => x.Name),
            _ => source
                .OrderBy(x => x.PreviewOnly)
                .ThenByDescending(x => IsFavorite(x.ItemID))
                .ThenByDescending(x => x.InPrismBox)
                .ThenByDescending(x => x.InCabinet)
                .ThenByDescending(x => x.IsSetPart)
                .ThenBy(x => x.ParentSetName)
                .ThenBy(x => x.Name)
        };

    private static string GetSourceLabel(UnifiedItem item)
    {
        List<string> labels = [];

        if (item.PreviewOnly)
            labels.Add(GetPreviewSourceLabel(item));

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

    private static string GetPreviewSourceLabel(UnifiedItem item)
    {
        if (!IsRetainerPreviewItem(item))
            return string.IsNullOrWhiteSpace(item.PreviewSourceName)
                ? Lang.Get("UnifiedGlamourManager-PreviewRecord")
                : item.PreviewSourceName;

        var owner = string.IsNullOrWhiteSpace(item.PreviewOwnerName)
            ? Lang.Get("UnifiedGlamourManager-UnknownRetainer")
            : item.PreviewOwnerName;

        return Lang.Get("UnifiedGlamourManager-RetainerSource", owner);
    }

    private static bool IsInventoryPreviewItem(UnifiedItem item)
        => item.PreviewOnly && !IsRetainerPreviewItem(item);

    private static bool IsRetainerPreviewItem(UnifiedItem item)
    {
        if (!item.PreviewOnly)
            return false;

        if (!string.IsNullOrWhiteSpace(item.PreviewSourceKey) &&
            item.PreviewSourceKey.StartsWith(RETAINER_RECORD_PREFIX, StringComparison.Ordinal))
            return true;

        return string.Equals(
            item.PreviewSourceName,
            Lang.Get("UnifiedGlamourManager-PreviewSourceRetainer"),
            StringComparison.Ordinal);
    }

    private static bool IsEquipSlotEnabled(EquipSlotCategory category, EquipSlotKind slot)
        => slot switch
        {
            EquipSlotKind.MainHand => category.MainHand != 0,
            EquipSlotKind.OffHand  => category.OffHand != 0,
            EquipSlotKind.Head     => category.Head != 0,
            EquipSlotKind.Body     => category.Body != 0,
            EquipSlotKind.Hands    => category.Gloves != 0,
            EquipSlotKind.Legs     => category.Legs != 0,
            EquipSlotKind.Feet     => category.Feet != 0,
            EquipSlotKind.Ears     => category.Ears != 0,
            EquipSlotKind.Neck     => category.Neck != 0,
            EquipSlotKind.Wrists   => category.Wrists != 0,
            EquipSlotKind.Ring     => category.FingerL != 0 || category.FingerR != 0,
            _                      => false
        };

    private static bool ReadJobCategoryFlag(ClassJobCategory category, JobKind job)
        => job switch
        {
            JobKind.GLA => category.GLA,
            JobKind.PGL => category.PGL,
            JobKind.MRD => category.MRD,
            JobKind.LNC => category.LNC,
            JobKind.ARC => category.ARC,
            JobKind.CNJ => category.CNJ,
            JobKind.THM => category.THM,
            JobKind.CRP => category.CRP,
            JobKind.BSM => category.BSM,
            JobKind.ARM => category.ARM,
            JobKind.GSM => category.GSM,
            JobKind.LTW => category.LTW,
            JobKind.WVR => category.WVR,
            JobKind.ALC => category.ALC,
            JobKind.CUL => category.CUL,
            JobKind.MIN => category.MIN,
            JobKind.BTN => category.BTN,
            JobKind.FSH => category.FSH,
            JobKind.PLD => category.PLD,
            JobKind.MNK => category.MNK,
            JobKind.WAR => category.WAR,
            JobKind.DRG => category.DRG,
            JobKind.BRD => category.BRD,
            JobKind.WHM => category.WHM,
            JobKind.BLM => category.BLM,
            JobKind.ACN => category.ACN,
            JobKind.SMN => category.SMN,
            JobKind.SCH => category.SCH,
            JobKind.ROG => category.ROG,
            JobKind.NIN => category.NIN,
            JobKind.MCH => category.MCH,
            JobKind.DRK => category.DRK,
            JobKind.AST => category.AST,
            JobKind.SAM => category.SAM,
            JobKind.RDM => category.RDM,
            JobKind.BLU => category.BLU,
            JobKind.GNB => category.GNB,
            JobKind.DNC => category.DNC,
            JobKind.RPR => category.RPR,
            JobKind.SGE => category.SGE,
            JobKind.VPR => category.VPR,
            JobKind.PCT => category.PCT,
            _           => false
        };

    private static byte SafeByte(uint value)
        => value > byte.MaxValue ? (byte)0 : (byte)value;
}
