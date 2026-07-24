using System.Numerics;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.KamiToolKit.Nodes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.CustomizeMapMarker;

public unsafe partial class CustomizeMapMarker
{
    private sealed class MarkerDetailsAddon
    (
        CustomizeMapMarker module
    ) : NativeAddon
    {
        private Guid markerID;

        private VerticalListNode?       containerNode;
        private LabelTextNode?          locationText;
        private TextInputNode?          nameInput;
        private TextInputNode?          groupInput;
        private TextMultiLineInputNode? descriptionInput;
        private NumericInputNode?       iconInput;
        private HoldButtonNode?         deleteButton;

        public void OpenMarker(Guid id)
        {
            markerID = id;

            if (IsOpen)
                RefreshMarker();
            else
                Open();
        }

        public void RefreshMarker()
        {
            if (!IsOpen) return;
            PopulateMarker();
        }

        private void PopulateMarker()
        {
            if (module.FindMarker(markerID) is not { } marker)
            {
                if (IsOpen)
                    Close();

                return;
            }

            locationText.String     = FormatMarkerLocation(marker);
            nameInput.String        = marker.Name;
            groupInput.String       = marker.Group;
            descriptionInput.String = marker.Description;
            iconInput.Value         = (int)marker.IconID;
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            containerNode = new()
            {
                Position    = ContentStartPosition,
                Size        = ContentSize,
                ItemSpacing = 6f
            };

            locationText = new()
            {
                Size      = ContentSize with { Y = 36 },
                String    = "测试",
                FontSize  = 18,
                TextFlags = TextFlags.Edge | TextFlags.Ellipsis
            };
            AtkColors.Label.ApplyTo(ref locationText);
            containerNode.AddNode(locationText);

            var nameLable = CreateLabel(Lang.Get("Name"));
            containerNode.AddNode(nameLable);
            
            nameInput = new()
            {
                Size              = ContentSize with { Y = 30 },
                MaxCharacters     = 80,
                PlaceholderString = Lang.Get("CustomizeMapMarker-Untitled")
            };
            containerNode.AddNode(nameInput);

            var groupLabel = CreateLabel(Lang.Get("CustomizeMapMarker-Group"));
            containerNode.AddNode(groupLabel);
            
            groupInput = new()
            {
                Size              = ContentSize with { Y = 30 },
                MaxCharacters     = 40,
                PlaceholderString = Lang.Get("CustomizeMapMarker-DefaultGroup")
            };
            containerNode.AddNode(groupInput);

            var descriptionLabel = CreateLabel(Lang.Get("Note"));
            containerNode.AddNode(descriptionLabel);
            
            descriptionInput = new()
            {
                Size              = ContentSize with { Y = 56 },
                MaxCharacters     = 160,
                MaxLines          = 3,
                PlaceholderString = Lang.Get("Note")
            };
            containerNode.AddNode(descriptionInput);

            var iconLabelRow = new ResNode
            {
                Size = ContentSize with { Y = 24 }
            };
            var iconLabel = CreateLabel(Lang.Get("CustomizeMapMarker-IconID"));
            iconLabel.AttachNode(iconLabelRow);
            var iconBrowserButton = new CircleButtonNode
            {
                Icon        = CircleButtonIcon.MagnifyingGlass,
                Size        = new(24),
                Position    = new(ContentSize.X - 24, 0),
                TextTooltip = Lang.Get("CustomizeMapMarker-OpenIconBrowser"),
                OnClick     = () => ChatManager.Instance().SendCommand("/xldata icon")
            };
            iconBrowserButton.AttachNode(iconLabelRow);
            containerNode.AddNode(iconLabelRow);

            iconInput = new()
            {
                Size = ContentSize with { Y = 30 },
                Min  = 1,
                Max  = int.MaxValue,
                Step = 1
            };
            containerNode.AddNode(iconInput);

            var actionRow = new HorizontalListNode
            {
                Size        = ContentSize with { Y = 30 },
                ItemSpacing = 6,
                Alignment   = HorizontalListAnchor.Center
            };

            var addFlag = new IconButtonNode
            {
                TextTooltip = Lang.Get("CustomizeMapMarker-SetFlag"),
                IconId      = FLAG_ICON_ID,
                Size        = new(30),
                Position    = new(0, -2),
                OnClick     = SetFlag
            };
            
            actionRow.AddNode(addFlag);
            actionRow.AddNode
            (
                new TextButtonNode
                {
                    String  = Lang.Get("Save"),
                    Size    = new(140, 30),
                    OnClick = SaveMarker
                }
            );
            deleteButton = new HoldButtonNode
            {
                String  = Lang.Get("Delete"),
                Size    = new(140, 30),
                OnClick = DeleteMarker
            };
            actionRow.AddNode(deleteButton);
            
            containerNode.AddNode(actionRow);
            
            containerNode.AttachNode(this);

            PopulateMarker();
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            containerNode    = null;
            locationText     = null;
            nameInput        = null;
            groupInput       = null;
            descriptionInput = null;
            iconInput        = null;
            deleteButton     = null;
        }

        private static LabelTextNode CreateLabel(string text) => new()
        {
            String        = text,
            TextFlags     = TextFlags.AutoAdjustNodeSize,
            Size          = new(100, 20),
            FontSize      = 12,
            AlignmentType = AlignmentType.Left
        };

        private void SetFlag()
        {
            if (module.FindMarker(markerID) is { } marker)
                SetGameFlag(marker);
        }

        private void SaveMarker()
        {
            if (module.FindMarker(markerID) is not { } marker) return;

            var name  = nameInput?.String.ToString().Trim();
            var group = groupInput?.String.ToString().Trim();

            marker.Name = string.IsNullOrWhiteSpace(name) ?
                              Lang.Get("CustomizeMapMarker-Untitled") :
                              name;
            marker.Group = string.IsNullOrWhiteSpace(group) ?
                               Lang.Get("CustomizeMapMarker-DefaultGroup") :
                               group;
            marker.Description = descriptionInput?.String.ToString().Trim() ?? string.Empty;
            marker.IconID      = (uint)Math.Max(1, iconInput?.Value ?? (int)DEFAULT_ICON_ID);

            module.SaveAndRefresh();
            NotifyHelper.ToastQuest
            (
                Lang.Get("CustomizeMapMarker-Saved"),
                new()
                {
                    DisplayCheckmark = true
                }
            );
        }

        private void DeleteMarker()
        {
            module.DeleteMarker(markerID);
            Close();
        }
    }
}
