using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsSource : NativeAddon
    {
        private const int   SECTIONS_PER_PAGE = 10;
        private const int   NPCS_PER_PAGE     = 5;
        private const int   MAX_COSTS         = 4;
        private const float NPC_COL_WIDTH     = 320f;
        private const float LOC_COL_WIDTH     = 300f;
        private const float MAP_BTN_WIDTH     = 28f;
        private const float ROW_SPACING_X     = 6f;
        private const float COST_ROW_HEIGHT   = 36f;
        private const float NPC_ROW_HEIGHT    = 32f;
        private const float ROW_SPACING       = 4f;
        private const float VERTICAL_PADDING  = 20f;

        private static Task? OpenAddonTask;

        private AddonNPCShopsSource(ItemSourceInfo sourceInfo) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsSource? Addon { get; set; }

        public ItemSourceInfo SourceInfo { get; set; }

        private List<CostGroup> costGroups = [];
        private int             currentPage;
        private int             totalPages;

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

        public static void OpenWithData(ItemSourceInfo sourceInfo)
        {
            if (sourceInfo is not { NPCInfos.Count: > 0 }) return;
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;
            CloseAndClear();

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    Addon ??= new(sourceInfo)
                    {
                        InternalName = "DRNPCShopsSource",
                        Title        = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Source"),
                        Size         = new(760f, 540f)
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var item = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.ItemID);

            var sortedNPCs = SourceInfo.NPCInfos
                                       .Where(x => x.Location != null)
                                       .DistinctBy(x => $"{x.Name}_{x.Location.GetTerritory().ExtractPlaceName()}")
                                       .OrderBy(x => x.Location.TerritoryID == 282)
                                       .ThenBy(x => GetLocationName(x.Location))
                                       .ThenBy(x => x.Name)
                                       .ToList();

            costGroups = sortedNPCs
                         .GroupBy(x => GetCostKey(x.CostInfos))
                         .Select(g => new CostGroup { CostInfos = g.First().CostInfos, NPCInfos = g.ToList() })
                         .ToList();

            totalPages  = Math.Max(1, (int)Math.Ceiling(costGroups.Count / (double)SECTIONS_PER_PAGE));
            currentPage = 0;

            var hasPagination = totalPages > 1;
            var headerHeight  = hasPagination ? 72f : 42f;
            
            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, headerHeight),
                Position    = ContentStartPosition + new Vector2(8, 2),
                ItemSpacing = 8
            };
            headerNode.AttachNode(this);

            var itemInfoRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 36),
                ItemSpacing = 0
            };
            headerNode.AddNode(itemInfoRow);

            var itemInfoTooltipOverlay = new ResNode
            {
                Size        = new(headerNode.Width, 36),
                Position    = new(0, 0),
                ItemTooltip = SourceInfo.ItemID
            };
            itemInfoTooltipOverlay.AttachNode(headerNode);

            var itemIconNode = new IconImageNode
            {
                IconId     = LuminaWrapper.GetItemIconID(SourceInfo.ItemID),
                Size       = new(36),
                FitTexture = true
            };
            itemInfoRow.AddNode(itemIconNode);

            itemInfoRow.AddDummy(6f);

            var itemNameNode = new TextNode
            {
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                String           = LuminaWrapper.GetItemName(SourceInfo.ItemID),
                FontSize         = 24,
                Position         = new(0, 3),
                AlignmentType    = AlignmentType.TopLeft,
                TextColor        = ColorHelper.GetColor(8),
                TextOutlineColor = ColorHelper.GetColor(7)
            };
            itemInfoRow.AddNode(itemNameNode);

            if (item.ItemSearchCategory.RowId > 0)
            {
                var marketButtonNode = new IconButtonNode
                {
                    IconId      = 60570,
                    TextTooltip = LuminaWrapper.GetAddonText(548),
                    Size        = new(32),
                    Position    = ContentStartPosition + new Vector2(ContentSize.X - 44, 2),
                    OnClick     = () => OpenMarket(SourceInfo.ItemID)
                };
                marketButtonNode.AttachNode(this);
            }

            paginationBar = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 28),
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
                Position  = new(0, 3), AlignmentType             = AlignmentType.Left,
                TextColor = ColorHelper.GetColor(2)
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

            sectionSlots = new SectionSlot[SECTIONS_PER_PAGE];

            for (var i = 0; i < SECTIONS_PER_PAGE; i++)
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

            var pageGroups = costGroups.Skip(currentPage * SECTIONS_PER_PAGE).Take(SECTIONS_PER_PAGE).ToList();

            for (var i = 0; i < SECTIONS_PER_PAGE; i++)
                if (i < pageGroups.Count)
                {
                    UpdateSectionContent(sectionSlots[i], pageGroups[i]);
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

        private void UpdateSectionContent(SectionSlot slot, CostGroup group)
        {
            for (var i = 0; i < MAX_COSTS; i++)
                if (i < group.CostInfos.Count)
                {
                    var costInfo = group.CostInfos[i];
                    slot.CostRows[i].IsVisible              = true;
                    slot.CostTooltipOverlays[i].ItemTooltip = costInfo.ItemID;
                    slot.CostIcons[i].IconId                = LuminaWrapper.GetItemIconID(costInfo.ItemID);
                    slot.CostNames[i].String                = costInfo.GetItemName();
                    slot.CostQuantities[i].String = costInfo.Collectablity != null
                                                        ? $"\ue03d ({costInfo.Collectablity.Value}~)"
                                                        : $"x{costInfo.Cost.ToChineseString()}";
                }
                else
                    slot.CostRows[i].IsVisible = false;

            slot.SortedNPCInfos = group.NPCInfos;
            slot.NPCCurrentPage = 0;
            ShowNPCPage(slot);
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

            var visibleCostCount = Math.Min(slot.SortedNPCInfos.First().CostInfos.Count, MAX_COSTS);
            var visibleNPCCount  = Math.Min(pageNpcs.Count,                              NPCS_PER_PAGE);
            var sectionHeight    = CalculateSectionHeight(visibleCostCount, visibleNPCCount, hasNPCPagination);
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

            slot.CostRows            = new ResNode[MAX_COSTS];
            slot.CostTooltipOverlays = new ResNode[MAX_COSTS];
            slot.CostIcons           = new IconImageNode[MAX_COSTS];
            slot.CostNames           = new TextNode[MAX_COSTS];
            slot.CostQuantities      = new TextNode[MAX_COSTS];

            for (var i = 0; i < MAX_COSTS; i++)
            {
                slot.CostRows[i] = new ResNode
                {
                    Size      = new(contentWidth, 36),
                    IsVisible = false
                };
                slot.Content.AddNode(slot.CostRows[i]);

                slot.CostTooltipOverlays[i] = new ResNode
                {
                    Size     = new(contentWidth, 36),
                    Position = new(0, 0)
                };
                slot.CostTooltipOverlays[i].AttachNode(slot.CostRows[i]);

                slot.CostIcons[i] = new IconImageNode
                {
                    Size           = new(32),
                    TextureSize    = new(32),
                    IconId         = 0,
                    Position       = new(0, 2),
                    ImageNodeFlags = ImageNodeFlags.AutoFit
                };
                slot.CostIcons[i].AttachNode(slot.CostRows[i]);

                slot.CostNames[i] = new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                    FontSize         = 16,
                    Position         = new(slot.CostIcons[i].Width + 4f, 5),
                    AlignmentType    = AlignmentType.Left,
                    TextColor        = ColorHelper.GetColor(8),
                    TextOutlineColor = ColorHelper.GetColor(7)
                };
                slot.CostNames[i].AttachNode(slot.CostRows[i]);

                slot.CostQuantities[i] = new TextNode
                {
                    TextFlags     = TextFlags.AutoAdjustNodeSize,
                    FontSize      = 14,
                    Position      = new(contentWidth - 80, 5),
                    Size          = new(70, 20),
                    AlignmentType = AlignmentType.Right
                };
                slot.CostQuantities[i].AttachNode(slot.CostRows[i]);
            }

            slot.NPCRows = new NPCRowSlot[NPCS_PER_PAGE];
            for (var i = 0; i < NPCS_PER_PAGE; i++)
                slot.NPCRows[i] = CreateNPCRowSlot(slot.Content, contentWidth);

            slot.NPCPaginationBar = new HorizontalListNode
            {
                Size        = new(contentWidth, 28),
                ItemSpacing = 8,
                IsVisible   = false
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

        private static void UpdateNPCRow(NPCRowSlot row, ShopNPCInfos npcInfo)
        {
            row.NPCNameNode.String = npcInfo.Name;

            row.MapButton.OnClick = () => OpenMap(npcInfo.Location, npcInfo.Name);

            row.LocationButton.String    = GetLocationName(npcInfo.Location);
            row.LocationButton.IsEnabled = npcInfo.Location.TerritoryID != 282;
            row.LocationButton.OnClick   = () => TeleportToLocation(npcInfo.Location);
        }

        private static float CalculateSectionHeight(int visibleCostCount, int visibleNPCCount, bool hasNPCPagination)
        {
            var paginationHeight = hasNPCPagination ? 32f : 0f;

            return VERTICAL_PADDING                                      +
                   (visibleCostCount                  * COST_ROW_HEIGHT) +
                   (Math.Max(0, visibleCostCount - 1) * ROW_SPACING)     +
                   (visibleCostCount > 0 ? ROW_SPACING : 0f)             +
                   (visibleNPCCount                  * NPC_ROW_HEIGHT)   +
                   (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING)      +
                   paginationHeight;
        }

        private static string GetCostKey(List<ShopItemCostInfo> costInfos) =>
            string.Join("|", costInfos.Select(c => $"{c.ItemID}:{c.Cost}:{c.Collectablity}"));

        private class CostGroup
        {
            public List<ShopItemCostInfo> CostInfos = [];
            public List<ShopNPCInfos>     NPCInfos  = [];
        }

        private class SectionSlot
        {
            public ResNode            Container           = null!;
            public SimpleNineGridNode Background          = null!;
            public VerticalListNode   Content             = null!;
            public ResNode[]          CostRows            = null!;
            public ResNode[]          CostTooltipOverlays = null!;
            public IconImageNode[]    CostIcons           = null!;
            public TextNode[]         CostNames           = null!;
            public TextNode[]         CostQuantities      = null!;
            public NPCRowSlot[]       NPCRows             = null!;
            public HorizontalListNode NPCPaginationBar    = null!;
            public TextButtonNode     NPCPrevButton       = null!;
            public TextNode           NPCPageIndicator    = null!;
            public TextButtonNode     NPCNextButton       = null!;
            public int                NPCCurrentPage;
            public List<ShopNPCInfos> SortedNPCInfos = [];
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
