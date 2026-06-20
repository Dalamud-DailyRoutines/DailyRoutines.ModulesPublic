using System.Numerics;
using DailyRoutines.Common.Extensions;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsDestination : NativeAddon
    {
        private const int   ITEMS_PER_PAGE = 20;
        private const int   NPCS_PER_PAGE  = 5;
        private const int   MAX_COSTS      = 4;
        private const float NPC_COL_WIDTH  = 320f;
        private const float LOC_COL_WIDTH  = 300f;
        private const float MAP_BTN_WIDTH  = 28f;
        private const float ROW_SPACING_X  = 6f;

        private static Task? OpenAddonTask;

        private AddonNPCShopsDestination(ExchangeItemsInfo sourceInfo) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsDestination? Addon      { get; set; }
        public        ExchangeItemsInfo         SourceInfo { get; set; }

        private List<ExchangeItemInfo> exchangeItems = [];
        private int                    currentPage;
        private int                    totalPages;

        private ScrollingNode<VerticalListNode>? scrollingAreaNode;
        private VerticalListNode?                contentNode;
        private HorizontalListNode?              paginationBar;
        private TextButtonNode?                  prevButton;
        private TextNode?                        pageIndicator;
        private TextButtonNode?                  nextButton;
        private SectionSlot[]?                   sectionSlots;

        public static void CloseAndClear()
        {
            if (Addon == null) return;
            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData(ExchangeItemsInfo sourceInfo)
        {
            if (sourceInfo is not { Items.Count: > 0 }) return;
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;
            CloseAndClear();

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    Addon ??= new(sourceInfo)
                    {
                        InternalName = "DRNPCShopsDestinations",
                        Title        = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Destination"),
                        Size         = new(760f, 540f)
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var costItem  = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.CostItemID);
            var itemCount = GetOwnedItemCount(SourceInfo.CostItemID);

            exchangeItems = SourceInfo.Items.OrderBy(x => x.GetItemName()).ToList();
            totalPages    = Math.Max(1, (int)Math.Ceiling(exchangeItems.Count / (double)ITEMS_PER_PAGE));
            currentPage   = 0;

            var hasPagination = totalPages > 1;
            var headerHeight  = hasPagination ? 104f : 72f;

            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, headerHeight),
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
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                String           = LuminaWrapper.GetItemName(SourceInfo.CostItemID),
                FontSize         = 24,
                Position         = new(iconNode.Position.X + iconNode.Width + 6f, 3),
                AlignmentType    = AlignmentType.TopLeft,
                TextColor        = ColorHelper.GetColor(8),
                TextOutlineColor = ColorHelper.GetColor(7)
            };
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

            var summaryRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 26),
                ItemSpacing = 16
            };
            headerNode.AddNode(summaryRow);

            summaryRow.AddNode
            (
                new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize,
                    String           = $"{LuminaWrapper.GetAddonText(358)}: {itemCount}",
                    Position         = new(0, 3),
                    TextColor        = ColorHelper.GetColor(34),
                    TextOutlineColor = ColorHelper.GetColor(7)
                }
            );
            summaryRow.AddNode
            (
                new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize,
                    String           = Lang.Get("AutoShowItemNPCShopInfo-ExchangeItemCount", SourceInfo.Items.Count),
                    Position         = new(0, 3),
                    TextColor        = ColorHelper.GetColor(3),
                    TextOutlineColor = ColorHelper.GetColor(7)
                }
            );

            paginationBar = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 30),
                ItemSpacing = 8,
                IsVisible   = hasPagination
            };
            headerNode.AddNode(paginationBar);

            prevButton = new TextButtonNode
            {
                String  = "<", Size = new(40, 24),
                OnClick = () => ShowPage(currentPage - 1)
            };
            paginationBar.AddNode(prevButton);

            pageIndicator = new TextNode
            {
                TextFlags = TextFlags.AutoAdjustNodeSize, String = $"1 / {totalPages}",
                Position  = new(0, 3), AlignmentType             = AlignmentType.Left, TextColor = ColorHelper.GetColor(2)
            };
            paginationBar.AddNode(pageIndicator);

            nextButton = new TextButtonNode
            {
                String  = ">", Size = new(40, 24),
                OnClick = () => ShowPage(currentPage + 1)
            };
            paginationBar.AddNode(nextButton);

            scrollingAreaNode = new ScrollingNode<VerticalListNode>
            {
                Position          = ContentStartPosition + new Vector2(6,  headerHeight + 6),
                Size              = ContentSize          - new Vector2(12, headerHeight + 6),
                ScrollSpeed       = 100,
                AutoHideScrollBar = true
            };
            scrollingAreaNode.ContentNode.Height = 100;
            scrollingAreaNode.AttachNode(this);

            contentNode             = scrollingAreaNode.ContentNode;
            contentNode.ItemSpacing = 6;

            sectionSlots = new SectionSlot[ITEMS_PER_PAGE];

            for (var i = 0; i < ITEMS_PER_PAGE; i++)
            {
                sectionSlots[i]                     = CreateSectionSlot(contentNode);
                sectionSlots[i].Container.IsVisible = false;
            }

            ShowPage(0);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();
                if (SystemMenu != null) SystemMenu->Close(true);
            }
        }

        private void ShowPage(int page)
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
            if (prevButton                     == null || nextButton == null || pageIndicator == null) return;
            prevButton.IsEnabled = currentPage > 0;
            nextButton.IsEnabled = currentPage < totalPages - 1;
            pageIndicator.String = $"{currentPage + 1} / {totalPages}";
        }

        private void UpdateSectionContent(SectionSlot slot, ExchangeItemInfo exchangeItem)
        {
            var exchangeTargetItem = LuminaGetter.GetRowOrDefault<Item>(exchangeItem.ItemID);

            slot.ItemTooltipOverlay.ItemTooltip = exchangeItem.ItemID;
            slot.ItemIcon.IconId               = LuminaWrapper.GetItemIconID(exchangeItem.ItemID);
            slot.ItemName.String               = exchangeItem.GetItemName();
            slot.ItemName.FontSize             = 16;
            slot.ItemName.AutoAdjustTextSize();

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

        private static void UpdateCostRow(SectionSlot slot, List<ShopItemCostInfo> costInfos)
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

        private void ShowNPCPage(SectionSlot slot, bool recalculateOuter = false)
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
                slot.NPCPrevButton.IsEnabled = npcPage > 0;
                slot.NPCNextButton.IsEnabled = npcPage < npcPages - 1;
                slot.NPCPageIndicator.String = $"{npcPage + 1} / {npcPages}";
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

        private SectionSlot CreateSectionSlot(VerticalListNode parent)
        {
            var slot           = new SectionSlot();
            var containerWidth = parent.Width - 4;

            slot.Container = new ResNode { Size = new(containerWidth, 100) };
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
                TextFlags        = TextFlags.Edge,
                AlignmentType    = AlignmentType.Left,
                String           = "",
                FontSize         = 16,
                Size             = new(contentWidth - 32 - 6 - 38, 32),
                Position         = new(0, 2),
                TextColor        = ColorHelper.GetColor(28),
                TextOutlineColor = ColorHelper.GetColor(509)
            };
            slot.ItemHeader.AddNode(slot.ItemName);

            slot.MarketButton = new IconButtonNode
            {
                IconId      = 60570,
                TextTooltip = LuminaWrapper.GetAddonText(548),
                Size        = new(28),
                Position    = new(contentWidth - 28, 0),
                IsVisible   = false
            };
            slot.MarketButton.AttachNode(slot.Content);

            slot.ItemTooltipOverlay = new ResNode
            {
                Size     = new(contentWidth, 36),
                Position = new(0, 0)
            };
            slot.ItemTooltipOverlay.AttachNode(slot.Content);

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

            slot.NPCPaginationBar = new HorizontalListNode
            {
                Size = new(contentWidth, 28), ItemSpacing = 8, IsVisible = false
            };
            slot.Content.AddNode(slot.NPCPaginationBar);

            slot.NPCPrevButton = new TextButtonNode
            {
                String = "<", Size = new(36, 22),
                OnClick = () =>
                {
                    slot.NPCCurrentPage--;
                    ShowNPCPage(slot, true);
                }
            };
            slot.NPCPaginationBar.AddNode(slot.NPCPrevButton);

            slot.NPCPageIndicator = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize, String = "1 / 1",
                Position      = new(0, 2),
                AlignmentType = AlignmentType.Left,
                TextColor     = ColorHelper.GetColor(2)
            };
            slot.NPCPaginationBar.AddNode(slot.NPCPageIndicator);

            slot.NPCNextButton = new TextButtonNode
            {
                String = ">", Size = new(36, 22),
                OnClick = () =>
                {
                    slot.NPCCurrentPage++;
                    ShowNPCPage(slot, true);
                }
            };
            slot.NPCPaginationBar.AddNode(slot.NPCNextButton);

            return slot;
        }

        private static NPCRowSlot CreateNPCRowSlot(VerticalListNode parent, float contentWidth)
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
                String    = "",
                Position  = new(0, 4),
                Size      = new(NPC_COL_WIDTH, 28f),
                FontSize  = 14,
                TextColor = ColorHelper.GetColor(2)
            };
            slot.Row.AddNode(slot.NPCNameNode);

            slot.MapButton = new IconButtonNode
            {
                IconId      = 60561,
                TextTooltip = LuminaWrapper.GetAddonText(467),
                Size        = new(MAP_BTN_WIDTH),
                Position    = new(0, -1)
            };
            slot.Row.AddNode(slot.MapButton);

            slot.LocationButton = new TextButtonNode
            {
                Size        = new(LOC_COL_WIDTH, 28),
                TextTooltip = LuminaWrapper.GetAddonText(1806)
            };
            slot.Row.AddNode(slot.LocationButton);

            return slot;
        }

        private static void UpdateNPCRow(NPCRowSlot row, ExchangeItemNPCInfo npcInfo)
        {
            row.NPCNameNode.String   = GetExchangeNPCDisplayText(npcInfo);
            row.NPCNameNode.FontSize = 14;
            row.NPCNameNode.AutoAdjustTextSize();

            row.MapButton.OnClick = () => OpenMap(npcInfo.Location, npcInfo.Name);

            row.LocationButton.String    = GetLocationName(npcInfo.Location);
            row.LocationButton.IsEnabled = npcInfo.Location.TerritoryID != 282;
            row.LocationButton.OnClick   = () => TeleportToLocation(npcInfo.Location);
        }

        private static float CalculateSectionHeight(int visibleNPCCount, bool hasDescription, bool hasCost, bool hasNPCPagination)
        {
            const float HEADER_HEIGHT    = 38f;
            const float ROW_HEIGHT       = 32f;
            const float ROW_SPACING      = 4f;
            const float VERTICAL_PADDING = 20f;
            var         descHeight       = hasDescription ? 18f : 0f;
            var         costHeight       = hasCost ? 28f : 0f;
            var         paginationHeight = hasNPCPagination ? 32f : 0f;

            return VERTICAL_PADDING                                 +
                   HEADER_HEIGHT                                    +
                   descHeight                                       +
                   costHeight                                       +
                   (visibleNPCCount                  * ROW_HEIGHT)  +
                   (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING) +
                   paginationHeight;
        }

        private static string GetExchangeNPCDisplayText(ExchangeItemNPCInfo npcInfo) =>
            string.IsNullOrWhiteSpace(npcInfo.ShopName) ? npcInfo.Name : $"{npcInfo.Name} ({npcInfo.ShopName})";

        private class SectionSlot
        {
            public ResNode                   Container           = null!;
            public SimpleNineGridNode        Background          = null!;
            public VerticalListNode          Content             = null!;
            public HorizontalListNode        ItemHeader          = null!;
            public IconImageNode             ItemIcon            = null!;
            public TextNode                  ItemName            = null!;
            public IconButtonNode            MarketButton        = null!;
            public ResNode                   ItemTooltipOverlay  = null!;
            public TextNode                  DescriptionNode     = null!;
            public ResNode                   CostRow             = null!;
            public ResNode                   CostTooltipOverlay  = null!;
            public IconImageNode[]           CostIcons           = null!;
            public TextNode[]                CostTexts           = null!;
            public NPCRowSlot[]              NPCRows             = null!;
            public HorizontalListNode        NPCPaginationBar    = null!;
            public TextButtonNode            NPCPrevButton       = null!;
            public TextNode                  NPCPageIndicator    = null!;
            public TextButtonNode            NPCNextButton       = null!;
            public int                       NPCCurrentPage;
            public List<ExchangeItemNPCInfo> SortedNPCInfos = [];
        }

        private class NPCRowSlot
        {
            public HorizontalListNode Row            = null!;
            public TextNode           NPCNameNode    = null!;
            public IconButtonNode     MapButton      = null!;
            public TextButtonNode     LocationButton = null!;
        }
    }
}
