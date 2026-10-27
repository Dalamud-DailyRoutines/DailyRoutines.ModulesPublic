using System.Diagnostics.CodeAnalysis;
using DailyRoutines.Common.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsDestination : AddonNPCShopsBase<AddonNPCShopsDestination.SectionSlot>
    {
        private const int ITEMS_PER_PAGE = 20;

        [SetsRequiredMembers]
        private AddonNPCShopsDestination
        (
            ExchangeItemsInfo sourceInfo
        ) :
            base("DRNPCShopsDestinations", Lang.Get("AutoShowItemNPCShopInfo-Addon-Destination")) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsDestination? Addon { get; set; }

        public ExchangeItemsInfo SourceInfo { get; set; }

        private List<ExchangeItemInfo> exchangeItems = [];

        protected override int  SectionCount    => exchangeItems.Count;
        protected override int  SectionsPerPage => ITEMS_PER_PAGE;
        protected override uint HeaderItemID    => SourceInfo.CostItemID;

        public static void CloseAndClear()
        {
            if (Addon == null) return;

            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData
        (
            ExchangeItemsInfo sourceInfo
        )
        {
            if (sourceInfo is not { Items.Count: > 0 }) return;

            CloseAndClear();

            Addon ??= new(sourceInfo);
            Addon.Open();
        }

        protected override void BuildSections() =>
            exchangeItems = [.. SourceInfo.Items.OrderBy(x => x.GetItemName())];

        protected override void UpdateSectionContent
        (
            SectionSlot slot,
            int         index
        )
        {
            var exchangeItem = exchangeItems[index];

            slot.ItemIcon.ItemID   = exchangeItem.ItemID;
            slot.ItemName.String   = exchangeItem.GetItemName();
            slot.ItemName.FontSize = 16;

            slot.SortedNPCInfos =
                SortNPCInfos
                (
                    [
                        .. exchangeItem.NPCInfos.Select(x => new NPCDisplayInfo(x.Name, x.Location, x.CostInfos))
                    ]
                );

            var firstNPC = slot.SortedNPCInfos.FirstOrDefault();
            var hasCost  = firstNPC is { CostInfos.Count: > 0 };
            slot.CostRow.IsVisible = hasCost;
            if (hasCost)
                UpdateCostRow(slot, firstNPC!.CostInfos);

            LayoutCostRow(slot);

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
            const float SECTION_HEADER_HEIGHT = 38f;
            const float ROW_HEIGHT            = 32f;
            const float VERTICAL_PADDING      = 20f;

            var paginationHeight = hasNPCPagination ?
                                       32f :
                                       0f;

            return VERTICAL_PADDING                                 +
                   SECTION_HEADER_HEIGHT                            +
                   (visibleNPCCount                  * ROW_HEIGHT)  +
                   (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING) +
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

            slot.ItemHeader = new HorizontalListNode
            {
                Size        = new(contentWidth, 36),
                ItemSpacing = 6
            };
            slot.Content.AddNode(slot.ItemHeader);

            slot.ItemIcon = new ItemIconNode
            {
                Size = new(42)
            };
            slot.ItemIcon.OnClick = (_, _, _, _, data) =>
            {
                if (!data->IsRightClick) return;
                ContextMenuManager.Instance().OpenItem(slot.ItemIcon.ItemID, AddonId);
            };
            slot.ItemHeader.AddNode(slot.ItemIcon);

            slot.ItemName = new TextNode
            {
                TextFlags     = TextFlags.Edge | TextFlags.MultiLine | TextFlags.WordWrap,
                AlignmentType = AlignmentType.Left,
                FontSize      = 16,
                Size          = new(contentWidth - slot.ItemIcon.Width - slot.ItemHeader.ItemSpacing, slot.ItemHeader.Height)
            };
            AtkColors.Label.ApplyTo(slot.ItemName);
            slot.ItemHeader.AddNode(slot.ItemName);

            slot.CostRow = new HorizontalListNode
            {
                ItemSpacing        = 4,
                FitToContentWidth  = true,
                FitToContentHeight = true,
                IsVisible          = false
            };
            slot.CostRow.AttachNode(slot.ItemHeader);

            slot.CostIcons = new ItemIconNode[MAX_COSTS];
            slot.CostTexts = new TextNode[MAX_COSTS];

            for (var i = 0; i < MAX_COSTS; i++)
            {
                slot.CostIcons[i] = new ItemIconNode
                {
                    Size = new(36)
                };
                var indexCopy = i;
                slot.CostIcons[i].OnClick = (_, _, _, _, data) =>
                {
                    if (!data->IsRightClick) return;
                    ContextMenuManager.Instance().OpenItem(slot.CostIcons[indexCopy].ItemID, AddonId);
                };
                slot.CostRow.AddNode(slot.CostIcons[i]);

                slot.CostRow.AddDummy();

                slot.CostTexts[i] = new TextNode
                {
                    FontSize      = 14,
                    TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                    AlignmentType = AlignmentType.Left,
                    Height        = 28
                };
                AtkColors.ValueEmphasize.ApplyTo(slot.CostTexts[i]);

                slot.CostRow.AddNode(slot.CostTexts[i]);
            }

            InitializeSectionFooter(slot, contentWidth);

            return slot;
        }

        private static void UpdateCostRow
        (
            SectionSlot            slot,
            List<ShopItemCostInfo> costInfos
        )
        {
            for (var i = 0; i < MAX_COSTS; i++)
                if (i < costInfos.Count)
                {
                    var costInfo = costInfos[i];

                    slot.CostIcons[i].ItemID    = costInfo.ItemID;
                    slot.CostIcons[i].IsVisible = true;
                    slot.CostTexts[i].String    = $"x{costInfo.Cost.ToChineseString()}";
                    slot.CostTexts[i].IsVisible = true;
                }
                else
                {
                    slot.CostIcons[i].IsVisible = false;
                    slot.CostTexts[i].IsVisible = false;
                }
        }

        private static void LayoutCostRow
        (
            SectionSlot slot
        )
        {
            slot.CostRow.RecalculateLayout();
            slot.CostRow.X = slot.ItemHeader.Width - slot.CostRow.Width;

            var costWidth = slot.CostRow.IsVisible ?
                                slot.CostRow.Width + slot.ItemHeader.ItemSpacing :
                                0f;

            slot.ItemName.Width = slot.ItemHeader.Width - slot.ItemIcon.Width - slot.ItemHeader.ItemSpacing - costWidth;
            slot.ItemHeader.RecalculateLayout();
        }

        public new class SectionSlot : AddonNPCShopsBase<SectionSlot>.SectionSlot
        {
            public HorizontalListNode ItemHeader = null!;
            public ItemIconNode       ItemIcon   = null!;
            public TextNode           ItemName   = null!;
            public HorizontalListNode CostRow    = null!;
            public ItemIconNode[]     CostIcons  = null!;
            public TextNode[]         CostTexts  = null!;
        }
    }
}
