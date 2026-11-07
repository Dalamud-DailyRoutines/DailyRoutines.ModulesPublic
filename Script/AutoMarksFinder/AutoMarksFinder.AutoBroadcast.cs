using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class HuntAutoBroadcast : IDisposable
    {
        private readonly AutoMarksFinder       module;
        private          NotoriousMonsterList? train;
        private          BroadcastStage        stage;
        private          long                  nextAt;

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

        public int  Index     { get; private set; }
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
            BroadcastStage.Starting                                  => Lang.Get("AutoMarksFinder-AutoBroadcastStarting"),
            BroadcastStage.WaitingForEnd or BroadcastStage.Finishing => Lang.Get("AutoMarksFinder-AutoBroadcastFinishing"),
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

            train = selected;
            Index = selected.MonstersFound.FindIndex(monster => monster.IsAlive);
            stage = BroadcastStage.WaitingForDeparture;

            if (Index < 0)
            {
                module.relay.Send(RelayType.AfterEnd, train);
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

            if (LocalPlayerState.Object == null      ||
                !UIModule.IsScreenReady()            ||
                ICondition.Instance().IsBetweenAreas ||
                module.relay.IsBusy)
                return;

            var now = Framework.GetServerTime();

            switch (stage)
            {
                case BroadcastStage.WaitingForDeparture:
                    Index = train.MonstersFound.FindIndex(monster => monster.IsAlive);

                    if (Index < 0)
                    {
                        if (!module.relay.Send(RelayType.AfterEnd, train))
                        {
                            Stop();
                            return;
                        }

                        stage = BroadcastStage.Finishing;
                        return;
                    }

                    if (now < DepartAt)
                        return;

                    if (train.MonstersFound.Any(monster => !monster.IsAlive))
                    {
                        nextAt = now;
                        stage  = BroadcastStage.WaitingForPoint;
                        return;
                    }

                    if (!module.relay.Send(RelayType.BeforeStart, train))
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

                    if (!module.relay.Send(RelayType.Position, train, train.MonstersFound[Index]))
                    {
                        Stop();
                        return;
                    }

                    module.selection.MonsterIndex = Index;
                    stage                         = BroadcastStage.WaitingForDeath;
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

                    Index = train.MonstersFound.FindIndex(monster => monster.IsAlive);

                    if (Index >= 0)
                    {
                        nextAt = now;
                        stage  = BroadcastStage.WaitingForPoint;
                        return;
                    }

                    if (!module.relay.Send(RelayType.AfterEnd, train))
                    {
                        Stop();
                        return;
                    }

                    stage = BroadcastStage.Finishing;
                    break;
                case BroadcastStage.Finishing:
                    Stop();
                    break;
            }
        }

        #region 常量

        private const uint UPDATE_INTERVAL_MS        = 500;
        private const int  FIRST_POINT_DELAY_SECONDS = 3;
        private const int  NEXT_POINT_DELAY_SECONDS  = 1;
        private const int  END_DELAY_SECONDS         = 2;

        #endregion
    }
}
