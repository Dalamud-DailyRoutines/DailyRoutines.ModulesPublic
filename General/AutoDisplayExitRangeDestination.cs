using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
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
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

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

    private Hook<IManagerBase.Delegates.Initialize>?   LayerInitializeHook;
    private Hook<IManagerBase.Delegates.Deinitialize>? LayerDeinitializeHook;

    private OverlayController   overlayController = null!;
    private ExitRangeFrameState frameState        = null!;
    private bool                sceneRefreshPending;

    protected override void Init()
    {
        overlayController = new();
        frameState        = new();
        overlayController.AddNode(frameState);

        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        GameState.Instance().Login               += RequestSceneRefresh;
        GameState.Instance().Logout              += ClearScene;

        if (GameState.IsLoggedIn)
            RequestSceneRefresh();
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        GameState.Instance().Login               -= RequestSceneRefresh;
        GameState.Instance().Logout              -= ClearScene;

        ClearScene();
        overlayController.Dispose();
    }

    private void OnTerritoryChanged
    (
        uint territoryID
    )
    {
        ClearScene();
        RequestSceneRefresh();
    }

    private void ClearScene()
    {
        LayerInitializeHook?.Disable();
        LayerDeinitializeHook?.Disable();

        FrameworkManager.Instance().Unreg(RefreshInstancePairs);

        sceneRefreshPending = false;
        frameState.Active   = false;
        frameState.Ready    = false;

        foreach (var labelNode in labelNodes.Values)
            overlayController.RemoveNode(labelNode);

        labelNodes.Clear();
        placeNames.Clear();
        destinations.Clear();
        currentInstances.Clear();
        removedInstances.Clear();
    }

    private void RequestSceneRefresh()
    {
        if (sceneRefreshPending)
            return;

        sceneRefreshPending = true;
        FrameworkManager.Instance().Reg(RefreshInstancePairs, throttleMS: 100);
    }

    private void LayerInitializeDetour
    (
        IManagerBase* instance
    )
    {
        LayerInitializeHook.Original(instance);
        RequestSceneRefresh();
    }

    private void LayerDeinitializeDetour
    (
        IManagerBase* instance
    )
    {
        LayerDeinitializeHook.Original(instance);
        RequestSceneRefresh();
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
        if (layout            == null                     ||
            layout->InitState != 7                        ||
            layout->FestivalStatus is not (0 or 5)        ||
            layout->InsideFestivalTransitionLayerUpdate   ||
            layout->FestivalLayersToAdd.LongCount    != 0 ||
            layout->FestivalLayersToRemove.LongCount != 0)
            return;

        if (LayerInitializeHook == null)
        {
            foreach (var layer in layout->Layers.Values)
            {
                if (layer.IsNull)
                    continue;

                LayerInitializeHook = layer.Value->VirtualTable->HookVFuncFromName
                (
                    nameof(IManagerBase.Initialize),
                    (IManagerBase.Delegates.Initialize)LayerInitializeDetour
                );
                LayerDeinitializeHook = layer.Value->VirtualTable->HookVFuncFromName
                (
                    nameof(IManagerBase.Deinitialize),
                    (IManagerBase.Delegates.Deinitialize)LayerDeinitializeDetour
                );
                break;
            }
        }

        LayerInitializeHook?.Enable();
        LayerDeinitializeHook?.Enable();

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

                    var instance        = (LineVfxLayoutInstance*)line.Value;
                    var transform       = instance->Transform;
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
                        labelNode = new(frameState);
                        labelNodes.Add(instanceID, labelNode);
                        overlayController.AddNode(labelNode);
                    }

                    OrientedBounds bounds;
                    instance->GetOrientedBoundsImpl(&bounds);
                    labelNode.UpdateLayout(transform, bounds.Transform.M42 + bounds.HalfExtents.Y - 1f, destination);
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

        FrameworkManager.Instance().Unreg(RefreshInstancePairs);
        sceneRefreshPending = false;
        frameState.Active   = labelNodes.Count != 0;
    }

    private readonly record struct ExitRangeDestination
    (
        Vector3 Position,
        string  Destination
    );

    private sealed class ExitRangeFrameState : OverlayNode
    {
        public bool    Active;
        public bool    Ready;
        public bool    IsFlying;
        public long    Now;
        public float   FadeStep;
        public Vector3 PlayerPosition;
        public Vector3 CameraPosition;
        public uint    PlayerRevision;
        public uint    HeightRevision;
        public uint    ProjectionRevision;
        public float   FlightHeightBlend;
        public float   RenderDistance;
        public float   RenderDistanceSquared;
        public float   FadeStartDistanceSquared;
        public float   FadeDistanceInverse;

        private Matrix4x4 viewProjection;
        private Matrix4x4 cameraView;
        private Matrix4x4 cameraProjection;
        private Vector2   viewportHalfSize;
        private Vector2   viewportCenter;
        private float     flightTransitionProgress = 1f;

        public override OverlayLayer OverlayLayer      => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi  => false;
        public override bool         HideWithUiToggled => false;

        public ExitRangeFrameState() => IsVisible = false;

        protected override void OnUpdate()
        {
            Ready = false;
            if (!Active || !GameState.IsTerritoryLoaded || !UIModule.IsScreenReady())
                return;

            var control = Control.Instance();
            var player  = control->LocalPlayer;
            var camera  = control->CameraManager.GetActiveCamera();
            if (player == null || camera == null)
                return;

            var sceneCamera = &camera->CameraBase.SceneCamera;
            if (sceneCamera->RenderCamera == null)
                return;

            Vector3 playerPosition = player->Position;
            var     isFlying       = player->MoveController.IsFlying();
            var     deltaTime      = GameState.DeltaTime;

            if (PlayerRevision == 0)
                FlightHeightBlend = isFlying ?
                                        1f :
                                        0f;
            else if (IsFlying != isFlying)
                flightTransitionProgress = 0f;

            if (flightTransitionProgress < 1f)
            {
                flightTransitionProgress = MathF.Min(1f, flightTransitionProgress + (deltaTime * 0.4f));
                var progress        = flightTransitionProgress;
                var progressSquared = progress                          * progress;
                var blend           = (progress + 1f - progressSquared) * progress;
                FlightHeightBlend = isFlying ?
                                        blend :
                                        1f - blend;
                HeightRevision++;
            }

            if (PlayerRevision == 0 || IsFlying != isFlying)
            {
                RenderDistance = isFlying ?
                                     FLIGHT_RENDER_DISTANCE :
                                     RENDER_DISTANCE;
                var fadeStartDistance = isFlying ?
                                            FLIGHT_FADE_START_DISTANCE :
                                            FADE_START_DISTANCE;
                RenderDistanceSquared    = RenderDistance    * RenderDistance;
                FadeStartDistanceSquared = fadeStartDistance * fadeStartDistance;
                FadeDistanceInverse      = 1f                / (RenderDistance - fadeStartDistance);
            }

            if (PlayerRevision == 0 || PlayerPosition != playerPosition || IsFlying != isFlying)
            {
                PlayerPosition = playerPosition;
                IsFlying       = isFlying;
                PlayerRevision++;
            }

            Matrix4x4 view       = sceneCamera->ViewMatrix;
            Matrix4x4 projection = sceneCamera->RenderCamera->ProjectionMatrix;
            var       device     = Device.Instance();
            var       halfSize   = new Vector2(device->Width * 0.5f, device->Height * 0.5f);
            var       center     = ImGui.GetMainViewport().Pos + halfSize;

            if (cameraView != view || cameraProjection != projection || viewportHalfSize != halfSize || viewportCenter != center)
            {
                cameraView       = view;
                cameraProjection = projection;
                viewProjection   = view * projection;
                viewportHalfSize = halfSize;
                viewportCenter   = center;
                ProjectionRevision++;
            }

            CameraPosition = sceneCamera->Position;
            FadeStep       = deltaTime * FADE_SPEED;
            Now            = Environment.TickCount64;
            Ready          = true;
        }

        public bool TryProject
        (
            Vector3     position,
            out Vector2 screenPosition
        )
        {
            var clip = Vector4.Transform(new Vector4(position, 1f), viewProjection);

            if (clip.W <= 0f || MathF.Abs(clip.X) >= clip.W || MathF.Abs(clip.Y) >= clip.W)
            {
                screenPosition = default;
                return false;
            }

            var inverseW = 1f                              / clip.W;
            screenPosition = (new Vector2(clip.X, -clip.Y) * inverseW * viewportHalfSize) + viewportCenter;
            return true;
        }
    }

    private sealed class ExitRangeLabelNode : OverlayNode
    {
        private readonly ExitRangeFrameState frameState;
        private readonly BackgroundNode      backgroundNode;
        private readonly IconImageNode       iconNode;
        private readonly TextNode            textNode;

        private Transform transform;
        private Vector3   leftAnchor;
        private Vector3   rightAnchor;
        private Vector3   groundHeights;
        private Vector3   heights;
        private Vector3   worldPosition;
        private Vector2   screenOffset;
        private float     maximumAnchorHeight;
        private float     groundCullDistanceSquared;
        private float     flightCullDistanceSquared;
        private string    text = string.Empty;

        private long  nextGroundUpdate;
        private long  nextOcclusionUpdate;
        private int   groundRetryInterval;
        private uint  playerRevision;
        private uint  heightRevision;
        private uint  projectionRevision;
        private int   anchorIndex        = 1;
        private int   desiredAnchorIndex = 1;
        private int   lastAlpha          = -1;
        private bool  spatialDirty       = true;
        private bool  projectionDirty    = true;
        private bool  alphaDirty         = true;
        private bool  inRange;
        private bool  onScreen;
        private bool  groundResolved;
        private bool  anchorInitialized;
        private bool  occluded;
        private float distanceAlpha;
        private float anchorAlpha    = 1f;
        private float occlusionAlpha = 1f;

        public override OverlayLayer OverlayLayer      => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi  => false;
        public override bool         HideWithUiToggled => false;

        public ExitRangeLabelNode
        (
            ExitRangeFrameState frameState
        )
        {
            this.frameState = frameState;
            IsVisible       = false;
            Scale           = new(1.5f);

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
            float     maximumHeight,
            string    destination
        )
        {
            if (transform.Translation != value.Translation ||
                transform.Rotation    != value.Rotation    ||
                transform.Scale       != value.Scale)
            {
                transform = value;

                var axis             = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, value.Rotation) with { Y = 0 });
                var offset           = axis * (value.Scale.X * 0.5f);
                var horizontalRadius = offset.Length();
                leftAnchor                = value.Translation - offset;
                rightAnchor               = value.Translation + offset;
                groundCullDistanceSquared = (RENDER_DISTANCE        + horizontalRadius) * (RENDER_DISTANCE        + horizontalRadius);
                flightCullDistanceSquared = (FLIGHT_RENDER_DISTANCE + horizontalRadius) * (FLIGHT_RENDER_DISTANCE + horizontalRadius);
                anchorInitialized         = false;
                anchorAlpha               = 1f;
                occlusionAlpha            = 1f;
            }

            maximumAnchorHeight = maximumHeight;
            spatialDirty        = true;
            projectionDirty     = true;
            groundResolved      = false;
            groundHeights       = new(float.NaN);
            groundRetryInterval = GROUND_RETRY_INITIAL_INTERVAL;
            nextGroundUpdate    = 0;
            nextOcclusionUpdate = 0;

            if (text == destination)
                return;

            text            = destination;
            textNode.String = destination;

            var textSize = textNode.GetTextDrawSize(false);
            textNode.Size = textSize;

            var labelSize = new Vector2
            (
                MathF.Max(textSize.X, ICON_SIZE) + (PADDING_X              * 2f),
                ICON_SIZE - (2 * ICON_GAP)       + textSize.Y + (PADDING_Y * 4f)
            );
            Size         = labelSize;
            screenOffset = labelSize * Scale * 0.5f;

            iconNode.Position = new((labelSize.X - ICON_SIZE)  * 0.5f, PADDING_Y             - ICON_GAP);
            textNode.Position = new((labelSize.X - textSize.X) * 0.5f, PADDING_Y + ICON_SIZE - (2 * ICON_GAP));
        }

        protected override void OnUpdate()
        {
            IsVisible = false;
            if (!frameState.Ready)
                return;

            var playerChanged = playerRevision != frameState.PlayerRevision;

            if (playerChanged || spatialDirty)
            {
                playerRevision = frameState.PlayerRevision;
                var offset = frameState.PlayerPosition - transform.Translation;
                offset.Y = 0;

                var cullDistanceSquared = frameState.IsFlying ?
                                              flightCullDistanceSquared :
                                              groundCullDistanceSquared;
                inRange = offset.LengthSquared() <= cullDistanceSquared;
            }

            if (!inRange)
            {
                spatialDirty = false;
                return;
            }

            if (!groundResolved && frameState.Now >= nextGroundUpdate)
            {
                var groundUpdated = false;

                if (float.IsNaN(groundHeights.X) && RaycastHelper.TryGetGroundHeight(leftAnchor, out var leftHeight))
                {
                    groundHeights.X = leftHeight;
                    groundUpdated   = true;
                }

                if (float.IsNaN(groundHeights.Y) && RaycastHelper.TryGetGroundHeight(transform.Translation, out var centerHeight))
                {
                    groundHeights.Y = centerHeight;
                    groundUpdated   = true;
                }

                if (float.IsNaN(groundHeights.Z) && RaycastHelper.TryGetGroundHeight(rightAnchor, out var rightHeight))
                {
                    groundHeights.Z = rightHeight;
                    groundUpdated   = true;
                }

                if (groundUpdated)
                {
                    spatialDirty        = true;
                    groundRetryInterval = GROUND_RETRY_INITIAL_INTERVAL;
                }

                groundResolved = !float.IsNaN(groundHeights.X) &&
                                 !float.IsNaN(groundHeights.Y) &&
                                 !float.IsNaN(groundHeights.Z);
                nextGroundUpdate    = frameState.Now + groundRetryInterval;
                groundRetryInterval = Math.Min(groundRetryInterval * 2, 1000);
            }

            if (playerChanged || spatialDirty || heightRevision != frameState.HeightRevision)
            {
                heightRevision = frameState.HeightRevision;
                if (RefreshAnchor(playerChanged))
                    UpdateAnchorPosition();
                spatialDirty = false;
            }

            if (anchorIndex != desiredAnchorIndex)
            {
                alphaDirty  = true;
                anchorAlpha = MathF.Max(0f, anchorAlpha - frameState.FadeStep);

                if (anchorAlpha <= 0f)
                {
                    anchorIndex         = desiredAnchorIndex;
                    nextOcclusionUpdate = 0;
                    UpdateAnchorPosition();
                }
            }
            else if (anchorAlpha < 1f)
            {
                alphaDirty  = true;
                anchorAlpha = MathF.Min(1f, anchorAlpha + frameState.FadeStep);
            }

            if (distanceAlpha <= 0f)
                return;

            if (projectionDirty || projectionRevision != frameState.ProjectionRevision)
            {
                projectionDirty    = false;
                projectionRevision = frameState.ProjectionRevision;
                onScreen           = frameState.TryProject(worldPosition, out var screenPosition);
                if (onScreen)
                    Position = screenPosition - screenOffset;
            }

            if (!onScreen)
                return;

            if (frameState.Now >= nextOcclusionUpdate)
            {
                occluded = !RaycastHelper.HasLineOfSight
                           (
                               frameState.CameraPosition,
                               worldPosition + (Vector3.UnitY * 2f),
                               new RaycastHelper.RaycastOptions
                               {
                                   MaterialMask  = OCCLUSION_MATERIAL,
                                   MaterialValue = OCCLUSION_MATERIAL
                               }
                           );
                nextOcclusionUpdate = frameState.Now + OCCLUSION_UPDATE_INTERVAL;
            }

            var nextOcclusionAlpha = occlusionAlpha switch
            {
                > 0f when occluded  => MathF.Max(0f, occlusionAlpha - frameState.FadeStep),
                < 1f when !occluded => MathF.Min(1f, occlusionAlpha + frameState.FadeStep),
                _                   => occlusionAlpha
            };

            if (!occlusionAlpha.Equals(nextOcclusionAlpha))
            {
                occlusionAlpha = nextOcclusionAlpha;
                alphaDirty     = true;
            }

            if (alphaDirty)
            {
                alphaDirty = false;
                var alpha     = distanceAlpha * anchorAlpha * occlusionAlpha;
                var alphaByte = (byte)(alpha * byte.MaxValue);

                if (lastAlpha != alphaByte)
                {
                    lastAlpha            = alphaByte;
                    iconNode.Alpha       = alpha;
                    textNode.Alpha       = alpha;
                    backgroundNode.Alpha = alpha;
                }
            }

            IsVisible = lastAlpha > 0;
        }

        private bool RefreshAnchor
        (
            bool playerChanged
        )
        {
            var playerPosition = frameState.PlayerPosition;
            var nextHeights = new Vector3
            (
                GetAnchorHeight(groundHeights.X, playerPosition.Y, frameState.FlightHeightBlend),
                GetAnchorHeight(groundHeights.Y, playerPosition.Y, frameState.FlightHeightBlend),
                GetAnchorHeight(groundHeights.Z, playerPosition.Y, frameState.FlightHeightBlend)
            );

            if (!playerChanged && !spatialDirty && heights == nextHeights)
                return false;

            heights = nextHeights;

            var centerOffset = playerPosition - transform.Translation;
            centerOffset.Y = 0;
            var nearestDistance = centerOffset.LengthSquared();
            desiredAnchorIndex = 1;

            if (MathF.Abs(heights.X - heights.Y) > ANCHOR_HEIGHT_TOLERANCE)
            {
                var leftOffset = playerPosition - leftAnchor;
                leftOffset.Y = 0;
                var distance = leftOffset.LengthSquared();

                if (distance <= nearestDistance)
                {
                    desiredAnchorIndex = 0;
                    nearestDistance    = distance;
                }
            }

            if (MathF.Abs(heights.Z - heights.Y) > ANCHOR_HEIGHT_TOLERANCE)
            {
                var rightOffset = playerPosition - rightAnchor;
                rightOffset.Y = 0;
                if (rightOffset.LengthSquared() < nearestDistance)
                    desiredAnchorIndex = 2;
            }

            if (anchorInitialized)
                return true;

            anchorInitialized = true;
            anchorIndex       = desiredAnchorIndex;
            return true;
        }

        private void UpdateAnchorPosition()
        {
            alphaDirty = true;
            var position = anchorIndex switch
            {
                0 => leftAnchor with { Y = heights.X            + 1f },
                2 => rightAnchor with { Y = heights.Z           + 1f },
                _ => transform.Translation with { Y = heights.Y + 1f }
            };

            if (worldPosition != position)
            {
                worldPosition   = position;
                projectionDirty = true;
            }

            var distanceSquared = Vector3.DistanceSquared(frameState.PlayerPosition, position);
            if (distanceSquared >= frameState.RenderDistanceSquared)
                distanceAlpha = 0f;
            else if (distanceSquared <= frameState.FadeStartDistanceSquared)
                distanceAlpha = 1f;
            else
                distanceAlpha = (frameState.RenderDistance - MathF.Sqrt(distanceSquared)) * frameState.FadeDistanceInverse;
        }

        private float GetAnchorHeight
        (
            float groundHeight,
            float playerHeight,
            float flightBlend
        )
        {
            if (float.IsNaN(groundHeight))
                return playerHeight;

            var height = groundHeight + ((playerHeight - groundHeight) * flightBlend);
            return MathF.Max(groundHeight, MathF.Min(height, maximumAnchorHeight));
        }

        protected override void OnSizeChanged() =>
            backgroundNode.Size = Size;

        #region 常量

        private const float PADDING_X                     = 6f;
        private const float PADDING_Y                     = 2f;
        private const float ICON_SIZE                     = 48f;
        private const float ICON_GAP                      = 8f;
        private const uint  DESTINATION_ICON_ID           = 60441;
        private const ulong OCCLUSION_MATERIAL            = 0x4000;
        private const float ANCHOR_HEIGHT_TOLERANCE       = 0.5f;
        private const int   GROUND_RETRY_INITIAL_INTERVAL = 100;
        private const long  OCCLUSION_UPDATE_INTERVAL     = 100;

        #endregion
    }

    #region 常量

    private const float FADE_START_DISTANCE        = 18f;
    private const float FLIGHT_FADE_START_DISTANCE = 40f;
    private const float RENDER_DISTANCE            = 20f;
    private const float FLIGHT_RENDER_DISTANCE     = 50f;
    private const float FADE_SPEED                 = 6f;

    #endregion
}
