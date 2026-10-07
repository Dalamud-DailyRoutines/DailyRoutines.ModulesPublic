using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.ImGuiOm.Widgets.MapRenderer;
using OmenTools.Info.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class OccultCrescentHelper
{
    private class TreasureManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private TaskHelper? treasureTaskHelper;

        private Queue<Vector3> queuedGatheringList = [];

        private List<(nint Ptr, Vector3 Position)> treasureObjects      = [];
        private List<Vector3>                      surveyPointPositions = [];
        private List<Vector3>                      carrotPositions      = [];

        private Vector3 origPosition;

        private List<Vector3> currentRoute = [];

        private readonly RoutePreview routePreview = new();

        private readonly ImGuiMapRenderer routeMapRenderer = new()
        {
            Zoomable             = false,
            Pannable             = false,
            EnableResizeGrip     = false,
            EnableDefaultMarkers = true
        };

        private ZoneIndicatorHandle? treasureHandle;
        private ZoneIndicatorHandle? surveyPointHandle;
        private ZoneIndicatorHandle? carrotHandle;

        private static readonly CompSig CalculateCollisionSig = new
            ("48 89 74 24 ?? 48 89 7C 24 ?? 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? F3 0F 10 42");

        private unsafe delegate nint CalculateCollisionDelegate
        (
            nint     moveControlInstance,
            Vector3* expectedPosition,
            nint     controlState,
            Vector3* currentPosition,
            nint     collisionFlags,
            ushort   movementType
        );

        private Hook<CalculateCollisionDelegate>? calculateCollisionHook;

        public override unsafe void Init()
        {
            calculateCollisionHook ??= CalculateCollisionSig.GetHook<CalculateCollisionDelegate>(CalculateCollisionDetour);

            treasureTaskHelper ??= new()
            {
                TimeoutMS       = 180_000,
                EnterBusyAction = () => calculateCollisionHook?.Enable(),
                LeaveBusyAction = () => calculateCollisionHook?.Disable()
            };

            WindowManager.Instance().PostDraw                += OnPosDraw;
            IClientState.Instance().TerritoryChanged += OnZoneChanged;
            OnZoneChanged(0);

            GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_TREASURE,
                new(OnCommandTreasure) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PTreasure-Help")}" }
            );
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_TREASURE);

            GamePacketManager.Instance().Unreg(OnPreSendPacket);

            IClientState.Instance().TerritoryChanged -= OnZoneChanged;
            WindowManager.Instance().PostDraw                -= OnPosDraw;

            treasureHandle?.Unreg();
            treasureHandle = null;

            treasureTaskHelper?.Abort();
            treasureTaskHelper?.Dispose();
            treasureTaskHelper = null;

            calculateCollisionHook?.Disable();
            calculateCollisionHook?.Dispose();
            calculateCollisionHook = null;

            treasureObjects.Clear();
        }


        #region 界面

        public override void DrawConfig()
        {
            using var tabBar = ImRaii.TabBar("TabBar");
            if (!tabBar) return;

            using (var item = ImRaii.TabItem(Lang.Get("General")))
            {
                if (item)
                {
                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToUInt(),
                        Lang.Get("OccultCrescentHelper-TreasureManager-AutoOpenTreasure")
                    );

                    using (ImRaii.PushIndent())
                    {
                        if (ImGui.Checkbox
                            (
                                $"{Lang.Get("Enable")}##AutoOpenTreasures",
                                ref MainModule.config.IsEnabledAutoOpenTreasure
                            ))
                            MainModule.config.Save(MainModule);

                        ImGuiOm.HelpMarker(Lang.Get("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Help"));

                        if (MainModule.config.IsEnabledAutoOpenTreasure)
                        {
                            ImGui.SetNextItemWidth(150f * GlobalUIScale);
                            ImGui.SliderFloat
                            (
                                $"{Lang.Get("OccultCrescentHelper-DistanceTo")}",
                                ref MainModule.config.DistanceToAutoOpenTreasure,
                                1.0f,
                                50f,
                                "%.1f"
                            );

                            if (ImGui.IsItemDeactivatedAfterEdit())
                                MainModule.config.Save(MainModule);

                            ImGuiOm.HelpMarker($"{Lang.Get("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-DistanceTo-Help")}");
                        }
                    }

                    if (origPosition != default || treasureObjects.Count > 0)
                    {
                        var textSize = ImGui.CalcTextSize($"{LuminaWrapper.GetAddonText(395)} [999.99, 999.99, 999.99]");

                        ImGui.NewLine();

                        ImGui.TextColored
                        (
                            KnownColor.LightSkyBlue.ToUInt(),
                            LuminaWrapper.GetAddonText(395)
                        );

                        if (origPosition != default)
                        {
                            ImGui.SameLine();
                            if (ImGui.SmallButton(Lang.Get("OccultCrescentHelper-TreasureManager-ReturnToOrigPostion")))
                                EnqueueMoveTo(origPosition);
                        }

                        using (ImRaii.PushIndent())
                        {
                            foreach (var (_, pos) in treasureObjects)
                            {
                                if (ImGui.Button
                                    (
                                        $"{pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}",
                                        new(textSize.X * 2, ImGui.GetFrameHeight())
                                    ))
                                {
                                    origPosition = LocalPlayerState.Object.Position;
                                    EnqueueMoveTo(pos);
                                }
                            }
                        }
                    }

                    ImGui.NewLine();

                    ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("OccultCrescentHelper-Highlight"));

                    using (ImRaii.PushIndent())
                    {
                        if (ImGui.Checkbox
                            (
                                $"{LuminaWrapper.GetAddonText(395)}",
                                ref MainModule.config.IsEnabledHighlightTreasure
                            ))
                            MainModule.config.Save(MainModule);

                        if (ImGui.Checkbox
                            (
                                $"{LuminaWrapper.GetEObjName(2014695)}",
                                ref MainModule.config.IsEnabledHighlightSurveyPoint
                            ))
                            MainModule.config.Save(MainModule);

                        if (ImGui.Checkbox
                            (
                                $"{LuminaWrapper.GetItemName(48096)}",
                                ref MainModule.config.IsEnabledHighlightCarrot
                            ))
                            MainModule.config.Save(MainModule);
                    }
                }
            }

            using (var item = ImRaii.TabItem(Lang.Get("OccultCrescentHelper-TreasureManager-AutoHuntTresures")))
            using (ImRaii.Disabled(GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent))
            {
                if (item)
                {
                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToUInt(),
                        Lang.Get("State")
                    );

                    using (ImRaii.PushIndent())
                    {
                        ImGui.TextWrapped($"{Lang.Get("OccultCrescentHelper-TreasureManager-AutoHuntTresures-LeftPoints")}: {queuedGatheringList.Count}");

                        if (ImGui.Button($"    {Lang.Get("Stop")}    "))
                            StopAutoTreasureHunt();
                    }

                    if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
                    {
                        ImGui.NewLine();

                        ImGui.TextColored
                        (
                            KnownColor.LightSkyBlue.ToUInt(),
                            Lang.Get("Route")
                        );

                        using (ImRaii.PushIndent())
                        using (ImRaii.Disabled(treasureTaskHelper.IsBusy))
                        {
                            Route? hoveredRoute = null;

                            var isFirst = true;
                            foreach (var route in Routes)
                            {
                                if (route.TerritoryType != GameState.TerritoryType) continue;

                                if (!isFirst)
                                    ImGui.SameLine();

                                isFirst = false;
                                
                                if (ImGui.Button($"    {route.Name}    "))
                                    EnqueueAutoTreasureHunt(route.Points);

                                if (route.Description is not null)
                                    ImGuiOm.TooltipHover(route.Description, 20f * GlobalUIScale);

                                if (ImGui.IsItemHovered())
                                    hoveredRoute = route;
                            }

                            routePreview.Update(hoveredRoute);
                        }
                    }

                    ImGui.NewLine();

                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToVector4(),
                        Lang.Get("Command")
                    );

                    using (ImRaii.PushIndent())
                        ImGui.TextWrapped($"/pdr {COMMAND_TREASURE} {Lang.Get("OccultCrescentHelper-Command-PTreasure-Help")}");
                }
                else if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
                {
                    ImGuiOm.TooltipHover
                    (
                        Lang.Get("OccultCrescentHelper-TreasureManager-AutoHuntTresures-Help"),
                        20f * GlobalUIScale
                    );
                }
            }
        }

        // 绘制寻宝路线地图
        private void DrawTreasureRouteMap
        (
            List<Vector3> route
        )
        {
            var mapID = GameState.Map;
            if (mapID == 0 || route.Count == 0) return;

            var displaySize = ScaledVector2(400);

            ImGui.SetNextWindowSize(displaySize                    + ScaledVector2(20, 40));
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().WorkPos + ScaledVector2(16, 16));
            ImGui.SetNextWindowBgAlpha(0.8f);

            if (ImGui.Begin("###AutoTreasureHuntMap", WINDOW_FLAGS))
            {
                routeMapRenderer.SetMap(mapID);

                routeMapRenderer.OnCustomMapDraw = (r, drawList) =>
                {
                    if (route.Count <= 1) return;

                    for (var i = 0; i < route.Count - 1; i++)
                    {
                        var currentScreenPos = r.WorldToScreen(route[i]);
                        var nextScreenPos    = r.WorldToScreen(route[i + 1]);

                        drawList.AddLine(currentScreenPos, nextScreenPos, 0x66000000,    7f);
                        drawList.AddLine(currentScreenPos, nextScreenPos, LineColorBlue, 5f);
                    }
                };

                routeMapRenderer.OnCustomForegroundDraw = (r, drawList) =>
                {
                    if (IObjectTable.Instance().LocalPlayer is not { } localPlayer) return;

                    var playerScreenPos = r.WorldToScreen(localPlayer.Position);

                    var direction = new Vector2(MathF.Sin(localPlayer.Rotation), MathF.Cos(localPlayer.Rotation));
                    var normal    = new Vector2(-direction.Y,                    direction.X);
                    var tip       = playerScreenPos + (direction * 10f);
                    var basePoint = playerScreenPos - (direction * 7f);

                    drawList.AddTriangleFilled(tip, basePoint + (normal * 7f), basePoint - (normal * 7f), PlayerColor);
                    drawList.AddTriangle(tip, basePoint       + (normal * 7f), basePoint - (normal * 7f), 0xFF000000, 2f);
                };

                routeMapRenderer.ClearMarkers();

                for (var i = 0; i < route.Count; i++)
                    routeMapRenderer.AddMarker
                    (
                        new()
                        {
                            ID          = $"TreasureRoute_{i}",
                            Position    = route[i],
                            Color       = DotColor,
                            Size        = new(16f),
                            ShowLabel   = false,
                            ShowTooltip = false
                        }
                    );

                routeMapRenderer.Draw(displaySize);
            }

            ImGui.End();
        }

        #endregion


        #region 事件

        private static unsafe nint CalculateCollisionDetour
        (
            nint     moveControlInstance,
            Vector3* expectedPosition,
            nint     controlState,
            Vector3* currentPosition,
            nint     collisionFlags,
            ushort   movementType
        ) =>
            nint.Zero;

        private void OnCommandTreasure
        (
            string command,
            string args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(args)) return;

            if (args == "abort")
            {
                StopAutoTreasureHunt();
                return;
            }

            var route = Routes.Where
                              (x => x.TerritoryType == GameState.TerritoryType &&
                                    x.Name.Contains(args, StringComparison.OrdinalIgnoreCase)
                              )
                              .OrderBy(x => x.Name.Length)
                              .FirstOrDefault();
            if (route is null) return;

            EnqueueAutoTreasureHunt(route.Points);
        }

        private void OnPreSendPacket
        (
            ref bool isPrevented,
            int      opcode,
            ref nint packet,
            ref bool isPrioritize
        )
        {
            if (opcode                         != UpstreamOpcode.PositionUpdateInstanceOpcode ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent         ||
                !treasureTaskHelper.IsBusy)
                return;

            isPrevented = true;
        }

        private void OnZoneChanged
        (
            uint u
        )
        {
            currentRoute.Clear();
            queuedGatheringList.Clear();

            treasureObjects.Clear();
            surveyPointPositions.Clear();
            carrotPositions.Clear();

            treasureHandle?.Unreg();
            treasureHandle = null;

            surveyPointHandle?.Unreg();
            surveyPointHandle = null;

            carrotHandle?.Unreg();
            carrotHandle = null;

            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            treasureHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () => MainModule.config.IsEnabledHighlightTreasure ?
                          treasureObjects :
                          [],
                x => x.Position,
                new()
                {
                    TextGetter = _ => new()
                    {
                        Text      = LuminaWrapper.GetAddonText(395),
                        TextScale = 1.4f
                    }
                }
            );

            surveyPointHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () => MainModule.config.IsEnabledHighlightSurveyPoint ?
                          surveyPointPositions :
                          [],
                pos => pos,
                new()
                {
                    TextGetter = _ => new()
                    {
                        Text      = LuminaWrapper.GetEObjName(2014695),
                        TextScale = 1.4f
                    }
                }
            );

            carrotHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () => MainModule.config.IsEnabledHighlightCarrot ?
                          carrotPositions :
                          [],
                pos => pos,
                new()
                {
                    TextGetter = _ => new()
                    {
                        Text      = LuminaWrapper.GetItemName(48096),
                        TextScale = 1.4f
                    }
                }
            );
        }

        // 更新箱子数据并处理开箱
        public override void OnUpdate()
        {
            RefreshSpecialObjectsAround();
            HandleAutoOpenTreasures();
        }

        // 绘制连接线和地图
        private void OnPosDraw()
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            routePreview.ResetIfNotDrawnThisFrame();

            // 绘制地图
            if (routePreview.Points is { } previewPoints)
                DrawTreasureRouteMap(previewPoints);
            else if (treasureTaskHelper.IsBusy)
                DrawTreasureRouteMap(currentRoute);
        }

        #endregion

        private void EnqueueAutoTreasureHunt
        (
            List<Vector3> routeData
        )
        {
            treasureTaskHelper.Abort();
            queuedGatheringList.Clear();

            var startPosition = GameState.TerritoryType == SOUTH_HORN_TERRITORY_ID ?
                                    CrescentAetheryte.ExpeditionBaseCamp.Position :
                                    CrescentAetheryte.NorthHornBaseCamp.Position;

            if (LocalPlayerState.DistanceTo2DSquared(startPosition.ToVector2()) <= 50f * 50f)
            {
                NotifyHelper.Instance().NotificationError(Lang.Get("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Notification-Danger"));
                return;
            }

            queuedGatheringList = PathPlanner.PlanShortestPath(LocalPlayerState.Object.Position, routeData);
            currentRoute        = [.. queuedGatheringList];
            MoveToNextTreasurePoint();
        }

        private void EnqueueMoveTo
        (
            Vector3 position
        ) =>
            treasureTaskHelper.EnqueueAsync
            (async ct =>
                {
                    unsafe
                    {
                        PlayerController.Instance()->MoveControllerWalk.IsMovementInputLocked = true;
                    }

                    await MovementManager.Instance().TPSmoothAsync
                    (
                        position,
                        ICondition.Instance()[ConditionFlag.Mounted] ?
                            24 :
                            12,
                        ct
                    );

                    if (!Throttler.Shared.Throttle("OccultCrescentHelper-TreasureManager-Pathfind-Check")) return false;

                    if (LocalPlayerState.DistanceTo2DSquared(position.ToVector2()) >= 3f * 3f) return false;

                    OnUpdate();

                    unsafe
                    {
                        PlayerController.Instance()->MoveControllerWalk.IsMovementInputLocked = false;
                    }

                    return true;
                }
            );

        private unsafe void StopAutoTreasureHunt()
        {
            treasureTaskHelper.Abort();
            queuedGatheringList.Clear();
            currentRoute.Clear();

            PlayerController.Instance()->MoveControllerWalk.IsMovementInputLocked = false;
        }

        private unsafe void MoveToNextTreasurePoint()
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
                !GameState.IsLoggedIn)
            {
                StopAutoTreasureHunt();
                return;
            }
            
            if (queuedGatheringList.Count == 0)
            {
                var currentPosition = LocalPlayerState.Object.Position;
                
                treasureTaskHelper.Enqueue
                (() =>
                    {
                        if (LocalPlayerState.DistanceTo2DSquared(currentPosition.ToVector2()) >= 50 * 50)
                            return true;

                        if (!UIModule.IsScreenReady())
                            return false;
                        
                        MovementManager.Instance().TPSmooth(currentPosition, 24f);
                        
                        if (ICondition.Instance()[ConditionFlag.Mounted])
                        {
                            MountCommand.Dismount();
                            return false;
                        }

                        if (ICondition.Instance()[ConditionFlag.Casting])
                            return false;
                        
                        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, DEMI_RETURN_ACTION_ID) != 0)
                            return false;

                        if (Throttler.Shared.Throttle("OccultCrescentHelper.TreasureManager.DemiReturn"))
                            UseActionManager.Instance().UseAction(ActionType.Action, DEMI_RETURN_ACTION_ID);
                        
                        return false;
                    }
                );
                
                treasureTaskHelper.Enqueue(() =>
                {
                    PlayerController.Instance()->MoveControllerWalk.IsMovementInputLocked = false;
                    currentRoute.Clear();
                    
                    var message = Lang.Get("OccultCrescentHelper-TreasureManager-AutoOpenTreasure-Notification-End");
                    NotifyHelper.Instance().NotificationInfo(message);
                    NotifyHelper.Speak(message);
                });
                
                return;
            }

            var origPosition = queuedGatheringList.Dequeue();
            var position     = origPosition;

            treasureTaskHelper.Enqueue
            (() =>
                {
                    if (ICondition.Instance()[ConditionFlag.Mounted]) return true;
                    if (!Throttler.Shared.Throttle("OccultCrescentHelper.TreasureManager.UseMount")) return false;

                    if (ICondition.Instance().IsCasting) return false;

                    UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                    return false;
                }
            );

            treasureTaskHelper.Enqueue
            (() =>
                {
                    PlayerController.Instance()->MoveControllerWalk.IsMovementInputLocked = true;
                    MovementManager.Instance().TPSmooth(position, 24);

                    if (!Throttler.Shared.Throttle("OccultCrescentHelper.TreasureManager.Pathfind.Check", 100))
                        return false;

                    if (LocalPlayerState.DistanceTo2DSquared(position.ToVector2()) >= 50f * 50f)
                        return false;

                    OnUpdate();

                    foreach (var (_, pos) in treasureObjects)
                    {
                        position   = pos;
                        position.Y = origPosition.Y;
                        return false;
                    }

                    // 点位没有, 直接去下一个
                    return true;
                }
            );

            treasureTaskHelper.Enqueue(MoveToNextTreasurePoint, "下一轮开始");
        }

        // 自动开箱
        private unsafe void HandleAutoOpenTreasures()
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent ||
                !MainModule.config.IsEnabledAutoOpenTreasure                          ||
                ICondition.Instance()[ConditionFlag.InCombat]                         ||
                treasureObjects is not { Count: > 0 })
                return;

            if (LocalPlayerState.Object is null) return;

            var treasures = EventObjectManager.Instance()->FindAll
            (ptr =>
                {
                    var gameObject = (Treasure*)ptr;
                    if (gameObject == null) return false;

                    if (gameObject->ObjectKind != ObjectKind.Treasure)
                        return false;

                    if (gameObject->Flags.IsSetAny(Treasure.TreasureFlags.Opened, Treasure.TreasureFlags.FadedOut))
                        return false;

                    var distanceSquared = MainModule.config.DistanceToAutoOpenTreasure * MainModule.config.DistanceToAutoOpenTreasure;

                    if (LocalPlayerState.DistanceTo2DSquared(gameObject->Position.ToVector2()) > distanceSquared)
                        return false;

                    return true;
                }
            );

            if (treasures.Count == 0)
                return;

            foreach (var treasure in treasures)
                InteractWithTreasure((Treasure*)treasure);
        }

        // 更新特殊物体数据
        private unsafe void RefreshSpecialObjectsAround()
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            List<Vector3> surveyPoints = [];
            List<Vector3> carrots      = [];

            var treasures = EventObjectManager.Instance()->FindAll
            (ptr =>
                {
                    var gameObject = (Treasure*)ptr;
                    if (gameObject == null) return false;

                    if (gameObject->ObjectKind != ObjectKind.Treasure)
                        return false;

                    if (gameObject->Flags.IsSetAny(Treasure.TreasureFlags.Opened, Treasure.TreasureFlags.FadedOut))
                        return false;

                    return true;
                }
            );

            foreach (var eventObjectPtr in EventObjectManager.Instance()->EventObjects)
            {
                if (eventObjectPtr.IsNull) continue;

                var eventObject = eventObjectPtr.Value;
                if (!eventObject->IsReadyToDraw()) return;

                switch (eventObject->ObjectKind)
                {
                    case ObjectKind.Treasure:
                        var treasureObject = (Treasure*)eventObject;
                        if (treasureObject->Flags.IsSetAny(Treasure.TreasureFlags.Opened, Treasure.TreasureFlags.FadedOut))
                            break;

                        treasures.Add((nint)treasureObject);
                        break;
                }
            }

            foreach (var eventObjectPtr in StandObjectManager.Instance()->EventObjects)
            {
                if (eventObjectPtr.IsNull) continue;

                var eventObject = eventObjectPtr.Value;
                if (!eventObject->IsReadyToDraw()) return;

                switch (eventObject->ObjectKind)
                {
                    case ObjectKind.EventObj:
                        switch (eventObject->BaseId)
                        {
                            // 调查地点
                            case 2014695:
                                surveyPoints.Add(eventObject->Position);
                                break;

                            // 胡萝卜
                            case 2010139:
                                carrots.Add(eventObject->Position);
                                break;
                        }

                        break;
                }
            }

            treasureObjects      = [.. treasures.Select(x => (x, (Vector3)((GameObject*)x)->Position))];
            surveyPointPositions = surveyPoints;
            carrotPositions      = carrots;
        }

        private unsafe void InteractWithTreasure
        (
            Treasure* treasure
        )
        {
            if (LocalPlayerState.Object is not { } localPlayer) return;

            var moveType     = MovementManager.Instance().GetInstanceMoveType(PositionUpdateInstancePacket.MoveType.NormalMove0);
            var origPosition = localPlayer.Position;

            var origTreasurePosition = (Vector3)treasure->Position;

            var treasurePosition = !treasureTaskHelper.IsBusy ?
                                       origTreasurePosition :
                                       origTreasurePosition.WithY(origPosition.Y);

            new PositionUpdateInstancePacket(localPlayer.Rotation, treasurePosition, moveType).Send();
            new TreasureOpenPacket(treasure->EntityId).Send();
            new PositionUpdateInstancePacket(localPlayer.Rotation, origPosition, moveType).Send();
        }


        #region 嵌套类

        private static class PathPlanner
        {
            public static Queue<Vector3> PlanShortestPath
            (
                Vector3       currentPosition,
                List<Vector3> locations
            )
            {
                if (locations is not { Count: > 0 })
                    return [];

                var startPoint = new Vector3(currentPosition.X, currentPosition.Y, currentPosition.Z);

                var allPoints = new List<Vector3> { startPoint };
                allPoints.AddRange(locations);

                var orderedPath = CreateInitialPathNearestNeighbor(allPoints);

                OptimizePath2Opt(orderedPath);

                orderedPath.RemoveAt(0);
                return new Queue<Vector3>(orderedPath);
            }

            private static List<Vector3> CreateInitialPathNearestNeighbor
            (
                List<Vector3> points
            )
            {
                var remainingPoints = new List<Vector3>(points);
                var orderedPath     = new List<Vector3>();

                var currentPoint = remainingPoints[0];
                orderedPath.Add(currentPoint);
                remainingPoints.RemoveAt(0);

                while (remainingPoints.Count > 0)
                {
                    Vector3? nearestPoint = null;

                    var minDistanceSQ = float.MaxValue;
                    foreach (var point in remainingPoints)
                    {
                        var distance = Vector3.DistanceSquared(currentPoint, point);
                        if (distance < minDistanceSQ)
                        {
                            minDistanceSQ  = distance;
                            nearestPoint = point;
                        }
                    }

                    if (nearestPoint != null)
                    {
                        orderedPath.Add(nearestPoint.Value);
                        remainingPoints.Remove(nearestPoint.Value);
                        currentPoint = nearestPoint.Value;
                    }
                }

                return orderedPath;
            }

            private static void OptimizePath2Opt
            (
                List<Vector3> path
            )
            {
                var improvementFound = true;
                var n                = path.Count;

                while (improvementFound)
                {
                    improvementFound = false;

                    for (var i = 0; i < n - 2; i++)
                    for (var j = i + 2; j < n - 1; j++)
                    {
                        var p1 = path[i];
                        var p2 = path[i + 1];
                        var p3 = path[j];
                        var p4 = path[j + 1];

                        var currentDist = Vector3.DistanceSquared(p1, p2) + Vector3.DistanceSquared(p3, p4);
                        var newDist     = Vector3.DistanceSquared(p1, p3) + Vector3.DistanceSquared(p2, p4);

                        if (newDist < currentDist)
                        {
                            path.Reverse(i + 1, j - i);
                            improvementFound = true;
                        }
                    }
                }
            }
        }

        private record Route
        (
            uint          TerritoryType,
            string        Name,
            string?       Description,
            List<Vector3> Points
        );

        private class RoutePreview
        {
            private Route?  route;
            private Vector2 plannedPosition;
            private int     drawnFrame;
            private long    lastUpdateTick;

            public List<Vector3>? Points { get; private set; }

            public void Update
            (
                Route? hoveredRoute
            )
            {
                drawnFrame = ImGui.GetFrameCount();

                if (hoveredRoute is null)
                {
                    route  = null;
                    Points = null;
                    return;
                }

                if (Points == null)
                {
                    Plan(hoveredRoute);
                    return;
                }

                var currentTick = Environment.TickCount64;
                if (currentTick - lastUpdateTick < 500)
                    return;

                lastUpdateTick = currentTick;

                if (ReferenceEquals(route, hoveredRoute) &&
                    LocalPlayerState.DistanceTo2DSquared(plannedPosition) <= 50f * 50f)
                    return;

                Plan(hoveredRoute);
            }

            private void Plan
            (
                Route target
            )
            {
                var playerPosition = LocalPlayerState.Object.Position;
                route           = target;
                plannedPosition = playerPosition.ToVector2();
                Points          = [.. PathPlanner.PlanShortestPath(playerPosition, target.Points)];
                lastUpdateTick  = Environment.TickCount64;
            }

            public void ResetIfNotDrawnThisFrame()
            {
                if (ImGui.GetFrameCount() - drawnFrame > 0)
                    Points = null;
            }
        }

        #endregion


        #region 常量

        private const ImGuiWindowFlags WINDOW_FLAGS =
            ImGuiWindowFlags.NoScrollbar           |
            ImGuiWindowFlags.AlwaysAutoResize      |
            ImGuiWindowFlags.NoTitleBar            |
            ImGuiWindowFlags.NoBackground          |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoFocusOnAppearing    |
            ImGuiWindowFlags.NoNavFocus            |
            ImGuiWindowFlags.NoDocking             |
            ImGuiWindowFlags.NoMove                |
            ImGuiWindowFlags.NoResize              |
            ImGuiWindowFlags.NoScrollWithMouse     |
            ImGuiWindowFlags.NoInputs              |
            ImGuiWindowFlags.NoSavedSettings;

        private const string COMMAND_TREASURE = "ptreasure";

        private static readonly uint LineColorBlue = KnownColor.CadetBlue.ToVector4().ToUInt();
        private static readonly uint DotColor      = KnownColor.IndianRed.ToVector4().ToUInt();
        private static readonly uint PlayerColor   = KnownColor.Orange.ToVector4().ToUInt();

        private static readonly Route[] Routes =
        [
            // 北征（内）
            new
            (
                NORTH_HORN_TERRITORY_ID,
                Lang.Get("InnerLoop"),
                null,
                [
                    new(-590.23f, -23f, -7.00f),
                    new(-633.72f, -23f, -146.01f),
                    new(-581.51f, -23f, -257.44f),
                    new(-439.57f, -23f, -558.46f),
                    new(-232.44f, -23f, -720.00f),
                    new(-265.77f, -23f, -439.54f),
                    new(-254.17f, -23f, -266.32f),
                    new(-168.23f, -23f, -153.46f),
                    new(43.78f, -23f, -108.20f),
                    new(85.59f, -23f, -281.15f),
                    new(-26.02f, -23f, -437.71f),
                    new(254.72f, -23f, -605.01f),
                    new(279.07f, -23f, -356.16f),
                    new(658.81f, -23f, -364.71f),
                    new(478.45f, -23f, -202.99f),
                    new(383.29f, -23f, -175.68f),
                    new(223.65f, -23f, -30.66f),
                    new(161.00f, -23f, 15.98f),
                    new(313.89f, -23f, 180.04f),
                    new(449.39f, -23f, 105.21f),
                    new(447.87f, -23f, 463.34f),
                    new(246.20f, -23f, 676.66f),
                    new(77.04f, -23f, 536.25f),
                    new(-22.69f, -23f, 628.99f),
                    new(-278.07f, -23f, 567.96f),
                    new(-144.73f, -23f, 304.92f),
                    new(41.21f, -23f, 168.47f),
                    new(-162.07f, -23f, 98.44f),
                    new(-287.77f, -23f, 125.66f),
                    new(-436.45f, -23f, 166.22f),
                    new(-631.80f, -23f, 239.98f)
                ]
            ),

            // 北征（外）
            new
            (
                NORTH_HORN_TERRITORY_ID,
                Lang.Get("OuterLoop"),
                null,
                [
                    new(-879.00f, -23f, -314.23f),
                    new(-707.39f, -70f, -396.99f),
                    new(-697.29f, -70f, -565.03f),
                    new(-857.60f, -70f, -609.83f),
                    new(-815.82f, -70f, -699.40f),
                    new(-928.65f, -70f, -744.96f),
                    new(-736.05f, -70f, -881.50f),
                    new(-525.81f, -23f, -783.47f),
                    new(-416.80f, -23f, -945.43f),
                    new(-2.33f, -23f, -814.91f),
                    new(147.84f, -23f, -868.77f),
                    new(389.52f, -23f, -733.03f),
                    new(639.03f, -23f, -698.76f),
                    new(634.79f, -23f, -831.82f),
                    new(633.11f, -23f, -910.25f),
                    new(865.45f, -23f, -874.11f),
                    new(815.43f, -23f, -657.34f),
                    new(658.72f, -23f, -552.33f),
                    new(950.19f, -23f, -359.00f),
                    new(649.53f, -23f, -157.79f),
                    new(719.33f, -23f, 268.30f),
                    new(758.14f, -23f, 506.80f),
                    new(811.98f, -23f, 668.97f),
                    new(673.73f, -23f, 729.64f),
                    new(676.97f, -23f, 957.43f),
                    new(222.89f, -23f, 913.60f),
                    new(-12.10f, -23f, 773.86f),
                    new(-256.98f, -23f, 812.19f),
                    new(-504.11f, -23f, 758.30f),
                    new(-612.24f, -23f, 578.55f),
                    new(-592.00f, -23f, 767.67f),
                    new(-645.44f, -23f, 967.93f),
                    new(-699.86f, -23f, 926.36f),
                    new(-857.82f, -23f, 772.21f),
                    new(-800.41f, -23f, 633.39f),
                    new(-775.91f, -23f, 377.13f),
                    new(-923.16f, -23f, 197.92f)
                ]
            ),

            // 南征（内）
            new
            (
                SOUTH_HORN_TERRITORY_ID,
                Lang.Get("InnerLoop"),
                null,
                [
                    new(-158.65f, -20f, -132.74f),
                    new(-256.89f, -20f, 125.08f),
                    new(-444.11f, -20f, 26.23f),
                    new(-394.89f, -20f, 175.43f),
                    new(-401.66f, -20f, 332.54f),
                    new(-283.99f, -20f, 377.04f),
                    new(-372.67f, -20f, 527.43f),
                    new(-197.19f, -20f, 618.34f),
                    new(35.72f, -20f, 648.95f),
                    new(8.99f, -20f, 426.96f),
                    new(-25.68f, -20f, 150.16f),
                    new(277.79f, -20f, 241.90f),
                    new(256.15f, -20f, 492.36f),
                    new(517.75f, -20f, 236.13f),
                    new(609.61f, -20f, 117.27f),
                    new(475.73f, -20f, -87.08f),
                    new(245.59f, -20f, -18.17f),
                    new(354.12f, -20f, -288.93f),
                    new(386.92f, -20f, -451.38f),
                    new(142.11f, -20f, -574.06f),
                    new(55.28f, -20f, -289.08f),
                    new(-140.46f, -20f, -414.27f),
                    new(-343.16f, -20f, -382.13f),
                    new(-487.11f, -20f, -205.46f)
                ]
            ),

            // 南征（外）
            new
            (
                SOUTH_HORN_TERRITORY_ID,
                Lang.Get("OuterLoop"),
                null,
                [
                    new(-682.80f, -20f, -195.27f),
                    new(-767.45f, -20f, -235.00f),
                    new(-798.25f, -20f, -310.57f),
                    new(-680.54f, -20f, -354.79f),
                    new(-491.02f, -20f, -529.59f),
                    new(-661.71f, -20f, -579.49f),
                    new(-884.12f, -20f, -682.03f),
                    new(-825.1f, -20f, -833.6f),
                    new(-729.43f, -20f, -724.82f),
                    new(-585.29f, -20f, -864.84f),
                    new(-451.68f, -20f, -775.57f),
                    new(-118.97f, -20f, -708.46f),
                    new(381.73f, -20f, -743.65f),
                    new(490.41f, -20f, -590.57f),
                    new(617.09f, -20f, -703.88f),
                    new(666.53f, -20f, -480.37f),
                    new(870.66f, -20f, -388.36f),
                    new(779.02f, -20f, -256.24f),
                    new(770.75f, -20f, -143.57f),
                    new(726.28f, -20f, -67.92f),
                    new(788.88f, -20f, 109.39f),
                    new(642.97f, -20f, 407.80f),
                    new(826.69f, -20f, 434.99f),
                    new(869.29f, -20f, 581.20f),
                    new(835.08f, -20f, 699.09f),
                    new(697.32f, -20f, 597.92f),
                    new(596.46f, -20f, 622.77f),
                    new(471.18f, -20f, 530.02f),
                    new(433.71f, -20f, 683.53f),
                    new(294.88f, -20f, 640.22f),
                    new(140.98f, -20f, 770.99f),
                    new(-225.02f, -20f, 804.99f),
                    new(-550.13f, -20f, 627.74f),
                    new(-676.42f, -20f, 640.38f),
                    new(-645.69f, -20f, 710.17f),
                    new(-600.27f, -20f, 802.64f),
                    new(-716.15f, -20f, 794.43f),
                    new(-784.76f, -20f, 699.76f),
                    new(-729.55f, -20f, 561.15f),
                    new(-648.00f, -20f, 403.95f),
                    new(-713.80f, -20f, 192.61f),
                    new(-756.83f, -20f, 97.37f),
                    new(-729.92f, -20f, -79.06f),
                    new(-856.96f, -20f, -93.16f)
                ]
            )
        ];

        #endregion
    }
}
