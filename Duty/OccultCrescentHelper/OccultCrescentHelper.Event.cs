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
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    private class EventManager
    (
        OccultCrescentHelper mainModule
    ) : BaseIslandModule(mainModule)
    {
        private          HashSet<IslandEventData> allIslandEvents = [];
        private readonly HashSet<string>          knownCENames    = [];

        private ZoneIndicatorHandle? fateHandle;
        private ZoneIndicatorHandle? ceHandle;

        private TaskHelper?         ceTaskHelper;
        private PathfindingSession? pathfindingSession;

        public override void Init()
        {
            ceTaskHelper ??= new() { TimeoutMS = 180_000 };

            IClientState.Instance().TerritoryChanged += OnZoneChanged;
            if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
                OnZoneChanged(0);

            ExecuteCommandManager.Instance().RegPost(OnPostReceivedCommand);
            LogMessageManager.Instance().RegPost(OnPostReceivedMessage);
            GameState.Instance().Logout       += OnLogout;
            FrameworkManager.Instance().Reg(OnPathfindingUpdate, throttleMS: 100);
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
            IClientState.Instance().TerritoryChanged -= OnZoneChanged;
            FrameworkManager.Instance().Unreg(OnPathfindingUpdate);
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
            using var tabBar = ImRaii.TabBar("TabBar");
            if (!tabBar) return;

            using (var item = ImRaii.TabItem(Lang.Get("General")))
            {
                if (item)
                {
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Pathfind"));

                    using (ImRaii.PushIndent())
                    {
                        foreach (var ce in allIslandEvents)
                        {
                            if (!ITextureProvider.Instance().TryGetFromGameIcon(new(ce.Event.IconID), out var texture)) continue;

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

                        ImGui.Spacing();

                        if (ImGui.SmallButton($"    {Lang.Get("Stop")}    ##StopCE"))
                            StopPathfinding();

                        ImGui.Spacing();

                        if (ImGui.Checkbox
                            (
                                Lang.Get("OccultCrescentHelper-CEManager-InterruptOnMovementInput"),
                                ref MainModule.config.InterruptPathfindingOnMovementInput
                            ))
                            MainModule.config.Save(MainModule);
                    }

                    ImGui.NewLine();

                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToUInt(),
                        Lang.Get("OccultCrescentHelper-CEManager-AutoDismount")
                    );

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

                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToUInt(),
                        Lang.Get("OccultCrescentHelper-Highlight")
                    );

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

                    ImGui.TextColored
                    (
                        KnownColor.LightSkyBlue.ToVector4(),
                        Lang.Get("Command")
                    );

                    using (ImRaii.PushIndent())
                    {
                        ImGui.TextWrapped($"/pdr {COMMAND_FATE} {Lang.Get("OccultCrescentHelper-Command-PFate-Help")}");

                        ImGui.TextWrapped($"/pdr {COMMAND_CE} {Lang.Get("OccultCrescentHelper-Command-PCE-Help")}");
                    }
                }
            }

            using (var item = ImRaii.TabItem(Lang.Get("Notification")))
            {
                if (item)
                {
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OccultCrescentHelper-CEManager-NotifyEventAppears"));

                    using (ImRaii.PushIndent())
                    {
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
                }
            }
        }

        private void OnLogout() =>
            StopPathfinding();

        private void OnZoneChanged
        (
            uint zone
        )
        {
            fateHandle?.Unreg();
            fateHandle = null;

            ceHandle?.Unreg();
            ceHandle = null;

            allIslandEvents.Clear();
            knownCENames.Clear();
            
            StopPathfinding();

            if (GameState.TerritoryIntendedUse != TerritoryIntendedUse.OccultCrescent) 
                return;

            fateHandle = ZoneIndicatorRenderer.Instance().RegTemporary
            (
                () =>
                {
                    if (!MainModule.config.IsEnabledHighlightFATE || ICondition.Instance()[ConditionFlag.InCombat])
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
                            ITextureProvider.Instance().TryGetFromGameIcon(new(data.Event.IconID), out var iconTex))
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
                    if (!MainModule.config.IsEnabledHighlightCE || ICondition.Instance()[ConditionFlag.InCombat])
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
                            ITextureProvider.Instance().TryGetFromGameIcon(new(data.Event.IconID), out var iconTex))
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

        public override unsafe void OnUpdate()
        {
            var publicInstance = PublicContentOccultCrescent.GetInstance();
            if (publicInstance == null) return;

            var currentCENames = new HashSet<string>();
            var newCEData      = new List<IslandEventData>();

            // FATE
            foreach (var fate in IFateTable.Instance())
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

        private unsafe void StartPathfinding
        (
            IslandEventData data
        )
        {
            if (!CanStartPathfinding(data)                                    ||
                IObjectTable.Instance().LocalPlayer is null            ||
                ceTaskHelper is not { } taskHelper)
                return;

            StopPathfinding();
            MainModule.aetheryteModule.StopPathfinding();

            var session = new PathfindingSession(data, data.Event.Position);
            pathfindingSession = session;

            session.TravelStage = PathfindingTravelStage.RoutePlanning;
            EnqueueAetheryteRoutePlanning(taskHelper, session, PathfindingTravelStage.RoutePlanning);

            taskHelper.Enqueue
            (() =>
                {
                    if (pathfindingSession  != session ||
                        session.TravelStage != PathfindingTravelStage.RoutePlanning)
                        return true;

                    if (session.Aetheryte is not { } aetheryte)
                    {
                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                        return true;
                    }

                    if (MainModule.aetheryteModule.TryUseAetheryte(aetheryte))
                    {
                        session.TravelStage              = PathfindingTravelStage.Aetheryte;
                        session.StopAetherytePathfinding = MainModule.aetheryteModule.StopPathfinding;
                        return true;
                    }

                    session.Aetheryte   = null;
                    session.TravelStage = PathfindingTravelStage.DemiReturn;
                    return true;
                }
            );

            var demiReturnStartPosition = default(Vector3);

            taskHelper.Enqueue
            (
                () =>
                {
                    if (pathfindingSession  != session ||
                        session.TravelStage != PathfindingTravelStage.DemiReturn)
                        return true;

                    if (ICondition.Instance()[ConditionFlag.Mounted])
                    {
                        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
                        return false;
                    }

                    if (ActionManager.Instance()->GetActionStatus(ActionType.Action, DEMI_RETURN_ACTION_ID) != 0)
                        return false;

                    if (IObjectTable.Instance().LocalPlayer is not { } player) return false;

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

                    return IObjectTable.Instance().LocalPlayer is { } player &&
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

            EnqueueAetheryteRoutePlanning(taskHelper, session, PathfindingTravelStage.DemiReturn);

            taskHelper.Enqueue
            (
                () =>
                {
                    if (pathfindingSession  != session ||
                        session.TravelStage != PathfindingTravelStage.RoutePlanning)
                        return true;

                    if (session.Aetheryte is not { } aetheryte)
                    {
                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                        return true;
                    }

                    if (!Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-DemiReturn-Aetheryte") ||
                        !MainModule.aetheryteModule.TryUseAetheryte(aetheryte))
                        return false;

                    session.TravelStage              = PathfindingTravelStage.Aetheryte;
                    session.StopAetherytePathfinding = MainModule.aetheryteModule.StopPathfinding;

                    return true;
                },
                timeoutMS: 10_000,
                timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                timeoutAction: () => session.TravelStage = PathfindingTravelStage.Pathfinding
            );

            EnqueueAetheryteArrival(taskHelper, session);

            taskHelper.Enqueue
            (() =>
                {
                    if (pathfindingSession != session) return true;

                    if (IObjectTable.Instance().LocalPlayer is not { } player)
                    {
                        StopPathfinding(session);
                        return true;
                    }

                    StartNavigationPath(session, player.Position);
                    return true;
                }
            );
        }

        private void EnqueueAetheryteRoutePlanning
        (
            TaskHelper             taskHelper,
            PathfindingSession     session,
            PathfindingTravelStage expectedStage
        )
        {
            taskHelper.Enqueue
            (() =>
                {
                    if (pathfindingSession  != session ||
                        session.TravelStage != expectedStage)
                        return true;

                    if (IObjectTable.Instance().LocalPlayer is not { } player)
                    {
                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                        return true;
                    }

                    session.Aetheryte   = null;
                    session.TravelStage = PathfindingTravelStage.RoutePlanning;

                    var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource
                        (session.CancellationTokenSource.Token);
                    cancellationTokenSource.CancelAfter(AETHERYTE_ROUTE_PLANNING_TIMEOUT_MS);
                    session.AetheryteRouteCancellationTokenSource = cancellationTokenSource;

                    session.AetheryteRoutePlanningTask = FindBestAetheryteRoute
                    (
                        player.Position,
                        session.Destination,
                        cancellationTokenSource.Token
                    );

                    return true;
                }
            );

            taskHelper.Enqueue
            (
                () =>
                {
                    if (pathfindingSession  != session ||
                        session.TravelStage != PathfindingTravelStage.RoutePlanning)
                        return true;

                    if (session.AetheryteRoutePlanningTask is not { } routePlanningTask)
                    {
                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                        return true;
                    }

                    if (!routePlanningTask.IsCompleted) return false;

                    session.AetheryteRoutePlanningTask = null;
                    session.AetheryteRouteCancellationTokenSource?.Dispose();
                    session.AetheryteRouteCancellationTokenSource = null;

                    if (!routePlanningTask.IsCompletedSuccessfully)
                    {
                        if (routePlanningTask.Exception is { } exception)
                            DLog.Error("新月岛魔路路线规划失败", exception.GetBaseException());

                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                        return true;
                    }

                    session.Aetheryte = routePlanningTask.Result;
                    return true;
                },
                timeoutMS: AETHERYTE_ROUTE_PLANNING_TIMEOUT_MS,
                timeoutBehaviour: TaskAbortBehaviour.AbortCurrent,
                timeoutAction: () =>
                {
                    session.AetheryteRouteCancellationTokenSource?.Cancel();
                    session.AetheryteRouteCancellationTokenSource?.Dispose();
                    session.AetheryteRouteCancellationTokenSource = null;
                    session.AetheryteRoutePlanningTask            = null;

                    if (pathfindingSession == session)
                        session.TravelStage = PathfindingTravelStage.Pathfinding;
                }
            );
        }

        private static async Task<CrescentAetheryte?> FindBestAetheryteRoute
        (
            Vector3           origin,
            Vector3           destination,
            CancellationToken cancellationToken
        )
        {
            var aetherytes = GameState.TerritoryType == SOUTH_HORN_TERRITORY_ID ?
                                 CrescentAetheryte.SouthHornAetherytes :
                                 CrescentAetheryte.NorthHornAetherytes;

            var rankedAetherytes = aetherytes
                                   .OrderBy(x => Vector3.DistanceSquared(x.Position, destination))
                                   .ToList();

            var bestAetheryte = rankedAetherytes[0];

            if (GameState.TerritoryType == NORTH_HORN_TERRITORY_ID &&
                (rankedAetherytes[0] == CrescentAetheryte.UnhallowedHamlet ||
                 rankedAetherytes[1] == CrescentAetheryte.UnhallowedHamlet))
            {
                var secondAetheryte       = rankedAetherytes[1];
                var firstPathfindingTask  = vnavmeshIPC.PathfindCancelable
                    (bestAetheryte.Position, destination, false, cancellationToken);
                var secondPathfindingTask = vnavmeshIPC.PathfindCancelable
                    (secondAetheryte.Position, destination, false, cancellationToken);

                if (firstPathfindingTask is not null && secondPathfindingTask is not null)
                {
                    var firstPath  = await firstPathfindingTask.ConfigureAwait(false);
                    var secondPath = await secondPathfindingTask.ConfigureAwait(false);

                    if (firstPath.Count == 0)
                    {
                        if (secondPath.Count != 0)
                            bestAetheryte = secondAetheryte;
                    }
                    else if (secondPath.Count != 0)
                    {
                        var firstPathDistance = CalculatePathDistance
                            (bestAetheryte.Position, destination, firstPath);
                        var secondPathDistance = CalculatePathDistance
                            (secondAetheryte.Position, destination, secondPath);

                        if (secondPathDistance < firstPathDistance)
                            bestAetheryte = secondAetheryte;
                    }
                }
            }

            return Vector3.Distance(origin, destination) / 12f >
                   (Vector3.Distance(bestAetheryte.Position, destination) / 12f) + 10f ?
                       bestAetheryte :
                       null;
        }

        private static float CalculatePathDistance
        (
            Vector3                origin,
            Vector3                destination,
            IReadOnlyList<Vector3> path
        )
        {
            var distance = Vector3.Distance(origin, path[0]);

            for (var i = 1; i < path.Count; i++)
                distance += Vector3.Distance(path[i - 1], path[i]);

            return distance + Vector3.Distance(path[^1], destination);
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

                    if (LocalPlayerState.DistanceTo3DSquared(aetheryte.Position) > 30f * 30f) return false;

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

        private void OnPathfindingUpdate
        (
            IFramework framework
        )
        {
            if (pathfindingSession is not { } session) return;

            if (IObjectTable.Instance().LocalPlayer is not { } localPlayer)
            {
                StopPathfinding(session);
                return;
            }

            if (!allIslandEvents.Contains(session.Data))
            {
                StopPathfinding(session);
                return;
            }

            if (session.Data.Event is { Type: CrescentEventType.CE, CEState: not DynamicEventState.Register })
            {
                StopPathfinding(session);
                return;
            }

            var playerPosition = localPlayer.Position.ToVector2();
            var isFate         = session.Data.Event.Type is CrescentEventType.FATE or CrescentEventType.MagicPot;
            if (isFate && session.TravelStage == PathfindingTravelStage.Pathfinding)
            {
                if (session.FateMonsterID == 0)
                    TrySetFateMonsterTarget(session);

                if (session.FateMonsterID != 0)
                {
                    EnsureFateMonsterSelected(session.FateMonsterID, playerPosition);

                    if (IsFateMonsterInHaterList(session.FateMonsterID))
                    {
                        StopPathfinding(session);
                        return;
                    }
                }
            }

            var eventRadius = session.Data.Event.Type == CrescentEventType.CE ?
                                  25f :
                                  session.Data.Event.Radius;

            var isInsideEvent = !isFate &&
                                eventRadius > 0f &&
                                Vector2.DistanceSquared
                                (
                                    playerPosition,
                                    session.Data.Event.Position.ToVector2()
                                ) <= eventRadius * eventRadius;
            var completionDistanceSquared = PATHFINDING_COMPLETION_DISTANCE * PATHFINDING_COMPLETION_DISTANCE;
            if (session.FateMonsterID != 0)
                completionDistanceSquared += session.FateMonsterRadius * session.FateMonsterRadius;

            var isAtDestination = Vector2.DistanceSquared
                (playerPosition, session.Destination.ToVector2()) <=
                completionDistanceSquared;

            if (isInsideEvent || (isAtDestination && (!isFate || session.FateMonsterID != 0)))
            {
                CompletePathfinding(session);
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

                if (session.PathfindingTaskDestination != session.Destination)
                {
                    session.Path = [];
                    session.IsAtPathfindingDestination = false;
                    return;
                }

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
                    if (isFate)
                    {
                        session.IsAtPathfindingDestination = true;
                        return;
                    }

                    StopPathfinding(session);
                    return;
                }

                session.IsAtPathfindingDestination = false;
                if (!session.IsMovementInterrupted)
                {
                    vnavmeshIPC.SetPathfindTolerance(0.1f);
                    vnavmeshIPC.PathfindWithPath([.. session.Path], false);
                }
            }

            if (session is
                {
                    TravelStage: PathfindingTravelStage.Pathfinding,
                    IsMovementInterrupted: false,
                    PathfindingTask: null,
                    Path.Count: 0,
                    IsAtPathfindingDestination: false
                })
                StartNavigationPath(session, localPlayer.Position);

            TryMountWhenPathIsLong(session);
        }

        private static void TryMountWhenPathIsLong
        (
            PathfindingSession session
        )
        {
            if (session.TravelStage != PathfindingTravelStage.Pathfinding) return;

            if (ICondition.Instance()[ConditionFlag.Mounted] ||
                ICondition.Instance().IsOccupiedInEvent)
                return;

            if (!vnavmeshIPC.GetIsPathfindRunning() ||
                vnavmeshIPC.GetPathfindWayPoints().GetTotalDistanceSquared() <= MOUNT_MINIMUM_DISTANCE * MOUNT_MINIMUM_DISTANCE)
                return;

            if (Throttler.Shared.Throttle("OccultCrescentHelper-CEManager-Mount", 1_000))
                UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
        }

        private void TrySetFateMonsterTarget
        (
            PathfindingSession session
        )
        {
            var monster = FindFateMonster(session);
            if (monster is null) return;

            session.FateMonsterID     = monster.EntityID;
            session.FateMonsterRadius = monster.HitboxRadius;
            SetNavigationTarget(session, monster.Position);
        }

        private static IGameObject? FindFateMonster
        (
            PathfindingSession session
        ) =>
            IObjectTable.Instance()
                    .SearchObjects
                    (
                        x => x is IBattleNPC { IsTargetable: true, IsDead: false } &&
                             x.FateID == (ushort)session.Data.Event.DataID
                    )
                    .OrderBy(x => Vector3.DistanceSquared(x.Position, session.Destination))
                    .FirstOrDefault();

        private static void EnsureFateMonsterSelected
        (
            uint    entityID,
            Vector2 playerPosition
        )
        {
            if (TargetManager.Target?.EntityID == entityID) return;

            if (IObjectTable.Instance().SearchByEntityID(entityID, IObjectTable.CharactersRange) is { } monster &&
                Vector2.DistanceSquared(playerPosition, monster.Position.ToVector2()) <=
                FATE_MONSTER_TARGET_DISTANCE_SQUARED)
                TargetManager.Target = monster;
        }

        private static unsafe bool IsFateMonsterInHaterList
        (
            uint entityID
        )
        {
            var hater      = UIState.Instance()->Hater;
            var haterCount = Math.Min(hater.HaterCount, hater.Haters.Length);

            for (var i = 0; i < haterCount; i++)
            {
                if (hater.Haters[i].EntityId == entityID)
                    return true;
            }

            return false;
        }

        private void SetNavigationTarget
        (
            PathfindingSession session,
            Vector3             destination
        )
        {
            if (session.Destination == destination) return;

            session.Destination = destination;
            session.IsAtPathfindingDestination = false;
            session.Path = [];
            vnavmeshIPC.StopPathfind();

            if (session.PathfindingTask == null && !session.IsMovementInterrupted &&
                IObjectTable.Instance().LocalPlayer is { } player)
                StartNavigationPath(session, player.Position);
        }

        private void StartNavigationPath
        (
            PathfindingSession session,
            Vector3             origin
        )
        {
            if (session.PathfindingTask != null || session.IsMovementInterrupted) return;

            try
            {
                session.PathfindingTaskDestination = session.Destination;
                session.PathfindingTask = vnavmeshIPC.PathfindCancelable
                (
                    origin,
                    session.Destination,
                    false,
                    session.CancellationTokenSource.Token
                );

                if (session.PathfindingTask is null)
                    StopPathfinding(session);
            }
            catch (Exception exception)
            {
                DLog.Error("新月岛区域事件寻路请求失败", exception);
                StopPathfinding(session);
            }
        }

        private void OnPathfindingDraw()
        {
            if (pathfindingSession is not { } session) return;
            DrawPath(session.Path);
        }

        private static bool IsMovementInputPressed() =>
            IKeyState.Instance()[VirtualKey.W] ||
            IKeyState.Instance()[VirtualKey.A] ||
            IKeyState.Instance()[VirtualKey.S] ||
            IKeyState.Instance()[VirtualKey.D];

        private static void DrawPath
        (
            List<Vector3> path
        )
        {
            if (path.Count == 0) return;

            var drawList           = ImGui.GetForegroundDrawList();
            var gameGUI            = IGameGui.Instance();
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
                (ICondition.Instance()[ConditionFlag.Mounted] && !shouldDismount))
                return;

            if (ceTaskHelper is not { } taskHelper) return;

            taskHelper.Enqueue
            (
                () => !ICondition.Instance()[ConditionFlag.Mounted]
            );
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

            if (expectedSession != null && !ReferenceEquals(session, expectedSession)) 
                return false;

            ceTaskHelper?.Abort();

            if (session == null) 
                return true;
            
            vnavmeshIPC.StopPathfind();
            
            session.StopAetherytePathfinding?.Invoke();
            
            session.CancellationTokenSource.Cancel();
            session.AetheryteRouteCancellationTokenSource?.Dispose();
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
            public IslandEventData          Data                                  { get; } = data;
            public Vector3                  Destination                           { get; set; } = destination;
            public CrescentAetheryte?       Aetheryte                             { get; set; }
            public PathfindingTravelStage   TravelStage                           { get; set; }
            public Action?                  StopAetherytePathfinding              { get; set; }
            public CancellationTokenSource  CancellationTokenSource               { get; } = new();
            public CancellationTokenSource? AetheryteRouteCancellationTokenSource { get; set; }
            public Task<CrescentAetheryte?>? AetheryteRoutePlanningTask            { get; set; }
            public Task<List<Vector3>>?     PathfindingTask                       { get; set; }
            public Vector3                  PathfindingTaskDestination             { get; set; }
            public List<Vector3>            Path                                  { get; set; } = [];
            public uint                     FateMonsterID                         { get; set; }
            public float                    FateMonsterRadius                     { get; set; }
            public bool                     IsAtPathfindingDestination            { get; set; }
            public bool                     IsMovementInterrupted                 { get; set; }
        }

        private enum PathfindingTravelStage : byte
        {
            RoutePlanning,
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
                if (!CrescentEvent.ForkTowers.Contains(data.RowId) && ce.MapMarker.Position == default) return null;

                var leftTime = ce.StartTimestamp - GameState.ServerTimeUnix;
                if (leftTime < 0)
                    leftTime = 0;

                var name = ce.Name.ToString();

                if (!CrescentEvent.ForkTowers.Contains(data.RowId)) // 两歧塔
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
                EventManager manager
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
        
        #region 常量

        private const string COMMAND_FATE = "pfate";
        private const string COMMAND_CE   = "pce";

        private const int AETHERYTE_ROUTE_PLANNING_TIMEOUT_MS = 30_000;

        private const float FATE_MONSTER_TARGET_DISTANCE_SQUARED = 50f * 50f;
        private const float MOUNT_MINIMUM_DISTANCE               = 40f;
        private const float PATHFINDING_COMPLETION_DISTANCE      = 5f;
        private const float PATH_POINT_RADIUS                    = 4f;
        private const float PATH_LINE_THICKNESS                  = 2f;

        private static readonly uint PathLineColor  = KnownColor.DeepSkyBlue.ToUInt();
        private static readonly uint PathPointColor = KnownColor.LightSkyBlue.ToUInt();

        #endregion
    }
}
