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
    private long                  autoBroadcastFirstPointAt;
    private bool                  autoBroadcastNextPending;
    private long                  autoBroadcastNextAt;
    private bool                  autoBroadcastEndPending;
    private long                  autoBroadcastEndAt;

    private void ToggleAutoBroadcast()
    {
        if (autoBroadcastOn)
        {
            StopAutoBroadcast();
            return;
        }

        var train = SelectedTrain;
        if (train == null ||
            train.MonstersFound.Count == 0)
            return;

        var hasDepartTime = !string.IsNullOrWhiteSpace(departTimeInput);
        if (hasDepartTime &&
            !TryParseDepartTime(out departAt))
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
            autoBroadcastFirstPointAt      = Environment.TickCount64 + AUTO_BROADCAST_FIRST_POINT_DELAY_MS;
            ExecuteBroadcastMacro(config.AutoBroadcastStartMacro);
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

    private bool TryParseDepartTime(out DateTime departAt)
    {
        departAt = default;

        if (!TimeSpan.TryParseExact(departTimeInput.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timeOfDay) ||
            timeOfDay < TimeSpan.Zero ||
            timeOfDay >= TimeSpan.FromDays(1))
            return false;

        var now = CalibratedNow;
        departAt = now.Date + timeOfDay;

        if (departAt <= now)
            departAt = departAt.AddDays(1);

        return true;
    }

    private void UpdateAutoBroadcast
    (
        IFramework _
    )
    {
        if (!autoBroadcastOn)
            return;

        if (LocalPlayerState.Object == null ||
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
            autoBroadcastFirstPointAt      = Environment.TickCount64 + AUTO_BROADCAST_FIRST_POINT_DELAY_MS;
            ExecuteBroadcastMacro(config.AutoBroadcastStartMacro);
            return;
        }

        if (autoBroadcastFirstPointPending)
        {
            if (Environment.TickCount64 < autoBroadcastFirstPointAt)
                return;

            while (autoBroadcastIndex < train.MonstersFound.Count &&
                   !train.MonstersFound[autoBroadcastIndex].IsAlive)
                autoBroadcastIndex++;

            if (autoBroadcastIndex >= train.MonstersFound.Count)
            {
                autoBroadcastFirstPointPending = false;
                autoBroadcastEndPending        = true;
                autoBroadcastEndAt             = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoBroadcastIndex]))
                autoBroadcastFirstPointPending = false;
            return;
        }

        if (autoBroadcastNextPending)
        {
            if (Environment.TickCount64 < autoBroadcastNextAt)
                return;

            while (autoBroadcastIndex < train.MonstersFound.Count &&
                   !train.MonstersFound[autoBroadcastIndex].IsAlive)
                autoBroadcastIndex++;

            if (autoBroadcastIndex >= train.MonstersFound.Count)
            {
                autoBroadcastNextPending = false;
                autoBroadcastEndPending  = true;
                autoBroadcastEndAt       = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
                return;
            }

            if (AnnouncePosition(train.MonstersFound[autoBroadcastIndex]))
                autoBroadcastNextPending = false;
            return;
        }

        if (autoBroadcastEndPending)
        {
            if (Environment.TickCount64 < autoBroadcastEndAt)
                return;

            autoBroadcastEndPending = false;
            ExecuteBroadcastMacro(config.AutoBroadcastEndMacro);
            StopAutoBroadcast();
            return;
        }

        if (autoBroadcastIndex < 0 ||
            autoBroadcastIndex >= train.MonstersFound.Count)
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
            autoBroadcastEndAt      = Environment.TickCount64 + AUTO_BROADCAST_END_DELAY_MS;
            return;
        }

        autoBroadcastNextPending = true;
        autoBroadcastNextAt      = Environment.TickCount64 + AUTO_BROADCAST_NEXT_DELAY_MS;
    }

    private static void ExecuteBroadcastMacro
    (
        string macro
    )
    {
        if (string.IsNullOrWhiteSpace(macro))
            return;

        var lines = macro.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 ||
            lines.Length > BROADCAST_MACRO_MAX_LINES)
            return;

        ChatManager.Instance().ExecuteMacro(lines);
    }

    private unsafe bool AnnouncePosition
    (
        NotoriousMonsterFound info
    )
    {
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return false;

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

    #region 常量

    private const uint AUTO_BROADCAST_TICK_MS = 500;

    private const long AUTO_BROADCAST_FIRST_POINT_DELAY_MS = 3_000;
    private const long AUTO_BROADCAST_NEXT_DELAY_MS        = 1_000;
    private const long AUTO_BROADCAST_END_DELAY_MS         = 2_000;
    private const int  BROADCAST_MACRO_MAX_LINES           = 15;

    #endregion
}
