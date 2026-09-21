using DailyRoutines.Common.KamiToolKit.Nodes;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Data.Parsing.Uld;

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

        private HorizontalLineNode headerLineNode;

        private TextNode nicknameNode;

        private TextNode               playerNameNode;
        private TextMultiLineInputNode remarkInputNode;

        private TextNode remarkNode;
        
        private ulong  ContentID { get; set; }
        private string Name      { get; set; } = string.Empty;
        private string WorldName { get; set; } = string.Empty;

        private OptimizedFriendList Instance { get; } = instance;

        public static DRFriendlistRemarkEdit Open
        (
            OptimizedFriendList owner,
            ulong               contentID,
            string              name,
            string              worldName
        )
        {
            var parentAddonID = FriendList is null ?
                                    0 :
                                    FriendList->Id;

            var addon = new DRFriendlistRemarkEdit(owner)
            {
                InternalName         = "DRFriendlistRemarkEdit",
                Title                = Lang.Get("OptimizedFriendList-Addon-Title"),
                Size                 = new(460f, 304f),
                ContentID            = contentID,
                Name                 = name,
                WorldName            = worldName,
                ParentAddonId        = parentAddonID,
                BlockedParentAddonId = parentAddonID
            };

            addon.Open();
            return addon;
        }

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
                Position      = new(12, 42),
                Size          = new(436, 26),
                String        = rented.Builder.ToReadOnlySeString(),
                FontSize      = 20,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            playerNameNode.AttachNode(this);

            headerLineNode = new()
            {
                Position = new(12, 72),
                Size     = new(436, 2)
            };
            headerLineNode.AttachNode(this);

            nicknameNode = new()
            {
                Position      = new(12, 78),
                Size          = new(100, 20),
                TextId        = 15207,
                SheetType     = NodeData.SheetType.Addon,
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            nicknameNode.AttachNode(this);

            nicknameInputNode = new()
            {
                Position      = new(12, 100),
                Size          = new(436, 26),
                MaxCharacters = 64,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedNickname
            };
            nicknameInputNode.AttachNode(this);

            remarkNode = new()
            {
                Position      = new(12, 132),
                Size          = new(100, 20),
                String        = Lang.Get("Note"),
                FontSize      = 14,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.Bold
            };
            remarkNode.AttachNode(this);

            remarkInputNode = new()
            {
                Position      = new(12, 154),
                MaxCharacters = 1024,
                MaxLines      = 5,
                ShowLimitText = true,
                AutoSelectAll = false,
                String        = existedRemark
            };
            remarkInputNode.Flags |= TextInputFlags.MultiLine;
            remarkInputNode.Size  =  new(436, (remarkInputNode.CurrentTextNode.LineSpacing * 5) + 26);

            remarkInputNode.AttachNode(this);

            confirmButtonNode = new()
            {
                Position    = new(308, 254),
                Size        = new(140, 36),
                TextureType = ButtonTextureType.ButtonB,
                String      = Lang.Get("Confirm"),
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
                Position = new(180, 258),
                Size     = new(120, 28),
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
    }
}
