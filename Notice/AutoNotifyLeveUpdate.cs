using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyLeveUpdate : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyLeveUpdateTitle"),
        Description = Lang.Get("AutoNotifyLeveUpdateDescription"),
        Category    = ModuleCategory.Notice,
        Author      = ["HSS"]
    };
    
    private Config config = null!;

    private DateTime nextLeveCheck = DateTime.MinValue;
    private DateTime finishTime    = StandardTimeManager.Instance().UTCNow;
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
        ImGui.TextUnformatted($"{Lang.Get("AutoNotifyLeveUpdate-NumText")}{lastLeve}");
        ImGui.TextUnformatted($"{Lang.Get("AutoNotifyLeveUpdate-FullTimeText")}{finishTime.ToLocalTime():g}");
        ImGui.TextUnformatted($"{Lang.Get("AutoNotifyLeveUpdate-UpdateTimeText")}{nextLeveCheck.ToLocalTime():g}");

        if (ImGui.Checkbox(Lang.Get("AutoNotifyLeveUpdate-OnChatMessageConfig"), ref config.OnChatMessage))
            config.Save(this);

        ImGui.SetNextItemWidth(200f * GlobalUIScale);

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

    private void OnUpdate(IFramework _)
    {
        if (!GameState.IsLoggedIn)
            return;

        var nowUTC         = StandardTimeManager.Instance().UTCNow;
        var leveAllowances = QuestManager.Instance()->NumLeveAllowances;
        if (lastLeve == leveAllowances) return;

        var decreasing = leveAllowances > lastLeve;
        lastLeve      = leveAllowances;
        nextLeveCheck = MathNextTime(nowUTC);
        finishTime    = MathFinishTime(leveAllowances, nowUTC);

        if (leveAllowances >= config.NotificationThreshold && decreasing)
        {
            var message = $"{Lang.Get("AutoNotifyLeveUpdate-NotificationTitle")}\n"                        +
                          $"{Lang.Get("AutoNotifyLeveUpdate-NumText")}{leveAllowances}\n"                  +
                          $"{Lang.Get("AutoNotifyLeveUpdate-FullTimeText")}{finishTime.ToLocalTime():g}\n" +
                          $"{Lang.Get("AutoNotifyLeveUpdate-UpdateTimeText")}{nextLeveCheck.ToLocalTime():g}";

            if (config.OnChatMessage)
                NotifyHelper.Instance().Chat(message);
            NotifyHelper.Instance().NotificationInfo(message);
        }
    }

    private static DateTime MathNextTime(DateTime nowUTC) =>
        nowUTC.AddHours(nowUTC.Hour >= 12 ? 24 - nowUTC.Hour : 12 - nowUTC.Hour).Date;

    private static DateTime MathFinishTime(int num, DateTime nowUTC)
    {
        if (num >= 100)
            return nowUTC;

        var requiredPeriods      = (100 - num + 2) / 3;
        var lastIncrementTimeUTC = new DateTime(nowUTC.Year, nowUTC.Month, nowUTC.Day, nowUTC.Hour >= 12 ? 12 : 0, 0, 0, DateTimeKind.Utc);
        return lastIncrementTimeUTC.AddHours(12 * requiredPeriods);
    }

    private class Config : ModuleConfig
    {
        public int  NotificationThreshold = 97;
        public bool OnChatMessage         = true;
    }
}
