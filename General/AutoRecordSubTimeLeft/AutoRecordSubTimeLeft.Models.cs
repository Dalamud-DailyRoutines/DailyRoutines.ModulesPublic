using DailyRoutines.Common.Module.Abstractions;
using OmenTools.ImGuiOm.Widgets;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoRecordSubTimeLeft
{
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
