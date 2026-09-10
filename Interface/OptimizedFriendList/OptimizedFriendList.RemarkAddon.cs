using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class OptimizedFriendList
{
    private class DRFriendlistRemarkEdit
    (
        OptimizedFriendList instance
    ) : NativeAddon
    {
        private TextButtonNode clearButtonNode;

        private TextButtonNode confirmButtonNode;
        private TextInputNode  nicknameInputNode;

        private TextNode nicknameNode;

        private TextNode               playerNameNode;
        private TextButtonNode         quertUsedNameButtonNode;
        private TextMultiLineInputNode remarkInputNode;

        private TextNode remarkNode;
        public  ulong    ContentID { get; private set; }
        public  string   Name      { get; private set; } = string.Empty;
        public  string   WorldName { get; private set; } = string.Empty;

        private OptimizedFriendList Instance { get; init; } = instance;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            if (ContentID == 0 || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(WorldName))
            {
                Close();
                return;
            }

            var existedNickname = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Nickname;
            var existedRemark   = Instance.config.PlayerInfos.GetValueOrDefault(ContentID, new()).Remark;

            using var rented = new RentedSeStringBuilder();
            rented.Builder
                  .Append(Name)
                  .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                  .Append(WorldName);

            playerNameNode = new()
            {
                Position      = new(10, 36),
                Size          = new(100, 48),
                String        = rented.Builder.ToReadOnlySeString(),
                FontSize      = 24,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            playerNameNode.AttachNode(this);

            nicknameNode = new()
            {
                Position      = new(10, 80),
                Size          = new(100, 28),
                String        = $"{LuminaWrapper.GetAddonText(15207)}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            nicknameNode.AttachNode(this);

            nicknameInputNode = new()
            {
                Position      = new(10, 108),
                Size          = new(440, 28),
                MaxCharacters = 64,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedNickname
            };
            nicknameInputNode.AttachNode(this);

            remarkNode = new()
            {
                Position      = new(10, 140),
                Size          = new(100, 28),
                String        = $"{LuminaWrapper.GetAddonText(13294).TrimEnd(':')}",
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };

            remarkNode.AttachNode(this);

            remarkInputNode = new()
            {
                Position      = new(10, 168),
                MaxCharacters = 1024,
                MaxLines      = 5,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedRemark
            };
            remarkInputNode.Flags |= TextInputFlags.MultiLine;

            remarkInputNode.Size = new(440, (remarkInputNode.CurrentTextNode.LineSpacing * 5) + 20);

            remarkInputNode.AttachNode(this);

            confirmButtonNode = new()
            {
                Position = new(10, 264),
                Size     = new(140, 28),
                String   = Lang.Get("Confirm"),
                OnClick = () =>
                {
                    Instance.config.PlayerInfos[ContentID] = new()
                    {
                        ContentID = ContentID,
                        Name      = Name,
                        Nickname  = nicknameInputNode.String.ToString(),
                        Remark    = remarkInputNode.String.ToString()
                    };
                    Instance.config.Save(Instance);

                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                }
            };
            confirmButtonNode.AttachNode(this);

            clearButtonNode = new()
            {
                Position = new(160, 264),
                Size     = new(140, 28),
                String   = Lang.Get("Clear"),
                OnClick = () =>
                {
                    Instance.config.PlayerInfos.TryRemove(ContentID, out _);
                    Instance.config.Save(Instance);

                    InfoProxyFriendList.Instance()->RequestData();
                    Close();
                }
            };
            clearButtonNode.AttachNode(this);

            quertUsedNameButtonNode = new()
            {
                Position = new(310, 264),
                Size     = new(140, 28),
                String   = Lang.Get("OptimizedFriendList-ObtainUsedNames"),
                OnClick = () =>
                {
                    var contentID = ContentID;
                    var name      = Name;
                    _ = OptimizedFriendListAsyncHelper.QueryUsedNamesAsync(contentID, name, GameState.HomeWorld);
                }
            };
            quertUsedNameButtonNode.AttachNode(this);
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
            ContentID = 0;
            Name      = string.Empty;
            WorldName = string.Empty;
        }

        public void OpenWithData
        (
            ulong  contentID,
            string name,
            string worldName
        )
        {
            ContentID = contentID;
            Name      = name;
            WorldName = worldName;

            Open();
        }
    }
}
