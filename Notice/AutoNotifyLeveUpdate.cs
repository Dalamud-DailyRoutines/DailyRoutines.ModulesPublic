using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace DailyRoutines.Modules;

public unsafe class AutoNotifyLeveUpdate : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyLeveUpdateTitle"),
        Description = GetLoc("AutoNotifyLeveUpdateDescription"),
        Category    = ModuleCategories.Notice,
        Author      = ["HSS"]
    };

    private static DateTime nextLeveCheck = DateTime.MinValue;
    private static DateTime finishTime = DateTime.UtcNow;
    private static int lastLeve;
    private static Config ModuleConfig = null!;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        FrameworkManager.Register(OnUpdate, throttleMS: 60_000);
    }

    public override void ConfigUI()
    {
        ImGui.Text($"{Lang.Get("AutoNotifyLeveUpdate-NumText")}{lastLeve}");
        ImGui.Text($"{Lang.Get("AutoNotifyLeveUpdate-FullTimeText")}{finishTime.ToLocalTime():g}");
        ImGui.Text($"{Lang.Get("AutoNotifyLeveUpdate-UpdateTimeText")}{nextLeveCheck.ToLocalTime():g}");

        if (ImGui.Checkbox(Lang.Get("AutoNotifyLeveUpdate-OnChatMessageConfig"), ref ModuleConfig.OnChatMessage))
            SaveConfig(ModuleConfig);

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.SliderInt(Lang.Get("AutoNotifyLeveUpdate-NotificationThreshold"),
                            ref ModuleConfig.NotificationThreshold, 1, 100))
        {
            lastLeve = 0;
            SaveConfig(ModuleConfig);
        }
    }

    private static void OnUpdate(IFramework _)
    {
        if (!DService.ClientState.IsLoggedIn || DService.ObjectTable.LocalPlayer == null)
            return;

        var nowUtc = DateTime.UtcNow;
        var leveAllowances = QuestManager.Instance()->NumLeveAllowances;
        if (lastLeve == leveAllowances) return;

        var decreasing = leveAllowances > lastLeve;
        lastLeve = leveAllowances;
        nextLeveCheck = MathNextTime(nowUtc);
        finishTime = MathFinishTime(leveAllowances, nowUtc);

        if (leveAllowances >= ModuleConfig.NotificationThreshold && decreasing)
        {
            var message = $"{Lang.Get("AutoNotifyLeveUpdate-NotificationTitle")}\n" +
                          $"{Lang.Get("AutoNotifyLeveUpdate-NumText")}{leveAllowances}\n" +
                          $"{Lang.Get("AutoNotifyLeveUpdate-FullTimeText")}{finishTime.ToLocalTime():g}\n" +
                          $"{Lang.Get("AutoNotifyLeveUpdate-UpdateTimeText")}{nextLeveCheck.ToLocalTime():g}";

            if (ModuleConfig.OnChatMessage) 
                Chat(message);
            NotificationInfo(message);
        }
    }

    private static DateTime MathNextTime(DateTime nowUtc) =>
        nowUtc.AddHours(nowUtc.Hour >= 12 ? 24 - nowUtc.Hour : 12 - nowUtc.Hour).Date;

    private static DateTime MathFinishTime(int num, DateTime nowUtc)
    {
        if (num >= 100) return nowUtc;
        var requiredPeriods = (100 - num + 2) / 3;
        var lastIncrementTimeUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour >= 12 ? 12 : 0, 0, 0, DateTimeKind.Utc);
        return lastIncrementTimeUtc.AddHours(12 * requiredPeriods);
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool OnChatMessage = true;
        public int NotificationThreshold = 97;
    }
}
