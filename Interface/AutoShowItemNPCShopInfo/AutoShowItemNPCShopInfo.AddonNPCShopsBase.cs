using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using DailyRoutines.Common.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private abstract class AddonNPCShopsBase<TSectionSlot> : NativeAddon
        where TSectionSlot : AddonNPCShopsBase<TSectionSlot>.SectionSlot
    {
        protected const int   MAX_COSTS     = 4;
        protected const int   NPCS_PER_PAGE = 5;
        protected const float HEADER_HEIGHT = 42f;
        protected const float ROW_SPACING   = 4f;

        private int currentPage;
        private int totalPages;

        private ScrollingNode<VerticalListNode>? scrollingAreaNode;
        private VerticalListNode?                contentNode;
        private PaginationNode?                  paginationBar;

        private TSectionSlot[]? sectionSlots;

        private NPCRowNode? selectedSlot;

        [SetsRequiredMembers]
        protected AddonNPCShopsBase
        (
            string           internalName,
            ReadOnlySeString title
        )
        {
            InternalName = internalName;
            Title        = title;
            Size         = new(700f, 550f);
        }

        protected abstract int  SectionCount    { get; }
        protected abstract int  SectionsPerPage { get; }
        protected abstract uint HeaderItemID    { get; }

        protected abstract void BuildSections();

        protected abstract TSectionSlot CreateSectionSlot
        (
            VerticalListNode parent
        );

        protected abstract void UpdateSectionContent
        (
            TSectionSlot slot,
            int          index
        );

        protected abstract float CalculateSectionHeight
        (
            TSectionSlot slot,
            int          visibleNPCCount,
            bool         hasNPCPagination
        );

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            BuildSections();

            totalPages  = Math.Max(1, (int)Math.Ceiling(SectionCount / (double)SectionsPerPage));
            currentPage = 0;

            var hasPagination = totalPages > 1;

            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, HEADER_HEIGHT),
                Position    = ContentStartPosition + new Vector2(8, 2),
                ItemSpacing = 4
            };
            headerNode.AttachNode(this);

            var itemInfoRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 36f),
                ItemSpacing = 4f
            };
            headerNode.AddNode(itemInfoRow);

            var itemIconNode = new ItemIconNode
            {
                ItemID = HeaderItemID,
                Size   = new(36f),
                OnClick = (_, _, _, _, data) =>
                {
                    if (!data->IsRightClick) return;
                    ContextMenuManager.Instance().OpenItem(HeaderItemID, AddonId);
                }
            };
            itemInfoRow.AddNode(itemIconNode);

            var itemNameNode = new TextNode
            {
                TextFlags     = TextFlags.Edge | TextFlags.MultiLine | TextFlags.WordWrap,
                String        = LuminaWrapper.GetItemName(HeaderItemID),
                FontSize      = 24,
                Height        = 36f,
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

            sectionSlots = new TSectionSlot[SectionsPerPage];

            for (var i = 0; i < SectionsPerPage; i++)
            {
                sectionSlots[i]                     = CreateSectionSlot(contentNode);
                sectionSlots[i].Container.IsVisible = false;
            }

            ShowPage(0);
        }

        protected static void InitializeSectionSlot
        (
            TSectionSlot     slot,
            VerticalListNode parent
        )
        {
            slot.Container = new ResNode { Size = new(parent.Width, 100) };
            parent.AddNode(slot.Container);

            slot.Background = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Offsets            = new(8f),
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
        }

        protected void InitializeSectionFooter
        (
            TSectionSlot slot,
            float        contentWidth
        )
        {
            slot.NPCRows = new NPCRowNode[NPCS_PER_PAGE];

            for (var i = 0; i < NPCS_PER_PAGE; i++)
            {
                var row = CreateNPCRow(slot.Content, contentWidth);

                row.OnClick = () =>
                {
                    if (row.NPCInfo is not { } info) return;

                    SelectNPCRow(row);
                    OpenMap(info.Location, info.Name);
                };

                slot.NPCRows[i] = row;
            }

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
        }

        protected static List<NPCDisplayInfo> SortNPCInfos
        (
            IEnumerable<NPCDisplayInfo> npcInfos
        ) =>
        [
            .. npcInfos.Where(x => x.Location != null)
                       .DistinctBy(x => $"{x.Location.TerritoryID}_{x.Name}")
                       .OrderBy(x => x.Location.TerritoryID == 282)
                       .ThenBy(x => x.Location.TerritoryID)
        ];

        protected void ShowPage
        (
            int page
        )
        {
            if (sectionSlots == null || contentNode == null || scrollingAreaNode == null) return;

            currentPage = Math.Clamp(page, 0, totalPages - 1);

            var firstIndex   = currentPage * SectionsPerPage;
            var sectionCount = Math.Min(SectionCount - firstIndex, SectionsPerPage);

            for (var i = 0; i < SectionsPerPage; i++)
            {
                if (i < sectionCount)
                {
                    UpdateSectionContent(sectionSlots[i], firstIndex + i);
                    sectionSlots[i].Container.IsVisible = true;
                }
                else
                    sectionSlots[i].Container.IsVisible = false;
            }

            selectedSlot?.Selected = false;
            selectedSlot           = null;

            scrollingAreaNode.ScrollBarNode.ScrollPosition = 0;
            contentNode.RecalculateLayout();
            scrollingAreaNode.RecalculateSizes();
            UpdatePaginationState();
        }

        protected void ShowNPCPage
        (
            TSectionSlot slot,
            bool         recalculateOuter = false
        )
        {
            var totalNpcs = slot.SortedNPCInfos.Count;
            var npcPages  = Math.Max(1, (int)Math.Ceiling(totalNpcs / (double)NPCS_PER_PAGE));
            var npcPage   = Math.Clamp(slot.NPCCurrentPage, 0, npcPages - 1);
            slot.NPCCurrentPage = npcPage;

            var pageNpcs = slot.SortedNPCInfos.Skip(npcPage * NPCS_PER_PAGE).Take(NPCS_PER_PAGE).ToList();

            for (var i = 0; i < NPCS_PER_PAGE; i++)
            {
                if (i < pageNpcs.Count)
                {
                    slot.NPCRows[i].SetNPCInfo(pageNpcs[i]);
                    slot.NPCRows[i].IsVisible = true;
                }
                else
                    slot.NPCRows[i].IsVisible = false;
            }

            selectedSlot?.Selected = false;
            selectedSlot           = null;

            var hasNPCPagination = totalNpcs > NPCS_PER_PAGE;
            slot.NPCPaginationBar.IsVisible = hasNPCPagination;

            if (hasNPCPagination)
            {
                slot.NPCPaginationBar.PreviousPageButtonNode.IsEnabled = npcPage > 0;
                slot.NPCPaginationBar.NextPageButtonNode.IsEnabled     = npcPage < npcPages - 1;
                slot.NPCPaginationBar.IndicatorTextNode.String         = $"{npcPage + 1} / {npcPages}";
            }

            var visibleNPCCount = Math.Min(pageNpcs.Count, NPCS_PER_PAGE);
            var sectionHeight   = CalculateSectionHeight(slot, visibleNPCCount, hasNPCPagination);
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

        protected static void CenterNPCPaginationBar
        (
            TSectionSlot slot
        ) =>
            slot.NPCPaginationBar.X = (slot.Content.Width - slot.NPCPaginationBar.Width) / 2.0f;

        private void SelectNPCRow
        (
            NPCRowNode row
        )
        {
            if (sectionSlots == null) return;

            selectedSlot?.Selected = false;

            row.Selected = true;
            selectedSlot = row;
        }

        protected static NPCRowNode CreateNPCRow
        (
            VerticalListNode parent,
            float            contentWidth
        )
        {
            var row = new NPCRowNode
            {
                Size      = new(contentWidth, 32f),
                IsVisible = false
            };

            parent.AddNode(row);

            return row;
        }

        private void UpdatePaginationState()
        {
            if (paginationBar == null) return;

            paginationBar.PreviousPageButtonNode.IsEnabled = currentPage > 0;
            paginationBar.NextPageButtonNode.IsEnabled     = currentPage < totalPages - 1;
            paginationBar.IndicatorTextNode.String         = $"{currentPage + 1}/{totalPages}";
        }

        private void CenterPaginationBar()
        {
            if (paginationBar is not { } bar) return;

            bar.X = ContentStartPosition.X + ((ContentSize.X - bar.Width) / 2.0f);
        }

        public sealed record NPCDisplayInfo
        (
            string                 Name,
            ShopNPCLocation?       Location,
            List<ShopItemCostInfo> CostInfos
        );

        public class SectionSlot
        {
            public ResNode              Container        = null!;
            public SimpleNineGridNode   Background       = null!;
            public VerticalListNode     Content          = null!;
            public NPCRowNode[]         NPCRows          = null!;
            public PaginationNode       NPCPaginationBar = null!;
            public int                  NPCCurrentPage;
            public List<NPCDisplayInfo> SortedNPCInfos = [];
        }

        public class NPCRowNode : ListButtonNode
        {
            public NPCRowNode()
            {
                AtkColors.Text.ApplyTo(LabelNode);

                CollisionNode.AddEvent
                (
                    AtkEventType.MouseClick,
                    (_, _, _, _, data) =>
                    {
                        if (!data->IsRightClick) return;
                        if (NPCInfo is not { } info) return;

                        OpenTeleportMenu(info.Location);
                    }
                );
            }

            public NPCDisplayInfo? NPCInfo { get; private set; }

            public void SetNPCInfo
            (
                NPCDisplayInfo info
            )
            {
                NPCInfo = info;

                var placeName = info.Location.GetTerritory().ExtractPlaceName();

                String      = $"{info.Name}（{placeName}）";
                TextTooltip = $"{info.Name}\n（{placeName}）";
            }

            private void OpenTeleportMenu
            (
                ShopNPCLocation location
            ) =>
                ContextMenuManager.Instance().Open
                (
                    new()
                    {
                        OwnerAddonID = ParentAddon->Id
                    },
                    [
                        new ContextMenuEntryInfo
                        (
                            nameof(AutoShowItemNPCShopInfo),
                            _ => new ContextMenuItem
                            {
                                Name      = LuminaWrapper.GetAddonText(1806),
                                IsEnabled = location.TerritoryID != 282,
                                OnClicked = _ =>
                                {
                                    IFramework.Instance().RunOnTick
                                    (
                                        () => TeleportToLocation(location),
                                        delayTicks: 1
                                    );
                                }
                            }
                        )
                    ]
                );
        }
    }
}
