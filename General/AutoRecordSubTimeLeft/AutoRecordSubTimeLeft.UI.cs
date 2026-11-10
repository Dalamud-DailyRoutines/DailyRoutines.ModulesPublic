using OmenTools.ImGuiOm.Widgets;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoRecordSubTimeLeft
{
    protected override void ConfigUI()
    {
        EnsureQueryState();

        DrawSubscriptionInfo(LocalPlayerState.ContentID);

        ImGui.NewLine();

        DrawPlaytimeStatistics();
    }

    private void DrawSubscriptionInfo
    (
        ulong contentID
    )
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "角色信息");

        using var indent = ImRaii.PushIndent();

        if (contentID == 0                                     ||
            !config.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                   ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "暂无可用信息, 请先登录至任一角色");
            return;
        }

        using var table = ImRaii.Table("AutoRecordSubTimeLeft-Subscription", 2, ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        DrawKeyValueRow("上次记录", info.Record.ToString("yyyy/MM/dd HH:mm:ss"));
        DrawKeyValueRow
        (
            "月卡剩余时间",
            FormatTimeSpan
            (
                info.LeftMonth == TimeSpan.MinValue ?
                    TimeSpan.Zero :
                    info.LeftMonth
            )
        );
        DrawKeyValueRow
        (
            "点卡剩余时间",
            FormatTimeSpan
            (
                info.LeftTime == TimeSpan.MinValue ?
                    TimeSpan.Zero :
                    info.LeftTime
            )
        );
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
                    stats.LongestDay == null ?
                        "暂无数据" :
                        $"{stats.LongestDay.Date:yyyy/MM/dd} ({FormatTimeSpan(stats.LongestDay.Duration)})"
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

    private static void DrawDatePickerButton
    (
        string       label,
        string       popupID,
        ref DateTime value,
        DatePicker   picker
    )
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

    private static void DrawKeyValueRow
    (
        string key,
        string value
    )
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(key);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }
}
