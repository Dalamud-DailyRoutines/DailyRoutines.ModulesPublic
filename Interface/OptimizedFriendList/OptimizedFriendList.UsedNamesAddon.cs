using System.Numerics;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Common.RemoteInteraction.Enums;
using DailyRoutines.Common.RemoteInteraction.Models;
using DailyRoutines.RemoteInteraction.UsedNames;
using DailyRoutines.RemoteInteraction.UsedNames.Models;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using OmenTools.Dalamud;
using TimeAgo;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class OptimizedFriendList
{
    private sealed class DRFriendlistUsedNames
    (
        WorldRegion region
    ) : NativeAddon
    {
        public const float WINDOW_WIDTH  = 560f;
        public const float WINDOW_HEIGHT = 480f;

        private const float HEADER_HEIGHT        = 34f;
        private const float COLUMN_HEADER_HEIGHT = 20f;
        private const float DIVIDER_HEIGHT       = 8f;
        private const float DIVIDER_SPACING      = 2f;

        private const float COLUMN_OLD_NAME_WIDTH = 192f;
        private const float COLUMN_NEW_NAME_WIDTH = 192f;
        private const float COLUMN_TIME_WIDTH     = 128f;

        private const float TIME_X = COLUMN_OLD_NAME_WIDTH + COLUMN_NEW_NAME_WIDTH;

        private static readonly (string TextKey, float X, float Width, AlignmentType Alignment)[] COLUMN_HEADERS =
        [
            ("OptimizedFriendList-UsedNames-OldName", 0f, COLUMN_OLD_NAME_WIDTH, AlignmentType.Left),
            ("OptimizedFriendList-UsedNames-NewName", COLUMN_OLD_NAME_WIDTH, COLUMN_NEW_NAME_WIDTH, AlignmentType.Left),
            ("OptimizedFriendList-UsedNames-Time", TIME_X, COLUMN_TIME_WIDTH, AlignmentType.Right)
        ];

        private IDisposable? observation;

        private UsedNamesListNode? listNode;

        public string PlayerName { get; private set; } = string.Empty;
        public string WorldName  { get; private set; } = string.Empty;

        private ulong ContentID { get; set; }

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            if (ContentID == 0 || string.IsNullOrWhiteSpace(PlayerName))
            {
                Close();
                return;
            }

            var contentStart = ContentStartPosition;
            var contentSize  = ContentSize;

            using var identityBuilder = new RentedSeStringBuilder();
            identityBuilder.Builder
                           .Append(PlayerName)
                           .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                           .Append(WorldName);

            var identityNode = new TextNode
            {
                Position      = contentStart,
                Size          = contentSize with { Y = HEADER_HEIGHT - 6f },
                FontSize      = 20,
                String        = identityBuilder.Builder.ToReadOnlySeString(),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Ellipsis
            };
            identityNode.AttachNode(this);

            foreach (var (textKey, x, width, alignment) in COLUMN_HEADERS)
            {
                var columnNode = new TextNode
                {
                    Position      = contentStart + new Vector2(x, HEADER_HEIGHT),
                    Size          = new(width, COLUMN_HEADER_HEIGHT),
                    FontSize      = 12,
                    String        = Lang.Get(textKey),
                    AlignmentType = alignment,
                    TextFlags     = TextFlags.None
                };
                AtkColors.ListHeader.ApplyTo(ref columnNode);
                columnNode.AttachNode(this);
            }

            var headerDividerNode = new HorizontalDashedLineNode
            {
                Position = contentStart + new Vector2(0f, HEADER_HEIGHT + COLUMN_HEADER_HEIGHT),
                Size     = contentSize with { Y = DIVIDER_HEIGHT }
            };
            headerDividerNode.AttachNode(this);

            const float LIST_TOP = HEADER_HEIGHT + COLUMN_HEADER_HEIGHT + DIVIDER_HEIGHT + DIVIDER_SPACING;
            listNode = new()
            {
                Position        = contentStart + new Vector2(0f, LIST_TOP),
                Size            = contentSize with { Y = contentSize.Y - LIST_TOP },
                AutoResetScroll = false,
                OptionsList     = []
            };
            listNode.AttachNode(this);

            var refreshButton = new CircleButtonNode
            {
                Position    = new(WINDOW_WIDTH - 70f, 5f),
                Size        = new(28f),
                Icon        = CircleButtonIcon.Refresh,
                TextTooltip = Lang.Get("Refresh"),
                OnClick     = Refresh
            };
            refreshButton.AttachNode(this);

            var windowNode = (WindowNode)WindowNode!;

            windowNode.ShowConfigButton = false;
            windowNode.ShowHelpButton   = false;

            observation = RemoteUsedNames.Observe(ContentID, region, BuildEntries);
            RemoteUsedNames.GetOrRequest(ContentID, region);

            if (RemoteUsedNames.TryGet(ContentID, region, out var cached))
                BuildEntries(new(RemoteSnapshotStatus.Ready, cached, DateTime.MinValue, null));
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (!FriendList->IsAddonAndNodesReady())
                Close();
        }

        protected override void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            observation?.Dispose();
            observation = null;

            listNode = null;

            ContentID  = 0;
            PlayerName = string.Empty;
            WorldName  = string.Empty;
        }

        public void OpenWithData
        (
            ulong  contentID,
            string playerName,
            string worldName
        )
        {
            ContentID  = contentID;
            PlayerName = playerName;
            WorldName  = worldName;

            Open();
        }

        private void Refresh()
        {
            RemoteUsedNames.Invalidate(ContentID, region);
            RemoteUsedNames.GetOrRequest(ContentID, region);
        }

        private void BuildEntries
        (
            RemoteSnapshot<List<UsedNamesChange>> snapshot
        )
        {
            if (listNode == null) return;

            if (snapshot.Error != null)
                DLog.Error("获取好友曾用名时发生错误", snapshot.Error);

            var entries = snapshot.Value?
                                  .OrderByDescending(change => change.ChangedTime)
                                  .Select(change => new UsedNamesEntry(change))
                                  .ToList() ??
                          [];

            listNode.NoResultsTextNode.String = snapshot.Status switch
            {
                RemoteSnapshotStatus.Empty or RemoteSnapshotStatus.Loading => Lang.Get("OptimizedFriendList-UsedNames-Querying"),
                RemoteSnapshotStatus.Failed                                => Lang.Get("OptimizedFriendList-UsedNames-QueryFailed"),
                _                                                          => Lang.Get("OptimizedFriendList-UsedNames-NotFound")
            };

            listNode.OptionsList = entries;
            listNode.ResetScroll();
        }

        private sealed class UsedNamesEntry
        (
            string oldName,
            string newName,
            int    year,
            int    month,
            int    day,
            int    hour,
            int    minute
        )
        {
            public UsedNamesEntry
            (
                UsedNamesChange change
            ) : this
            (
                change.BeforeName,
                change.AfterName,
                change.ChangedTime.Year,
                change.ChangedTime.Month,
                change.ChangedTime.Day,
                change.ChangedTime.Hour,
                change.ChangedTime.Minute
            )
            { }

            public string ChangedTimeText { get; } = new DateTime(year, month, day, hour, minute, 0).ToString("yyyy/MM/dd HH:mm");

            public string ChangedTimeTooltip { get; } = new DateTime(year, month, day, hour, minute, 0).TimeAgo();

            public string OldName { get; } = oldName;

            public string NewName { get; } = newName;
        }

        private sealed class UsedNamesListNode : ListNode<UsedNamesEntry, UsedNamesItemNode>;

        private sealed class UsedNamesItemNode : ListItemNode<UsedNamesEntry>, IListItemNode
        {
            public static float ItemHeight => 25f;

            private readonly TextNode oldNameNode;
            private readonly TextNode newNameNode;
            private readonly TextNode timeNode;

            public UsedNamesItemNode()
            {
                oldNameNode = new()
                {
                    Position  = new(0f, 0f),
                    Size      = new(COLUMN_OLD_NAME_WIDTH, ItemHeight),
                    FontSize  = 14,
                    TextFlags = TextFlags.None
                };
                AtkColors.ListRow.ApplyTo(ref oldNameNode);
                oldNameNode.AttachNode(this);

                newNameNode = new()
                {
                    Position  = new(COLUMN_OLD_NAME_WIDTH, 0f),
                    Size      = new(COLUMN_NEW_NAME_WIDTH, ItemHeight),
                    FontSize  = 14,
                    TextFlags = TextFlags.None
                };
                AtkColors.ListRow.ApplyTo(ref newNameNode);
                newNameNode.AttachNode(this);

                timeNode = new()
                {
                    Position  = new(TIME_X, 0f),
                    Size      = new(COLUMN_TIME_WIDTH, ItemHeight),
                    FontSize  = 14,
                    TextFlags = TextFlags.None
                };
                AtkColors.ListRow.ApplyTo(ref timeNode);
                timeNode.AttachNode(this);

                EnableSelection = false;
            }

            protected override void OnSizeChanged()
            {
                base.OnSizeChanged();

                foreach (var backgroundNode in new[] { SelectedBackgroundNode, HoveredBackgroundNode })
                {
                    backgroundNode.Position     = new(3f, -1f);
                    backgroundNode.Size         = new(Width - 6f, ItemHeight - 3f);
                    backgroundNode.TopOffset    = 0;
                    backgroundNode.BottomOffset = 0;
                    backgroundNode.LeftOffset   = 16;
                    backgroundNode.RightOffset  = 1;
                }
            }

            protected override void SetNodeData
            (
                UsedNamesEntry itemData
            )
            {
                oldNameNode.String = itemData.OldName;
                newNameNode.String = itemData.NewName;

                timeNode.String      = itemData.ChangedTimeText;
                timeNode.TextTooltip = itemData.ChangedTimeTooltip;
            }
        }
    }
}
