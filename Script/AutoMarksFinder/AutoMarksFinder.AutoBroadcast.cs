using System.Globalization;
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
        }

        public string DepartTimeInput = string.Empty;

        public long? DepartAt  { get; private set; }
        public int   Index     { get; private set; }
        public bool  IsRunning => stage != BroadcastStage.Stopped;

        public string Status => stage switch
        {
            BroadcastStage.WaitingForDeparture => Lang.Get
            (
                "AutoMarksFinder-AutoBroadcastRemain",
                new Dictionary<string, object>
                {
                    ["remainingTimeText"] = TimeSpan.FromSeconds(Math.Max(0, DepartAt.GetValueOrDefault() - Framework.GetServerTime())).ToString(@"hh\:mm\:ss")
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

        public void ApplyDeparture(bool now = false)
        {
            if (IsRunning)
                return;

            var serverNow = DateTimeOffset.FromUnixTimeSeconds(Framework.GetServerTime());

            if (now)
            {
                DepartAt        = serverNow.ToUnixTimeSeconds();
                DepartTimeInput = serverNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                return;
            }

            if (!TimeOnly.TryParseExact(DepartTimeInput.Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-AutoBroadcastInvalidDepartTime"), Lang.Get("AutoMarksFinder-AutoBroadcast"));
                return;
            }

            var departure = new DateTimeOffset(serverNow.Date + time.ToTimeSpan(), TimeSpan.Zero);
            if (departure < serverNow)
                departure = departure.AddDays(1);

            DepartAt = departure.ToUnixTimeSeconds();
        }

        public void InvalidateDeparture() => DepartAt = null;

        public void Start()
        {
            if (IsRunning                                                         ||
                DepartAt == null                                                  ||
                module.SelectedTrain is not { MonstersFound.Count: > 0 } selected ||
                module.config.RelayCommands.Count == 0                            ||
                string.IsNullOrWhiteSpace(module.config.RelayPositionPattern)     ||
                module.scanner?.IsRunning == true                                 ||
                module.relay.IsBusy)
                return;

            train = selected;
            Index = 0;
            stage = BroadcastStage.WaitingForDeparture;
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
                    if (now < DepartAt)
                        return;

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
