using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;
using TimeAgo;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyLeveUpdate : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyLeveUpdateTitle"),
        Description = Lang.Get("AutoNotifyLeveUpdateDescription"),
        Category    = ModuleCategory.Notification,
        Author      = ["HSS"]
    };

    private Config config = null!;

    private DateTime finishTime = StandardTimeManager.Instance().UTCNow;
    private int      lastLeve;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        FrameworkManager.Instance().Reg(OnUpdate, 60_000);
    }

    protected override void Uninit() =>
        FrameworkManager.Instance().Unreg(OnUpdate);

    protected override void ConfigUI()
    {
        if (ImGui.SliderInt
            (
                Lang.Get("AutoNotifyLeveUpdate-NotificationThreshold"),
                ref config.NotificationThreshold,
                1,
                100
            ))
        {
            lastLeve = 0;
            config.Save(this);
        }
    }

    private void OnUpdate
    (
        IFramework _
    )
    {
        if (!GameState.IsLoggedIn ||
            GameState.ContentFinderCondition != 0)
            return;

        var nowUTC         = StandardTimeManager.Instance().UTCNow;
        var leveAllowances = QuestManager.Instance()->NumLeveAllowances;
        if (lastLeve == leveAllowances)
            return;

        var decreasing = leveAllowances > lastLeve;
        lastLeve   = leveAllowances;
        finishTime = GetFinishTime(leveAllowances, nowUTC);

        if (leveAllowances < config.NotificationThreshold || !decreasing)
            return;

        NotifyHelper.Instance().Chat
        (
            Lang.Get
            (
                "AutoNotifyLeveUpdate-Notification",
                new Dictionary<string, object>
                {
                    ["count"]            = leveAllowances,
                    ["date"]             = finishTime.ToLocalTime(),
                    ["relativeTimeText"] = finishTime.TimeAgo()
                }
            )
        );
    }

    private static DateTime GetFinishTime
    (
        int      num,
        DateTime nowUTC
    )
    {
        if (num >= 100)
            return nowUTC;

        var requiredPeriods = (100 - num + 2) / 3;
        var lastIncrementTimeUTC = new DateTime
        (
            nowUTC.Year,
            nowUTC.Month,
            nowUTC.Day,
            nowUTC.Hour >= 12 ?
                12 :
                0,
            0,
            0,
            DateTimeKind.Utc
        );
        return lastIncrementTimeUTC.AddHours(12 * requiredPeriods);
    }

    private class Config : ModuleConfig
    {
        public int NotificationThreshold = 97;
    }
}
