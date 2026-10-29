using System.Diagnostics.CodeAnalysis;
using DailyRoutines.Common.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsSource : AddonNPCShopsBase<AddonNPCShopsSource.SectionSlot>
    {
        private const int   SECTIONS_PER_PAGE = 10;
        private const float COST_ROW_HEIGHT   = 36f;
        private const float NPC_ROW_HEIGHT    = 32f;
        private const float VERTICAL_PADDING  = 20f;

        [SetsRequiredMembers]
        private AddonNPCShopsSource
        (
            ItemSourceInfo sourceInfo
        ) :
            base("DRNPCShopsSource", Lang.Get("AutoShowItemNPCShopInfo-Addon-Source")) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsSource? Addon { get; set; }

        public ItemSourceInfo SourceInfo { get; set; }

        private List<CostGroup> costGroups = [];

        protected override int  SectionCount    => costGroups.Count;
        protected override int  SectionsPerPage => SECTIONS_PER_PAGE;
        protected override uint HeaderItemID    => SourceInfo.ItemID;

        public static void CloseAndClear()
        {
            if (Addon == null) return;

            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData
        (
            ItemSourceInfo sourceInfo
        )
        {
            if (sourceInfo is not { NPCInfos.Count: > 0 }) return;

            CloseAndClear();

            Addon ??= new(sourceInfo);
            Addon.Open();
        }

        protected override void BuildSections() =>
            costGroups =
            [
                .. SourceInfo.NPCInfos
                             .GroupBy(x => GetCostKey(x.CostInfos))
                             .Select
                             (g => new CostGroup
                                 {
                                     CostInfos = g.First().CostInfos,
                                     NPCInfos  = [.. g]
                                 }
                             )
            ];

        protected override void UpdateSectionContent
        (
            SectionSlot slot,
            int         index
        )
        {
            var group = costGroups[index];

            for (var i = 0; i < MAX_COSTS; i++)
                if (i < group.CostInfos.Count)
                {
                    var costInfo = group.CostInfos[i];
                    slot.CostRows[i].IsVisible = true;
                    slot.CostIcons[i].ItemID   = costInfo.ItemID;
                    slot.CostNames[i].String   = costInfo.GetItemName();
                    slot.CostQuantities[i].String =
                        costInfo.Collectablity != null ?
                            $"\ue03d ({costInfo.Collectablity.Value}~)" :
                            $"x{costInfo.Cost.ToChineseString()}";
                }
                else
                    slot.CostRows[i].IsVisible = false;

            slot.SortedNPCInfos =
                SortNPCInfos
                (
                    [
                        .. group.NPCInfos.Select(x => new NPCDisplayInfo(x.Name, x.Location, x.CostInfos))
                    ]
                );
            slot.NPCCurrentPage = 0;
            ShowNPCPage(slot);
        }

        protected override float CalculateSectionHeight
        (
            SectionSlot slot,
            int         visibleNPCCount,
            bool        hasNPCPagination
        )
        {
            var visibleCostCount = Math.Min(slot.SortedNPCInfos.First().CostInfos.Count, MAX_COSTS);
            var paginationHeight = hasNPCPagination ?
                                       32f :
                                       0f;

            return VERTICAL_PADDING                                      +
                   (visibleCostCount                  * COST_ROW_HEIGHT) +
                   (Math.Max(0, visibleCostCount - 1) * ROW_SPACING)     +
                   (visibleCostCount > 0 ?
                        ROW_SPACING :
                        0f)                                            +
                   (visibleNPCCount                  * NPC_ROW_HEIGHT) +
                   (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING)    +
                   paginationHeight;
        }

        protected override SectionSlot CreateSectionSlot
        (
            VerticalListNode parent
        )
        {
            var slot = new SectionSlot();
            InitializeSectionSlot(slot, parent);

            var contentWidth = slot.Content.Width;

            slot.CostRows       = new HorizontalListNode[MAX_COSTS];
            slot.CostIcons      = new ItemIconNode[MAX_COSTS];
            slot.CostNames      = new TextNode[MAX_COSTS];
            slot.CostQuantities = new TextNode[MAX_COSTS];

            for (var i = 0; i < MAX_COSTS; i++)
            {
                slot.CostRows[i] = new HorizontalListNode
                {
                    Size        = new(contentWidth, 32),
                    ItemSpacing = 6f
                };
                slot.Content.AddNode(slot.CostRows[i]);

                slot.CostIcons[i] = new ItemIconNode
                {
                    Size = new(slot.CostRows[i].Height)
                };
                var indexCopy = i;
                slot.CostIcons[i].OnClick = (_, _, _, _, data) =>
                {
                    if (!data->IsRightClick) return;
                    ContextMenuManager.Instance().OpenItem(slot.CostIcons[indexCopy].ItemID, AddonId);
                };
                slot.CostRows[i].AddNode(slot.CostIcons[i]);

                slot.CostNames[i] = new TextNode
                {
                    TextFlags     = TextFlags.Edge | TextFlags.MultiLine | TextFlags.WordWrap,
                    FontSize      = 16,
                    AlignmentType = AlignmentType.Left,
                    Size          = new(contentWidth - slot.CostIcons[i].Width - slot.CostRows[i].ItemSpacing - 38, slot.CostRows[i].Height)
                };
                AtkColors.Label.ApplyTo(slot.CostNames[i]);
                slot.CostRows[i].AddNode(slot.CostNames[i]);

                slot.CostQuantities[i] = new TextNode
                {
                    TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                    FontSize      = 16,
                    Position      = new(contentWidth - 10, 5),
                    Size          = new(70, slot.CostRows[i].Height),
                    AlignmentType = AlignmentType.Right
                };
                AtkColors.ValueEmphasize.ApplyTo(slot.CostQuantities[i]);
                slot.CostQuantities[i].AttachNode(slot.CostRows[i]);
            }

            InitializeSectionFooter(slot, contentWidth);

            return slot;
        }

        private static string GetCostKey
        (
            List<ShopItemCostInfo> costInfos
        ) =>
            string.Join("|", costInfos.Select(c => $"{c.ItemID}:{c.Cost}:{c.Collectablity}"));

        public new class SectionSlot : AddonNPCShopsBase<SectionSlot>.SectionSlot
        {
            public HorizontalListNode[] CostRows       = null!;
            public ItemIconNode[]       CostIcons      = null!;
            public TextNode[]           CostNames      = null!;
            public TextNode[]           CostQuantities = null!;
        }

        private class CostGroup
        {
            public List<ShopItemCostInfo> CostInfos = [];
            public List<ShopNPCInfos>     NPCInfos  = [];
        }
    }
}
