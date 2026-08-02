using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ZoneIndicator;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using OmenTools.Threading.TaskHelper.Enums;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace DailyRoutines.ModulesPublic.Duty;

public partial class OccultCrescentHelper
{
    private unsafe class CEManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private const string COMMAND_FATE = "pfate";
        private const string COMMAND_CE   = "pce";
        
        private const float MOUNT_MINIMUM_DISTANCE          = 50f;
        private const float PATHFINDING_COMPLETION_DISTANCE = 5f;
        private const float PATH_POINT_RADIUS               = 4f;
        private const float PATH_LINE_THICKNESS             = 2f;

        private static readonly uint PathLineColor  = KnownColor.DeepSkyBlue.ToUInt();
        private static readonly uint PathPointColor = KnownColor.LightSkyBlue.ToUInt();

        private          HashSet<IslandEventData> allIslandEvents = [];
        private readonly HashSet<string>          knownCENames    = [];

        private ZoneIndicatorHandle? fateHandle;
        private ZoneIndicatorHandle? ceHandle;

        private TaskHelper?         ceTaskHelper;
        private PathfindingSession? pathfindingSession;

        public override void Init()
        {
            ceTaskHelper ??= new() { TimeoutMS = 180_000 };

            DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
            OnZoneChanged(0);

            ExecuteCommandManager.Instance().RegPost(OnPostReceivedCommand);
            LogMessageManager.Instance().RegPost(OnPostReceivedMessage);
            GameState.Instance().Logout       += OnLogout;
            WindowManager.Instance().PostDraw += OnPathfindingDraw;

            var isAnyNewCategory = false;

            foreach (var eventType in Enum.GetValues<CrescentEventType>())
            {
                if (!MainModule.config.IsEnabledNotifyEventsCategoried.TryAdd(eventType, true)) continue;
                isAnyNewCategory = true;
            }

            if (isAnyNewCategory)
                MainModule.config.Save(MainModule);

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_FATE,
                new(OnCommandFate) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PFate-Help")}" }
            );

            CommandManager.Instance().AddSubCommand
            (
                COMMAND_CE,
                new(OnCommandCE) { HelpMessage = $"{Lang.Get("OccultCrescentHelper-Command-PCE-Help")}" }
            );
        }

        public override void Uninit()
        {
            CommandManager.Instance().RemoveSubCommand(COMMAND_FATE);
            CommandManager.Instance().RemoveSubCommand(COMMAND_CE);

            GameState.Instance().Logout -= OnLogout;
            ExecuteCommandManager.Instance().Unreg(OnPostReceivedCommand);
            LogMessageManager.Instance().Unreg(OnPostReceivedMessage);
            DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
            WindowManager.Instance().PostDraw                -= OnPathfindingDraw;

            fateHandle?.Unreg();
            fateHandle = null;

            ceHandle?.Unreg();
            ceHandle = null;

            allIslandEvents.Clear();
            knownCENames.Clear();
            StopPathfinding();

            ceTaskHelper?.Dispose();
            ceTaskHelper = null;
        }

        public override void DrawConfig()
        {
            using var id = ImRaii.PushId("CEManager");

            if (allIslandEvents.Count > 0)
            {
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Pathfind"));

                ImGui.SameLine(0, 8f * GlobalUIScale);

                if (ImGui.SmallButton($"{Lang.Get("Stop")}##StopCE"))
                    StopPathfinding();

                using (ImRaii.PushIndent())
                {
                    foreach (var ce in allIslandEvents)
                    {
                        if (!DService.Instance().Texture.TryGetFromGameIcon(new(ce.Event.IconID), out var texture)) continue;

                        using (ImRaii.Disabled(!CanStartPathfinding(ce)))
                        {
                            if (ImGuiOm.SelectableImageWithText
                                (
                                    texture.GetWrapOrEmpty().Handle,
                                    new(ImGui.GetTextLineHeightWithSpacing()),
                                    $"{ce.Event.NameDisplay}",
                                    false
                                ))
                                StartPathfinding(ce);
                        }
                    }
                }

                ImGui.NewLine();
            }

            if (ImGui.Checkbox(Lang.Get("OccultCrescentHelper-CEManager-InterruptOnMovementInput"), ref MainModule.config.InterruptPathfindingOnMovementInput))
                MainModule.config.Save(MainModule);

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("OccultCrescentHelper-CEManager-AutoDismount"));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox
                    (
                        $"{CrescentEvent.GetEventTypeName(CrescentEventType.FATE)}##AutoDismountFATE",
                        ref MainModule.config.IsEnabledDismountFATE
                    ))
                    MainModule.config.Save(MainModule);

                if (ImGui.Checkbox
                    (
                        $"{CrescentEvent.GetEventTypeName(CrescentEventType.CE)}##AutoDismountCE",
                        ref MainModule.config.IsEnabledDismountCE
                    ))
                    MainModule.config.Save(MainModule);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("OccultCrescentHelper-Highlight"));

            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox
                    (
                        $"{CrescentEvent.GetEventTypeName(CrescentEventType.CE)}",
                        ref MainModule.config.IsEnabledHighlightCE
                    ))
                    MainModule.config.Save(MainModule);

                if (ImGui.Checkbox
                    (
                        $"{CrescentEvent.GetEventTypeName(CrescentEventType.FATE)}",
                        ref MainModule.config.IsEnabledHighlightFATE
                    ))
                    MainModule.config.Save(MainModule);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-CEManager-NotifyEventAppears"));

            using (ImRaii.PushIndent())
            {
                var counter = 0;

                foreach (var (type, isEnabled) in MainModule.config.IsEnabledNotifyEventsCategoried)
                {
                    using var isEnabledNotifyEventsDataID = ImRaii.PushId($"{type}");

                    using (ImRaii.Group())
                    {
                        var isEnabledCopy = isEnabled;

                        if (ImGui.Checkbox($"{CrescentEvent.GetEventTypeName(type)}##{type}", ref isEnabledCopy))
                        {
                            MainModule.config.IsEnabledNotifyEventsCategoried[type] = isEnabledCopy;
                            MainModule.config.Save(MainModule);
                        }
                    }

                    if (counter != 7 && counter != 11 && counter != MainModule.config.IsEnabledNotifyEventsCategoried.Count - 1)
                        ImGui.SameLine(0, 4f * GlobalUIScale);
                    counter++;
                }
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-CEManager-NotifyCEStarts"));

            using (ImRaii.PushId("NotifyCEStarts"))
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("SendNotification"), ref MainModule.config.IsEnabledNotifyCENotification))
                    MainModule.config.Save(MainModule);

                if (ImGui.Checkbox(Lang.Get("SendTTS"), ref MainModule.config.IsEnabledNotifyCETTS))
                    MainModule.config.Save(MainModule);

                if (ImGui.Checkbox(Lang.Get("SendSystemSound"), ref MainModule.config.IsEnabledNotifyCESystemSound))
                    MainModule.config.Save(MainModule);
            }

            ImGui.NewLine();

            ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

            using (ImRaii.PushIndent())
            {
                ImGui.TextWrapped($"/pdr {COMMAND_FATE} {Lang.Get("OccultCrescentHelper-Command-PFate-Help")}");

                ImGui.TextWrapped($"/pdr {COMMAND_CE} {Lang.Get("OccultCrescentHelper-Command-PCE-Help")}");
            }
        }

        private void OnLogout() =>
            StopPathfinding();

        private void OnZoneChanged
        (
            uint u
        )
        {
            fateHandle?.Unreg();
            fateHandle = null;

            ceHandle?.Unreg();
            ceHandle = null;

            allIslandEvents.Clear();
            knownCENames.Clear();
            StopPathfinding();

            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            fateHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () =>
                {
                    if (!MainModule.config.IsEnabledHighlightFATE || DService.Instance().Condition[ConditionFlag.InCombat])
                        return [];

                    return allIslandEvents
                           .Where(x => x.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot)
                           .ToList();
                },
                data => data.Event.Position,
                new()
                {
                    TextGetter = data =>
                    {
                        ZoneIndicatorText.TextImage? image = null;

                        if (data.Event.IconID != 0 &&
                            DService.Instance().Texture.TryGetFromGameIcon(new(data.Event.IconID), out var iconTex))
                        {
                            image = new()
                            {
                                Texture    = iconTex,
                                SizeGetter = () => new(ImGui.GetTextLineHeightWithSpacing())
                            };
                        }

                        return new()
                        {
                            Text      = $"{data.Event.NameDisplay} ({TimeSpan.FromSeconds(data.FateTimeRemaining):mm\\:ss})",
                            TextScale = 1.2f,
                            Image     = image,
                            TextColor = KnownColor.LawnGreen.ToVector4()
                        };
                    },
                    RenderRadius = 300
                }
            );

            ceHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () =>
                {
                    if (!MainModule.config.IsEnabledHighlightCE || DService.Instance().Condition[ConditionFlag.InCombat])
                        return [];

                    return allIslandEvents
                           .Where(x => x.Event.Type is CrescentEventType.CE)
                           .ToList();
                },
                data => data.Event.Position,
                new()
                {
                    TextGetter = data =>
                    {
                        var text = data.Event.CEState switch
                        {
                            DynamicEventState.Register => $"{data.Event.Name} ({TimeSpan.FromSeconds(data.Event.CELeftTimeSecond):mm\\:ss})",
                            _                          => data.Event.NameDisplay
                        };

                        ZoneIndicatorText.TextImage? image = null;

                        if (data.Event.IconID != 0 &&
                            DService.Instance().Texture.TryGetFromGameIcon(new(data.Event.IconID), out var iconTex))
                        {
                            image = new()
                            {
                                Texture    = iconTex,
                                SizeGetter = () => new Vector2(20f)
                            };
                        }

                        return new()
                        {
                            Text      = new ReadOnlySeString(text),
                            TextScale = 1.2f,
                            TextColor = KnownColor.LawnGreen.ToVector4(),
                            Image     = image
                        };
                    },
                    RenderRadius = 300
                }
            );
        }

        public override void OnUpdate()
        {
            var publicInstance = PublicContentOccultCrescent.GetInstance();
            if (publicInstance == null) return;

            var currentCENames = new HashSet<string>();
            var newCEData      = new List<IslandEventData>();

            // FATE
            foreach (var fate in DService.Instance().Fate)
            {
                if (IslandEventData.Parse(fate) is not { } safeFate) continue;

                newCEData.Add(safeFate);
                currentCENames.Add(safeFate.Event.Name);

                if (allIslandEvents.TryGetValue(safeFate, out var existed))
                    existed.Update(fate);
                else
                    allIslandEvents.Add(safeFate);

                if (knownCENames.Add(safeFate.Event.Name))
                    NotifyNewCE(safeFate);
            }

            // CE
            var data = publicInstance->DynamicEventContainer.Events
                                                            .ToArray()
                                                            .Select(x => x)
                                                            .ToList();

            foreach (var dynamicEvent in data)
            {
                if (IslandEventData.Parse(dynamicEvent) is not { } safeCE) continue;

                newCEData.Add(safeCE);
                currentCENames.Add(safeCE.Event.Name);

                if (allIslandEvents.TryGetValue(safeCE, out var existed))
                    existed.Update(dynamicEvent);
                else
                    allIslandEvents.Add(safeCE);

                if (knownCENames.Add(safeCE.Event.Name))
                    NotifyNewCE(safeCE);

            }

            knownCENames.IntersectWith(currentCENames);
            allIslandEvents.IntersectWith(newCEData);

        }

        private void OnPostReceivedCommand
        (
            ExecuteCommandFlag command,
            uint               param1,
            uint               param2,
            uint               param3,
            uint               param4
        )
        {
            if (command                        != ExecuteCommandFlag.LoadFate ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            OnUpdate();
        }

        // CE 开始
        private void OnPostReceivedMessage
        (
            uint                logMessageID,
            LogMessageQueueItem values
        )
        {
            if (logMessageID                   != 11002 ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent)
                return;

            var message = Lang.Get("OccultCrescentHelper-CEManager-Notification-CEStart");

            if (MainModule.config.IsEnabledNotifyCENotification)
                NotifyHelper.Instance().NotificationInfo(message);
            if (MainModule.config.IsEnabledNotifyCETTS)
                NotifyHelper.Speak(message);
            if (MainModule.config.IsEnabledNotifyCESystemSound)
                NotifyHelper.SystemInformation();
        }

        private void OnClickPathfind
        (
            uint     id,
            SeString message
        )
        {
            if (allIslandEvents.FirstOrDefault(x => x.LinkPayloadID == id) is not { } ce) return;
            StartPathfinding(ce);
        }

        private void OnCommandFate
        (
            string command,
            string args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();

            if (args == "abort")
            {
                StopPathfinding();
                return;
            }

            var fate = allIslandEvents.Where(x => x.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot)
                                      .Where(CanStartPathfinding)
                                      .OrderBy(x => x.Event.Progress)
                                      .FirstOrDefault();
            if (fate == null) return;

            StartPathfinding(fate);
        }

        private void OnCommandCE
        (
            string command,
            string args
        )
        {
            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) return;

            args = args.Trim().ToLowerInvariant();

            if (args == "abort")
            {
                StopPathfinding();
                return;
            }

            var ce = allIslandEvents.Where(x => x.Event.Type == CrescentEventType.CE)
                                    .FirstOrDefault(CanStartPathfinding);
            if (ce == null) return;

            StartPathfinding(ce);
        }

        private static bool CanStartPathfinding
        (
            IslandEventData data
        ) =>
            vnavmeshIPC.IsPluginEnabled() &&
            data.Event.Type switch
            {
                CrescentEventType.FATE or CrescentEventType.MagicPot => true,
                CrescentEventType.CE                                 => data.Event is { CEState: DynamicEventState.Register },
                _                                                    => false
            };

        private void StartPathfinding
        (
            IslandEventData data
        )
        {
            if (!CanStartPathfinding(data)                                         ||
                DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer ||
                ceTaskHelper is not { } taskHelper)
                return;

            StopPathfinding();
            MainModule.aetheryteModule.StopPathfinding();

            var session = new PathfindingSession(data, data.Event.GetRandomPointNearEdge());
            pathfindingSession = session;

            var aetherytes = GameState.TerritoryType == 1252 ?
                                 CrescentAetheryte.SouthHornAetherytes :
                                 CrescentAetheryte.NorthHornAetherytes;

            var nearestAetheryte = aetherytes.MinBy
                (x => Vector3.DistanceSquared(x.Position, session.Destination));

            if (nearestAetheryte != null &&
                IsAetheryteRouteFaster(localPlayer.Position, nearestAetheryte, session.Destination))
            {
                if (MainModule.aetheryteModule.TryUseAetheryte(nearestAetheryte))
                {
                    session.Aetheryte                = nearestAetheryte;
                    session.TravelStage              = PathfindingTravelStage.Aetheryte;
                    session.StopAetherytePathfinding = MainModule.aetheryteModule.StopPathfinding;
                    EnqueueAetheryteArrival(taskHelper, session);
                }
                else
                {
                    var demiReturnStartPosition = localPlayer.Position;

                    session.TravelStage = PathfindingTravelStage.DemiReturn;

                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (pathfindingSession  != session ||
                                session.TravelStage != PathfindingTravelStage.DemiReturn)
                                return true;

                            if (DService.Instance().Condition[ConditionFlag.Mounted])
                            {
                                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                                return false;
                            }

                            if (ActionManager.Instance()->GetActionStatus(ActionType.Action, DEMI_RETURN_ACTION_ID) != 0)
                                return false;

                            if (DService.Instance().ObjectTable.LocalPlayer is not { } player) return false;

                            demiReturnStartPosition = player.Position;
                            return UseActionManager.Instance().UseAction(ActionType.Action, DEMI_RETURN_ACTION_ID);
                        },
                        timeoutMS: 5_000,
                        timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                        timeoutAction: () => session.TravelStage = PathfindingTravelStage.Pathfinding
                    );

                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (pathfindingSession  != session ||
                                session.TravelStage != PathfindingTravelStage.DemiReturn)
                                return true;

                            return DService.Instance().ObjectTable.LocalPlayer is { } player &&
                                   player.Position != demiReturnStartPosition;
                        },
                        timeoutMS: 30_000,
                        timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                        timeoutAction: () => session.TravelStage = PathfindingTravelStage.Pathfinding
                    );

                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (pathfindingSession  != session ||
                                session.TravelStage != PathfindingTravelStage.DemiReturn)
                                return true;

                            return UIModule.IsScreenReady();
                        },
                        timeoutMS: 30_000,
                        timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                        timeoutAction: () => session.TravelStage = PathfindingTravelStage.Pathfinding
                    );

                    taskHelper.Enqueue
                    (
                        () =>
                        {
                            if (pathfindingSession  != session ||
                                session.TravelStage != PathfindingTravelStage.DemiReturn)
                                return true;

                            if (DService.Instance().ObjectTable.LocalPlayer is not { } player) return false;

                            if (!IsAetheryteRouteFaster(player.Position, nearestAetheryte, session.Destination))
                            {
                                session.TravelStage = PathfindingTravelStage.Pathfinding;
                                return true;
                            }

                            if (!Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-DemiReturn-Aetheryte") ||
                                !MainModule.aetheryteModule.TryUseAetheryte(nearestAetheryte))
                                return false;

                            session.Aetheryte                = nearestAetheryte;
                            session.TravelStage              = PathfindingTravelStage.Aetheryte;
                            session.StopAetherytePathfinding = MainModule.aetheryteModule.StopPathfinding;

                            return true;
                        },
                        timeoutMS: 10_000,
                        timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                        timeoutAction: () => session.TravelStage = PathfindingTravelStage.Pathfinding
                    );

                    EnqueueAetheryteArrival(taskHelper, session);
                }
            }

            taskHelper.Enqueue
            (() =>
                {
                    if (pathfindingSession != session) return true;
                    if (DService.Instance().Condition.IsOccupiedInEvent) return false;
                    if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;
                    if (DService.Instance().ObjectTable.LocalPlayer is not { } player) return false;
                    if (Vector3.DistanceSquared(player.Position, session.Destination) <=
                        MOUNT_MINIMUM_DISTANCE * MOUNT_MINIMUM_DISTANCE)
                        return true;

                    return UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
                }
            );

            taskHelper.Enqueue
            (() =>
                {
                    if (pathfindingSession != session) return true;

                    if (DService.Instance().ObjectTable.LocalPlayer is not { } player)
                    {
                        StopPathfinding(session);
                        return true;
                    }

                    try
                    {
                        session.PathfindingTask = vnavmeshIPC.PathfindCancelable
                        (
                            player.Position,
                            session.Destination,
                            false,
                            session.CancellationTokenSource.Token
                        );
                    }
                    catch (Exception exception)
                    {
                        DLog.Error("新月岛区域事件寻路请求失败", exception);
                        StopPathfinding(session);
                        return true;
                    }

                    if (session.PathfindingTask != null) return true;

                    StopPathfinding(session);
                    return true;
                }
            );
        }

        private void EnqueueAetheryteArrival
        (
            TaskHelper         taskHelper,
            PathfindingSession session
        ) =>
            taskHelper.Enqueue
            (
                () =>
                {
                    if (pathfindingSession  != session                          ||
                        session.TravelStage != PathfindingTravelStage.Aetheryte ||
                        session.Aetheryte is not { } aetheryte)
                        return true;

                    if (LocalPlayerState.DistanceTo3D(aetheryte.Position) > 30f) return false;

                    session.Aetheryte                = null;
                    session.TravelStage              = PathfindingTravelStage.Pathfinding;
                    session.StopAetherytePathfinding = null;
                    return true;
                },
                timeoutMS: 30_000,
                timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                timeoutAction: () =>
                {
                    session.Aetheryte   = null;
                    session.TravelStage = PathfindingTravelStage.Pathfinding;
                    session.StopAetherytePathfinding?.Invoke();
                    session.StopAetherytePathfinding = null;
                }
            );

        private static bool IsAetheryteRouteFaster
        (
            Vector3            origin,
            CrescentAetheryte aetheryte,
            Vector3            destination
        ) =>
            Vector3.Distance(origin, destination) / 12f >
            (Vector3.Distance(aetheryte.Position, destination) / 12f) + 10f;

        private void OnPathfindingDraw()
        {
            if (pathfindingSession is not { } session) return;

            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            {
                StopPathfinding(session);
                return;
            }

            if (!allIslandEvents.Contains(session.Data))
            {
                StopPathfinding(session);
                return;
            }

            var playerPosition = localPlayer.Position.ToVector2();
            var eventRadius = session.Data.Event.Type == CrescentEventType.CE ?
                                  25f :
                                  session.Data.Event.Radius;

            if ((eventRadius                                                                      > 0f &&
                 Vector2.DistanceSquared(playerPosition, session.Data.Event.Position.ToVector2()) <= eventRadius * eventRadius) ||
                Vector2.DistanceSquared(playerPosition, session.Destination.ToVector2()) <=
                PATHFINDING_COMPLETION_DISTANCE * PATHFINDING_COMPLETION_DISTANCE)
            {
                CompletePathfinding(session);
                return;
            }

            if (session.Data.Event is { Type: CrescentEventType.CE, CEState: not DynamicEventState.Register })
            {
                StopPathfinding(session);
                return;
            }

            if (MainModule.config.InterruptPathfindingOnMovementInput       &&
                !session.IsMovementInterrupted                              &&
                (session.PathfindingTask != null || session.Path.Count > 0) &&
                IsMovementInputPressed())
            {
                session.IsMovementInterrupted = true;
                vnavmeshIPC.StopPathfind();
            }

            if (session.PathfindingTask is { IsCompleted: true } pathfindingTask)
            {
                session.PathfindingTask = null;

                if (!pathfindingTask.IsCompletedSuccessfully)
                {
                    if (pathfindingTask.Exception is { } exception)
                        DLog.Error("新月岛区域事件寻路失败", exception.GetBaseException());

                    StopPathfinding(session);
                    return;
                }

                session.Path = pathfindingTask.Result;

                if (session.Path.Count == 0)
                {
                    StopPathfinding(session);
                    return;
                }

                if (!session.IsMovementInterrupted)
                    vnavmeshIPC.PathfindWithPath([.. session.Path], false);
            }

            DrawPath(session.Path);
        }

        private static bool IsMovementInputPressed() =>
            DService.Instance().KeyState[VirtualKey.W] ||
            DService.Instance().KeyState[VirtualKey.A] ||
            DService.Instance().KeyState[VirtualKey.S] ||
            DService.Instance().KeyState[VirtualKey.D];

        private static void DrawPath
        (
            List<Vector3> path
        )
        {
            if (path.Count == 0) return;

            var drawList           = ImGui.GetForegroundDrawList();
            var gameGUI            = DService.Instance().GameGUI;
            var previousWorldPoint = path[0];
            var previousInFront    = gameGUI.WorldToScreen
            (
                previousWorldPoint,
                out var previousScreenPoint,
                out var previousInView
            );

            for (var i = 1; i < path.Count; i++)
            {
                var worldPoint = path[i];
                var isInFront  = gameGUI.WorldToScreen(worldPoint, out var screenPoint, out var isInView);

                DrawPathSegment
                (
                    drawList,
                    gameGUI,
                    previousWorldPoint,
                    worldPoint,
                    previousInFront ? previousScreenPoint : null,
                    isInFront ? screenPoint : null
                );

                if (previousInView)
                    drawList.AddCircleFilled(previousScreenPoint, PATH_POINT_RADIUS * GlobalUIScale, PathPointColor);

                previousWorldPoint  = worldPoint;
                previousScreenPoint = screenPoint;
                previousInFront     = isInFront;
                previousInView      = isInView;
            }

            if (previousInView)
                drawList.AddCircleFilled(previousScreenPoint, PATH_POINT_RADIUS * GlobalUIScale, PathPointColor);
        }

        private static void DrawPathSegment
        (
            ImDrawListPtr drawList,
            IGameGui      gameGUI,
            Vector3       worldStart,
            Vector3       worldEnd,
            Vector2?      screenStart,
            Vector2?      screenEnd
        )
        {
            if (screenStart is { } start && screenEnd is { } end)
            {
                drawList.AddLine(start, end, PathLineColor, PATH_LINE_THICKNESS * GlobalUIScale);
                return;
            }

            if (screenStart is null && screenEnd is null) return;

            var inFrontWorldPoint  = screenStart is not null ? worldStart : worldEnd;
            var behindWorldPoint   = screenStart is null ? worldStart : worldEnd;
            var inFrontScreenPoint = screenStart ?? screenEnd!.Value;

            if (TryClipPathSegmentToNearPlane(gameGUI, inFrontWorldPoint, behindWorldPoint, out var clippedScreenPoint))
                drawList.AddLine(inFrontScreenPoint, clippedScreenPoint, PathLineColor, PATH_LINE_THICKNESS * GlobalUIScale);
        }

        private static bool TryClipPathSegmentToNearPlane
        (
            IGameGui gameGUI,
            Vector3  inFrontWorldPoint,
            Vector3  behindWorldPoint,
            out Vector2 clippedScreenPoint
        )
        {
            var inFrontRatio = 0f;
            var behindRatio  = 1f;

            for (var i = 0; i < 8; i++)
            {
                var ratio = (inFrontRatio + behindRatio) * 0.5f;

                if (gameGUI.WorldToScreen(Vector3.Lerp(inFrontWorldPoint, behindWorldPoint, ratio), out _, out _))
                    inFrontRatio = ratio;
                else
                    behindRatio = ratio;
            }

            return gameGUI.WorldToScreen
            (
                Vector3.Lerp(inFrontWorldPoint, behindWorldPoint, inFrontRatio),
                out clippedScreenPoint,
                out _
            );
        }

        private void CompletePathfinding
        (
            PathfindingSession session
        )
        {
            var eventType = session.Data.Event.Type;
            var shouldDismount = eventType switch
            {
                CrescentEventType.FATE or CrescentEventType.MagicPot => MainModule.config.IsEnabledDismountFATE,
                CrescentEventType.CE                                 => MainModule.config.IsEnabledDismountCE,
                _                                                    => false
            };

            if (!StopPathfinding(session)) return;

            if (shouldDismount)
                ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);

            if (eventType is not (CrescentEventType.FATE or CrescentEventType.MagicPot) ||
                (DService.Instance().Condition[ConditionFlag.Mounted] && !shouldDismount))
                return;

            if (ceTaskHelper is not { } taskHelper) return;

            taskHelper.Enqueue
            (() =>
                {
                    if (DService.Instance().Condition[ConditionFlag.Mounted]) return false;

                    ChatManager.Instance().SendMessage("/tenemy");
                    return true;
                }
            );
            taskHelper.DelayNext(100);
            taskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/facetarget"));
            taskHelper.DelayNext(100);
            taskHelper.Enqueue(() => ChatManager.Instance().SendMessage("/automove on"));
        }

        private bool StopPathfinding
        (
            PathfindingSession? expectedSession = null
        )
        {
            var session = expectedSession == null ?
                              Interlocked.Exchange(ref pathfindingSession, null) :
                              Interlocked.CompareExchange(ref pathfindingSession, null, expectedSession);

            if (expectedSession != null && !ReferenceEquals(session, expectedSession)) return false;

            ceTaskHelper?.Abort();
            vnavmeshIPC.StopPathfind();

            if (session == null) return true;

            session.StopAetherytePathfinding?.Invoke();

            session.CancellationTokenSource.Cancel();
            session.CancellationTokenSource.Dispose();
            return true;
        }

        private void NotifyNewCE
        (
            IslandEventData ce
        )
        {
            if (!MainModule.config.IsEnabledNotifyEventsCategoried.GetValueOrDefault(ce.Event.Type, false))
                return;

            var ceName   = ce.Event.NameDisplay;
            var position = ce.Event.Position;

            var mapPos = PositionHelper.WorldToMap(position.ToVector2(), GameState.MapData);

            var message = new SeStringBuilder()
                          .AddUiForeground(25)
                          .AddText($"[{MainModule.Info.Title}] ")
                          .AddUiForegroundOff()
                          .AddText($"{ce.GetNotificationTitle()}")
                          .Add(NewLinePayload.Payload)
                          .AddText($"{Lang.Get("Name")}: ")
                          .AddUiForeground(45)
                          .AddText(ceName)
                          .AddUiForegroundOff()
                          .Add(NewLinePayload.Payload)
                          .AddText($"{Lang.Get("Position")}: ")
                          .Append(SeString.CreateMapLink(GameState.TerritoryTypeData.ExtractPlaceName(), mapPos.X, mapPos.Y));

            if (ce.Event.SpecialWeaponMaterialID != 0)
            {
                message.Add(NewLinePayload.Payload)
                       .AddText($"{Lang.Get("Item")}: ")
                       .AddItemLink(ce.Event.SpecialWeaponMaterialID, false)
                       .AddText($" ({LuminaWrapper.GetAddonText(358)}: {LocalPlayerState.GetItemCount(ce.Event.SpecialWeaponMaterialID)})");
            }

            if (ce.Event.SpecialRewards is { Count: > 0 } specialRewards)
            {
                var prefix = Lang.Get("OccultCrescentHelper-CEManager-SpecialRewards");
                message.Add(NewLinePayload.Payload)
                       .AddText($"{prefix}: ");

                foreach (var specialReward in specialRewards)
                {
                    var isObtained = CrescentEvent.IsSpecialRewardUnlocked(specialReward);

                    ushort textColor = isObtained switch
                    {
                        true  => 45,
                        false => 17,
                        null  => 32
                    };

                    var text = isObtained switch
                    {
                        true  => "✓",
                        false => "x",
                        null  => "?"
                    };

                    message.Add(NewLinePayload.Payload)
                           .AddText("      ")
                           .AddItemLink(specialReward)
                           .AddText(" (")
                           .AddUiForeground(textColor)
                           .AddText(text)
                           .AddUiForegroundOff()
                           .AddText(")");
                }
            }

            if (CanStartPathfinding(ce))
            {
                var linkPayload = ce.GetOrAddLinkPayload(this);

                message.Add(NewLinePayload.Payload)
                       .AddText($"{Lang.Get("Operation")}: ")
                       .Add(RawPayload.LinkTerminator)
                       .Add(linkPayload)
                       .AddText("[")
                       .AddIcon(BitmapFontIcon.Aethernet)
                       .AddUiForeground(35)
                       .AddText($"{Lang.Get("Pathfind")}")
                       .AddUiForegroundOff()
                       .AddText("]")
                       .Add(RawPayload.LinkTerminator);
            }

            // TODO: 改成 ReadOnlyString
            NotifyHelper.Instance().Chat(message.Build().Encode());

            NotifyHelper.Instance().NotificationInfo($"{ceName}", $"{ce.GetNotificationTitle()}");
            NotifyHelper.Speak($"{ce.GetNotificationTitle()}");
        }

        private sealed class PathfindingSession
        (
            IslandEventData data,
            Vector3         destination
        )
        {
            public IslandEventData         Data                     { get; } = data;
            public Vector3                 Destination              { get; } = destination;
            public CrescentAetheryte?      Aetheryte                { get; set; }
            public PathfindingTravelStage  TravelStage              { get; set; }
            public Action?                 StopAetherytePathfinding { get; set; }
            public CancellationTokenSource CancellationTokenSource  { get; } = new();
            public Task<List<Vector3>>?    PathfindingTask          { get; set; }
            public List<Vector3>           Path                     { get; set; } = [];
            public bool                    IsMovementInterrupted    { get; set; }
        }

        private enum PathfindingTravelStage : byte
        {
            Pathfinding,
            DemiReturn,
            Aetheryte
        }

        public class IslandEventData
        (
            uint dataID
        ) : IEquatable<IslandEventData>
        {
            public CrescentEvent Event { get; } = new(dataID);

            public int                LinkPayloadID { get; private set; } = -1;
            public DalamudLinkPayload LinkPayload   { get; private set; }

            public float FateTimeRemaining { get; set; }

            public bool Equals
            (
                IslandEventData? other
            )
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;

                return Event == other.Event;
            }

            public static IslandEventData? Parse
            (
                IFate fate
            )
            {
                if (fate.MapIconId == 0                                                   ||
                    fate.State is FateState.Ended or FateState.Ending or FateState.Failed ||
                    fate.Position == default)
                    return null;

                var name = $"{fate.Name} ({fate.Progress}%)";
                if (string.IsNullOrEmpty(name)) return null;

                var data = new IslandEventData(fate.FateId);
                data.Event.UpdateTempDataFATE(name, fate.Progress, fate.State);
                data.Event.UpdatePositionAndRadius(fate.Position, fate.Radius);
                data.FateTimeRemaining = fate.TimeRemaining;

                return data;
            }

            public static IslandEventData? Parse
            (
                DynamicEvent ce
            )
            {
                if (!LuminaGetter.TryGetRow(ce.DynamicEventId, out Lumina.Excel.Sheets.DynamicEvent data)) return null;
                if (ce.State is DynamicEventState.Inactive) return null;
                if (data.RowId != 48 && ce.MapMarker.Position == default) return null;

                var leftTime = ce.StartTimestamp - GameState.ServerTimeUnix;
                if (leftTime < 0)
                    leftTime = 0;

                var name = ce.Name.ToString();

                if (data.RowId != 48) // 两歧塔 力之塔
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                if (string.IsNullOrEmpty(name)) return null;

                var returnValue = new IslandEventData(data.RowId);
                returnValue.Event.UpdateTempDataCE
                (
                    name,
                    ce.Progress,
                    ce.State,
                    ce.State == DynamicEventState.Register ?
                        ce.StartTimestamp :
                        ce.StartTimestamp - 1200,
                    leftTime
                );
                returnValue.Event.UpdatePositionAndRadius(ce.MapMarker.Position, 0);
                return returnValue;
            }

            public void Update
            (
                IFate fate
            )
            {
                var name = $"{fate.Name} ({fate.Progress}%)";
                Event.UpdateTempDataFATE(name, fate.Progress, fate.State);
                FateTimeRemaining = fate.TimeRemaining;
            }

            public void Update
            (
                DynamicEvent ce
            )
            {
                if (!LuminaGetter.TryGetRow(ce.DynamicEventId, out Lumina.Excel.Sheets.DynamicEvent data))
                    return;

                var leftTime = ce.StartTimestamp - GameState.ServerTimeUnix;
                if (leftTime < 0)
                    leftTime = 0;

                var name = ce.Name.ToString();

                if (data.RowId != 48)
                {
                    name = ce.State switch
                    {
                        DynamicEventState.Battle   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-InBattle", ce.Participants, ce.Progress)})",
                        DynamicEventState.Register => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-Register", leftTime)})",
                        DynamicEventState.Warmup   => $"{ce.Name} ({Lang.Get("OccultCrescentHelper-CEManager-CEName-WarmUp")})",
                        _                          => $"{ce.Name}"
                    };
                }

                Event.UpdateTempDataCE
                (
                    name,
                    ce.Progress,
                    ce.State,
                    ce.State == DynamicEventState.Register ?
                        ce.StartTimestamp :
                        ce.StartTimestamp - 1200,
                    leftTime
                );
            }

            public string GetNotificationTitle() => Event.Type switch
            {
                CrescentEventType.FATE      => Lang.Get("OccultCrescentHelper-CEManager-Notification-FATE"),
                CrescentEventType.MagicPot  => Lang.Get("OccultCrescentHelper-CEManager-Notification-MagicPot"),
                CrescentEventType.CE        => Lang.Get("OccultCrescentHelper-CEManager-Notification-CE"),
                CrescentEventType.ForkTower => Lang.Get("OccultCrescentHelper-CEManager-Notification-ForkTower"),
                _                           => Lang.Get("OccultCrescentHelper-CEManager-Notification-FATE")
            };

            public DalamudLinkPayload GetOrAddLinkPayload
            (
                CEManager manager
            )
            {
                if (LinkPayloadID != -1) return LinkPayload;

                LinkPayload   = LinkPayloadManager.Instance().Reg(manager.OnClickPathfind, out var id);
                LinkPayloadID = (int)id;

                return LinkPayload;
            }

            public override bool Equals
            (
                object? obj
            ) => Equals(obj as IslandEventData);

            public override int GetHashCode() => HashCode.Combine(Event);

            public static bool operator ==
            (
                IslandEventData? left,
                IslandEventData? right
            ) => Equals(left, right);

            public static bool operator !=
            (
                IslandEventData? left,
                IslandEventData? right
            ) => !Equals(left, right);
        }
    }
}
