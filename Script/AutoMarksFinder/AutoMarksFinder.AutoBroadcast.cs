using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoMarksFinder
{
    private sealed class HuntAutoBroadcast : IDisposable
    {
        private readonly AutoMarksFinder       module;
        private          NotoriousMonsterList? train;
        private          BroadcastStage        stage;
        private          long                  nextAt;
        private          bool                  departureNotified;
        private          uint                  arrivalZone;
        private          long                  arrivalAt;
        private          bool                  navigateAfterArrival;

        public HuntAutoBroadcast(AutoMarksFinder module)
        {
            this.module              =  module;
            module.selection.Changed += Stop;
            DepartAt                 =  Framework.GetServerTime();
        }

        public long DepartAt { get; private set; }

        public DateTime LocalDeparture
        {
            get => DateTimeOffset.FromUnixTimeSeconds(DepartAt).LocalDateTime;
            set => DepartAt = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)).ToUnixTimeSeconds();
        }

        public int Index { get; private set; }

        public bool IsRunning => stage != BroadcastStage.Stopped;

        public string Status => stage switch
        {
            BroadcastStage.WaitingForDeparture => Lang.Get
            (
                "AutoMarksFinder-AutoBroadcastRemain",
                new Dictionary<string, object>
                {
                    ["remainingTimeText"] = TimeSpan.FromSeconds(Math.Max(0, DepartAt - Framework.GetServerTime())).ToString("g")
                }
            ),
            BroadcastStage.Starting                                  => string.Empty,
            BroadcastStage.WaitingForEnd or BroadcastStage.Finishing => string.Empty,
            _ => Lang.Get
            (
                "AutoMarksFinder-AutoBroadcastProgress",
                new Dictionary<string, object>
                {
                    ["pointIndex"] = Math.Min(Index + 1, train?.MonstersFound.Count ?? 0)
                }
            )
        };

        public void Start()
        {
            if (IsRunning                                                         ||
                module.SelectedTrain is not { MonstersFound.Count: > 0 } selected ||
                module.config.RelayCommands.Count == 0                            ||
                (selected.MonstersFound.Any(monster => monster.IsAlive) &&
                 string.IsNullOrWhiteSpace(module.config.RelayPositionPattern)) ||
                module.scanner?.IsRunning == true                               ||
                module.relay.IsBusy)
                return;

            train             = selected;
            Index             = selected.MonstersFound.FindIndex(monster => monster.IsAlive);
            stage             = BroadcastStage.WaitingForDeparture;
            nextAt            = 0;
            departureNotified = false;

            if (Index < 0)
            {
                module.relay.Send(RelayType.AfterEnd, train, automatic: true);
                stage = BroadcastStage.Finishing;
            }

            FrameworkManager.Instance().Reg(Update, UPDATE_INTERVAL_MS);
        }

        public void Stop()
        {
            if (!IsRunning)
                return;

            FrameworkManager.Instance().Unreg(Update);
            module.relay.Cancel();
            train = null;
            stage = BroadcastStage.Stopped;
            Index = 0;
        }

        public void Dispose()
        {
            Stop();
            module.selection.Changed -= Stop;
        }

        private void Update(IFramework framework)
        {
            if (!GameState.IsLoggedIn ||
                !ReferenceEquals(module.SelectedTrain, train))
            {
                Stop();
                return;
            }

            if (LocalPlayerState.Object == null ||
                !UIModule.IsScreenReady()       ||
                ICondition.Instance().IsBetweenAreas)
            {
                arrivalAt = 0;
                return;
            }

            if (module.relay.IsBusy)
                return;

            var now = Framework.GetServerTime();

            switch (stage)
            {
                case BroadcastStage.WaitingForDeparture:
                    Index = train.MonstersFound.FindIndex(candidate => candidate.IsAlive);

                    if (Index < 0)
                    {
                        if (!module.relay.Send(RelayType.AfterEnd, train, automatic: true))
                        {
                            Stop();
                            return;
                        }

                        stage = BroadcastStage.Finishing;
                        return;
                    }

                    if (!departureNotified)
                    {
                        if (now < DepartAt - DEPARTURE_NOTICE_SECONDS)
                            return;

                        var message = Lang.Get
                        (
                            "AutoMarksFinder-AutoBroadcastStarting",
                            new Dictionary<string, object>
                            {
                                ["count"] = PlayersManager.Instance().PlayersAroundCount
                            }
                        );
                        NotifyHelper.Instance().Chat(message);
                        NotifyHelper.Instance().TrayInfo(message, Lang.Get("AutoMarksFinderTitle"));
                        NotifyHelper.Toast(message);
                        departureNotified = true;

                        if (now >= DepartAt)
                            nextAt = now + LATE_START_DELAY_SECONDS;
                    }

                    if (now < DepartAt ||
                        now < nextAt)
                        return;

                    if (train.MonstersFound.Any(monster => !monster.IsAlive))
                    {
                        nextAt = now;
                        stage  = BroadcastStage.WaitingForPoint;
                        return;
                    }

                    if (!module.relay.Send(RelayType.BeforeStart, train, automatic: true))
                    {
                        Stop();
                        return;
                    }

                    stage = BroadcastStage.Starting;
                    break;
                case BroadcastStage.Starting:
                    nextAt = now + FIRST_POINT_DELAY_SECONDS;
                    stage  = BroadcastStage.WaitingForPoint;
                    break;
                case BroadcastStage.WaitingForPoint:
                    if (now < nextAt)
                        return;

                    while (Index < train.MonstersFound.Count &&
                           !train.MonstersFound[Index].IsAlive)
                        Index++;

                    if (Index >= train.MonstersFound.Count)
                    {
                        nextAt = now + END_DELAY_SECONDS;
                        stage  = BroadcastStage.WaitingForEnd;
                        return;
                    }

                    var monster = train.MonstersFound[Index];
                    var changingArea = GameState.TerritoryType != monster.ZoneID ||
                                       (monster.Instance != 0 && InstancesManager.CurrentInstance != monster.Instance);
                    var gather = module.config.PreferAetheryteBroadcast &&
                                 changingArea;

                    if (!module.relay.Send
                        (
                            gather ?
                                RelayType.WaitAetheryte :
                                RelayType.Position,
                            train,
                            monster,
                            automatic: true,
                            arriveBeforeNavigation: changingArea
                        ))
                    {
                        Stop();
                        return;
                    }

                    module.selection.MonsterIndex = Index;
                    arrivalZone                   = 0;
                    arrivalAt                     = 0;
                    navigateAfterArrival          = changingArea;
                    stage = changingArea ?
                                BroadcastStage.WaitingForArrival :
                                BroadcastStage.WaitingForDeath;
                    break;
                case BroadcastStage.WaitingForArrival:
                    if (!train.MonstersFound[Index].IsAlive)
                    {
                        Index++;
                        nextAt = now + NEXT_POINT_DELAY_SECONDS;
                        stage  = BroadcastStage.WaitingForPoint;
                        return;
                    }

                    if (navigateAfterArrival &&
                        module.movement.IsBusy)
                    {
                        arrivalAt = 0;
                        return;
                    }

                    var destination = train.MonstersFound[Index];
                    var currentZone = GameState.TerritoryType;
                    var inMonsterArea = currentZone == destination.ZoneID &&
                                        (destination.Instance == 0 || InstancesManager.CurrentInstance == destination.Instance);
                    var inAetheryteArea = currentZone != destination.ZoneID &&
                                          currentZone == destination.NearestAetheryteRecord?.ZoneID;

                    if ((!inMonsterArea && !inAetheryteArea) ||
                        currentZone == arrivalZone)
                    {
                        arrivalAt = 0;
                        return;
                    }

                    if (arrivalAt == 0)
                        arrivalAt = Environment.TickCount64;

                    if (module.config.PreferAetheryteBroadcast &&
                        Environment.TickCount64 - arrivalAt < module.config.AreaBroadcastDelayMS)
                        return;

                    if (!module.relay.Send(RelayType.Position, train, destination, automatic: navigateAfterArrival))
                    {
                        Stop();
                        return;
                    }

                    arrivalZone          = currentZone;
                    arrivalAt            = 0;
                    navigateAfterArrival = false;
                    if (inMonsterArea)
                        stage = BroadcastStage.WaitingForDeath;
                    break;
                case BroadcastStage.WaitingForDeath:
                    if (train.MonstersFound[Index].IsAlive)
                        return;

                    Index++;
                    nextAt = now + NEXT_POINT_DELAY_SECONDS;
                    stage  = BroadcastStage.WaitingForPoint;
                    break;
                case BroadcastStage.WaitingForEnd:
                    if (now < nextAt)
                        return;

                    Index = train.MonstersFound.FindIndex(candidate => candidate.IsAlive);

                    if (Index >= 0)
                    {
                        nextAt = now;
                        stage  = BroadcastStage.WaitingForPoint;
                        return;
                    }

                    if (!module.relay.Send(RelayType.AfterEnd, train, automatic: true))
                    {
                        Stop();
                        return;
                    }

                    stage = BroadcastStage.Finishing;
                    break;
                case BroadcastStage.Finishing:
                    Stop();
                    var completedMessage = Lang.Get("AutoMarksFinder-AutoBroadcastCompleted");
                    NotifyHelper.Instance().Chat(completedMessage);
                    NotifyHelper.Instance().TrayInfo(completedMessage, Lang.Get("AutoMarksFinderTitle"));
                    NotifyHelper.Toast(completedMessage);
                    break;
            }
        }

        #region 常量

        private const uint UPDATE_INTERVAL_MS        = 500;
        private const int  FIRST_POINT_DELAY_SECONDS = 3;
        private const int  NEXT_POINT_DELAY_SECONDS  = 1;
        private const int  END_DELAY_SECONDS         = 2;
        private const int  DEPARTURE_NOTICE_SECONDS  = 30;
        private const int  LATE_START_DELAY_SECONDS  = 5;

        #endregion
    }
}
