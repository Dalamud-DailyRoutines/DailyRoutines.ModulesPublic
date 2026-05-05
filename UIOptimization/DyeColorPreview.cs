using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Dalamud.Bindings.ImGui;

namespace DailyRoutines.ModulesPublic;

public unsafe class DyeColorPreview : ModuleBase
{
    #region Module

    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("DyeColorPreviewTitle"),
        Description = Lang.Get("DyeColorPreviewDescription"),
        Category = ModuleCategory.UIOptimization,
        Author = ["ErxCharlotte"],
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true,
    };

    #endregion

    #region Fields

    private DyeInfo? currentDye;
    private OverlayController? overlayController;
    private TooltipModification? tooltipModification;
    private AtkUnitBase* currentTooltipAddon;

    #endregion

    #region Module Lifecycle

    protected override void Init()
    {
        GameTooltipManager.Instance().RegGenerateItemTooltipModifier(OnItemTooltipGenerate);

        overlayController = new OverlayController();
        overlayController.AddNode(new DyePreviewOverlayNode(() => currentDye, IsCurrentTooltipVisible));
    }

    protected override void Uninit()
    {
        GameTooltipManager.Instance().Unreg(generateItemModifiers: OnItemTooltipGenerate);
        RemoveTooltipModification();

        overlayController?.Dispose();
        overlayController = null;
        currentDye = null;
        currentTooltipAddon = null;
    }

    #endregion

    #region Tooltip

    private void OnItemTooltipGenerate(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        RemoveTooltipModification();
        currentDye = null;
        currentTooltipAddon = null;

        var itemID = AgentItemDetail.Instance()->ItemId % 100_0000;
        if (!Dyes.TryGetValue(itemID, out var dye))
        {
            return;
        }

        currentTooltipAddon = addonItemDetail;
        currentDye = dye;

        tooltipModification = GameTooltipManager.Instance().AddItemDetail
        (
            itemID,
            TooltipItemType.ItemDescription,
            BuildTooltipText(dye),
            TooltipModifyMode.Append
        );
    }
    
    private void RemoveTooltipModification()
    {
        if (tooltipModification == null) return;

        GameTooltipManager.Instance().RemoveItemDetail(tooltipModification);
        tooltipModification = null;
    }
    
    private bool IsCurrentTooltipVisible()
    {
        if (currentDye == null || currentTooltipAddon == null)
            return false;
        
        if (currentTooltipAddon->IsVisible)
            return true;

        currentDye = null;
        currentTooltipAddon = null;
        RemoveTooltipModification();
        return false;
    }

    private static SeString BuildTooltipText(DyeInfo dye)
    {
        return new SeString
        (
            new NewLinePayload(),
            new UIForegroundPayload(3),
            new TextPayload(Lang.Get("DyeColorPreviewTooltip", dye.StainIDs.Length)),
            new UIForegroundPayload(0)
        );
    }

    #endregion

    #region Overlay

    private sealed class DyePreviewOverlayNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;
        public override bool HideWithNativeUi => false;

        private const float BackgroundPadding = 24f;
        private const float ContentPaddingX = 18f;
        private const float TitleY = 12f;
        private const float CountY = 46f;
        private const float FirstRowY = 84f;

        private readonly Func<DyeInfo?> getCurrentDye;
        private readonly Func<bool> isTooltipVisible;
        private readonly List<TextNode> colorNodes = [];

        private readonly SimpleNineGridNode backgroundNode = new()
        {
            TexturePath = "ui/uld/EnemyList_hr1.tex",
            TextureCoordinates = new Vector2(96, 80),
            TextureSize = new Vector2(24, 20),
            Offsets = new Vector4(8, 8, 8, 8),
            MultiplyColor = new Vector3(0f),
            Alpha = 1f,
            IsVisible = true,
        };

        private readonly TextNode titleNode = new()
        {
            Position = new Vector2(ContentPaddingX, TitleY),
            Size = new Vector2(560, 34),
            FontSize = 22,
            TextFlags = TextFlags.Edge,
            TextColor = Vector4.One,
            TextOutlineColor = new Vector4(0, 0, 0, 1),
            AlignmentType = AlignmentType.TopLeft,
        };

        private readonly TextNode countNode = new()
        {
            Position = new Vector2(ContentPaddingX, CountY),
            Size = new Vector2(560, 26),
            FontSize = 16,
            TextFlags = TextFlags.Edge,
            TextColor = Vector4.One,
            TextOutlineColor = new Vector4(0, 0, 0, 1),
            AlignmentType = AlignmentType.TopLeft,
        };

        private DyeInfo? lastDye;

        public DyePreviewOverlayNode(Func<DyeInfo?> getCurrentDye, Func<bool> isTooltipVisible)
        {
            this.getCurrentDye = getCurrentDye;
            this.isTooltipVisible = isTooltipVisible;

            Size = new Vector2(600, 160);

            backgroundNode.Position = new Vector2(-BackgroundPadding, -BackgroundPadding);
            backgroundNode.Size = Size + new Vector2(BackgroundPadding * 2f);
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
            var mouse = ImGui.GetMousePos();
            var display = ImGui.GetIO().DisplaySize;
            var position = mouse + new Vector2(36f, 24f);

            if (position.X + Size.X > display.X)
                position.X = mouse.X - Size.X - 36f;

            if (position.Y + Size.Y > display.Y)
                position.Y = display.Y - Size.Y - 20f;

            return Vector2.Max(position, new Vector2(20f));
        }

        private void Rebuild(DyeInfo dye)
        {
            titleNode.String = $"★ {Lang.Get(dye.NameKey)}";
            titleNode.TextColor = dye.HighlightColor;
            countNode.String = Lang.Get("DyeColorPreviewAvailableColors", dye.StainIDs.Length);

            ClearColorNodes();

            var count = dye.StainIDs.Length;
            var compact = count > 40;
            var perRow = compact ? 5 : 4;
            var cellWidth = compact ? 112f : 132f;
            var cellHeight = compact ? 36f : 40f;
            var squareSize = compact ? 24 : 28;
            var nameSize = compact ? 14 : 16;

            for (var i = 0; i < count; i++)
                AddStainNode(dye.StainIDs[i], i, perRow, cellWidth, cellHeight, squareSize, nameSize);

            var rows = (int)MathF.Ceiling(count / (float)perRow);
            Size = new Vector2(perRow * cellWidth + ContentPaddingX * 2f, 104f + rows * cellHeight);

            backgroundNode.Position = new Vector2(-BackgroundPadding, -BackgroundPadding);
            backgroundNode.Size = Size + new Vector2(BackgroundPadding * 2f);
        }

        private void AddStainNode(uint stainID, int index, int perRow, float cellWidth, float cellHeight, int squareSize, int nameSize)
        {
            if (!LuminaGetter.TryGetRow<Stain>(stainID, out var stain)) return;

            var x = ContentPaddingX + index % perRow * cellWidth;
            var y = FirstRowY + index / perRow * cellHeight;
            var color = stain.Color.ToVector4();
            color = new Vector4(color.Z, color.Y, color.X, 1f);

            AddColorNode(new TextNode
            {
                Position = new Vector2(x, y),
                Size = new Vector2(squareSize + 6, squareSize + 6),
                FontSize = (byte)squareSize,
                TextColor = color,
                AlignmentType = AlignmentType.TopLeft,
                String = "■",
            });

            AddColorNode(new TextNode
            {
                Position = new Vector2(x + squareSize + 8, y + 5),
                Size = new Vector2(cellWidth - squareSize - 10, cellHeight),
                FontSize = (byte)nameSize,
                TextFlags = TextFlags.Edge,
                TextColor = Vector4.One,
                TextOutlineColor = new Vector4(0, 0, 0, 1),
                AlignmentType = AlignmentType.TopLeft,
                String = stain.Name.ExtractText(),
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

    #endregion

    #region Data

    private sealed record DyeInfo(string NameKey, Vector4 HighlightColor, uint[] StainIDs);

    private static readonly Dictionary<uint, DyeInfo> Dyes = new()
    {
        [52254] = new
        (
            "DyeColorPreviewGeneralDye",
            new Vector4(1f, 0.95f, 0.65f, 1f),
            Enumerable.Range(1, 85).Select(x => (uint)x).ToArray()
        ),
        [52255] = new
        (
            "DyeColorPreviewExtraDye1",
            new Vector4(1f, 0.25f, 0.25f, 1f),
            [86, 87, 88, 89, 90, 91, 92, 93, 94]
        ),
        [52256] = new
        (
            "DyeColorPreviewExtraDye2",
            new Vector4(0.25f, 0.45f, 1f, 1f),
            [95, 96, 97, 98, 99, 100, 121, 122, 123, 124, 125]
        ),
    };

    #endregion
}
