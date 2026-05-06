using System.Globalization;
using DailyRoutines.Common.Module.Abstractions;
using OmenTools.ImGuiOm.Widgets;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoRecordSubTimeLeft
{
    private void EnsureQueryState()
    {
        startDatePicker ??= new(CultureInfo.GetCultureInfo("zh-CN")) { DateFormat = "yyyy 年 MM 月" };
        endDatePicker   ??= new(CultureInfo.GetCultureInfo("zh-CN")) { DateFormat = "yyyy 年 MM 月" };

        if (queryEndDate != default) return;

        var today = StandardTimeManager.Instance().Now.Date;
        queryStartDate = today.AddDays(-6);
        queryEndDate   = today;
    }

    private void DrawSubscriptionInfo(ulong contentID)
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "角色信息");

        using var indent = ImRaii.PushIndent();

        if (contentID == 0                                           ||
            !config.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue)
        {
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "暂无可用信息, 请先登录至任一角色");
            return;
        }

        using var table = ImRaii.Table("AutoRecordSubTimeLeft-Subscription", 2, ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        DrawKeyValueRow("上次记录",   info.Record.ToString("yyyy/MM/dd HH:mm:ss"));
        DrawKeyValueRow("月卡剩余时间", FormatTimeSpan(info.LeftMonth == TimeSpan.MinValue ? TimeSpan.Zero : info.LeftMonth));
        DrawKeyValueRow("点卡剩余时间", FormatTimeSpan(info.LeftTime  == TimeSpan.MinValue ? TimeSpan.Zero : info.LeftTime));
    }

    private void DrawPlaytimeStatistics()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "游玩时间信息统计");

        using var indent = ImRaii.PushIndent();
        
        if (tracker == null)
        {
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "游玩时长跟踪器尚未初始化");
            return;
        }

        DrawRangePresetButtons();
        
        DrawDatePickerButton("开始日期", "AutoRecordSubTimeLeft-StartDate", ref queryStartDate, startDatePicker);
        
        ImGui.SameLine();
        DrawDatePickerButton("结束日期", "AutoRecordSubTimeLeft-EndDate", ref queryEndDate, endDatePicker);

        NormalizeQueryRange();

        var stats = tracker.QueryRange(queryStartDate, queryEndDate);

        ImGui.Spacing();

        using (var summaryTable = ImRaii.Table("AutoRecordSubTimeLeft-Summary", 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (summaryTable)
            {
                DrawKeyValueRow("查询区间",  $"{queryStartDate:yyyy/MM/dd} - {queryEndDate:yyyy/MM/dd}");
                DrawKeyValueRow("区间总时长", FormatTimeSpan(stats.Total));
                DrawKeyValueRow("活跃天数",  $"{stats.ActiveDays} 天");
                DrawKeyValueRow("日均游玩",  FormatTimeSpan(stats.AveragePerActiveDay));
                DrawKeyValueRow
                (
                    "单日最长游玩",
                    stats.LongestDay == null
                        ? "暂无数据"
                        : $"{stats.LongestDay.Date:yyyy/MM/dd} ({FormatTimeSpan(stats.LongestDay.Duration)})"
                );
            }
        }

        ImGui.Spacing();
        
        using var dayIndent = ImRaii.PushIndent();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "按天明细");

        using var detailTable = ImRaii.Table
        (
            "AutoRecordSubTimeLeft-DailyRows",
            2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new(ImGui.GetContentRegionAvail().X, 220f * GlobalUIScale)
        );

        if (!detailTable) return;

        ImGui.TableSetupColumn("日期",   ImGuiTableColumnFlags.WidthFixed, 180f * GlobalUIScale);
        ImGui.TableSetupColumn("游玩时长", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in stats.Rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Date.ToString("yyyy/MM/dd"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatTimeSpan(row.Duration));
        }
    }

    private void DrawRangePresetButtons()
    {
        if (ImGui.Button("今天"))
            ApplyPresetRange(1);

        ImGui.SameLine();
        if (ImGui.Button("近 7 天"))
            ApplyPresetRange(7);

        ImGui.SameLine();
        if (ImGui.Button("近 30 天"))
            ApplyPresetRange(30);

        ImGui.Spacing();
    }

    private void ApplyPresetRange(int days)
    {
        var today = StandardTimeManager.Instance().Now.Date;
        queryEndDate   = today;
        queryStartDate = today.AddDays(1 - days);
    }

    private static void DrawDatePickerButton(string label, string popupID, ref DateTime value, DatePicker picker)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        
        ImGui.SameLine();
        if (ImGui.Button($"{value:yyyy/MM/dd}##{popupID}"))
            ImGui.OpenPopup(popupID);

        using var popup = ImRaii.Popup(popupID, ImGuiWindowFlags.NoTitleBar);
        if (!popup) return;
        
        var tempValue = value;
        if (picker.Draw($"##{popupID}-Picker", ref tempValue))
        {
            value = tempValue.Date;
            ImGui.CloseCurrentPopup();
        }

    }

    private void NormalizeQueryRange()
    {
        queryStartDate = queryStartDate.Date;
        queryEndDate   = queryEndDate.Date;

        if (queryStartDate <= queryEndDate) return;

        (queryStartDate, queryEndDate) = (queryEndDate, queryStartDate);
    }

    private static void DrawKeyValueRow(string key, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(key);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static unsafe void UpdateCharacterSelectRemain(TimeSpan leftMonth, TimeSpan leftTime)
    {
        if (CharaSelectRemain == null) return;

        var textNode = CharaSelectRemain->GetTextNodeById(7);
        if (textNode == null) return;

        textNode->SetPositionFloat(-20, 40);
        textNode->SetText
        (
            $"月卡: {FormatTimeSpan(leftMonth == TimeSpan.MinValue ? TimeSpan.Zero : leftMonth)}\n" +
            $"点卡: {FormatTimeSpan(leftTime  == TimeSpan.MinValue ? TimeSpan.Zero : leftTime)}"
        );
    }

    private static TimeSpan NormalizeSubscriptionTime(int totalSeconds) =>
        totalSeconds <= 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(totalSeconds);

    private static DateTime UTCToLocalDateTime(long utcTicks) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();

    private static int ToDateKey(DateTime localDate) =>
        localDate.Year * 10_000 + localDate.Month * 100 + localDate.Day;

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan == TimeSpan.MinValue || timeSpan <= TimeSpan.Zero)
            return "0 秒";

        var parts = new List<string>(4);

        if (timeSpan.Days > 0)
            parts.Add($"{timeSpan.Days} 天");
        if (timeSpan.Hours > 0)
            parts.Add($"{timeSpan.Hours} 小时");
        if (timeSpan.Minutes > 0)
            parts.Add($"{timeSpan.Minutes} 分");
        if (timeSpan.Seconds > 0)
            parts.Add($"{timeSpan.Seconds} 秒");

        return parts.Count > 0 ? $"{string.Join(" ", parts)} [{timeSpan.TotalMinutes:F0} 分钟]" : "0 秒";
    }

    private sealed class Config : ModuleConfig
    {
        public Dictionary<ulong, (DateTime Record, TimeSpan LeftMonth, TimeSpan LeftTime)> Infos = [];
    }

    private sealed class PlaytimeStoreV2
    {
        public static PlaytimeStoreV2 Empty { get; } = new();

        public int Version { get; init; } = 2;

        public Dictionary<int, long> DailyTotals { get; init; } = [];

        public Dictionary<string, SessionLease> ActiveSessions { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record SessionLease
    {
        public required int  ProcessID             { get; init; }
        public required long StartUTCTicks         { get; init; }
        public required long LastHeartbeatUTCTicks { get; init; }
    }

    private sealed record PlaytimeSnapshot
    {
        public static PlaytimeSnapshot Empty { get; } = new();

        public TimeSpan Today      { get; init; }
        public TimeSpan Yesterday  { get; init; }
        public TimeSpan Last7Days  { get; init; }
        public TimeSpan Last30Days { get; init; }
        public TimeSpan Total      { get; init; }
    }

    private sealed record PlaytimeRangeStats
    {
        public static PlaytimeRangeStats Empty { get; } = new() { Rows = [] };

        public TimeSpan                        Total               { get; init; }
        public int                             ActiveDays          { get; init; }
        public TimeSpan                        AveragePerActiveDay { get; init; }
        public PlaytimeDailyRow?               LongestDay          { get; init; }
        public IReadOnlyList<PlaytimeDailyRow> Rows                { get; init; } = [];
    }

    private sealed record PlaytimeDailyRow
    (
        DateTime Date,
        TimeSpan Duration
    );

    private sealed class LegacySessionState
    {
        public DateTime? StartUTC     { get; set; }
        public DateTime  LastEventUTC { get; set; }
    }

    private readonly record struct TimeRange
    (
        DateTime StartUTC,
        DateTime EndUTC
    );
}
