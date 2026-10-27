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

    private bool                  autoPilotOn;
    private bool                  autoPilotDeparted;
    private int                   autoPilotIndex;
    private string                departTimeInput = string.Empty;
    private DateTime              departAt;
    private double                timeCalibOffsetSec;
    private bool                  timeCalibrated;
    private NotoriousMonsterList? autoPilotTrain;
    private bool                  autoPilotFirstPointPending;
    private DateTime              autoPilotFirstPointAt;
    private bool                  autoPilotNextPending;
    private DateTime              autoPilotNextAt;
    private bool                  autoPilotEndPending;
    private DateTime              autoPilotEndAt;

    private const int AUTO_PILOT_FIRST_POINT_DELAY_SEC = 3;
    private const int AUTO_PILOT_NEXT_DELAY_SEC        = 1;
    private const int AUTO_PILOT_END_DELAY_SEC         = 2;

    private void ToggleAutoPilot()
    {
        if (autoPilotOn)
        {
            StopAutoPilot();
            return;
        }

        var train = SelectedTrain;
        if (train == null || train.MonstersFound.Count == 0) return;

        var hasDepartTime = !string.IsNullOrWhiteSpace(departTimeInput);
        if (hasDepartTime && !TryParseDepartTime(out departAt))
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-AutoPilotInvalidDepartTime"), Lang.Get("AutoMarksFinder-AutoPilot"));
            return;
        }

        autoPilotOn                 = true;
        autoPilotIndex              = 0;
        autoPilotTrain              = train;
        autoPilotNextPending        = false;
        autoPilotNextAt             = default;
        autoPilotEndPending         = false;
        autoPilotEndAt              = default;

        if (hasDepartTime)
        {
            autoPilotDeparted          = false;
            autoPilotFirstPointPending = false;
            autoPilotFirstPointAt      = default;
        }
        else
        {
            autoPilotDeparted          = true;
            autoPilotFirstPointPending = true;
            autoPilotFirstPointAt      = CalibratedNow.AddSeconds(AUTO_PILOT_FIRST_POINT_DELAY_SEC);
            RelayText(config.AutoPilotStartMacroPattern);
        }

        NotifyHelper.Instance().NotificationInfo
        (
            Lang.Get("AutoMarksFinder-AutoPilotAHNotice"),
            Lang.Get("AutoMarksFinder-AutoPilot")
        );
    }

    private void StopAutoPilot()
    {
        autoPilotOn                  = false;
        autoPilotDeparted            = false;
        autoPilotIndex               = 0;
        autoPilotTrain               = null;
        autoPilotFirstPointPending   = false;
        autoPilotFirstPointAt        = default;
        autoPilotNextPending         = false;
        autoPilotNextAt              = default;
        autoPilotEndPending          = false;
        autoPilotEndAt               = default;
    }

    private void ResetAutoPilot() => StopAutoPilot();

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

    private void UpdateAutoPilot()
    {
        if (!autoPilotOn) return;

        if (IObjectTable.Instance().LocalPlayer == null ||
            !UIModule.IsScreenReady() ||
            ICondition.Instance().IsBetweenAreas)
            return;

        if (autoPilotTrain is not { MonstersFound.Count: > 0 } train ||
            !config.Trains.Any(x => ReferenceEquals(x, train)) ||
            !ReferenceEquals(SelectedTrain, train))
        {
            StopAutoPilot();
            return;
        }

        if (!autoPilotDeparted)
        {
            if (CalibratedNow < departAt)
                return;

            autoPilotDeparted          = true;
            autoPilotIndex             = 0;
            autoPilotFirstPointPending = true;
            autoPilotFirstPointAt      = CalibratedNow.AddSeconds(AUTO_PILOT_FIRST_POINT_DELAY_SEC);
            RelayText(config.AutoPilotStartMacroPattern);
            return;
        }

        if (autoPilotFirstPointPending)
        {
            if (CalibratedNow < autoPilotFirstPointAt)
                return;

            while (autoPilotIndex < train.MonstersFound.Count && !train.MonstersFound[autoPilotIndex].IsAlive)
                autoPilotIndex++;

            if (autoPilotIndex >= train.MonstersFound.Count)
            {
                autoPilotFirstPointPending = false;
                autoPilotEndPending        = true;
                autoPilotEndAt             = CalibratedNow.AddSeconds(AUTO_PILOT_END_DELAY_SEC);
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoPilotIndex]))
                autoPilotFirstPointPending = false;
            return;
        }

        if (autoPilotNextPending)
        {
            if (CalibratedNow < autoPilotNextAt)
                return;

            while (autoPilotIndex < train.MonstersFound.Count && !train.MonstersFound[autoPilotIndex].IsAlive)
                autoPilotIndex++;

            if (autoPilotIndex >= train.MonstersFound.Count)
            {
                autoPilotNextPending = false;
                autoPilotEndPending  = true;
                autoPilotEndAt       = CalibratedNow.AddSeconds(AUTO_PILOT_END_DELAY_SEC);
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoPilotIndex]))
                autoPilotNextPending = false;
            return;
        }

        if (autoPilotEndPending)
        {
            if (CalibratedNow < autoPilotEndAt)
                return;

            autoPilotEndPending = false;
            RelayText(config.AutoPilotEndMacroPattern);
            StopAutoPilot();
            return;
        }

        if (autoPilotIndex < 0 || autoPilotIndex >= train.MonstersFound.Count)
        {
            StopAutoPilot();
            return;
        }

        if (train.MonstersFound[autoPilotIndex].IsAlive)
            return;

        autoPilotIndex++;
        while (autoPilotIndex < train.MonstersFound.Count &&
               !train.MonstersFound[autoPilotIndex].IsAlive)
            autoPilotIndex++;

        if (autoPilotIndex >= train.MonstersFound.Count)
        {
            autoPilotEndPending = true;
            autoPilotEndAt      = CalibratedNow.AddSeconds(AUTO_PILOT_END_DELAY_SEC);
            return;
        }

        autoPilotNextPending = true;
        autoPilotNextAt      = CalibratedNow.AddSeconds(AUTO_PILOT_NEXT_DELAY_SEC);
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
