using System.Numerics;
using DailyRoutines.Common.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace DailyRoutines.ModulesPublic.Interface.CustomizeMapMarker;

public unsafe partial class CustomizeMapMarker
{
    private sealed class MarkerListAddon
    (
        CustomizeMapMarker module
    ) : NativeAddon
    {
        private const int   GROUPS_PER_PAGE  = 5;
        private const int   MARKERS_PER_PAGE = 5;
        private const float HEADER_HEIGHT    = 64f;

        private readonly Dictionary<string, int>                            groupPages = [];
        private          ScrollingNode<VerticalListNode>?                   scrollArea;
        private          TreeListNode<MarkerListEntry, MarkerTreeItemNode>? markerTree;
        private          LabelTextNode?                                     mapNameText;
        private          HorizontalListNode?                                paginationBar;
        private          TextButtonNode?                                    previousPageButton;
        private          TextNode?                                          pageIndicator;
        private          TextButtonNode?                                    nextPageButton;
        private          CircleButtonNode?                                  importButton;

        private uint currentMapID = uint.MaxValue;
        private int  currentPage;
        private int  totalPages;
        private bool isRebuilding;

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            mapNameText = new()
            {
                Position  = ContentStartPosition + new Vector2(0, 4),
                Size      = new(ContentSize.X - 36, 36),
                FontSize  = 18,
                TextFlags = TextFlags.Edge | TextFlags.Ellipsis
            };
            AtkColors.Label.ApplyTo(ref mapNameText);
            mapNameText.AttachNode(this);

            importButton = new CircleButtonNode
            {
                Position    = ContentStartPosition + new Vector2(ContentSize.X - 28, 4),
                Icon        = CircleButtonIcon.Document,
                TextTooltip = Lang.Get("CustomizeMapMarker-Import"),
                Size        = new(28),
                OnClick     = module.ImportMarkers
            };
            importButton.AttachNode(this);

            paginationBar = new HorizontalListNode
            {
                Position    = ContentStartPosition + new Vector2(0, 32),
                Size        = ContentSize with { Y = 28 },
                Alignment   = HorizontalListAnchor.Center,
                ItemSpacing = 8,
                IsVisible   = false
            };
            paginationBar.AttachNode(this);

            previousPageButton = CreatePageButton("<", () => ShowPage(currentPage - 1));
            paginationBar.AddNode(previousPageButton);

            pageIndicator = CreatePageIndicator();
            paginationBar.AddNode(pageIndicator);

            nextPageButton = CreatePageButton(">", () => ShowPage(currentPage + 1));
            paginationBar.AddNode(nextPageButton);

            scrollArea = new ScrollingNode<VerticalListNode>
            {
                Position          = ContentStartPosition + new Vector2(0, HEADER_HEIGHT),
                Size              = ContentSize          - new Vector2(0, HEADER_HEIGHT),
                ScrollSpeed       = 100,
                AutoHideScrollBar = true,
                ContentNode =
                {
                    FitContents = true,
                    ItemSpacing = 4
                }
            };
            scrollArea.AttachNode(this);

            markerTree = new TreeListNode<MarkerListEntry, MarkerTreeItemNode>
            {
                Size            = new(ContentSize.X, ContentSize.Y - HEADER_HEIGHT),
                ItemSpacing     = 4,
                NoResultsString = Lang.Get("CustomizeMapMarker-Empty")
            };
            scrollArea.ContentNode.AddNode(markerTree);

            BuildList();
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            var mapID = AgentMap.Instance()->SelectedMapId;
            if (mapID == currentMapID) return;

            currentMapID = mapID;
            currentPage  = 0;
            groupPages.Clear();
            BuildList();
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            groupPages.Clear();
            scrollArea         = null;
            markerTree         = null;
            mapNameText        = null;
            paginationBar      = null;
            previousPageButton = null;
            pageIndicator      = null;
            nextPageButton     = null;
            importButton       = null;
            currentMapID       = uint.MaxValue;
            currentPage        = 0;
            totalPages         = 0;
        }

        public void RebuildList()
        {
            if (!IsOpen) return;
            BuildList();
        }

        private void BuildList()
        {
            if (markerTree is null || mapNameText is null || paginationBar is null || isRebuilding) return;

            isRebuilding = true;

            try
            {
                var mapID = AgentMap.Instance()->SelectedMapId;

                if (mapID != currentMapID)
                {
                    currentMapID = mapID;
                    currentPage  = 0;
                    groupPages.Clear();
                }

                var groups = module.config.Markers
                                   .Where(marker => marker.MapID == currentMapID)
                                   .OrderBy(marker => marker.Group)
                                   .ThenBy(marker => marker.Name)
                                   .GroupBy(marker => marker.Group)
                                   .ToList();
                totalPages  = Math.Max(1, (groups.Count + GROUPS_PER_PAGE - 1) / GROUPS_PER_PAGE);
                currentPage = Math.Clamp(currentPage, 0, totalPages - 1);

                mapNameText.String      = FormatMapName(currentMapID);
                paginationBar.IsVisible = totalPages > 1;
                UpdatePaginationState();

                var options = new Dictionary<ReadOnlySeString, List<MarkerListEntry>>(GROUPS_PER_PAGE);

                foreach (var group in groups.Skip(currentPage * GROUPS_PER_PAGE).Take(GROUPS_PER_PAGE))
                {
                    var groupPageCount = Math.Max(1, (group.Count() + MARKERS_PER_PAGE - 1) / MARKERS_PER_PAGE);
                    var groupPage = groupPages.TryGetValue(group.Key, out var savedPage) ?
                                        Math.Clamp(savedPage, 0, groupPageCount - 1) :
                                        0;
                    groupPages[group.Key] = groupPage;

                    var entries = new List<MarkerListEntry>
                    {
                        new()
                        {
                            Export      = () => ExportGroup(group.Key),
                            MarkerCount = group.Count()
                        }
                    };
                    entries.AddRange
                    (
                        group.Skip(groupPage * MARKERS_PER_PAGE)
                             .Take(MARKERS_PER_PAGE)
                             .Select(CreateMarkerEntry)
                    );

                    if (groupPageCount > 1)
                    {
                        entries.Add
                        (
                            new MarkerListEntry
                            {
                                PreviousPage = () => ShowGroupPage(group.Key, groupPage - 1),
                                NextPage     = () => ShowGroupPage(group.Key, groupPage + 1),
                                Page         = groupPage,
                                TotalPages   = groupPageCount
                            }
                        );
                    }

                    options[new(group.Key)] = entries;
                }

                markerTree.Options = options;
                scrollArea?.ScrollToTop();
                scrollArea?.ContentNode.RecalculateLayout();
                scrollArea?.RecalculateSizes();
            }
            finally
            {
                isRebuilding = false;
            }
        }

        private MarkerListEntry CreateMarkerEntry(MarkerRecord marker) => new()
        {
            Marker      = marker,
            OpenMarker  = () => module.markerDetailsAddon?.OpenMarker(marker.ID),
            SetGameFlag = () => SetGameFlag(marker)
        };

        private static TextButtonNode CreatePageButton(string text, Action? onClick = null) => new()
        {
            String  = text,
            Size    = new(36, 24),
            OnClick = onClick
        };

        private static TextNode CreatePageIndicator() => new()
        {
            TextFlags     = TextFlags.AutoAdjustNodeSize,
            String        = "1 / 1",
            Position      = new(0, 3),
            AlignmentType = AlignmentType.Left
        };

        private void ShowPage(int page)
        {
            currentPage = Math.Clamp(page, 0, totalPages - 1);
            BuildList();
        }

        private void ShowGroupPage(string group, int page)
        {
            groupPages[group] = page;
            BuildList();
        }

        private void UpdatePaginationState()
        {
            if (previousPageButton is null ||
                pageIndicator is null      ||
                nextPageButton is null)
                return;

            previousPageButton.IsEnabled = currentPage > 0;
            nextPageButton.IsEnabled     = currentPage < totalPages - 1;
            pageIndicator.String         = $"{currentPage + 1} / {totalPages}";
        }

        private void ExportGroup(string group)
        {
            ExportMarkers
            (
                module.config.Markers.Where(marker => marker.MapID == currentMapID && marker.Group == group)
            );
        }

        private sealed class MarkerListEntry
        {
            public MarkerRecord? Marker       { get; init; }
            public Action?       OpenMarker   { get; init; }
            public Action?       SetGameFlag  { get; init; }
            public Action?       Export       { get; init; }
            public int           MarkerCount  { get; init; }
            public Action?       PreviousPage { get; init; }
            public Action?       NextPage     { get; init; }
            public int           Page         { get; init; }
            public int           TotalPages   { get; init; }
            public bool          IsExport     => Export is not null;
            public bool          IsPagination => Marker is null && !IsExport;
        }

        private sealed class MarkerTreeItemNode : TreeListItemNode<MarkerListEntry>, ITreeListItemNode
        {
            public static float ItemHeight => 32;

            private readonly HorizontalListNode row;
            private readonly TextButtonNode     markerButton;
            private readonly IconButtonNode     flagButton;
            private readonly TextNode           groupCountText;
            private readonly CircleButtonNode   exportButton;
            private readonly TextButtonNode     previousPageButton;
            private readonly TextNode           pageIndicator;
            private readonly TextButtonNode     nextPageButton;

            public MarkerTreeItemNode()
            {
                row = new()
                {
                    Size        = new(0, ItemHeight),
                    ItemSpacing = 6
                };
                row.AttachNode(this);

                markerButton = new();
                row.AddNode(markerButton);

                flagButton = new IconButtonNode
                {
                    IconId      = DEFAULT_ICON_ID,
                    Size        = new(32),
                    Position    = new(0, -2),
                    TextTooltip = Lang.Get("CustomizeMapMarker-SetFlag")
                };
                row.AddNode(flagButton);

                groupCountText = new TextNode
                {
                    TextFlags     = TextFlags.Edge,
                    AlignmentType = AlignmentType.Left
                };
                AtkColors.Value.ApplyTo(ref groupCountText);
                row.AddNode(groupCountText);

                exportButton = new CircleButtonNode
                {
                    Icon        = CircleButtonIcon.RightArrow,
                    Size        = new(28),
                    TextTooltip = Lang.Get("CustomizeMapMarker-Export")
                };
                row.AddNode(exportButton);

                previousPageButton = CreatePageButton("<");
                row.AddNode(previousPageButton);

                pageIndicator = CreatePageIndicator();
                row.AddNode(pageIndicator);

                nextPageButton = CreatePageButton(">");
                row.AddNode(nextPageButton);
            }

            protected override void OnSizeChanged()
            {
                base.OnSizeChanged();
                row.Size            = Size;
                markerButton.Size   = new(Math.Max(0, Width - 38), Height);
                groupCountText.Size = new(Math.Max(0, Width - 34), Height);
            }

            protected override void SetNodeData(MarkerListEntry itemData)
            {
                var isExport     = itemData.IsExport;
                var isPagination = itemData.IsPagination;

                row.Alignment                = HorizontalListAnchor.Left;
                markerButton.IsVisible       = itemData.Marker is not null;
                flagButton.IsVisible         = itemData.Marker is not null;
                groupCountText.IsVisible     = isExport;
                exportButton.IsVisible       = isExport;
                previousPageButton.IsVisible = isPagination;
                pageIndicator.IsVisible      = isPagination;
                nextPageButton.IsVisible     = isPagination;

                if (itemData.Marker is { } marker)
                {
                    markerButton.String  = marker.Name;
                    markerButton.OnClick = itemData.OpenMarker;
                    flagButton.OnClick   = itemData.SetGameFlag;
                }
                else if (isExport)
                {
                    groupCountText.String = Lang.Get("CustomizeMapMarker-MarkerCount", itemData.MarkerCount);
                    exportButton.OnClick  = itemData.Export;
                }
                else
                {
                    previousPageButton.OnClick = itemData.PreviousPage;
                    nextPageButton.OnClick     = itemData.NextPage;
                    pageIndicator.String       = $"{itemData.Page + 1} / {itemData.TotalPages}";
                }

                row.RecalculateLayout();
            }
        }
    }
}
