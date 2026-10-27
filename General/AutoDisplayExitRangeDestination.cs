using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
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
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Nodes.Background;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayExitRangeDestination : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayExitRangeDestinationTitle"),
        Description = Lang.Get("AutoDisplayExitRangeDestinationDescription"),
        Category    = ModuleCategory.General,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoDisplayExitRangeDestination/preview-1.webp"
        ]
    };

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
                    labelNode.UpdateLayout(bounds, destination);
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

        public GameViewProjection Projection;

        private float flightTransitionProgress = 1f;

        public override OverlayLayer OverlayLayer      => OverlayLayer.Background;
        public override bool         HideWithNativeUi  => true;
        public override bool         HideWithUiToggled => true;
        public override bool         IgnoreGlobalScale => false;

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
            if (!GameViewHelper.TryGetProjection(out var projection))
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

            if (Projection != projection)
            {
                Projection = projection;
                ProjectionRevision++;
            }

            CameraPosition = sceneCamera->Position;
            FadeStep       = deltaTime * FADE_SPEED;
            Now            = Environment.TickCount64;
            Ready          = true;
        }
    }

    private struct ExitRangeAnchor
    {
        public Vector3 Top;
        public Vector3 Bottom;
        public Vector3 GroundPosition;
        public long    NextGroundUpdate;
        public int     GroundRetryInterval;
        public bool    GroundResolved;
        public bool    GroundCentered;
    }

    private struct ExitRangeGroundSample
    {
        public Vector3 Position;
        public long    NextUpdate;
        public int     RetryInterval;
        public bool    Sampled;
        public bool    Resolved;
    }

    private sealed class ExitRangeLabelNode : OverlayNode
    {
        private readonly ExitRangeFrameState frameState;
        private readonly BackgroundNode      backgroundNode;
        private readonly IconImageNode       iconNode;
        private readonly TextNode            textNode;

        private ExitRangeAnchor[]       anchors       = [];
        private ExitRangeGroundSample[] groundSamples = [];
        private ExitRangeAnchor         activeAnchor;
        private Matrix4x4               boundsTransform;
        private Vector3                 boundsHalfExtents;
        private Vector3                 lineStart;
        private Vector3                 lineDirection;
        private Vector3                 worldPosition;
        private Vector2                 screenOffset;
        private float                   inverseLineLengthSquared;
        private float                   lineDistanceSquared;
        private string                  text = string.Empty;

        private long  nextAnchorUpdate;
        private long  nextGroundUpdate;
        private long  nextOcclusionUpdate;
        private uint  playerRevision;
        private uint  heightRevision;
        private uint  projectionRevision;
        private uint  anchorPlayerRevision;
        private uint  anchorProjectionRevision;
        private uint  anchorHeightRevision;
        private int   nearestAnchorIndex;
        private int   anchorIndex        = -1;
        private int   desiredAnchorIndex = -1;
        private int   lastAlpha          = -1;
        private bool  spatialDirty       = true;
        private bool  projectionDirty    = true;
        private bool  alphaDirty         = true;
        private bool  inRange;
        private bool  onScreen;
        private bool  occlusionInitialized;
        private bool  occluded;
        private float distanceAlpha;
        private float anchorAlpha;
        private float occlusionAlpha = 1f;

        public override OverlayLayer OverlayLayer      => frameState.OverlayLayer;
        public override bool         HideWithNativeUi  => frameState.HideWithNativeUi;
        public override bool         HideWithUiToggled => frameState.HideWithUiToggled;
        public override bool         IgnoreGlobalScale => frameState.IgnoreGlobalScale;

        public ExitRangeLabelNode
        (
            ExitRangeFrameState inFrameState
        )
        {
            frameState = inFrameState;
            IsVisible  = false;

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
            OrientedBounds bounds,
            string         destination
        )
        {
            Matrix4x4 transform   = bounds.Transform;
            Vector3   halfExtents = bounds.HalfExtents;

            if (boundsTransform != transform || boundsHalfExtents != halfExtents)
            {
                boundsTransform   = transform;
                boundsHalfExtents = halfExtents;

                var left  = Vector3.Transform(new(-halfExtents.X, 0f, 0f), transform);
                var right = Vector3.Transform(new(halfExtents.X, 0f, 0f),  transform);
                lineStart     = left with { Y = 0f };
                lineDirection = (right - left) with { Y = 0f };

                var lengthSquared = lineDirection.LengthSquared();
                inverseLineLengthSquared = lengthSquared > 0f ?
                                               1f / lengthSquared :
                                               0f;
                var count = Math.Max(3, (int)MathF.Ceiling(MathF.Sqrt(lengthSquared) / ANCHOR_SPACING) | 1);
                anchors       = new ExitRangeAnchor[count];
                groundSamples = new ExitRangeGroundSample[Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(lengthSquared) / GROUND_SAMPLE_SPACING)) + 1];

                for (var i = 0; i < count; i++)
                {
                    var localX = halfExtents.X * (((i + 0.5f) / count * 2f) - 1f);
                    var top    = Vector3.Transform(new(localX, halfExtents.Y, 0f),  transform);
                    var bottom = Vector3.Transform(new(localX, -halfExtents.Y, 0f), transform);
                    anchors[i] = new()
                    {
                        Top                 = top,
                        Bottom              = bottom,
                        GroundPosition      = bottom,
                        GroundRetryInterval = SPATIAL_UPDATE_INTERVAL
                    };
                }

                anchorIndex          = -1;
                desiredAnchorIndex   = -1;
                anchorAlpha          = 0f;
                occlusionAlpha       = 1f;
                occlusionInitialized = false;
            }
            else
            {
                Array.Clear(groundSamples);

                foreach (ref var anchor in anchors.AsSpan())
                {
                    anchor.GroundResolved      = false;
                    anchor.GroundRetryInterval = SPATIAL_UPDATE_INTERVAL;
                    anchor.NextGroundUpdate    = 0;
                }
            }

            spatialDirty        = true;
            projectionDirty     = true;
            nextAnchorUpdate    = 0;
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
            screenOffset = labelSize * 0.5f;
            Origin       = screenOffset;

            iconNode.Position = new((labelSize.X - ICON_SIZE)  * 0.5f, PADDING_Y             - ICON_GAP);
            textNode.Position = new((labelSize.X - textSize.X) * 0.5f, PADDING_Y + ICON_SIZE - (2 * ICON_GAP));
        }

        protected override void OnUpdate()
        {
            IsVisible = false;

            if (!frameState.Ready || anchors.Length == 0)
            {
                anchorAlpha = 0f;
                return;
            }

            var playerChanged = playerRevision != frameState.PlayerRevision;

            if (playerChanged || spatialDirty)
            {
                playerRevision = frameState.PlayerRevision;
                var offset = frameState.PlayerPosition - lineStart;
                offset.Y = 0f;
                var ratio = Math.Clamp(Vector3.Dot(offset, lineDirection) * inverseLineLengthSquared, 0f, 1f);
                lineDistanceSquared = (offset - (lineDirection * ratio)).LengthSquared();
                nearestAnchorIndex  = Math.Min((int)(ratio * anchors.Length), anchors.Length - 1);
                var isInRange = lineDistanceSquared < frameState.RenderDistanceSquared;

                if (isInRange && !inRange)
                    nextAnchorUpdate = 0;

                inRange = isInRange;

                if (!inRange)
                    distanceAlpha = 0f;
                else if (lineDistanceSquared <= frameState.FadeStartDistanceSquared)
                    distanceAlpha = 1f;
                else
                    distanceAlpha = (frameState.RenderDistance - MathF.Sqrt(lineDistanceSquared)) * frameState.FadeDistanceInverse;

                alphaDirty = true;
            }

            var groundSampleDistance = frameState.RenderDistance + (ANCHOR_SPACING * 2f);

            if (lineDistanceSquared >= groundSampleDistance * groundSampleDistance)
            {
                anchorIndex        = -1;
                desiredAnchorIndex = -1;
                anchorAlpha        = 0f;
                nextAnchorUpdate   = 0;
                spatialDirty       = false;
                return;
            }

            if (frameState.Now >= nextAnchorUpdate &&
                (anchorIndex < 0                                           ||
                 spatialDirty                                              ||
                 anchorPlayerRevision     != frameState.PlayerRevision     ||
                 anchorProjectionRevision != frameState.ProjectionRevision ||
                 anchorHeightRevision     != frameState.HeightRevision     ||
                 frameState.Now           >= nextGroundUpdate))
                RefreshAnchorSelection();

            if (!inRange || anchorIndex < 0)
            {
                anchorAlpha  = 0f;
                spatialDirty = false;
                return;
            }

            if (playerChanged || spatialDirty || heightRevision != frameState.HeightRevision)
            {
                heightRevision = frameState.HeightRevision;
                UpdateAnchorPosition();
                spatialDirty = false;
            }

            if (anchorIndex != desiredAnchorIndex || activeAnchor.GroundPosition != anchors[desiredAnchorIndex].GroundPosition)
            {
                alphaDirty  = true;
                anchorAlpha = MathF.Max(0f, anchorAlpha - frameState.FadeStep);

                if (anchorAlpha <= 0f)
                {
                    anchorIndex          = desiredAnchorIndex;
                    activeAnchor         = anchors[anchorIndex];
                    nextOcclusionUpdate  = 0;
                    occlusionInitialized = false;
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
                onScreen           = frameState.Projection.WorldToScreen(worldPosition, out var screenPosition);
                if (onScreen)
                    Position = screenPosition - screenOffset;
            }

            if (!onScreen)
            {
                anchorAlpha = 0f;
                return;
            }

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
                nextOcclusionUpdate = frameState.Now + SPATIAL_UPDATE_INTERVAL;

                if (!occlusionInitialized)
                {
                    occlusionInitialized = true;
                    occlusionAlpha = occluded ?
                                         0f :
                                         1f;
                    alphaDirty = true;
                }
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

        private void RefreshAnchorSelection()
        {
            nextAnchorUpdate         = frameState.Now + SPATIAL_UPDATE_INTERVAL;
            anchorPlayerRevision     = frameState.PlayerRevision;
            anchorProjectionRevision = frameState.ProjectionRevision;
            anchorHeightRevision     = frameState.HeightRevision;
            spatialDirty             = true;

            nextGroundUpdate = long.MaxValue;
            var first = Math.Max(0, nearestAnchorIndex - 1);
            var last  = Math.Min(anchors.Length        - 1, nearestAnchorIndex + 1);

            if (anchors.Length == 3)
            {
                first = 0;
                last  = 2;
            }

            var bestIndex             = -1;
            var bestDistanceSquared   = float.PositiveInfinity;
            var bestOnScreen          = false;
            var bestCentered          = false;
            var raycastBudget         = 8;
            var groundSamplingPending = false;

            for (var i = first; i <= last; i++)
                groundSamplingPending |= anchors[i].NextGroundUpdate == 0;

            for (var i = first; i <= last; i++)
            {
                ref var anchor = ref anchors[i];

                if (!groundSamplingPending || anchor.NextGroundUpdate == 0)
                    RefreshGroundSample(i, ref raycastBudget);

                nextGroundUpdate = Math.Min(nextGroundUpdate, anchor.NextGroundUpdate);
                if (!anchor.GroundResolved)
                    continue;

                var position        = GetAnchorPosition(anchor);
                var visible         = frameState.Projection.WorldToScreen(position, out _);
                var distanceSquared = Vector3.DistanceSquared(frameState.PlayerPosition, position);

                if (bestIndex < 0              ||
                    (visible && !bestOnScreen) ||
                    (visible == bestOnScreen &&
                     ((anchor.GroundCentered                 && !bestCentered) ||
                      (anchor.GroundCentered == bestCentered && distanceSquared < bestDistanceSquared))))
                {
                    bestIndex           = i;
                    bestDistanceSquared = distanceSquared;
                    bestOnScreen        = visible;
                    bestCentered        = anchor.GroundCentered;
                }
            }

            if (!inRange || bestIndex < 0 || (anchorIndex < 0 && nextGroundUpdate == 0))
                return;

            var retainedIndex = desiredAnchorIndex;

            if (retainedIndex >= first && retainedIndex <= last && retainedIndex != bestIndex && anchors[retainedIndex].GroundResolved)
            {
                var retainedPosition        = GetAnchorPosition(anchors[retainedIndex]);
                var retainedOnScreen        = frameState.Projection.WorldToScreen(retainedPosition, out _);
                var retainedDistanceSquared = Vector3.DistanceSquared(frameState.PlayerPosition, retainedPosition);

                if (retainedOnScreen &&
                    (!bestOnScreen ||
                     (anchors[retainedIndex].GroundCentered                     == bestCentered &&
                      MathF.Sqrt(bestDistanceSquared) + (ANCHOR_SPACING * 0.2f) >= MathF.Sqrt(retainedDistanceSquared))))
                    bestIndex = retainedIndex;
            }

            desiredAnchorIndex = bestIndex;

            if (anchorIndex                 < 0                                  ||
                activeAnchor.GroundPosition == anchors[bestIndex].GroundPosition ||
                distanceAlpha               <= 0f                                ||
                !frameState.Projection.WorldToScreen(GetAnchorPosition(activeAnchor), out _))
            {
                if (anchorIndex < 0 || activeAnchor.GroundPosition != anchors[bestIndex].GroundPosition)
                {
                    anchorAlpha          = 0f;
                    nextOcclusionUpdate  = 0;
                    occlusionInitialized = false;
                }

                anchorIndex  = bestIndex;
                activeAnchor = anchors[bestIndex];
            }
        }

        private void RefreshGroundSample
        (
            int     index,
            ref int raycastBudget
        )
        {
            ref var anchor = ref anchors[index];
            if (frameState.Now < anchor.NextGroundUpdate)
                return;

            var intervals    = groundSamples.Length - 1;
            var sourceRatio  = (index + 0.5f) / anchors.Length;
            var sourceIndex  = (int)MathF.Round(sourceRatio           * intervals);
            var radius       = (int)MathF.Ceiling(ANCHOR_SPACING * 2f / GROUND_SAMPLE_SPACING);
            var first        = Math.Max(0, sourceIndex         - radius);
            var last         = Math.Min(intervals, sourceIndex + radius);
            var nextUpdate   = long.MaxValue;
            var retrySamples = anchor.NextGroundUpdate != 0;

            for (var i = first; i <= last; i++)
            {
                ref var sample = ref groundSamples[i];

                if (!sample.Sampled || (retrySamples && !sample.Resolved && frameState.Now >= sample.NextUpdate))
                {
                    if (raycastBudget <= 0)
                    {
                        anchor.NextGroundUpdate = 0;
                        return;
                    }

                    raycastBudget--;
                    var localX       = boundsHalfExtents.X * (((float)i / intervals * 2f) - 1f);
                    var sampleTop    = Vector3.Transform(new(localX, boundsHalfExtents.Y, 0f),  boundsTransform);
                    var sampleBottom = Vector3.Transform(new(localX, -boundsHalfExtents.Y, 0f), boundsTransform);
                    sample.Sampled = true;

                    if (RaycastHelper.TryRaycastBetween(sampleTop, sampleBottom, out var sampleHit))
                    {
                        sample.Position = sampleHit.Point;
                        sample.Resolved = true;
                    }
                    else
                    {
                        if (sample.RetryInterval == 0)
                            sample.RetryInterval = SPATIAL_UPDATE_INTERVAL;

                        sample.NextUpdate    = frameState.Now + sample.RetryInterval;
                        sample.RetryInterval = Math.Min(sample.RetryInterval * 2, 500);
                    }
                }

                if (!sample.Sampled)
                {
                    anchor.NextGroundUpdate = 0;
                    return;
                }

                if (!sample.Resolved)
                    nextUpdate = Math.Min(nextUpdate, sample.NextUpdate);
            }

            var bestRatio         = sourceRatio;
            var bestWidth         = 0f;
            var bestHeight        = 0f;
            var bestCentered      = false;
            var runStart          = -1;
            var maximumHeightStep = MathF.Sqrt(lineDirection.LengthSquared()) * MAX_FLAT_GROUND_SLOPE / intervals;

            for (var i = first; i <= last; i++)
            {
                ref var sample = ref groundSamples[i];

                if (!sample.Resolved)
                {
                    runStart = -1;
                    continue;
                }

                if (runStart < 0)
                    runStart = i;

                if (i < last)
                {
                    ref var next = ref groundSamples[i + 1];
                    if (next.Resolved && MathF.Abs(next.Position.Y - sample.Position.Y) <= maximumHeightStep)
                        continue;
                }

                var startRatio = MathF.Max(0f, (runStart - 0.5f) / intervals);
                var endRatio   = MathF.Min(1f, (i        + 0.5f) / intervals);

                if (i > runStart)
                {
                    var centered = (runStart > first || first == 0) && (i < last || last == intervals);
                    var ratio = centered ?
                                    (startRatio + endRatio) * 0.5f :
                                    Math.Clamp(sourceRatio, startRatio, endRatio);
                    var width = endRatio - startRatio;

                    if ((centered && !bestCentered) ||
                        (centered == bestCentered &&
                         (width > bestWidth ||
                          (width.Equals(bestWidth) && MathF.Abs(ratio - sourceRatio) < MathF.Abs(bestRatio - sourceRatio)))))
                    {
                        bestRatio    = ratio;
                        bestWidth    = width;
                        bestHeight   = (groundSamples[runStart].Position.Y + sample.Position.Y) * 0.5f;
                        bestCentered = centered;
                    }
                }

                runStart = -1;
            }

            var anchorX = boundsHalfExtents.X * ((bestRatio * 2f) - 1f);
            var top     = Vector3.Transform(new(anchorX, boundsHalfExtents.Y, 0f),  boundsTransform);
            var bottom  = Vector3.Transform(new(anchorX, -boundsHalfExtents.Y, 0f), boundsTransform);

            if (!anchor.GroundResolved || anchor.Top != top || anchor.Bottom != bottom)
            {
                if (raycastBudget <= 0)
                {
                    anchor.NextGroundUpdate = 0;
                    return;
                }

                raycastBudget--;

                if (!RaycastHelper.TryRaycastBetween(top, bottom, out var hit))
                {
                    anchor.NextGroundUpdate    = frameState.Now + anchor.GroundRetryInterval;
                    anchor.GroundRetryInterval = Math.Min(anchor.GroundRetryInterval * 2, 500);
                    return;
                }

                anchor.Top            = top;
                anchor.Bottom         = bottom;
                anchor.GroundPosition = hit.Point;
                anchor.GroundResolved = true;
            }

            anchor.GroundCentered   = bestCentered && MathF.Abs(anchor.GroundPosition.Y - bestHeight) <= CENTER_HEIGHT_TOLERANCE;
            anchor.NextGroundUpdate = nextUpdate;
        }

        private void UpdateAnchorPosition()
        {
            var position = GetAnchorPosition(activeAnchor);

            if (worldPosition != position)
            {
                worldPosition   = position;
                projectionDirty = true;
            }

        }

        private Vector3 GetAnchorPosition
        (
            in ExitRangeAnchor anchor
        )
        {
            var position = anchor.GroundPosition;
            var height   = position.Y + ((frameState.PlayerPosition.Y - position.Y) * frameState.FlightHeightBlend);
            position.Y = MathF.Max(position.Y, MathF.Min(height, anchor.Top.Y - 1f)) + 1f;
            return position;
        }

        protected override void OnSizeChanged() =>
            backgroundNode.Size = Size;

        #region 常量

        private const float PADDING_X               = 6f;
        private const float PADDING_Y               = 2f;
        private const float ICON_SIZE               = 48f;
        private const float ICON_GAP                = 8f;
        private const uint  DESTINATION_ICON_ID     = 60441;
        private const ulong OCCLUSION_MATERIAL      = 0x4000;
        private const float ANCHOR_SPACING          = 10f;
        private const float GROUND_SAMPLE_SPACING   = 1f;
        private const float MAX_FLAT_GROUND_SLOPE   = 0.35f;
        private const float CENTER_HEIGHT_TOLERANCE = 1f;
        private const int   SPATIAL_UPDATE_INTERVAL = 100;

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
