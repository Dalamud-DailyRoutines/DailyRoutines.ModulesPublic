using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private NotoriousMonsterList? SelectedTrain =>
        selectedTrainIndex >= 0 && selectedTrainIndex < config.Trains.Count ?
            config.Trains[selectedTrainIndex] :
            null;

    private DateTime CalibratedNow =>
        autoBroadcast.TimeCalibrated ?
            DateTime.Now.AddSeconds(-autoBroadcast.TimeCalibOffsetSec) :
            DateTime.Now;

    private readonly AutoBroadcastSession autoBroadcast = new();

    private void ToggleAutoBroadcast()
    {
        if (autoBroadcast.On)
        {
            StopAutoBroadcast();
            return;
        }

        var train = SelectedTrain;
        if (train                     == null ||
            train.MonstersFound.Count == 0)
            return;

        var      hasDepartTime = !string.IsNullOrWhiteSpace(autoBroadcast.DepartTimeInput);
        DateTime parsed        = default;

        if (hasDepartTime &&
            !TryParseDepartTime(out parsed))
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-AutoBroadcastInvalidDepartTime"), Lang.Get("AutoMarksFinder-AutoBroadcast"));
            return;
        }

        relayTaskHelper.Abort();
        autoBroadcast.On          = true;
        autoBroadcast.Index       = 0;
        autoBroadcast.Train       = train;
        autoBroadcast.NextPending = false;
        autoBroadcast.NextAt      = 0;
        autoBroadcast.EndPending  = false;
        autoBroadcast.EndAt       = 0;

        if (hasDepartTime)
        {
            autoBroadcast.Departed          = false;
            autoBroadcast.FirstPointPending = false;
            autoBroadcast.FirstPointAt      = 0;
            autoBroadcast.DepartAt          = parsed;
        }
        else
        {
            autoBroadcast.Departed          = true;
            autoBroadcast.FirstPointPending = true;
            autoBroadcast.FirstPointAt      = Environment.TickCount64 + AUTO_BROADCAST_FIRST_POINT_DELAY_MS;

            if (!Relay(RelayType.BeforeStart))
            {
                StopAutoBroadcast();
                return;
            }
        }
    }

    private void StopAutoBroadcast()
    {
        relayTaskHelper?.Abort();
        autoBroadcast.On                = false;
        autoBroadcast.Departed          = false;
        autoBroadcast.Index             = 0;
        autoBroadcast.Train             = null;
        autoBroadcast.FirstPointPending = false;
        autoBroadcast.FirstPointAt      = 0;
        autoBroadcast.NextPending       = false;
        autoBroadcast.NextAt            = 0;
        autoBroadcast.EndPending        = false;
        autoBroadcast.EndAt             = 0;
    }

    private bool TryParseDepartTime
    (
        out DateTime parsed
    )
    {
        parsed = default;

        if (!TimeSpan.TryParseExact(autoBroadcast.DepartTimeInput.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timeOfDay) ||
            timeOfDay < TimeSpan.Zero                                                                                                     ||
            timeOfDay >= TimeSpan.FromDays(1))
            return false;

        var now = CalibratedNow;
        parsed = now.Date + timeOfDay;

        if (parsed <= now)
            parsed = parsed.AddDays(1);

        return true;
    }

    private void UpdateAutoBroadcast
    (
        IFramework _
    )
    {
        if (!autoBroadcast.On)
            return;

        if (LocalPlayerState.Object == null ||
            !UIModule.IsScreenReady()       ||
            ICondition.Instance().IsBetweenAreas)
            return;

        if (autoBroadcast.Train is not { MonstersFound.Count: > 0 } train ||
            !config.Trains.Any(x => ReferenceEquals(x, train))            ||
            !ReferenceEquals(SelectedTrain, train))
        {
            StopAutoBroadcast();
            return;
        }

        if (relayTaskHelper.IsBusy)
            return;

        if (!autoBroadcast.Departed)
        {
            if (CalibratedNow < autoBroadcast.DepartAt)
                return;

            autoBroadcast.Departed          = true;
            autoBroadcast.Index             = 0;
            autoBroadcast.FirstPointPending = true;
            autoBroadcast.FirstPointAt      = Environment.TickCount64 + AUTO_BROADCAST_FIRST_POINT_DELAY_MS;
            if (!Relay(RelayType.BeforeStart))
                StopAutoBroadcast();
            return;
        }

        if (autoBroadcast.FirstPointPending)
        {
            if (Environment.TickCount64 < autoBroadcast.FirstPointAt)
                return;

            while (autoBroadcast.Index < train.MonstersFound.Count &&
                   !train.MonstersFound[autoBroadcast.Index].IsAlive)
                autoBroadcast.Index++;

            if (autoBroadcast.Index >= train.MonstersFound.Count)
            {
                autoBroadcast.FirstPointPending = false;
                autoBroadcast.EndPending        = true;
                autoBroadcast.EndAt             = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
                return;
            }

            if (Relay(RelayType.Position, train.MonstersFound[autoBroadcast.Index]))
                autoBroadcast.FirstPointPending = false;
            return;
        }

        if (autoBroadcast.NextPending)
        {
            if (Environment.TickCount64 < autoBroadcast.NextAt)
                return;

            while (autoBroadcast.Index < train.MonstersFound.Count &&
                   !train.MonstersFound[autoBroadcast.Index].IsAlive)
                autoBroadcast.Index++;

            if (autoBroadcast.Index >= train.MonstersFound.Count)
            {
                autoBroadcast.NextPending = false;
                autoBroadcast.EndPending  = true;
                autoBroadcast.EndAt       = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
                return;
            }

            if (Relay(RelayType.Position, train.MonstersFound[autoBroadcast.Index]))
                autoBroadcast.NextPending = false;
            return;
        }

        if (autoBroadcast.EndPending)
        {
            if (Environment.TickCount64 < autoBroadcast.EndAt)
                return;

            autoBroadcast.EndPending = false;
            Relay(RelayType.AfterEnd);
            relayTaskHelper.Enqueue(StopAutoBroadcast);
            return;
        }

        if (autoBroadcast.Index < 0 ||
            autoBroadcast.Index >= train.MonstersFound.Count)
        {
            StopAutoBroadcast();
            return;
        }

        if (train.MonstersFound[autoBroadcast.Index].IsAlive)
            return;

        autoBroadcast.Index++;
        while (autoBroadcast.Index < train.MonstersFound.Count &&
               !train.MonstersFound[autoBroadcast.Index].IsAlive)
            autoBroadcast.Index++;

        if (autoBroadcast.Index >= train.MonstersFound.Count)
        {
            autoBroadcast.EndPending = true;
            autoBroadcast.EndAt      = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
            return;
        }

        autoBroadcast.NextPending = true;
        autoBroadcast.NextAt      = Environment.TickCount64 + AUTO_BROADCAST_NEXT_DELAY_MS;
    }

    #region 常量

    private const uint AUTO_BROADCAST_TICK_MS = 500;

    private const long AUTO_BROADCAST_FIRST_POINT_DELAY_MS = 3_000;
    private const long AUTO_BROADCAST_NEXT_DELAY_MS        = 1_000;
    private const long AUTO_BROADCAST_END_DELAY_MS         = 2_000;

    #endregion
}
