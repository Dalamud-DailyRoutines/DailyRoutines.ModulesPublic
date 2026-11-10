using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoRecordSubTimeLeft
{
    private static string FormatTimeSpan
    (
        TimeSpan timeSpan,
        bool     displayTotalMinutes = true
    )
    {
        if (timeSpan < TimeSpan.FromSeconds(1))
            return "0秒";

        if (timeSpan.TotalSeconds is var totalSeconds and < 60)
            return $"{(int)totalSeconds}秒";

        var parts = new List<string>(4);
        if (timeSpan.Days > 0)
            parts.Add($"{timeSpan.Days}天");
        if (timeSpan.Hours > 0)
            parts.Add($"{timeSpan.Hours}小时");
        if (timeSpan.Minutes > 0)
            parts.Add($"{timeSpan.Minutes}分");
        if (parts.Count == 0) // 管他呢
            parts.Add($"{timeSpan.Seconds}秒");

        var text = string.Join(string.Empty, parts);
        if (displayTotalMinutes && timeSpan.TotalMinutes is var totalMinutes and >= 60)
            text += $"［{(int)totalMinutes}分钟］";

        return text;
    }

    private static unsafe (long MonthTime, long PointTime) GetLeftTimeSecond
    (
        in LobbySubscriptionInfo info
    )
    {
        var ptr = Unsafe.AsPointer(ref Unsafe.AsRef(in info));
        return (Marshal.ReadInt64((nint)ptr, 16), Marshal.ReadInt64((nint)ptr, 24));
    }

    private void EnsureQueryState()
    {
        startDatePicker ??= new(CultureInfo.GetCultureInfo("zh-CN")) { DateFormat = "yyyy 年 MM 月" };
        endDatePicker   ??= new(CultureInfo.GetCultureInfo("zh-CN")) { DateFormat = "yyyy 年 MM 月" };

        if (queryEndDate != default) return;

        var today = StandardTimeManager.Instance().Now.Date;
        queryStartDate = today.AddDays(-6);
        queryEndDate   = today;
    }


    private void ApplyPresetRange
    (
        int days
    )
    {
        var today = StandardTimeManager.Instance().Now.Date;
        queryEndDate   = today;
        queryStartDate = today.AddDays(1 - days);
    }

    private void NormalizeQueryRange()
    {
        queryStartDate = queryStartDate.Date;
        queryEndDate   = queryEndDate.Date;

        if (queryStartDate <= queryEndDate) return;

        (queryStartDate, queryEndDate) = (queryEndDate, queryStartDate);
    }

    private static unsafe void UpdateCharacterSelectRemain
    (
        TimeSpan leftMonth,
        TimeSpan leftTime
    )
    {
        if (CharaSelectRemain == null) return;

        var textNode = CharaSelectRemain->GetTextNodeById(7);
        if (textNode == null) return;

        textNode->SetPositionFloat(-20, 40);
        textNode->SetText
        (
            $"月卡：{FormatTimeSpan(leftMonth == TimeSpan.MinValue ? TimeSpan.Zero : leftMonth, false)}\n" +
            $"点卡：{FormatTimeSpan(leftTime  == TimeSpan.MinValue ? TimeSpan.Zero : leftTime)}"
        );
    }

    private static TimeSpan NormalizeSubscriptionTime
    (
        long totalSeconds
    ) =>
        totalSeconds <= 0 ?
            TimeSpan.MinValue :
            TimeSpan.FromSeconds(totalSeconds);

    private static DateTime UTCToLocalDateTime
    (
        long utcTicks
    ) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();

    private static int ToDateKey
    (
        DateTime localDate
    ) =>
        (localDate.Year * 10_000) + (localDate.Month * 100) + localDate.Day;
}
