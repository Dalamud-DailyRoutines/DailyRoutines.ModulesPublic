using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes.Background;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayExitRangeDestination : ModuleBase
{
    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private List<ExitRangePair> instancePairs = [];

    private Dictionary<Vector3, AnchorDisplayState> anchorStates = [];

    private OverlayController    overlayController = null!;
    private ExitRangeOverlayNode overlayNode       = null!;

    protected override void Init()
    {
        overlayController = new();
        overlayNode       = new();
        overlayController.AddNode(overlayNode);

        FrameworkManager.Instance().Reg(RefreshInstancePairs, throttleMS: 1000);
        FrameworkManager.Instance().Reg(RefreshRenderInfos);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(RefreshRenderInfos, RefreshInstancePairs);

        overlayController.Dispose();
    }

    private void RefreshInstancePairs
    (
        IFramework framework
    )
    {
        if (!GameState.IsTerritoryLoaded)
            return;

        if (!LayoutWorld.Instance()->ActiveLayout->InstancesByType.TryGetValuePointer
            (
                InstanceType.ExitRange,
                out var exitRanges
            ))
            return;

        if (!LayoutWorld.Instance()->ActiveLayout->InstancesByType.TryGetValuePointer
            (
                InstanceType.LineVfx,
                out var lines
            ))
            return;

        List<ExitRangePair> pairs = [];

        foreach (var line in lines->Value->Values)
        {
            if (line.IsNull)
                continue;

            var transform = *line.Value->GetTransformImpl();
            var axis      = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, transform.Rotation) with { Y = 0 });

            var placeName    = string.Empty;
            var lastDistance = float.MaxValue;

            foreach (var exitRange in exitRanges->Value->Values)
            {
                if (exitRange.IsNull)
                    continue;

                var pExitRange = (ExitRangeLayoutInstance*)exitRange.Value;
                if (pExitRange->GetReturnInstance() == null ||
                    !LuminaGetter.TryGetRow<TerritoryType>(pExitRange->TerritoryType, out var territoryType))
                    continue;

                if (Vector3.DistanceSquared(transform.Translation, pExitRange->Transform.Translation) is var distance &&
                    distance <= lastDistance)
                {
                    placeName    = territoryType.ExtractPlaceName();
                    lastDistance = distance;
                }
            }

            var length = transform.Scale.X;
            var start  = transform.Translation - (axis * (length * 0.5f));

            Vector3[] anchors =
            [
                start,
                transform.Translation,
                start + (axis * length)
            ];

            pairs.Add(new(transform.Translation, anchors, placeName));
        }

        HashSet<Vector3> knownCenters = [];
        foreach (var pair in pairs)
            knownCenters.Add(pair.Center);

        List<Vector3> staleCenters = [];

        foreach (var center in anchorStates.Keys)
        {
            if (!knownCenters.Contains(center))
                staleCenters.Add(center);
        }

        foreach (var center in staleCenters)
            anchorStates.Remove(center);

        instancePairs = pairs;
    }

    private void RefreshRenderInfos
    (
        IFramework framework
    )
    {
        if (!GameState.IsTerritoryLoaded ||
            !UIModule.IsScreenReady())
        {
            overlayNode.Infos = [];
            return;
        }

        var camera = CameraManager.Instance()->Camera;
        var player = LocalPlayerState.Object;
        if (camera == null || player == null)
            return;

        var cameraPosition = camera->CameraBase.SceneCamera.Position;
        var playerPosition = player.Position;
        var isFlying       = ICondition.Instance()[ConditionFlag.InFlight];
        var deltaSeconds   = (float)framework.UpdateDelta.TotalSeconds;

        List<ExitRangeInfo> renderInfos      = [];
        Span<float>         heights          = stackalloc float[ANCHOR_COUNT];
        Span<int>           candidateIndices = stackalloc int[ANCHOR_COUNT];

        foreach (var (center, anchors, destination) in instancePairs)
        {
            var middle = anchors.Length / 2;

            for (var i = 0; i < anchors.Length; i++)
                heights[i] = GetAnchorHeight(anchors[i], playerPosition.Y, isFlying);

            var leftSame  = MathF.Abs(heights[0]                  - heights[middle]) <= ANCHOR_HEIGHT_TOLERANCE;
            var rightSame = MathF.Abs(heights[anchors.Length - 1] - heights[middle]) <= ANCHOR_HEIGHT_TOLERANCE;

            var candidateCount = 0;
            if (!leftSame)
                candidateIndices[candidateCount++] = 0;

            candidateIndices[candidateCount++] = middle;

            if (!rightSame)
                candidateIndices[candidateCount++] = anchors.Length - 1;

            var anchorIndex     = candidateIndices[0];
            var nearestDistance = float.MaxValue;

            for (var i = 0; i < candidateCount; i++)
            {
                var index = candidateIndices[i];

                var anchorOffset = playerPosition - anchors[index];
                anchorOffset.Y = 0;

                var anchorDistance = anchorOffset.LengthSquared();
                if (anchorDistance >= nearestDistance)
                    continue;

                anchorIndex     = index;
                nearestDistance = anchorDistance;
            }

            if (!anchorStates.TryGetValue(center, out var state))
            {
                state                = new() { Index = anchorIndex };
                anchorStates[center] = state;
            }
            else if (state.Index != anchorIndex)
            {
                state.Alpha = MathF.Max(0f, state.Alpha - (deltaSeconds * FADE_SPEED));
                if (state.Alpha <= 0f)
                    state.Index = anchorIndex;
            }
            else
            {
                state.Alpha = MathF.Min(1f, state.Alpha + (deltaSeconds * FADE_SPEED));
            }

            var pos = anchors[state.Index];
            pos.Y = heights[state.Index] + 1;

            var renderDistance = isFlying ?
                                     FLIGHT_RENDER_DISTANCE :
                                     RENDER_DISTANCE;
            var fadeStartDistance = isFlying ?
                                        FLIGHT_FADE_START_DISTANCE :
                                        FADE_START_DISTANCE;

            var distance = MathF.Sqrt(Vector3.DistanceSquared(playerPosition, pos));
            if (distance > renderDistance)
                continue;

            state.OcclusionAlpha = IsOccluded(cameraPosition, pos) ?
                                       MathF.Max(0f, state.OcclusionAlpha - (deltaSeconds * OCCLUSION_FADE_SPEED)) :
                                       MathF.Min(1f, state.OcclusionAlpha + (deltaSeconds * OCCLUSION_FADE_SPEED));

            if (state.OcclusionAlpha <= 0f)
                continue;

            var alpha = Math.Clamp
                        (
                            (renderDistance - distance) / (renderDistance - fadeStartDistance),
                            0f,
                            1f
                        )           *
                        state.Alpha *
                        state.OcclusionAlpha;

            renderInfos.Add(new(pos, destination, alpha));
        }

        overlayNode.Infos = renderInfos;
    }

    private static float GetAnchorHeight
    (
        Vector3 anchor,
        float   playerHeight,
        bool    isFlying
    )
    {
        if (!RaycastHelper.TryGetGroundPosition(anchor, out var groundPosition))
            return playerHeight;

        return isFlying ?
                   MathF.Max(playerHeight, groundPosition.Y) :
                   groundPosition.Y;
    }

    private static bool IsOccluded
    (
        Vector3 cameraPosition,
        Vector3 worldPosition
    )
    {
        var target = worldPosition + (Vector3.UnitY * 2f);
        var ray    = target        - cameraPosition;

        return RaycastHelper.Intersects
        (
            cameraPosition,
            ray,
            ray.Length(),
            new RaycastHelper.RaycastOptions
            {
                MaterialMask  = OCCLUSION_MATERIAL,
                MaterialValue = OCCLUSION_MATERIAL
            }
        );
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button("测试"))
        {

        }
    }

    private record ExitRangePair
    (
        Vector3   Center,
        Vector3[] Anchors,
        string    Destination
    );

    private sealed class AnchorDisplayState
    {
        public int   Index          = 1;
        public float Alpha          = 1f;
        public float OcclusionAlpha = 1f;
    }

    private record ExitRangeInfo
    (
        Vector3 WorldPosition,
        string  Destination,
        float   Alpha
    );

    private sealed class ExitRangeOverlayNode : OverlayNode
    {
        private readonly List<ExitRangeLabelNode> labelNodes = [];

        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;

        public override bool HideWithNativeUi => false;

        public override bool HideWithUiToggled => false;

        public IReadOnlyList<ExitRangeInfo> Infos { get; set; } = [];

        protected override void OnUpdate()
        {
            var usedCount = 0;

            foreach (var (worldPosition, destination, alpha) in Infos)
            {
                if (!IGameGui.Instance().WorldToScreen(worldPosition, out var screenPosition))
                    continue;

                GetLabelNode(usedCount++).Update(destination, alpha, screenPosition);
            }

            for (var i = usedCount; i < labelNodes.Count; i++)
                labelNodes[i].IsVisible = false;
        }

        private ExitRangeLabelNode GetLabelNode
        (
            int index
        )
        {
            while (labelNodes.Count <= index)
            {
                var labelNode = new ExitRangeLabelNode();
                labelNode.AttachNode(this);
                labelNodes.Add(labelNode);
            }

            return labelNodes[index];
        }
    }

    private sealed class ExitRangeLabelNode : ResNode
    {
        private readonly BackgroundNode backgroundNode;
        private readonly IconImageNode  iconNode;
        private readonly TextNode       textNode;

        private Vector2 labelSize;
        private string  text = string.Empty;

        public ExitRangeLabelNode()
        {
            Scale = new(1.5f);

            backgroundNode = new BackgroundNode
            {
                Type = BackgroundType.HorizontalBar
            };
            backgroundNode.AttachNode(this);

            iconNode = new IconImageNode
            {
                IconId     = DESTINATION_ICON_ID,
                FitTexture = true,
                Size       = new(ICON_SIZE)
            };
            iconNode.AttachNode(this);

            textNode = new TextNode
            {
                FontSize           = 20,
                LineSpacing        = 20,
                TextFlags          = TextFlags.Bold | TextFlags.Edge,
                TextColorID        = 535,
                TextOutlineColorID = 0
            };
            textNode.AttachNode(this);
        }

        public void Update
        (
            string  destination,
            float   alpha,
            Vector2 screenPosition
        )
        {
            IsVisible = destination.Length is not 0;

            if (text != destination)
            {
                text            = destination;
                textNode.String = destination;

                var textSize = textNode.GetTextDrawSize(false);
                textNode.Size = textSize;

                labelSize = new
                (
                    MathF.Max(textSize.X, ICON_SIZE)                  + (PADDING_X              * 2f),
                    ICON_SIZE                        - (2 * ICON_GAP) + textSize.Y + (PADDING_Y * 4f)
                );
                Size = labelSize;

                iconNode.Position = new((labelSize.X - ICON_SIZE)  * 0.5f, PADDING_Y             - ICON_GAP);
                textNode.Position = new((labelSize.X - textSize.X) * 0.5f, PADDING_Y + ICON_SIZE - (2 * ICON_GAP));
            }

            Position             = screenPosition - (labelSize * Scale * 0.5f);
            iconNode.Alpha       = alpha;
            textNode.Alpha       = alpha;
            backgroundNode.Alpha = alpha;
        }

        protected override void OnSizeChanged() =>
            backgroundNode.Size = Size;

        #region 常量

        private const float PADDING_X = 6f;
        private const float PADDING_Y = 2f;

        private const float ICON_SIZE = 48f;
        private const float ICON_GAP  = 8f;

        private const uint DESTINATION_ICON_ID = 60441;

        #endregion
    }

    #region 常量

    private const ulong OCCLUSION_MATERIAL = 0x4000;

    private const float FADE_START_DISTANCE        = 18f;
    private const float FLIGHT_FADE_START_DISTANCE = 40f;
    private const float RENDER_DISTANCE            = 20f;
    private const float FLIGHT_RENDER_DISTANCE     = 50f;

    private const float FADE_SPEED = 6f;

    private const float OCCLUSION_FADE_SPEED = 6f;

    private const float ANCHOR_HEIGHT_TOLERANCE = 0.5f;

    private const int ANCHOR_COUNT = 3;

    #endregion
}
