using System.Numerics;
using DailyRoutines.Common.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsSource : NativeAddon
    {
        private const int   SECTIONS_PER_PAGE = 10;
        private const int   NPCS_PER_PAGE     = 5;
        private const int   MAX_COSTS         = 4;
        private const float MAP_BTN_WIDTH     = 28f;
        private const float ROW_SPACING_X     = 6f;
        private const float COST_ROW_HEIGHT   = 36f;
        private const float NPC_ROW_HEIGHT    = 32f;
        private const float ROW_SPACING       = 4f;
        private const float VERTICAL_PADDING  = 20f;
        private const float HEADER_HEIGHT     = 42f;

        private AddonNPCShopsSource
        (
            ItemSourceInfo sourceInfo
        ) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsSource? Addon { get; set; }

        public ItemSourceInfo SourceInfo { get; set; }

        private List<CostGroup> costGroups = [];
        private int             currentPage;
        private int             totalPages;

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
            ItemSourceInfo sourceInfo
        )
        {
            if (sourceInfo is not { NPCInfos.Count: > 0 }) return;

            CloseAndClear();

            Addon ??= new(sourceInfo)
            {
                InternalName = "DRNPCShopsSource",
                Title        = Lang.Get("AutoShowItemNPCShopInfo-Addon-Source"),
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
            var item = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.ItemID);

            var sortedNPCs = SourceInfo.NPCInfos
                                       .Where(x => x.Location != null)
                                       .DistinctBy(x => $"{x.Name}_{x.Location.GetTerritory().ExtractPlaceName()}")
                                       .OrderBy(x => x.Location.TerritoryID == 282)
                                       .ThenBy(x => GetLocationName(x.Location))
                                       .ThenBy(x => x.Name)
                                       .ToList();

            costGroups =
            [
                .. sortedNPCs
                   .GroupBy(x => GetCostKey(x.CostInfos))
                   .Select(g => new CostGroup { CostInfos = g.First().CostInfos, NPCInfos = [.. g] })
            ];

            totalPages  = Math.Max(1, (int)Math.Ceiling(costGroups.Count / (double)SECTIONS_PER_PAGE));
            currentPage = 0;

            var hasPagination = totalPages > 1;

            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, HEADER_HEIGHT),
                Position    = ContentStartPosition + new Vector2(8, 2),
                ItemSpacing = 8
            };
            headerNode.AttachNode(this);

            var itemInfoRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 48f),
                ItemSpacing = 4f
            };
            headerNode.AddNode(itemInfoRow);

            var itemIconNode = new ItemIconNode
            {
                ItemID = SourceInfo.ItemID,
                Size   = new(50f),
                OnClick = (_, _, _, _, data) =>
                {
                    if (!data->IsRightClick) return;
                    ContextMenuManager.Instance().OpenItem(SourceInfo.ItemID, AddonId);
                }
            };
            itemInfoRow.AddNode(itemIconNode);

            var itemNameNode = new TextNode
            {
                TextFlags     = TextFlags.Edge | TextFlags.MultiLine | TextFlags.WordWrap,
                String        = LuminaWrapper.GetItemName(SourceInfo.ItemID),
                FontSize      = 24,
                Height        = 42f,
                AlignmentType = AlignmentType.Left
            };
            AtkColors.Label.ApplyTo(itemNameNode);

            itemInfoRow.AddNode(itemNameNode);

            paginationBar = new PaginationNode
            {
                IsVisible              = hasPagination,
                IsDisplayIndicatorText = true,
                OnPreviousPage         = () => ShowPage(currentPage - 1),
                OnNextPage             = () => ShowPage(currentPage + 1)
            };
            paginationBar.Position      = new(0.0f, ContentStartPosition.Y + ContentSize.Y - paginationBar.Height);
            paginationBar.OnSizeUpdated = CenterPaginationBar;
            paginationBar.AttachNode(this);

            CenterPaginationBar();

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

            sectionSlots = new SectionSlot[SECTIONS_PER_PAGE];

            for (var i = 0; i < SECTIONS_PER_PAGE; i++)
            {
                sectionSlots[i]                     = CreateSectionSlot(contentNode);
                sectionSlots[i].Container.IsVisible = false;
            }

            ShowPage(0);
        }

        private void CenterPaginationBar()
        {
            if (paginationBar is not { } bar) return;

            bar.X = ContentStartPosition.X + ((ContentSize.X - bar.Width) / 2.0f);
        }

        private static void CenterNPCPaginationBar
        (
            SectionSlot slot
        ) =>
            slot.NPCPaginationBar.X = (slot.Content.Width - slot.NPCPaginationBar.Width) / 2.0f;

        private void ShowPage
        (
            int page
        )
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
            if (paginationBar == null) return;

            paginationBar.PreviousPageButtonNode.IsEnabled = currentPage > 0;
            paginationBar.NextPageButtonNode.IsEnabled     = currentPage < totalPages - 1;
            paginationBar.IndicatorTextNode.String         = $"{currentPage + 1} / {totalPages}";
        }

        private void UpdateSectionContent
        (
            SectionSlot slot,
            CostGroup   group
        )
        {
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

            slot.SortedNPCInfos = group.NPCInfos;
            slot.NPCCurrentPage = 0;
            ShowNPCPage(slot);
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

            var visibleCostCount = Math.Min(slot.SortedNPCInfos.First().CostInfos.Count, MAX_COSTS);
            var visibleNPCCount  = Math.Min(pageNpcs.Count,                              NPCS_PER_PAGE);
            var sectionHeight    = CalculateSectionHeight(visibleCostCount, visibleNPCCount, hasNPCPagination);
            slot.Container.Size  = new(slot.Container.Width, sectionHeight);
            slot.Background.Size = slot.Container.Size;
            slot.Content.Size    = slot.Container.Size - new Vector2(20f, 20f);
            slot.Content.RecalculateLayout();

            CenterNPCPaginationBar(slot);

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

            slot.CostRows       = new HorizontalListNode[MAX_COSTS];
            slot.CostIcons      = new ItemIconNode[MAX_COSTS];
            slot.CostNames      = new TextNode[MAX_COSTS];
            slot.CostQuantities = new TextNode[MAX_COSTS];

            for (var i = 0; i < MAX_COSTS; i++)
            {
                slot.CostRows[i] = new HorizontalListNode
                {
                    Size        = new(contentWidth, 36),
                    ItemSpacing = 6f
                };
                slot.Content.AddNode(slot.CostRows[i]);

                slot.CostIcons[i] = new ItemIconNode
                {
                    Size = new(42)
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
                    Size          = new(contentWidth - 32 - 6 - 38, 34)
                };
                AtkColors.Label.ApplyTo(slot.CostNames[i]);
                slot.CostRows[i].AddNode(slot.CostNames[i]);

                slot.CostQuantities[i] = new TextNode
                {
                    TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                    FontSize      = 16,
                    Position      = new(contentWidth - 10, 5),
                    Size          = new(70, 20),
                    AlignmentType = AlignmentType.Right
                };
                AtkColors.ValueEmphasize.ApplyTo(slot.CostQuantities[i]);
                slot.CostQuantities[i].AttachNode(slot.CostRows[i]);
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
                },
                OnSizeUpdated = () => CenterNPCPaginationBar(slot)
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
                String   = "",
                Position = new(0, 4),
                Size     = new(contentWidth - (3 * ROW_SPACING) - (2 * MAP_BTN_WIDTH), 28f),
                FontSize = 14
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
            NPCRowSlot   row,
            ShopNPCInfos npcInfo
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
            int  visibleCostCount,
            int  visibleNPCCount,
            bool hasNPCPagination
        )
        {
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

        private static string GetCostKey
        (
            List<ShopItemCostInfo> costInfos
        ) =>
            string.Join("|", costInfos.Select(c => $"{c.ItemID}:{c.Cost}:{c.Collectablity}"));

        private class CostGroup
        {
            public List<ShopItemCostInfo> CostInfos = [];
            public List<ShopNPCInfos>     NPCInfos  = [];
        }

        private class SectionSlot
        {
            public ResNode              Container        = null!;
            public SimpleNineGridNode   Background       = null!;
            public VerticalListNode     Content          = null!;
            public HorizontalListNode[] CostRows         = null!;
            public ItemIconNode[]       CostIcons        = null!;
            public TextNode[]           CostNames        = null!;
            public TextNode[]           CostQuantities   = null!;
            public NPCRowSlot[]         NPCRows          = null!;
            public PaginationNode       NPCPaginationBar = null!;
            public int                  NPCCurrentPage;
            public List<ShopNPCInfos>   SortedNPCInfos = [];
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
