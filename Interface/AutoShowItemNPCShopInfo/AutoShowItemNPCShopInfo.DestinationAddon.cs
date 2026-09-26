using System.Numerics;
using DailyRoutines.Common.Info;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsDestination : NativeAddon
    {
        private const int   ITEMS_PER_PAGE = 20;
        private const int   NPCS_PER_PAGE  = 5;
        private const int   MAX_COSTS      = 4;
        private const float MAP_BTN_WIDTH  = 28f;
        private const float ROW_SPACING_X  = 6f;
        private const float ROW_SPACING    = 4f;
        private const float HEADER_HEIGHT  = 42f;
        
        private AddonNPCShopsDestination
        (
            ExchangeItemsInfo sourceInfo
        ) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsDestination? Addon      { get; set; }
        public        ExchangeItemsInfo         SourceInfo { get; set; }

        private List<ExchangeItemInfo> exchangeItems = [];
        private int                    currentPage;
        private int                    totalPages;

        private ScrollingNode<VerticalListNode>? scrollingAreaNode;
        private VerticalListNode?                contentNode;
        private PaginationNode?                  paginationBar;
        private SectionSlot[]?                   sectionSlots;

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

            Addon ??= new(sourceInfo)
            {
                InternalName = "DRNPCShopsDestinations",
                Title        = Lang.Get("AutoShowItemNPCShopInfo-Addon-Destination"),
                Size         = new(700f, 550f)
            };
            Addon.Open();
        }

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var costItem = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.CostItemID);

            exchangeItems = [.. SourceInfo.Items.OrderBy(x => x.GetItemName())];
            totalPages    = Math.Max(1, (int)Math.Ceiling(exchangeItems.Count / (double)ITEMS_PER_PAGE));
            currentPage   = 0;

            var hasPagination = totalPages > 1;

            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, HEADER_HEIGHT),
                Position    = ContentStartPosition + new Vector2(8, 2),
                ItemSpacing = 4
            };
            headerNode.AttachNode(this);

            var itemInfoRow = new ResNode
            {
                Size        = new(headerNode.Width, 40),
                ItemTooltip = SourceInfo.CostItemID
            };
            headerNode.AddNode(itemInfoRow);

            var iconNode = new IconImageNode
            {
                IconId     = LuminaWrapper.GetItemIconID(SourceInfo.CostItemID),
                Size       = new(36),
                FitTexture = true
            };
            iconNode.AttachNode(itemInfoRow);

            var nameNode = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                String        = LuminaWrapper.GetItemName(SourceInfo.CostItemID),
                FontSize      = 24,
                Position      = new(iconNode.Position.X + iconNode.Width + 6f, 3),
                AlignmentType = AlignmentType.TopLeft
            };
            AtkColors.Label.ApplyTo(nameNode);
            nameNode.AttachNode(itemInfoRow);

            if (costItem.ItemSearchCategory.RowId > 0)
            {
                var marketButton = new IconButtonNode
                {
                    IconId      = 60570,
                    TextTooltip = LuminaWrapper.GetAddonText(548),
                    Size        = new(32),
                    Position    = new(nameNode.Position.X + nameNode.Width, 3),
                    OnClick     = () => OpenMarket(SourceInfo.CostItemID)
                };
                marketButton.AttachNode(itemInfoRow);
            }

            paginationBar = new()
            {
                IsVisible              = hasPagination,
                IsDisplayIndicatorText = true,
                OnNextPage             = () => ShowPage(currentPage + 1),
                OnPreviousPage         = () => ShowPage(currentPage - 1)
            };
            paginationBar.Position = new(0.0f, ContentStartPosition.Y + ContentSize.Y - paginationBar.Height);
            paginationBar.AttachNode(this);

            var footerHeight = hasPagination ?
                                   paginationBar.Height + 6.0f :
                                   0.0f;

            scrollingAreaNode = new ScrollingNode<VerticalListNode>
            {
                ContentNode =
                {
                    FitContents = true,
                    ItemSpacing = 6
                },
                Position          = ContentStartPosition + new Vector2(6,  HEADER_HEIGHT + 6),
                Size              = ContentSize          - new Vector2(12, HEADER_HEIGHT + 6 + footerHeight),
                ScrollSpeed       = 100,
                AutoHideScrollBar = true
            };
            scrollingAreaNode.AttachNode(this);

            contentNode = scrollingAreaNode.ContentNode;

            sectionSlots = new SectionSlot[ITEMS_PER_PAGE];

            for (var i = 0; i < ITEMS_PER_PAGE; i++)
            {
                sectionSlots[i]                     = CreateSectionSlot(contentNode);
                sectionSlots[i].Container.IsVisible = false;
            }

            ShowPage(0);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (IKeyState.Instance()[VirtualKey.ESCAPE])
            {
                Close();
                if (SystemMenu != null) SystemMenu->Close(true);
            }

            if (paginationBar is { IsVisible: true })
                paginationBar.X = ContentStartPosition.X + ((ContentSize.X - paginationBar.Width) / 2.0f);

            if (sectionSlots == null) return;

            foreach (var slot in sectionSlots)
            {
                if (!slot.NPCPaginationBar.IsVisible) continue;

                slot.NPCPaginationBar.X = (slot.Content.Width - slot.NPCPaginationBar.Width) / 2.0f;
            }
        }

        private void ShowPage
        (
            int page
        )
        {
            if (sectionSlots == null || contentNode == null || scrollingAreaNode == null) return;
            currentPage = Math.Clamp(page, 0, totalPages - 1);

            var pageItems = exchangeItems.Skip(currentPage * ITEMS_PER_PAGE).Take(ITEMS_PER_PAGE).ToList();

            for (var i = 0; i < ITEMS_PER_PAGE; i++)
                if (i < pageItems.Count)
                {
                    UpdateSectionContent(sectionSlots[i], pageItems[i]);
                    sectionSlots[i].Container.IsVisible = true;
                }
                else sectionSlots[i].Container.IsVisible = false;

            scrollingAreaNode.ScrollBarNode.ScrollPosition = 0;
            contentNode.RecalculateLayout();
            scrollingAreaNode.RecalculateSizes();
            UpdatePaginationState();
        }

        private void UpdatePaginationState()
        {
            if (paginationBar == null) return;

            paginationBar.PreviousPageButtonNode.IsEnabled = currentPage > 0;
            paginationBar.NextPageButtonNode.IsEnabled     = currentPage < totalPages - 1;
            paginationBar.IndicatorTextNode.String         = $"{currentPage + 1}/{totalPages}";
        }

        private void UpdateSectionContent
        (
            SectionSlot      slot,
            ExchangeItemInfo exchangeItem
        )
        {
            var exchangeTargetItem = LuminaGetter.GetRowOrDefault<Item>(exchangeItem.ItemID);

            slot.ItemTooltipOverlay.ItemTooltip = exchangeItem.ItemID;
            slot.ItemIcon.IconId                = LuminaWrapper.GetItemIconID(exchangeItem.ItemID);
            slot.ItemName.String                = exchangeItem.GetItemName();
            slot.ItemName.FontSize              = 16;

            slot.MarketButton.IsVisible = exchangeTargetItem.ItemSearchCategory.RowId > 0;
            slot.MarketButton.OnClick   = () => OpenMarket(exchangeItem.ItemID);

            var hasDescription = !string.IsNullOrWhiteSpace(exchangeItem.AchievementDescription);
            slot.DescriptionNode.IsVisible = hasDescription;
            if (hasDescription)
                slot.DescriptionNode.String = exchangeItem.AchievementDescription;

            slot.SortedNPCInfos = exchangeItem.NPCInfos
                                              .Where(x => x.Location               != null)
                                              .OrderBy(x => x.Location.TerritoryID == 282)
                                              .ThenBy(x => GetLocationName(x.Location))
                                              .ThenBy(x => x.Name)
                                              .ToList();

            var firstNPC = slot.SortedNPCInfos.FirstOrDefault();
            var hasCost  = firstNPC is { CostInfos.Count: > 0 };
            slot.CostRow.IsVisible = hasCost;
            if (hasCost)
                UpdateCostRow(slot, firstNPC!.CostInfos);

            slot.NPCCurrentPage = 0;
            ShowNPCPage(slot);
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

                    slot.CostTooltipOverlay.ItemTooltip = costInfo.ItemID;
                    slot.CostIcons[i].IconId            = LuminaWrapper.GetItemIconID(costInfo.ItemID);
                    slot.CostIcons[i].IsVisible         = true;
                    slot.CostTexts[i].String            = costInfo.ToString();
                    slot.CostTexts[i].IsVisible         = true;
                }
                else
                {
                    slot.CostIcons[i].IsVisible = false;
                    slot.CostTexts[i].IsVisible = false;
                }
        }

        private void ShowNPCPage
        (
            SectionSlot slot,
            bool        recalculateOuter = false
        )
        {
            var totalNpcs = slot.SortedNPCInfos.Count;
            var npcPages  = Math.Max(1, (int)Math.Ceiling(totalNpcs / (double)NPCS_PER_PAGE));
            var npcPage   = Math.Clamp(slot.NPCCurrentPage, 0, npcPages - 1);
            slot.NPCCurrentPage = npcPage;

            var pageNpcs = slot.SortedNPCInfos.Skip(npcPage * NPCS_PER_PAGE).Take(NPCS_PER_PAGE).ToList();

            for (var i = 0; i < NPCS_PER_PAGE; i++)
                if (i < pageNpcs.Count)
                {
                    UpdateNPCRow(slot.NPCRows[i], pageNpcs[i]);
                    slot.NPCRows[i].Row.IsVisible = true;
                }
                else slot.NPCRows[i].Row.IsVisible = false;

            var hasNPCPagination = totalNpcs > NPCS_PER_PAGE;
            slot.NPCPaginationBar.IsVisible = hasNPCPagination;

            if (hasNPCPagination)
            {
                slot.NPCPaginationBar.PreviousPageButtonNode.IsEnabled = npcPage > 0;
                slot.NPCPaginationBar.NextPageButtonNode.IsEnabled     = npcPage < npcPages - 1;
                slot.NPCPaginationBar.IndicatorTextNode.String         = $"{npcPage + 1} / {npcPages}";
            }

            var visibleNPCCount = Math.Min(pageNpcs.Count, NPCS_PER_PAGE);
            var sectionHeight   = CalculateSectionHeight(visibleNPCCount, slot.DescriptionNode.IsVisible, slot.CostRow.IsVisible, hasNPCPagination);
            slot.Container.Size  = new(slot.Container.Width, sectionHeight);
            slot.Background.Size = slot.Container.Size;
            slot.Content.Size    = slot.Container.Size - new Vector2(20f, 20f);
            slot.Content.RecalculateLayout();

            if (recalculateOuter && contentNode != null && scrollingAreaNode != null)
            {
                contentNode.RecalculateLayout();
                scrollingAreaNode.RecalculateSizes();
            }
        }

        private SectionSlot CreateSectionSlot
        (
            VerticalListNode parent
        )
        {
            var slot = new SectionSlot
            {
                Container = new ResNode { Size = new(parent.Width, 100) }
            };

            parent.AddNode(slot.Container);

            slot.Background = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Offsets            = new Vector4(8f),
                MultiplyColor      = new(0.45f, 0.45f, 0.48f),
                Alpha              = 0.28f,
                Size               = slot.Container.Size
            };
            slot.Background.AttachNode(slot.Container);

            slot.Content = new VerticalListNode
            {
                Size        = slot.Container.Size - new Vector2(20f, 20f),
                Position    = new(10f, 10f),
                ItemSpacing = 4
            };
            slot.Content.AttachNode(slot.Container);

            var contentWidth = slot.Content.Width;

            slot.ItemHeader = new HorizontalListNode
            {
                Size        = new(contentWidth, 36),
                ItemSpacing = 6
            };
            slot.Content.AddNode(slot.ItemHeader);

            slot.ItemIcon = new IconImageNode
            {
                IconId     = 0,
                Size       = new(32),
                FitTexture = true,
                Position   = new(0, 2)
            };
            slot.ItemHeader.AddNode(slot.ItemIcon);

            slot.ItemName = new TextNode
            {
                TextFlags        = TextFlags.Edge | TextFlags.Ellipsis,
                AlignmentType    = AlignmentType.Left,
                FontSize         = 16,
                Size             = new(contentWidth - 32 - 6 - 38, 32),
                Position         = new(0, 2),
                TextColor        = ColorHelper.GetColor(28),
                TextOutlineColor = ColorHelper.GetColor(509)
            };
            slot.ItemHeader.AddNode(slot.ItemName);

            slot.ItemTooltipOverlay = new ResNode
            {
                Size     = new(contentWidth, 36),
                Position = new(0, 0)
            };
            slot.ItemTooltipOverlay.AttachNode(slot.Content);

            slot.MarketButton = new IconButtonNode
            {
                IconId      = 60570,
                TextTooltip = LuminaWrapper.GetAddonText(548),
                Size        = new(28),
                Position    = new(contentWidth - 28, 0),
                IsVisible   = false
            };
            slot.MarketButton.AttachNode(slot.Content);

            slot.DescriptionNode = new TextNode
            {
                TextFlags        = TextFlags.AutoAdjustNodeSize,
                String           = "",
                FontSize         = 12,
                TextColor        = ColorHelper.GetColor(3),
                TextOutlineColor = ColorHelper.GetColor(7),
                IsVisible        = false
            };
            slot.Content.AddNode(slot.DescriptionNode);

            slot.CostRow = new ResNode
            {
                Size      = new(contentWidth, 24),
                IsVisible = false
            };
            slot.Content.AddNode(slot.CostRow);

            slot.CostTooltipOverlay = new ResNode
            {
                Size     = new(contentWidth, 24),
                Position = new(0, 0)
            };
            slot.CostTooltipOverlay.AttachNode(slot.CostRow);

            slot.CostIcons = new IconImageNode[MAX_COSTS];
            slot.CostTexts = new TextNode[MAX_COSTS];

            for (var i = 0; i < MAX_COSTS; i++)
            {
                slot.CostIcons[i] = new IconImageNode
                {
                    TextureSize    = new(20),
                    Size           = new(20),
                    IconId         = 0,
                    ImageNodeFlags = ImageNodeFlags.AutoFit,
                    IsVisible      = false
                };
                slot.CostIcons[i].AttachNode(slot.CostRow);

                slot.CostTexts[i] = new TextNode
                {
                    FontSize  = 12,
                    Position  = new(slot.CostIcons[i].X + slot.CostIcons[i].Width + 4, 2),
                    TextFlags = TextFlags.AutoAdjustNodeSize,
                    IsVisible = false
                };
                slot.CostTexts[i].AttachNode(slot.CostRow);
            }

            slot.NPCRows = new NPCRowSlot[NPCS_PER_PAGE];
            for (var i = 0; i < NPCS_PER_PAGE; i++)
                slot.NPCRows[i] = CreateNPCRowSlot(slot.Content, contentWidth);

            slot.NPCPaginationBar = new PaginationNode
            {
                IsVisible              = false,
                IsDisplayIndicatorText = true,
                OnPreviousPage = () =>
                {
                    slot.NPCCurrentPage--;
                    ShowNPCPage(slot, true);
                },
                OnNextPage = () =>
                {
                    slot.NPCCurrentPage++;
                    ShowNPCPage(slot, true);
                }
            };
            slot.Content.AddNode(slot.NPCPaginationBar);

            return slot;
        }

        private static NPCRowSlot CreateNPCRowSlot
        (
            VerticalListNode parent,
            float            contentWidth
        )
        {
            var slot = new NPCRowSlot
            {
                Row = new HorizontalListNode
                {
                    Size        = new(contentWidth, 32),
                    ItemSpacing = ROW_SPACING_X,
                    IsVisible   = false
                }
            };

            parent.AddNode(slot.Row);

            slot.NPCNameNode = new TextNode
            {
                TextFlags = TextFlags.Ellipsis,
                Position  = new(0, 4),
                Size      = new(contentWidth - (3 * ROW_SPACING) - (2 * MAP_BTN_WIDTH), 28f),
                FontSize  = 14,
            };
            AtkColors.Text.ApplyTo(slot.NPCNameNode);
            slot.Row.AddNode(slot.NPCNameNode);

            slot.MapButton = new IconButtonNode
            {
                IconId      = 60561,
                TextTooltip = LuminaWrapper.GetAddonText(467),
                Size        = new(MAP_BTN_WIDTH),
                Position    = new(0, -1)
            };
            slot.Row.AddNode(slot.MapButton);

            slot.LocationButton = new IconButtonNode
            {
                IconId      = 60453,
                Size        = new(MAP_BTN_WIDTH),
                TextTooltip = LuminaWrapper.GetAddonText(1806),
                Position    = new(0, -1)
            };
            slot.Row.AddNode(slot.LocationButton);

            return slot;
        }

        private static void UpdateNPCRow
        (
            NPCRowSlot          row,
            ExchangeItemNPCInfo npcInfo
        )
        {
            row.NPCNameNode.String      = $"{npcInfo.Name}（{npcInfo.Location.GetTerritory().ExtractPlaceName()}）";
            row.NPCNameNode.TextTooltip = $"{npcInfo.Name}\n（{npcInfo.Location.GetTerritory().ExtractPlaceName()}）";

            row.MapButton.OnClick = () => OpenMap(npcInfo.Location, npcInfo.Name);

            row.LocationButton.IsEnabled = npcInfo.Location.TerritoryID != 282;
            row.LocationButton.OnClick   = () => TeleportToLocation(npcInfo.Location);
        }

        private static float CalculateSectionHeight
        (
            int  visibleNPCCount,
            bool hasDescription,
            bool hasCost,
            bool hasNPCPagination
        )
        {
            const float HEADER_HEIGHT    = 38f;
            const float ROW_HEIGHT       = 32f;
            const float VERTICAL_PADDING = 20f;
            var descHeight = hasDescription ?
                                 18f :
                                 0f;
            var costHeight = hasCost ?
                                 28f :
                                 0f;
            var paginationHeight = hasNPCPagination ?
                                       32f :
                                       0f;

            return VERTICAL_PADDING                                 +
                   HEADER_HEIGHT                                    +
                   descHeight                                       +
                   costHeight                                       +
                   (visibleNPCCount                  * ROW_HEIGHT)  +
                   (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING) +
                   paginationHeight;
        }

        private class SectionSlot
        {
            public ResNode                   Container          = null!;
            public SimpleNineGridNode        Background         = null!;
            public VerticalListNode          Content            = null!;
            public HorizontalListNode        ItemHeader         = null!;
            public IconImageNode             ItemIcon           = null!;
            public TextNode                  ItemName           = null!;
            public IconButtonNode            MarketButton       = null!;
            public ResNode                   ItemTooltipOverlay = null!;
            public TextNode                  DescriptionNode    = null!;
            public ResNode                   CostRow            = null!;
            public ResNode                   CostTooltipOverlay = null!;
            public IconImageNode[]           CostIcons          = null!;
            public TextNode[]                CostTexts          = null!;
            public NPCRowSlot[]              NPCRows            = null!;
            public PaginationNode            NPCPaginationBar   = null!;
            public int                       NPCCurrentPage;
            public List<ExchangeItemNPCInfo> SortedNPCInfos = [];
        }

        private class NPCRowSlot
        {
            public HorizontalListNode Row            = null!;
            public TextNode           NPCNameNode    = null!;
            public IconButtonNode     MapButton      = null!;
            public IconButtonNode     LocationButton = null!;
        }
    }
}
