using System.Globalization;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private NotoriousMonsterList? SelectedTrain =>
        selectedTrainIndex >= 0 && selectedTrainIndex < config.Trains.Count
            ? config.Trains[selectedTrainIndex]
            : null;

    private DateTime CalibratedNow =>
        timeCalibrated ?
            DateTime.Now.AddSeconds(-timeCalibOffsetSec) :
            DateTime.Now;

    private bool                  autoBroadcastOn;
    private bool                  autoBroadcastDeparted;
    private int                   autoBroadcastIndex;
    private string                departTimeInput = string.Empty;
    private DateTime              departAt;
    private double                timeCalibOffsetSec;
    private bool                  timeCalibrated;
    private NotoriousMonsterList? autoBroadcastTrain;
    private bool                  autoBroadcastFirstPointPending;
    private DateTime              autoBroadcastFirstPointAt;
    private bool                  autoBroadcastNextPending;
    private DateTime              autoBroadcastNextAt;
    private bool                  autoBroadcastEndPending;
    private DateTime              autoBroadcastEndAt;

    private const int AUTO_BROADCAST_FIRST_POINT_DELAY_SEC = 3;
    private const int AUTO_BROADCAST_NEXT_DELAY_SEC        = 1;
    private const int AUTO_BROADCAST_END_DELAY_SEC         = 2;

    private void ToggleAutoBroadcast()
    {
        if (autoBroadcastOn)
        {
            StopAutoBroadcast();
            return;
        }

        var train = SelectedTrain;
        if (train == null || train.MonstersFound.Count == 0) return;

        var hasDepartTime = !string.IsNullOrWhiteSpace(departTimeInput);
        if (hasDepartTime && !TryParseDepartTime(out departAt))
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-AutoBroadcastInvalidDepartTime"), Lang.Get("AutoMarksFinder-AutoBroadcast"));
            return;
        }

        autoBroadcastOn                 = true;
        autoBroadcastIndex              = 0;
        autoBroadcastTrain              = train;
        autoBroadcastNextPending        = false;
        autoBroadcastNextAt             = default;
        autoBroadcastEndPending         = false;
        autoBroadcastEndAt              = default;

        if (hasDepartTime)
        {
            autoBroadcastDeparted          = false;
            autoBroadcastFirstPointPending = false;
            autoBroadcastFirstPointAt      = default;
        }
        else
        {
            autoBroadcastDeparted          = true;
            autoBroadcastFirstPointPending = true;
            autoBroadcastFirstPointAt      = CalibratedNow.AddSeconds(AUTO_BROADCAST_FIRST_POINT_DELAY_SEC);
            RelayText(config.AutoBroadcastStartMacroPattern);
        }

        NotifyHelper.Instance().NotificationInfo
        (
            Lang.Get("AutoMarksFinder-AutoBroadcastNotice"),
            Lang.Get("AutoMarksFinder-AutoBroadcast")
        );
    }

    private void StopAutoBroadcast()
    {
        autoBroadcastOn                  = false;
        autoBroadcastDeparted            = false;
        autoBroadcastIndex               = 0;
        autoBroadcastTrain               = null;
        autoBroadcastFirstPointPending   = false;
        autoBroadcastFirstPointAt        = default;
        autoBroadcastNextPending         = false;
        autoBroadcastNextAt              = default;
        autoBroadcastEndPending          = false;
        autoBroadcastEndAt               = default;
    }

    private void ResetAutoBroadcast() => StopAutoBroadcast();

    private bool TryParseDepartTime(out DateTime departAt)
    {
        departAt = default;

        if (!TimeSpan.TryParseExact(departTimeInput.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timeOfDay) ||
            timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1))
            return false;

        var now = CalibratedNow;
        departAt = now.Date + timeOfDay;

        if (departAt <= now)
            departAt = departAt.AddDays(1);

        return true;
    }

    private void UpdateAutoBroadcast()
    {
        if (!autoBroadcastOn) return;

        if (IObjectTable.Instance().LocalPlayer == null ||
            !UIModule.IsScreenReady() ||
            ICondition.Instance().IsBetweenAreas)
            return;

        if (autoBroadcastTrain is not { MonstersFound.Count: > 0 } train ||
            !config.Trains.Any(x => ReferenceEquals(x, train)) ||
            !ReferenceEquals(SelectedTrain, train))
        {
            StopAutoBroadcast();
            return;
        }

        if (!autoBroadcastDeparted)
        {
            if (CalibratedNow < departAt)
                return;

            autoBroadcastDeparted          = true;
            autoBroadcastIndex             = 0;
            autoBroadcastFirstPointPending = true;
            autoBroadcastFirstPointAt      = CalibratedNow.AddSeconds(AUTO_BROADCAST_FIRST_POINT_DELAY_SEC);
            RelayText(config.AutoBroadcastStartMacroPattern);
            return;
        }

        if (autoBroadcastFirstPointPending)
        {
            if (CalibratedNow < autoBroadcastFirstPointAt)
                return;

            while (autoBroadcastIndex < train.MonstersFound.Count && !train.MonstersFound[autoBroadcastIndex].IsAlive)
                autoBroadcastIndex++;

            if (autoBroadcastIndex >= train.MonstersFound.Count)
            {
                autoBroadcastFirstPointPending = false;
                autoBroadcastEndPending        = true;
                autoBroadcastEndAt             = CalibratedNow.AddSeconds(AUTO_BROADCAST_END_DELAY_SEC);
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoBroadcastIndex]))
                autoBroadcastFirstPointPending = false;
            return;
        }

        if (autoBroadcastNextPending)
        {
            if (CalibratedNow < autoBroadcastNextAt)
                return;

            while (autoBroadcastIndex < train.MonstersFound.Count && !train.MonstersFound[autoBroadcastIndex].IsAlive)
                autoBroadcastIndex++;

            if (autoBroadcastIndex >= train.MonstersFound.Count)
            {
                autoBroadcastNextPending = false;
                autoBroadcastEndPending  = true;
                autoBroadcastEndAt       = CalibratedNow.AddSeconds(AUTO_BROADCAST_END_DELAY_SEC);
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoBroadcastIndex]))
                autoBroadcastNextPending = false;
            return;
        }

        if (autoBroadcastEndPending)
        {
            if (CalibratedNow < autoBroadcastEndAt)
                return;

            autoBroadcastEndPending = false;
            RelayText(config.AutoBroadcastEndMacroPattern);
            StopAutoBroadcast();
            return;
        }

        if (autoBroadcastIndex < 0 || autoBroadcastIndex >= train.MonstersFound.Count)
        {
            StopAutoBroadcast();
            return;
        }

        if (train.MonstersFound[autoBroadcastIndex].IsAlive)
            return;

        autoBroadcastIndex++;
        while (autoBroadcastIndex < train.MonstersFound.Count &&
               !train.MonstersFound[autoBroadcastIndex].IsAlive)
            autoBroadcastIndex++;

        if (autoBroadcastIndex >= train.MonstersFound.Count)
        {
            autoBroadcastEndPending = true;
            autoBroadcastEndAt      = CalibratedNow.AddSeconds(AUTO_BROADCAST_END_DELAY_SEC);
            return;
        }

        autoBroadcastNextPending = true;
        autoBroadcastNextAt      = CalibratedNow.AddSeconds(AUTO_BROADCAST_NEXT_DELAY_SEC);
    }

    private void RelayText
    (
        string text
    )
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var command in config.RelayCommands)
            ChatManager.Instance().SendMessage($"{command} {text}");
    }

    private unsafe bool AnnouncePosition
    (
        NotoriousMonsterFound info
    )
    {
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return false;

        agentMap->SetFlagMapMarker(info.ZoneID, info.GetMapID(), info.Position);
        agentMap->OpenMap(info.GetMapID(), info.ZoneID, info.GetBNPCName());

        foreach (var command in config.RelayCommands)
        {
            var formatted = FormatPattern
            (
                config.RelayMacroPattern,
                "<flag>",
                info.GetBNPCName(),
                info.Instance == 0 ?
                    null :
                    $"{Lang.Get("AutoMarksFinder-RelayInstanceDisplay", info.Instance.ToSESquareCount())}"
            );
            ChatManager.Instance().SendMessage($"{command} {formatted}");
        }

        return true;
    }
}
