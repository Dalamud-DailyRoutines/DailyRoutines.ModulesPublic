using Lumina.Excel.Sheets;
using Dalamud.Interface.Textures;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class UnifiedGlamourManager
{
    #region 筛选枚举

    private enum SourceFilter
    {
        All,
        PrismBox,
        Cabinet,
        Favorite,
        InventoryPreview,
        RetainerPreview
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

    private enum JobKind
    {
        GLA,
        PGL,
        MRD,
        LNC,
        ARC,
        CNJ,
        THM,
        CRP,
        BSM,
        ARM,
        GSM,
        LTW,
        WVR,
        ALC,
        CUL,
        MIN,
        BTN,
        FSH,
        PLD,
        MNK,
        WAR,
        DRG,
        BRD,
        WHM,
        BLM,
        ACN,
        SMN,
        SCH,
        ROG,
        NIN,
        MCH,
        DRK,
        AST,
        SAM,
        RDM,
        BLU,
        GNB,
        DNC,
        RPR,
        SGE,
        VPR,
        PCT
    }

    private enum EquipSlotKind
    {
        MainHand,
        OffHand,
        Head,
        Body,
        Hands,
        Legs,
        Feet,
        Ears,
        Neck,
        Wrists,
        Ring
    }

    #endregion

    #region 物品模型

    private sealed class UnifiedItem
    {
        public uint ItemID { get; set; }
        public uint RawItemID { get; set; }
        public uint PrismBoxIndex { get; set; }
        public uint CabinetID { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint Stain0ID { get; set; }
        public uint Stain1ID { get; set; }
        public ulong ModelMain { get; set; }
        public uint EquipSlotCategoryRowID { get; set; }
        public uint ClassJobCategoryRowID { get; set; }
        public uint IconID { get; set; }
        public uint LevelEquip { get; set; }
        public bool InPrismBox { get; set; }
        public bool InCabinet { get; set; }
        public bool PreviewOnly { get; set; }
        public string PreviewSourceName { get; set; } = string.Empty;
        public string PreviewOwnerName { get; set; } = string.Empty;
        public string PreviewSourceKey { get; set; } = string.Empty;
        public long PreviewUpdatedAt { get; set; }
        public bool IsSetContainer { get; set; }
        public bool IsSetPart { get; set; }
        public uint ParentSetItemID { get; set; }
        public string ParentSetName { get; set; } = string.Empty;
        public string SetPartLabel { get; set; } = string.Empty;

        public bool CanUseInPlate => !PreviewOnly && (InPrismBox || InCabinet);
    }

    private sealed class SavedItem
    {
        public uint ItemID { get; set; }
        public string Name { get; set; } = string.Empty;
        public long AddedAt { get; set; }
    }

    private sealed class CachedPreviewItem
    {
        public uint ItemID { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint IconID { get; set; }
        public uint LevelEquip { get; set; }
        public uint EquipSlotCategoryRowID { get; set; }
        public uint ClassJobCategoryRowID { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string SourceKey { get; set; } = string.Empty;
        public string SourceLabel { get; set; } = string.Empty;
        public int SlotIndex { get; set; }
        public long UpdatedAt { get; set; }
    }

    private sealed record IconTextureCacheEntry(ISharedImmediateTexture Texture, LinkedListNode<uint> Node);

    #endregion

    #region 临时数据

    private readonly record struct PreviewScanItem(uint ItemID, int SlotIndex, string SourceLabel);

    private readonly record struct SetPartInfo(uint ItemID, string PartLabel, int SlotIndex);

    private readonly record struct JobFilterOption(string Label, JobKind[] Jobs);

    private readonly record struct PlateSlotDefinition(
        uint Index,
        string LangKey,
        Func<EquipSlotCategory, bool> CanEquip);
    
    #endregion

    #region 工具类型

    #endregion
}
