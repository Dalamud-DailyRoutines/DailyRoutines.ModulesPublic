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

    private readonly Dictionary<ulong, ExitRangeLabelNode> labelNodes       = [];
    private readonly Dictionary<ushort, string>            placeNames       = [];
    private readonly List<ExitRangeDestination>            destinations     = [];
    private readonly HashSet<ulong>                        currentInstances = [];
    private readonly List<ulong>                           removedInstances = [];

    private OverlayController overlayController = null!;

    protected override void Init()
    {
        overlayController = new();

        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        FrameworkManager.Instance().Reg(RefreshInstancePairs, throttleMS: 1000);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        FrameworkManager.Instance().Unreg(RefreshInstancePairs);

        overlayController.Dispose();
        labelNodes.Clear();
        placeNames.Clear();
        destinations.Clear();
        currentInstances.Clear();
        removedInstances.Clear();
    }

    private void OnTerritoryChanged
    (
        uint territoryID
    )
    {
        overlayController.RemoveAllNodes();
        labelNodes.Clear();
        placeNames.Clear();
        destinations.Clear();
        currentInstances.Clear();
        removedInstances.Clear();
        FrameworkManager.Instance().ResetThrottle(RefreshInstancePairs);
    }

    private void RefreshInstancePairs
    (
        IFramework framework
    )
    {
        if (!GameState.IsTerritoryLoaded)
            return;

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
            return;

        var layout = layoutWorld->ActiveLayout;
        if (layout == null || layout->InitState != 7)
            return;

        destinations.Clear();
        currentInstances.Clear();
        removedInstances.Clear();

        if (layout->InstancesByType.TryGetValuePointer(InstanceType.ExitRange, out var exitRanges) &&
            layout->InstancesByType.TryGetValuePointer(InstanceType.LineVfx,   out var lines))
        {
            foreach (var exitRange in exitRanges->Value->Values)
            {
                if (exitRange.IsNull)
                    continue;

                var instance = (ExitRangeLayoutInstance*)exitRange.Value;
                if (instance->GetReturnInstance() == null)
                    continue;

                if (!placeNames.TryGetValue(instance->TerritoryType, out var destination))
                {
                    if (!LuminaGetter.TryGetRow<TerritoryType>(instance->TerritoryType, out var territoryType))
                        continue;

                    destination = territoryType.ExtractPlaceName();
                    placeNames.Add(instance->TerritoryType, destination);
                }

                if (destination.Length != 0)
                    destinations.Add(new(instance->Transform.Translation, destination));
            }

            if (destinations.Count != 0)
            {
                foreach (var (instanceID, line) in *lines->Value)
                {
                    if (line.IsNull)
                        continue;

                    var transform       = ((LineVfxLayoutInstance*)line.Value)->Transform;
                    var destination     = string.Empty;
                    var nearestDistance = float.MaxValue;

                    foreach (var candidate in destinations)
                    {
                        var distance = Vector3.DistanceSquared(transform.Translation, candidate.Position);
                        if (distance > nearestDistance)
                            continue;

                        destination     = candidate.Destination;
                        nearestDistance = distance;
                    }

                    if (!labelNodes.TryGetValue(instanceID, out var labelNode))
                    {
                        labelNode = new();
                        labelNodes.Add(instanceID, labelNode);
                        overlayController.AddNode(labelNode);
                    }

                    labelNode.UpdateLayout(transform, destination);
                    currentInstances.Add(instanceID);
                }
            }
        }

        foreach (var instanceID in labelNodes.Keys)
        {
            if (!currentInstances.Contains(instanceID))
                removedInstances.Add(instanceID);
        }

        foreach (var instanceID in removedInstances)
        {
            overlayController.RemoveNode(labelNodes[instanceID]);
            labelNodes.Remove(instanceID);
        }
    }

    private readonly record struct ExitRangeDestination
    (
        Vector3 Position,
        string  Destination
    );

    private sealed class ExitRangeLabelNode : OverlayNode
    {
        private readonly BackgroundNode backgroundNode;
        private readonly IconImageNode  iconNode;
        private readonly TextNode       textNode;

        private Transform transform;
        private Vector3   leftAnchor;
        private Vector3   rightAnchor;
        private Vector3   groundHeights;
        private Vector2   labelSize;
        private float     horizontalRadius;
        private string    text = string.Empty;

        private long  nextGroundUpdate;
        private long  nextOcclusionUpdate;
        private int   anchorIndex = 1;
        private bool  anchorInitialized;
        private bool  occluded;
        private float anchorAlpha    = 1f;
        private float occlusionAlpha = 1f;

        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;

        public override bool HideWithNativeUi => false;

        public override bool HideWithUiToggled => false;

        public ExitRangeLabelNode()
        {
            IsVisible = false;
            Scale     = new(1.5f);

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

        public void UpdateLayout
        (
            Transform value,
            string    destination
        )
        {
            if (transform.Translation != value.Translation ||
                transform.Rotation    != value.Rotation    ||
                transform.Scale       != value.Scale)
            {
                transform = value;

                var axis   = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, value.Rotation) with { Y = 0 });
                var offset = axis * (value.Scale.X * 0.5f);
                leftAnchor          = value.Translation - offset;
                rightAnchor         = value.Translation + offset;
                horizontalRadius    = offset.Length();
                nextGroundUpdate    = 0;
                nextOcclusionUpdate = 0;
                anchorInitialized   = false;
                anchorAlpha         = 1f;
                occlusionAlpha      = 1f;
            }

            if (text == destination)
                return;

            text            = destination;
            textNode.String = destination;

            var textSize = textNode.GetTextDrawSize(false);
            textNode.Size = textSize;

            labelSize = new
            (
                MathF.Max(textSize.X, ICON_SIZE) + (PADDING_X              * 2f),
                ICON_SIZE - (2 * ICON_GAP)       + textSize.Y + (PADDING_Y * 4f)
            );
            Size = labelSize;

            iconNode.Position = new((labelSize.X - ICON_SIZE)  * 0.5f, PADDING_Y             - ICON_GAP);
            textNode.Position = new((labelSize.X - textSize.X) * 0.5f, PADDING_Y + ICON_SIZE - (2 * ICON_GAP));
        }

        protected override void OnUpdate()
        {
            IsVisible = false;

            if (!GameState.IsTerritoryLoaded || !UIModule.IsScreenReady())
                return;

            var camera = CameraManager.Instance()->Camera;
            var player = LocalPlayerState.Object;
            if (camera == null || player == null)
                return;

            var playerPosition = player.Position;
            var isFlying       = ICondition.Instance()[ConditionFlag.InFlight];
            var renderDistance = isFlying ?
                                     FLIGHT_RENDER_DISTANCE :
                                     RENDER_DISTANCE;
            var horizontalOffset = playerPosition - transform.Translation;
            horizontalOffset.Y = 0;

            var maximumDistance = renderDistance + horizontalRadius;
            if (horizontalOffset.LengthSquared() > maximumDistance * maximumDistance)
                return;

            var now = Environment.TickCount64;

            if (now >= nextGroundUpdate)
            {
                groundHeights.X = RaycastHelper.TryGetGroundHeight(leftAnchor, out var leftHeight) ?
                                      leftHeight :
                                      float.NaN;
                groundHeights.Y = RaycastHelper.TryGetGroundHeight(transform.Translation, out var centerHeight) ?
                                      centerHeight :
                                      float.NaN;
                groundHeights.Z = RaycastHelper.TryGetGroundHeight(rightAnchor, out var rightHeight) ?
                                      rightHeight :
                                      float.NaN;
                nextGroundUpdate = now + GROUND_UPDATE_INTERVAL;
            }

            var heights = new Vector3
            (
                GetAnchorHeight(groundHeights.X, playerPosition.Y, isFlying),
                GetAnchorHeight(groundHeights.Y, playerPosition.Y, isFlying),
                GetAnchorHeight(groundHeights.Z, playerPosition.Y, isFlying)
            );

            var nearestIndex    = 1;
            var nearestDistance = horizontalOffset.LengthSquared();

            if (MathF.Abs(heights.X - heights.Y) > ANCHOR_HEIGHT_TOLERANCE)
            {
                var leftOffset = playerPosition - leftAnchor;
                leftOffset.Y = 0;

                var distance = leftOffset.LengthSquared();

                if (distance <= nearestDistance)
                {
                    nearestIndex    = 0;
                    nearestDistance = distance;
                }
            }

            if (MathF.Abs(heights.Z - heights.Y) > ANCHOR_HEIGHT_TOLERANCE)
            {
                var rightOffset = playerPosition - rightAnchor;
                rightOffset.Y = 0;

                if (rightOffset.LengthSquared() < nearestDistance)
                    nearestIndex = 2;
            }

            var deltaSeconds = GameState.DeltaTime;

            if (!anchorInitialized)
            {
                anchorIndex       = nearestIndex;
                anchorInitialized = true;
            }
            else if (anchorIndex != nearestIndex)
            {
                anchorAlpha = MathF.Max(0f, anchorAlpha - (deltaSeconds * FADE_SPEED));

                if (anchorAlpha <= 0f)
                {
                    anchorIndex         = nearestIndex;
                    nextOcclusionUpdate = 0;
                }
            }
            else
                anchorAlpha = MathF.Min(1f, anchorAlpha + (deltaSeconds * FADE_SPEED));

            var position = anchorIndex switch
            {
                0 => leftAnchor with { Y = heights.X            + 1f },
                2 => rightAnchor with { Y = heights.Z           + 1f },
                _ => transform.Translation with { Y = heights.Y + 1f }
            };

            var distanceSquared = Vector3.DistanceSquared(playerPosition, position);
            if (distanceSquared > renderDistance * renderDistance ||
                !IGameGui.Instance().WorldToScreen(position, out var screenPosition))
                return;

            if (now >= nextOcclusionUpdate)
            {
                var cameraPosition = camera->CameraBase.SceneCamera.Position;
                occluded = !RaycastHelper.HasLineOfSight
                           (
                               cameraPosition,
                               position + (Vector3.UnitY * 2f),
                               new RaycastHelper.RaycastOptions
                               {
                                   MaterialMask  = OCCLUSION_MATERIAL,
                                   MaterialValue = OCCLUSION_MATERIAL
                               }
                           );
                nextOcclusionUpdate = now + OCCLUSION_UPDATE_INTERVAL;
            }

            occlusionAlpha = occluded ?
                                 MathF.Max(0f, occlusionAlpha - (deltaSeconds * OCCLUSION_FADE_SPEED)) :
                                 MathF.Min(1f, occlusionAlpha + (deltaSeconds * OCCLUSION_FADE_SPEED));

            var fadeStartDistance = isFlying ?
                                        FLIGHT_FADE_START_DISTANCE :
                                        FADE_START_DISTANCE;
            var alpha = Math.Clamp
                        (
                            (renderDistance - MathF.Sqrt(distanceSquared)) / (renderDistance - fadeStartDistance),
                            0f,
                            1f
                        )           *
                        anchorAlpha *
                        occlusionAlpha;

            if (alpha <= 0f)
                return;

            Position             = screenPosition - (labelSize * Scale * 0.5f);
            iconNode.Alpha       = alpha;
            textNode.Alpha       = alpha;
            backgroundNode.Alpha = alpha;
            IsVisible            = true;
        }

        private static float GetAnchorHeight
        (
            float groundHeight,
            float playerHeight,
            bool  isFlying
        )
        {
            if (float.IsNaN(groundHeight))
                return playerHeight;

            return isFlying ?
                       MathF.Max(playerHeight, groundHeight) :
                       groundHeight;
        }

        protected override void OnSizeChanged() =>
            backgroundNode.Size = Size;

        #region 常量

        private const float PADDING_X = 6f;
        private const float PADDING_Y = 2f;

        private const float ICON_SIZE = 48f;
        private const float ICON_GAP  = 8f;

        private const uint DESTINATION_ICON_ID = 60441;

        private const ulong OCCLUSION_MATERIAL = 0x4000;

        private const float FADE_START_DISTANCE        = 18f;
        private const float FLIGHT_FADE_START_DISTANCE = 40f;
        private const float RENDER_DISTANCE            = 20f;
        private const float FLIGHT_RENDER_DISTANCE     = 50f;

        private const float FADE_SPEED              = 6f;
        private const float OCCLUSION_FADE_SPEED    = 6f;
        private const float ANCHOR_HEIGHT_TOLERANCE = 0.5f;

        private const long GROUND_UPDATE_INTERVAL    = 1000;
        private const long OCCLUSION_UPDATE_INTERVAL = 100;

        #endregion
    }
}
