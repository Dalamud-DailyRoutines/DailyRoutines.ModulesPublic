using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayFateItemCount : ModuleBase
{
    private static OverlayController? Controller;

    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("AutoDisplayFateItemCountTitle"),
        Description     = Lang.Get("AutoDisplayFateItemCountDescription"),
        Category        = ModuleCategory.Combat,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/AutoDisplayFateItemCount-UI.png"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        Controller ??= new();
        Controller.CreateNode(() => new FateInfoNode());
    }

    protected override void Uninit()
    {
        Controller?.Dispose();
        Controller = null;
    }

    private class FateInfoNode : OverlayNode
    {
        public FateInfoNode()
        {
            Scale = new(1.5f);
            Size  = new(200, 76);

            HeaderNode = new HorizontalListNode
            {
                Size      = new(200, 36),
                IsVisible = false
            };

            IconNode = new()
            {
                Size       = new(32),
                IconId     = 60498,
                FitTexture = true,
                IsVisible  = true
            };
            HeaderNode.AddNode(IconNode);

            HeaderNode.AddDummy(3f);

            NameNode = new()
            {
                Size             = new(160, 64),
                String           = "测试物品",
                FontSize         = 20,
                Position         = new(2),
                TextFlags        = TextFlags.Edge,
                AlignmentType    = AlignmentType.TopLeft,
                TextColor        = ColorHelper.GetColor(50),
                TextOutlineColor = ColorHelper.GetColor(30)
            };
            HeaderNode.AddNode(NameNode);

            HeaderNode.AttachNode(this);

            TableNode = new()
            {
                Position = new(0, 40),
                Size     = new Vector2(100, 28) * 2,
                GridSize = new(2, 2)
            };

            HoldLabelNode = new()
            {
                String           = Lang.Get("AutoDisplayFateItemCount-HoldCount"),
                FontSize         = 14,
                TextFlags        = TextFlags.Edge,
                TextOutlineColor = ColorHelper.GetColor(37),
                TextColor        = ColorHelper.GetColor(50)
            };
            HoldLabelNode.AttachNode(TableNode[0, 0]);

            HoldCountNode = new()
            {
                String           = "0",
                FontSize         = 18,
                TextFlags        = TextFlags.Edge,
                FontType         = FontType.Miedinger,
                TextOutlineColor = ColorHelper.GetColor(30),
                TextColor        = ColorHelper.GetColor(50)
            };
            HoldCountNode.AttachNode(TableNode[0, 1]);

            HandInLabelNode = new()
            {
                String           = Lang.Get("AutoDisplayFateItemCount-HandInCount"),
                FontSize         = 14,
                TextFlags        = TextFlags.Edge,
                TextOutlineColor = ColorHelper.GetColor(37),
                TextColor        = ColorHelper.GetColor(50)
            };
            HandInLabelNode.AttachNode(TableNode[1, 0]);

            HandInCountNode = new()
            {
                String           = "0",
                FontSize         = 18,
                TextFlags        = TextFlags.Edge,
                FontType         = FontType.Miedinger,
                TextOutlineColor = ColorHelper.GetColor(30),
                TextColor        = ColorHelper.GetColor(50)
            };
            HandInCountNode.AttachNode(TableNode[1, 1]);

            TableNode.AttachNode(this);
        }

        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => false;

        private GridNode TableNode { get; }

        private HorizontalListNode HeaderNode { get; }
        private IconImageNode      IconNode   { get; }
        private TextNode           NameNode   { get; }

        private TextNode HoldLabelNode   { get; }
        private TextNode HoldCountNode   { get; }
        private TextNode HandInLabelNode { get; }
        private TextNode HandInCountNode { get; }

        protected override void OnUpdate()
        {
            var currentFate = FateManager.Instance()->CurrentFate;

            if (currentFate == null                                                  ||
                !LuminaGetter.TryGetRow<Fate>(currentFate->FateId, out var fateData) ||
                fateData.EventItem.RowId      == 0                                   ||
                fateData.EventItem.Value.Icon == 0                                   ||
                !ToDoList->IsAddonAndNodesReady())
            {
                IsVisible = false;
                return;
            }

            IsVisible = true;

            AtkResNode*       nodeItemCount   = null;
            AtkComponentNode* nodeDescription = null;
            AtkComponentNode* nodeProgressBar = null;

            for (var i = 0; i < ToDoList->UldManager.NodeListCount; i++)
            {
                var node = ToDoList->UldManager.NodeList[i];
                if (node == null) continue;

                if (node->NodeId == 23001)
                    nodeItemCount = node;
                if (node->NodeId == 63101)
                    nodeDescription = (AtkComponentNode*)node;
                if (node->NodeId == 113001)
                    nodeProgressBar = (AtkComponentNode*)node;
            }

            if (nodeItemCount == null || nodeDescription == null || nodeProgressBar == null) return;

            var progressBarState = nodeProgressBar->Component->UldManager.SearchNodeById(4)->GetNodeState();

            var nodeStateProgressBar = nodeProgressBar->GetNodeState();
            var nodeStateDescription = nodeDescription->GetNodeState();
            var nodeStateItemCount   = nodeItemCount->GetNodeState();
            Position = progressBarState.TopLeft +
                       new Vector2
                       (
                           0,
                           nodeStateProgressBar.Height +
                           nodeStateDescription.Height +
                           nodeStateItemCount.Height   +
                           12f
                       );

            UpdateFateItemHeader(fateData.EventItem.Value);
            HoldCountNode.SetNumber((int)LocalPlayerState.GetItemCount(fateData.EventItem.RowId));
            HandInCountNode.SetNumber(currentFate->HandInCount);
        }

        private void UpdateFateItemHeader(EventItem item)
        {
            HeaderNode.IsVisible = true;

            IconNode.IconId = item.Icon;
            NameNode.String = $"{item.Singular}";
            while (NameNode.FontSize > 1 && NameNode.GetTextDrawSize(NameNode.String).X > NameNode.Size.X)
                NameNode.FontSize--;
        }
    }
}
