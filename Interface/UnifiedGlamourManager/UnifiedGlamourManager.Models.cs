using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using ItemSource = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData.ItemSource;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
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

    private sealed class UnifiedItem : IEquatable<UnifiedItem>
    {
        public uint   ItemID                 { get; set; }
        public uint   RawItemID              { get; set; }
        public uint   PrismBoxIndex          { get; set; }
        public uint   CabinetID              { get; set; }
        public string Name                   { get; set; } = string.Empty;
        public uint   Stain0ID               { get; set; }
        public uint   Stain1ID               { get; set; }
        public uint   EquipSlotCategoryRowID { get; set; }
        public uint   ClassJobCategoryRowID  { get; set; }
        public uint   IconID                 { get; set; }
        public uint   LevelEquip             { get; set; }
        public bool   InPrismBox             { get; set; }
        public bool   InCabinet              { get; set; }
        public bool   IsSetContainer         { get; set; }
        public bool   IsSetPart              { get; set; }
        public uint   ParentSetItemID        { get; set; }
        public string ParentSetName          { get; set; } = string.Empty;
        public string SetPartLabel           { get; set; } = string.Empty;

        public bool CanUseInPlate => (InPrismBox || InCabinet) && !IsSetContainer;

        public Stain? Stain0 => LuminaGetter.TryGetRow<Stain>(Stain0ID, out var stain) ? stain : null;
        public Stain? Stain1 => LuminaGetter.TryGetRow<Stain>(Stain1ID, out var stain) ? stain : null;

        public string SourceLabel
        {
            get
            {
                List<string> labels = [];
                if (InPrismBox) labels.Add(LuminaWrapper.GetAddonText(11910));
                if (InCabinet) labels.Add(LuminaWrapper.GetAddonText(12216));
                if (IsSetPart || IsSetContainer) labels.Add(LuminaWrapper.GetAddonText(15624));
                return labels.Count == 0 ? Lang.Get("Unknown") : string.Join(" / ", labels);
            }
        }

        public string AvailableSlotsLabel
        {
            get
            {
                if (!LuminaGetter.TryGetRow<EquipSlotCategory>(EquipSlotCategoryRowID, out var category))
                    return Lang.Get("Unknown");

                var availableSlots = PlateSlotDefinitions
                                     .Where(x => x.CanUse(category))
                                     .Select(x => LuminaWrapper.GetAddonText(x.AddonTextID))
                                     .Where(x => !string.IsNullOrWhiteSpace(x))
                                     .Distinct()
                                     .ToArray();

                return availableSlots.Length > 0 ? string.Join(" / ", availableSlots) : Lang.Get("Unknown");
            }
        }

        public bool IsCompatibleWithSlot(uint slotIndex)
        {
            if (!LuminaGetter.TryGetRow<EquipSlotCategory>(EquipSlotCategoryRowID, out var categoryRow)) return false;
            return slotIndex < PlateSlotDefinitions.Length && IsEquipSlotCategoryCompatibleWithPlateSlot(categoryRow, slotIndex);
        }

        public bool IsCompatibleWithJobs(uint[] classJobIDs) =>
            classJobIDs.Length == 0 || classJobIDs.Any(id => ClassJobCategory.IsClassJobInCategory(id, ClassJobCategoryRowID));

        public void MergeWith(UnifiedItem other)
        {
            if (other is null) return;
            InPrismBox |= other.InPrismBox;
            InCabinet  |= other.InCabinet;

            if (other.InPrismBox)
            {
                RawItemID              = other.RawItemID;
                PrismBoxIndex          = other.PrismBoxIndex;
                Stain0ID               = other.Stain0ID;
                Stain1ID               = other.Stain1ID;
                IconID                 = other.IconID;
                EquipSlotCategoryRowID = other.EquipSlotCategoryRowID;
                ClassJobCategoryRowID  = other.ClassJobCategoryRowID;
                LevelEquip             = other.LevelEquip;
                IsSetContainer         = other.IsSetContainer;
            }

            if (other.InCabinet)
            {
                CabinetID              =  other.CabinetID;
                IconID                 =  IconID                 == 0 ? other.IconID : IconID;
                EquipSlotCategoryRowID =  EquipSlotCategoryRowID == 0 ? other.EquipSlotCategoryRowID : EquipSlotCategoryRowID;
                ClassJobCategoryRowID  =  ClassJobCategoryRowID  == 0 ? other.ClassJobCategoryRowID : ClassJobCategoryRowID;
                LevelEquip             =  LevelEquip             == 0 ? other.LevelEquip : LevelEquip;
                IsSetContainer         |= other.IsSetContainer;
            }
        }

        public static UnifiedItem Create
        (
            uint       itemID,
            uint       rawItemID,
            string     name,
            ItemSheet  row,
            ItemSource source,
            uint       prismBoxIndex,
            uint       cabinetID,
            uint       stain0ID,
            uint       stain1ID,
            bool       isSetContainer,
            bool       isSetPart       = false,
            uint       parentSetItemID = 0,
            string     parentSetName   = "",
            string     setPartLabel    = ""
        ) =>
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
                LevelEquip             = row.LevelEquip,
                InPrismBox             = source == ItemSource.PrismBox,
                InCabinet              = source == ItemSource.Cabinet,
                IsSetContainer         = isSetContainer,
                IsSetPart              = isSetPart,
                ParentSetItemID        = parentSetItemID,
                ParentSetName          = parentSetName,
                SetPartLabel           = setPartLabel
            };

        public bool Equals(UnifiedItem? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return ItemID          == other.ItemID        &&
                   PrismBoxIndex   == other.PrismBoxIndex &&
                   IsSetPart       == other.IsSetPart     &&
                   ParentSetItemID == other.ParentSetItemID;
        }

        public override bool Equals(object? obj) => Equals(obj as UnifiedItem);

        public override int GetHashCode() => HashCode.Combine(ItemID, PrismBoxIndex, IsSetPart, ParentSetItemID);

        public static bool operator ==(UnifiedItem? left, UnifiedItem? right)
        {
            if (left is null) return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(UnifiedItem? left, UnifiedItem? right) => !(left == right);
    }

    private sealed class SavedItem
    {
        public uint   ItemID  { get; set; }
        public string Name    { get; set; } = string.Empty;
        public long   AddedAt { get; set; }
    }

    private readonly record struct SetPartInfo
    (
        uint   ItemID,
        string PartLabel,
        int    SlotIndex
    );

    private readonly record struct PlateSlotDefinition
    (
        uint                          AddonTextID,
        Func<EquipSlotCategory, bool> CanUse
    );
}
