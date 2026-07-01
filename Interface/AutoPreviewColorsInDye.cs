using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Internal;
using Dalamud.Utility;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.UiOverlay;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoPreviewColorsInDye : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPreviewColorsInDyeTitle"),
        Description = Lang.Get("AutoPreviewColorsInDyeDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private DyeInfo?           currentDye;
    private OverlayController? overlayController;
    private DyePinnedAddon?    pinnedAddon;

    protected override void Init()
    {
        TooltipManager.Instance().RegItem(OnItemTooltip);

        overlayController = new();
        overlayController.AddNode
        (
            new DyePreviewOverlayNode
            (
                () => currentDye,
                IsCurrentTooltipVisible,
                () => PluginConfig.Instance().ConflictKeyBinding.IsPressed(),
                () => PluginConfig.Instance().ConflictKeyBinding.ToString(),
                () => pinnedAddon != null,
                OpenPinnedAddon
            )
        );
    }

    protected override void Uninit()
    {
        TooltipManager.Instance().Unreg(OnItemTooltip);

        pinnedAddon?.Dispose();
        pinnedAddon = null;

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

    private void OpenPinnedAddon(DyeInfo dye)
    {
        pinnedAddon?.Dispose();

        var count      = dye.StainIDs.Length;
        var compact    = count > COMPACT_THRESHOLD;
        var perRow     = compact ? COMPACT_PER_ROW : NORMAL_PER_ROW;
        var cellWidth  = compact ? COMPACT_CELL_WIDTH : NORMAL_CELL_WIDTH;
        var cellHeight = compact ? COMPACT_CELL_HEIGHT : NORMAL_CELL_HEIGHT;
        var rows       = (int)MathF.Ceiling(count / (float)perRow);

        var gridSize = new Vector2
        (
            perRow * cellWidth + WINDOW_PADDING * 2f,
            46f                + 28f + rows     * cellHeight + (rows - 1) * GRID_ROW_SPACING + WINDOW_PADDING
        );

        pinnedAddon = new DyePinnedAddon
        (
            dye,
            () => PluginConfig.Instance().ConflictKeyBinding.IsPressed(),
            () => pinnedAddon = null
        )
        {
            InternalName = "DRDyePreviewPinned",
            Title        = $"★ {LuminaWrapper.GetItemName(dye.ItemID)}",
            Size         = gridSize
        };

        pinnedAddon.Open();
    }

    private sealed class DyePreviewOverlayNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer      => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi  => false;
        public override bool         HideWithUiToggled => false;

        private readonly Func<DyeInfo?>  getCurrentDye;
        private readonly Func<bool>      isTooltipVisible;
        private readonly Func<bool>      isConflictKeyPressed;
        private readonly Func<string>    getConflictKeyString;
        private readonly Func<bool>      isPinned;
        private readonly Action<DyeInfo> onPinRequest;

        private readonly SimpleNineGridNode backgroundNode = new()
        {
            TexturePath        = "ui/uld/EnemyList_hr1.tex",
            TextureCoordinates = new(96, 80),
            TextureSize        = new(24, 20),
            Offsets            = new(8),
            MultiplyColor      = new(0f),
            Alpha              = 1f
        };

        private readonly TextNode titleNode = new()
        {
            Position         = new(OVERLAY_PADDING_X, OVERLAY_TITLE_Y),
            Size             = new(560, 34),
            FontSize         = 22,
            TextFlags        = TextFlags.Edge,
            TextColor        = Vector4.One,
            TextOutlineColor = Vector4.Zero.WithW(1),
            AlignmentType    = AlignmentType.TopLeft
        };

        private readonly TextNode countNode = new()
        {
            Position         = new(OVERLAY_PADDING_X, OVERLAY_COUNT_Y),
            Size             = new(560, 26),
            FontSize         = 16,
            TextFlags        = TextFlags.Edge,
            TextColor        = Vector4.One,
            TextOutlineColor = Vector4.Zero.WithW(1),
            AlignmentType    = AlignmentType.TopLeft
        };

        private readonly List<NodeBase> dynamicNodes = [];

        private DyeInfo? lastDye;
        private bool     wasConflictKeyPressed;

        public DyePreviewOverlayNode
        (
            Func<DyeInfo?>  getCurrentDye,
            Func<bool>      isTooltipVisible,
            Func<bool>      isConflictKeyPressed,
            Func<string>    getConflictKeyString,
            Func<bool>      isPinned,
            Action<DyeInfo> onPinRequest
        )
        {
            this.getCurrentDye        = getCurrentDye;
            this.isTooltipVisible     = isTooltipVisible;
            this.isConflictKeyPressed = isConflictKeyPressed;
            this.getConflictKeyString = getConflictKeyString;
            this.isPinned             = isPinned;
            this.onPinRequest         = onPinRequest;

            Size = new(600, 160);

            backgroundNode.Position = new(-OVERLAY_BG_PADDING, -OVERLAY_BG_PADDING);
            backgroundNode.Size     = Size + new Vector2(OVERLAY_BG_PADDING * 2f);
            backgroundNode.AttachNode(this);

            titleNode.AttachNode(this);
            countNode.AttachNode(this);
        }

        protected override void OnUpdate()
        {
            var dye             = getCurrentDye();
            var conflictPressed = isConflictKeyPressed();

            if (conflictPressed && !wasConflictKeyPressed && !isPinned() && dye != null && isTooltipVisible())
                onPinRequest(dye);

            wasConflictKeyPressed = conflictPressed;

            if (isPinned() || dye == null || !isTooltipVisible())
            {
                IsVisible = false;
                lastDye   = null;
                ClearDynamicNodes();
                return;
            }

            IsVisible = true;
            Position  = GetOverlayPosition();

            if (ReferenceEquals(lastDye, dye)) return;

            RebuildNormal(dye);
            lastDye = dye;
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

        private void UpdateBackgroundSize()
        {
            backgroundNode.Position = new(-OVERLAY_BG_PADDING, -OVERLAY_BG_PADDING);
            backgroundNode.Size     = Size + new Vector2(OVERLAY_BG_PADDING * 2f);
        }

        private void RebuildNormal(DyeInfo dye)
        {
            ClearDynamicNodes();

            titleNode.Position  = new(OVERLAY_PADDING_X, OVERLAY_TITLE_Y);
            titleNode.FontSize  = 22;
            titleNode.String    = $"★ {LuminaWrapper.GetItemName(dye.ItemID)}";
            titleNode.TextColor = dye.HighlightColor;
            titleNode.IsVisible = true;

            countNode.Position  = new(OVERLAY_PADDING_X, OVERLAY_COUNT_Y);
            countNode.String    = Lang.Get("AutoPreviewColorsInDye-AvailableColors", dye.StainIDs.Length);
            countNode.IsVisible = true;

            var count      = dye.StainIDs.Length;
            var compact    = count > COMPACT_THRESHOLD;
            var perRow     = compact ? COMPACT_PER_ROW : NORMAL_PER_ROW;
            var cellWidth  = compact ? COMPACT_CELL_WIDTH : NORMAL_CELL_WIDTH;
            var cellHeight = compact ? COMPACT_CELL_HEIGHT : NORMAL_CELL_HEIGHT;
            var squareSize = compact ? COMPACT_SQUARE_SIZE : NORMAL_SQUARE_SIZE;
            var nameSize   = compact ? COMPACT_NAME_SIZE : NORMAL_NAME_SIZE;

            for (var i = 0; i < count; i++)
                AddDisplayStainNode(dye.StainIDs[i], i, perRow, cellWidth, cellHeight, squareSize, nameSize);

            var rows          = (int)MathF.Ceiling(count / (float)perRow);
            var contentHeight = OVERLAY_BASE_HEIGHT + rows * cellHeight;
            var overlayWidth  = perRow                     * cellWidth + OVERLAY_PADDING_X * 2f;

            var hintNode = new TextNode
            {
                Position         = new(0, contentHeight),
                Size             = new(overlayWidth, 22),
                FontSize         = 13,
                TextFlags        = TextFlags.Edge,
                TextColor        = ColorHelper.GetColor(7),
                TextOutlineColor = ColorHelper.GetColor(28),
                AlignmentType    = AlignmentType.Center,
                String           = Lang.Get("AutoPreviewColorsInDye-Hint", getConflictKeyString())
            };
            hintNode.AttachNode(this);
            dynamicNodes.Add(hintNode);

            Size = new(overlayWidth, contentHeight + 28f);
            UpdateBackgroundSize();
        }

        private void AddDisplayStainNode
        (
            uint  stainID,
            int   index,
            int   perRow,
            float cellWidth,
            float cellHeight,
            int   squareSize,
            int   nameSize
        )
        {
            if (!LuminaGetter.TryGetRow<Stain>(stainID, out var stain))
                return;

            var x     = OVERLAY_PADDING_X   + index % perRow * cellWidth;
            var y     = OVERLAY_FIRST_ROW_Y + index / perRow * cellHeight;
            var color = stain.Color.ReverseToVector4();

            var squareNode = new TextNode
            {
                Position      = new(x, y),
                Size          = new(squareSize + 6, squareSize + 6),
                FontSize      = (byte)squareSize,
                TextColor     = color,
                AlignmentType = AlignmentType.TopLeft,
                String        = "■"
            };
            squareNode.AttachNode(this);
            dynamicNodes.Add(squareNode);

            var nameNode = new TextNode
            {
                Position         = new(x         + squareSize + 8, y + 5),
                Size             = new(cellWidth - squareSize - 10, cellHeight),
                FontSize         = (byte)nameSize,
                TextFlags        = TextFlags.Edge,
                TextColor        = Vector4.One,
                TextOutlineColor = Vector4.Zero.WithW(1),
                AlignmentType    = AlignmentType.TopLeft,
                String           = stain.Name.ExtractText()
            };
            nameNode.AttachNode(this);
            dynamicNodes.Add(nameNode);
        }

        private void ClearDynamicNodes()
        {
            foreach (var node in dynamicNodes)
                node.Dispose();

            dynamicNodes.Clear();
        }
    }

    private sealed class DyePinnedAddon : NativeAddon
    {
        private readonly DyeInfo        dye;
        private readonly Func<bool>     isConflictKeyPressed;
        private readonly Action         onClose;

        private readonly List<NodeBase> dynamicNodes = [];

        private int  selectedStainIndex     = -1;
        private int  lastSelectedStainIndex = -1;
        private bool wasConflictKeyPressed  = true;
        private int  frameCount;
        private bool isClosing;

        public Vector2 OpenPosition { get; init; }

        public DyePinnedAddon
        (
            DyeInfo    dye,
            Func<bool> isConflictKeyPressed,
            Action     onClose
        )
        {
            this.dye                  = dye;
            this.isConflictKeyPressed = isConflictKeyPressed;
            this.onClose              = onClose;

            RememberClosePosition = false;
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues) =>
            RebuildGrid();

        protected override void OnShow(AtkUnitBase* addon) =>
            SetWindowPosition(OpenPosition);

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            frameCount++;

            if (frameCount < 5)
            {
                wasConflictKeyPressed = isConflictKeyPressed();
                return;
            }

            var conflictPressed = isConflictKeyPressed();
            if (conflictPressed && !wasConflictKeyPressed)
            {
                Close();
                return;
            }

            wasConflictKeyPressed = conflictPressed;

            if (!(WindowNode as WindowNode).BorderTextureNode.IsVisible)
            {
                Close();
                return;
            }

            // 模式切换
            if (lastSelectedStainIndex == selectedStainIndex)
                return;

            if (selectedStainIndex >= 0)
                RebuildDetail();
            else
                RebuildGrid();

            lastSelectedStainIndex = selectedStainIndex;
        }

        protected override void OnHide(AtkUnitBase* addon)
        {
            if (!isClosing)
            {
                isClosing = true;
                onClose();
            }
        }

        // 固定网格模式
        private void RebuildGrid()
        {
            ClearDynamicNodes();

            WindowNode?.SetTitle($"★ {LuminaWrapper.GetItemName(dye.ItemID)}");

            var count      = dye.StainIDs.Length;
            var compact    = count > COMPACT_THRESHOLD;
            var perRow     = compact ? COMPACT_PER_ROW : NORMAL_PER_ROW;
            var cellWidth  = compact ? COMPACT_CELL_WIDTH : NORMAL_CELL_WIDTH;
            var cellHeight = compact ? COMPACT_CELL_HEIGHT : NORMAL_CELL_HEIGHT;
            var squareSize = compact ? COMPACT_SQUARE_SIZE : NORMAL_SQUARE_SIZE;
            var nameSize   = compact ? COMPACT_NAME_SIZE : NORMAL_NAME_SIZE;

            var contentStart = ContentStartPosition;
            var contentWidth = perRow * cellWidth;

            // 提示文本
            var countNode = new TextNode
            {
                Position         = contentStart,
                Size             = new(contentWidth, 24),
                FontSize         = 16,
                TextFlags        = TextFlags.Edge,
                TextColor        = Vector4.One,
                TextOutlineColor = Vector4.Zero.WithW(1),
                AlignmentType    = AlignmentType.TopLeft,
                String           = Lang.Get("AutoPreviewColorsInDye-ClickToViewDetail")
            };
            countNode.AttachNode(this);
            dynamicNodes.Add(countNode);

            var gridList = new VerticalListNode
            {
                Position    = contentStart with { Y = contentStart.Y + 28f },
                Size        = new Vector2(contentWidth, 0),
                FitContents = true,
                ItemSpacing = GRID_ROW_SPACING
            };
            gridList.AttachNode(this);
            dynamicNodes.Add(gridList);

            var rowCount = (int)Math.Ceiling(count / (float)perRow);

            for (var row = 0; row < rowCount; row++)
            {
                var rowList = new HorizontalListNode
                {
                    Size = new Vector2(contentWidth, cellHeight)
                };

                for (var col = 0; col < perRow && row * perRow + col < count; col++)
                {
                    var index = row * perRow + col;
                    AddInteractiveStainNode(rowList, dye.StainIDs[index], index, cellWidth, cellHeight, squareSize, nameSize);
                }

                gridList.AddNode(rowList);
            }

            var totalWidth  = contentWidth   + contentStart.X + WINDOW_PADDING;
            var totalHeight = contentStart.Y + 36f            + gridList.Height + WINDOW_PADDING;
            SetWindowSize(new Vector2(totalWidth, totalHeight));

            contentStart       = ContentStartPosition;
            countNode.Position = contentStart;
            gridList.Position  = contentStart with { Y = contentStart.Y + 28f };
            gridList.RecalculateLayout();
        }

        private void RebuildDetail()
        {
            ClearDynamicNodes();

            if (!LuminaGetter.TryGetRow<Stain>(dye.StainIDs[selectedStainIndex], out var stain))
                return;

            var stainName    = stain.Name.ExtractText();
            var displayColor = stain.Color.ReverseToVector4();

            var r = (byte)(displayColor.X * 255f);
            var g = (byte)(displayColor.Y * 255f);
            var b = (byte)(displayColor.Z * 255f);

            var vector4Str = $"({displayColor.X:F2}, {displayColor.Y:F2}, {displayColor.Z:F2}, {displayColor.W:F2})";
            var uintVal    = displayColor.ToUInt();
            var uintStr    = $"{uintVal}";
            var hexStr     = $"#{r:X2}{g:X2}{b:X2}FF";

            WindowNode?.SetTitle(Lang.Get("AutoPreviewColorsInDye-ColorDetail"));

            var contentStart = ContentStartPosition;
            var contentWidth = DETAIL_WIDTH - contentStart.X - WINDOW_PADDING;

            var container = new ResNode
            {
                Position = contentStart,
                Size     = new(contentWidth, 0)
            };
            container.AttachNode(this);
            dynamicNodes.Add(container);

            var y = 0f;

            var backButton = new CircleButtonNode
            {
                Icon     = CircleButtonIcon.LeftArrow,
                Size     = new(24, 24),
                Position = new(0, y),
                OnClick  = () => selectedStainIndex = -1
            };
            backButton.AttachNode(container);
            dynamicNodes.Add(backButton);

            var nameNode = new TextNode
            {
                Position         = new(32, y),
                Size             = new(contentWidth - 32, 24),
                FontSize         = 20,
                TextColor        = displayColor,
                TextOutlineColor = Vector4.Zero.WithW(1),
                AlignmentType    = AlignmentType.Left,
                String           = stainName
            };
            nameNode.AttachNode(container);
            dynamicNodes.Add(nameNode);

            y += 24 + 12f;

            const float PREVIEW_SIZE = 120f;
            const float ROW_HEIGHT   = 30f;
            const float ROW_GAP      = 6f;

            // 大色块预览
            var previewBorder = new ColorImageNode
            {
                Color    = new(0.85f, 0.85f, 0.85f, 1f),
                Position = new(0,                y),
                Size     = new(PREVIEW_SIZE + 6, PREVIEW_SIZE + 6)
            };
            previewBorder.AttachNode(container);
            dynamicNodes.Add(previewBorder);

            var preview = new ColorImageNode
            {
                Color    = displayColor,
                Position = new Vector2(3,            y + 3),
                Size     = new Vector2(PREVIEW_SIZE, PREVIEW_SIZE)
            };
            preview.AttachNode(container);
            dynamicNodes.Add(preview);

            y += PREVIEW_SIZE + 6 + 20f;

            y = AddDetailRow
            (
                container,
                y,
                contentWidth,
                ROW_HEIGHT,
                Lang.Get("Name"),
                stainName,
                stainName
            );
            y += ROW_GAP;
            y = AddDetailRow
            (
                container,
                y,
                contentWidth,
                ROW_HEIGHT,
                "Vector4",
                vector4Str,
                vector4Str
            );
            y += ROW_GAP;
            y = AddDetailRow
            (
                container,
                y,
                contentWidth,
                ROW_HEIGHT,
                "UInt",
                uintStr,
                uintStr
            );
            y += ROW_GAP;
            y = AddDetailRow
            (
                container,
                y,
                contentWidth,
                ROW_HEIGHT,
                "HEX",
                hexStr,
                hexStr
            );

            var totalHeight = contentStart.Y + y + WINDOW_PADDING + 8f;
            SetWindowSize(new(DETAIL_WIDTH, totalHeight));

            contentStart       = ContentStartPosition;
            container.Position = contentStart;
        }

        private float AddDetailRow
        (
            ResNode container,
            float   y,
            float   width,
            float   height,
            string  label,
            string  value,
            string  copyText
        )
        {
            var row = new ResNode
            {
                Position = new Vector2(0,     y),
                Size     = new Vector2(width, height),
                NodeFlags = NodeFlags.Visible        |
                            NodeFlags.Enabled        |
                            NodeFlags.HasCollision   |
                            NodeFlags.RespondToMouse |
                            NodeFlags.EmitsEvents
            };
            row.AttachNode(container);
            dynamicNodes.Add(row);

            var hoverBg = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/ListItemA.tex",
                TextureCoordinates = new(0f, 22f),
                TextureSize        = new(64f, 22f),
                Position           = new(0, 4),
                TopOffset          = 4,
                BottomOffset       = 4,
                LeftOffset         = 8,
                RightOffset        = 8,
                Size               = row.Size,
                IsVisible          = false
            };
            hoverBg.AttachNode(row);
            dynamicNodes.Add(hoverBg);

            var labelNode = new TextNode
            {
                Position         = new(10, 5),
                Size             = new(90, height),
                FontSize         = 14,
                TextColor        = ColorHelper.GetColor(2),
                AlignmentType    = AlignmentType.Left,
                String           = label
            };
            labelNode.AttachNode(row);
            dynamicNodes.Add(labelNode);

            var valueNode = new TextNode
            {
                Position         = new(112, 5),
                Size             = new(width - 122, height),
                FontSize         = 14,
                TextFlags        = TextFlags.Edge,
                AlignmentType    = AlignmentType.Left,
                TextColor        = ColorHelper.GetColor(8),
                TextOutlineColor = ColorHelper.GetColor(7),
                String           = value
            };
            valueNode.AttachNode(row);
            dynamicNodes.Add(valueNode);

            var capturedText = copyText;
            row.AddEvent
            (
                AtkEventType.MouseDown,
                () =>
                {
                    ImGui.SetClipboardText(capturedText);
                    NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {capturedText}");
                }
            );
            row.AddEvent(AtkEventType.MouseOver, () => hoverBg.IsVisible = true);
            row.AddEvent(AtkEventType.MouseOut,  () => hoverBg.IsVisible = false);
            row.AddDrawFlags(DrawFlags.ClickableCursor);

            return y + height;
        }

        private void AddInteractiveStainNode
        (
            HorizontalListNode rowList,
            uint               stainID,
            int                index,
            float              cellWidth,
            float              cellHeight,
            int                squareSize,
            int                nameSize
        )
        {
            if (!LuminaGetter.TryGetRow<Stain>(stainID, out var stain))
                return;

            var color = stain.Color.ReverseToVector4();
            var container = new ResNode
            {
                Size = new Vector2(cellWidth, cellHeight),
                NodeFlags = NodeFlags.Visible        |
                            NodeFlags.Enabled        |
                            NodeFlags.HasCollision   |
                            NodeFlags.RespondToMouse |
                            NodeFlags.EmitsEvents
            };

            var hoverBg = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/ListItemA.tex",
                TextureCoordinates = new(0f, 22f),
                TextureSize        = new(64f, 22f),
                TopOffset          = 6,
                BottomOffset       = 6,
                LeftOffset         = 16,
                RightOffset        = 1,
                Size               = container.Size,
                IsVisible          = false
            };
            hoverBg.AttachNode(container);

            var squareNode = new TextNode
            {
                Position      = new(4, 4),
                Size          = new(squareSize + 6, squareSize + 6),
                FontSize      = (byte)squareSize,
                TextColor     = color,
                AlignmentType = AlignmentType.TopLeft,
                String        = "■"
            };
            squareNode.AttachNode(container);

            var nameNode = new TextNode
            {
                Position         = new(squareSize + 12, 9),
                Size             = new(cellWidth  - squareSize - 14, cellHeight),
                FontSize         = (byte)nameSize,
                TextFlags        = TextFlags.Edge,
                TextColor        = Vector4.One,
                TextOutlineColor = Vector4.Zero.WithW(1),
                AlignmentType    = AlignmentType.TopLeft,
                String           = stain.Name.ExtractText()
            };
            nameNode.AttachNode(container);

            var capturedIndex = index;
            container.AddEvent(AtkEventType.MouseDown, () => selectedStainIndex = capturedIndex);
            container.AddEvent(AtkEventType.MouseOver, () => hoverBg.IsVisible  = true);
            container.AddEvent(AtkEventType.MouseOut,  () => hoverBg.IsVisible  = false);
            container.AddDrawFlags(DrawFlags.ClickableCursor);

            rowList.AddNode(container);
        }

        private void ClearDynamicNodes()
        {
            foreach (var node in dynamicNodes)
            {
                if (node is LayoutListNode layoutNode)
                    layoutNode.Clear();

                node.Dispose();
            }

            dynamicNodes.Clear();
        }
    }

    private sealed record DyeInfo
    (
        uint    ItemID,
        Vector4 HighlightColor,
        uint[]  StainIDs
    );

    #region 常量

    private const float OVERLAY_BG_PADDING  = 24f;
    private const float OVERLAY_PADDING_X   = 18f;
    private const float OVERLAY_TITLE_Y     = 12f;
    private const float OVERLAY_COUNT_Y     = 46f;
    private const float OVERLAY_FIRST_ROW_Y = 84f;
    private const float OVERLAY_BASE_HEIGHT = 104f;

    private const float WINDOW_PADDING   = 12f;
    private const float GRID_ROW_SPACING = 4f;

    private const int COMPACT_THRESHOLD   = 40;
    private const int NORMAL_PER_ROW      = 4;
    private const int COMPACT_PER_ROW     = 5;
    private const int NORMAL_SQUARE_SIZE  = 28;
    private const int COMPACT_SQUARE_SIZE = 24;
    private const int NORMAL_NAME_SIZE    = 16;
    private const int COMPACT_NAME_SIZE   = 14;

    private const float NORMAL_CELL_WIDTH   = 132f;
    private const float COMPACT_CELL_WIDTH  = 112f;
    private const float NORMAL_CELL_HEIGHT  = 40f;
    private const float COMPACT_CELL_HEIGHT = 36f;

    private const float DETAIL_WIDTH = 360f;

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
