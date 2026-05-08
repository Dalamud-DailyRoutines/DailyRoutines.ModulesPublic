using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoPreviewColorsInDye : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPreviewColorsInDyeTitle"),
        Description = Lang.Get("AutoPreviewColorsInDyeDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private DyeInfo?           currentDye;
    private OverlayController? overlayController;

    protected override void Init()
    {
        TooltipManager.Instance().RegItem(OnItemTooltip);

        overlayController = new();
        overlayController.AddNode(new DyePreviewOverlayNode(() => currentDye, IsCurrentTooltipVisible));
    }

    protected override void Uninit()
    {
        TooltipManager.Instance().Unreg(OnItemTooltip);

        overlayController?.Dispose();
        overlayController = null;
        
        currentDye = null;
    }

    private void OnItemTooltip(ItemKind itemKind, uint itemID, ref List<TooltipItemModification> modifications)
    {
        if (itemKind != ItemKind.Normal || !Dyes.TryGetValue(itemID, out var dye))
        {
            currentDye = null;
            return;
        }

        currentDye = dye;
    }

    private bool IsCurrentTooltipVisible() =>
        currentDye != null && ItemDetail->IsAddonAndNodesReady();

    private sealed class DyePreviewOverlayNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;
        public override bool HideWithNativeUi => false;

        private readonly Func<DyeInfo?> getCurrentDye;
        private readonly Func<bool> isTooltipVisible;
        private readonly List<TextNode> colorNodes = [];

        private readonly SimpleNineGridNode backgroundNode = new()
        {
            TexturePath        = "ui/uld/EnemyList_hr1.tex",
            TextureCoordinates = new Vector2(96, 80),
            TextureSize        = new Vector2(24, 20),
            Offsets            = new Vector4(8, 8, 8, 8),
            MultiplyColor      = new Vector3(0f),
            Alpha              = 1f,
            IsVisible          = true,
        };

        private readonly TextNode titleNode = new()
        {
            Position         = new Vector2(CONTENT_PADDING_X, TITLE_Y),
            Size             = new Vector2(560, 34),
            FontSize         = 22,
            TextFlags        = TextFlags.Edge,
            TextColor        = Vector4.One,
            TextOutlineColor = new Vector4(0, 0, 0, 1),
            AlignmentType    = AlignmentType.TopLeft,
        };

        private readonly TextNode countNode = new()
        {
            Position         = new Vector2(CONTENT_PADDING_X, COUNT_Y),
            Size             = new Vector2(560, 26),
            FontSize         = 16,
            TextFlags        = TextFlags.Edge,
            TextColor        = Vector4.One,
            TextOutlineColor = new Vector4(0, 0, 0, 1),
            AlignmentType    = AlignmentType.TopLeft,
        };

        private DyeInfo? lastDye;

        public DyePreviewOverlayNode(Func<DyeInfo?> getCurrentDye, Func<bool> isTooltipVisible)
        {
            this.getCurrentDye     = getCurrentDye;
            this.isTooltipVisible  = isTooltipVisible;

            Size = new Vector2(600, 160);

            backgroundNode.Position = new Vector2(-BACKGROUND_PADDING, -BACKGROUND_PADDING);
            backgroundNode.Size     = Size + new Vector2(BACKGROUND_PADDING * 2f);
            backgroundNode.AttachNode(this);

            titleNode.AttachNode(this);
            countNode.AttachNode(this);
        }

        protected override void OnUpdate()
        {
            var dye = getCurrentDye();
            IsVisible = dye != null && isTooltipVisible();

            if (!IsVisible)
            {
                lastDye = null;
                return;
            }

            if (!ReferenceEquals(lastDye, dye))
            {
                Rebuild(dye);
                lastDye = dye;
            }

            Position = GetOverlayPosition();
        }

        private Vector2 GetOverlayPosition()
        {
            var mouse    = ImGui.GetMousePos();
            var display  = ImGui.GetIO().DisplaySize;
            var position = mouse + new Vector2(36f, 24f);

            if (position.X + Size.X > display.X)
                position.X = mouse.X - Size.X - 36f;

            if (position.Y + Size.Y > display.Y)
                position.Y = display.Y - Size.Y - 20f;

            return Vector2.Max(position, new Vector2(20f));
        }

        private void Rebuild(DyeInfo dye)
        {
            titleNode.String    = $"★ {LuminaWrapper.GetItemName(dye.ItemID)}";
            titleNode.TextColor = dye.HighlightColor;
            countNode.String    = Lang.Get("AutoPreviewColorsInDye-AvailableColors", dye.StainIDs.Length);

            ClearColorNodes();

            var count      = dye.StainIDs.Length;
            var compact    = count > COMPACT_THRESHOLD;
            var perRow     = compact ? COMPACT_PER_ROW : NORMAL_PER_ROW;
            var cellWidth  = compact ? COMPACT_CELL_WIDTH : NORMAL_CELL_WIDTH;
            var cellHeight = compact ? COMPACT_CELL_HEIGHT : NORMAL_CELL_HEIGHT;
            var squareSize = compact ? COMPACT_SQUARE_SIZE : NORMAL_SQUARE_SIZE;
            var nameSize   = compact ? COMPACT_NAME_SIZE : NORMAL_NAME_SIZE;

            for (var i = 0; i < count; i++)
                AddStainNode(dye.StainIDs[i], i, perRow, cellWidth, cellHeight, squareSize, nameSize);

            var rows = (int)MathF.Ceiling(count / (float)perRow);
            Size = new Vector2(perRow * cellWidth + CONTENT_PADDING_X * 2f, BASE_HEIGHT + rows * cellHeight);

            backgroundNode.Position = new Vector2(-BACKGROUND_PADDING, -BACKGROUND_PADDING);
            backgroundNode.Size     = Size + new Vector2(BACKGROUND_PADDING * 2f);
        }

        private void AddStainNode(uint stainID, int index, int perRow, float cellWidth, float cellHeight, int squareSize, int nameSize)
        {
            if (!LuminaGetter.TryGetRow<Stain>(stainID, out var stain))
                return;

            var x     = CONTENT_PADDING_X + index % perRow * cellWidth;
            var y     = FIRST_ROW_Y + index / perRow * cellHeight;
            var color = stain.Color.ToVector4();
            color = new Vector4(color.Z, color.Y, color.X, 1f);

            AddColorNode(new TextNode
            {
                Position      = new Vector2(x, y),
                Size          = new Vector2(squareSize + 6, squareSize + 6),
                FontSize      = (byte)squareSize,
                TextColor     = color,
                AlignmentType = AlignmentType.TopLeft,
                String        = "■",
            });

            AddColorNode(new TextNode
            {
                Position         = new Vector2(x + squareSize + 8, y + 5),
                Size             = new Vector2(cellWidth - squareSize - 10, cellHeight),
                FontSize         = (byte)nameSize,
                TextFlags        = TextFlags.Edge,
                TextColor        = Vector4.One,
                TextOutlineColor = new Vector4(0, 0, 0, 1),
                AlignmentType    = AlignmentType.TopLeft,
                String           = stain.Name.ExtractText(),
            });
        }

        private void AddColorNode(TextNode node)
        {
            node.AttachNode(this);
            colorNodes.Add(node);
        }

        private void ClearColorNodes()
        {
            foreach (var node in colorNodes)
                node.Dispose();

            colorNodes.Clear();
        }
    }

    private sealed record DyeInfo(uint ItemID, Vector4 HighlightColor, uint[] StainIDs);

    #region 常量

    private const float BACKGROUND_PADDING = 24f;
    private const float CONTENT_PADDING_X  = 18f;
    private const float TITLE_Y            = 12f;
    private const float COUNT_Y            = 46f;
    private const float FIRST_ROW_Y        = 84f;
    private const float BASE_HEIGHT        = 104f;

    private const int COMPACT_THRESHOLD   = 40;
    private const int NORMAL_PER_ROW      = 4;
    private const int COMPACT_PER_ROW     = 5;
    private const int NORMAL_SQUARE_SIZE  = 28;
    private const int COMPACT_SQUARE_SIZE = 24;
    private const int NORMAL_NAME_SIZE    = 16;
    private const int COMPACT_NAME_SIZE   = 14;

    private const float NORMAL_CELL_WIDTH    = 132f;
    private const float COMPACT_CELL_WIDTH   = 112f;
    private const float NORMAL_CELL_HEIGHT   = 40f;
    private const float COMPACT_CELL_HEIGHT  = 36f;
    
    private static readonly FrozenDictionary<uint, DyeInfo> Dyes = new Dictionary<uint, DyeInfo>
    {
        [52254] = new
        (
            52254,
            new(1f, 0.95f, 0.65f, 1f),
            Enumerable.Range(1, 85).Select(static x => (uint)x).ToArray()
        ),
        [52255] = new
        (
            52255,
            new(1f, 0.25f, 0.25f, 1f),
            [86, 87, 88, 89, 90, 91, 92, 93, 94]
        ),
        [52256] = new
        (
            52256,
            new(0.25f, 0.45f, 1f, 1f),
            [95, 96, 97, 98, 99, 100, 121, 122, 123, 124, 125]
        )
    }.ToFrozenDictionary();

    #endregion
}
