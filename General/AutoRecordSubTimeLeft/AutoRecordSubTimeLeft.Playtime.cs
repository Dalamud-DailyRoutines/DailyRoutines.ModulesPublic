using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoRecordSubTimeLeft
{
    private sealed class PlaytimeTracker : IDisposable
    {
        private static readonly UTF8Encoding UTF8NoBOM = new(false);
        private static readonly TimeSpan SnapshotInterval  = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan StaleLeaseGrace   = TimeSpan.FromSeconds(40);

        private readonly string storePath;
        private readonly string legacyLogPath;
        private readonly Mutex  fileMutex;
        private readonly int    processID = Environment.ProcessId;
        private readonly string sessionID = Guid.NewGuid().ToString("N");

        private CancellationTokenSource? cancelSource;
        private Task?                    backgroundTask;
        private PlaytimeStoreV2          storeState = PlaytimeStoreV2.Empty;
        private PlaytimeSnapshot         snapshot   = PlaytimeSnapshot.Empty;

        private int  sessionActive;
        private long sessionStartUTCTicks;
        private long lastHeartbeatPersistedUTCTicks;

        public PlaytimeTracker(string storePath, string legacyLogPath)
        {
            this.storePath     = storePath;
            this.legacyLogPath = legacyLogPath;

            var directory = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var mutexName = $@"Global\DailyRoutines-PlaytimeTracker-{GetStableHashCode(Path.GetFullPath(storePath).ToUpperInvariant())}";
            fileMutex = new(false, mutexName);

            InitializeStore();
            RefreshSnapshot(StandardTimeManager.Instance().UTCNow);
        }

        public PlaytimeSnapshot Snapshot =>
            Volatile.Read(ref snapshot);

        public void Start()
        {
            if (cancelSource != null) return;

            cancelSource   = new();
            backgroundTask = Task.Run(() => BackgroundLoopAsync(cancelSource.Token));

            if (GameState.IsLoggedIn)
                OnLogin();
        }

        public void OnLogin() =>
            ActivateSession(StandardTimeManager.Instance().UTCNow);

        public void OnLogout() =>
            FinalizeSession(StandardTimeManager.Instance().UTCNow);

        public PlaytimeRangeStats QueryRange(DateTime startDate, DateTime endDate)
        {
            if (endDate < startDate)
                (startDate, endDate) = (endDate, startDate);

            startDate = startDate.Date;
            endDate   = endDate.Date;

            var totals = BuildEffectiveDailyTotals(StandardTimeManager.Instance().UTCNow);
            var rows   = new List<PlaytimeDailyRow>((endDate - startDate).Days + 1);

            long              totalTicks = 0;
            var               activeDays = 0;
            PlaytimeDailyRow? longestDay = null;

            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                var duration = TimeSpan.FromTicks(totals.GetValueOrDefault(ToDateKey(date)));
                var row      = new PlaytimeDailyRow(date, duration);

                rows.Add(row);
                totalTicks += duration.Ticks;

                if (duration > TimeSpan.Zero)
                    activeDays++;

                if (longestDay == null || duration > longestDay.Duration)
                    longestDay = duration > TimeSpan.Zero ? row : longestDay;
            }

            return new()
            {
                Total               = TimeSpan.FromTicks(totalTicks),
                ActiveDays          = activeDays,
                AveragePerActiveDay = activeDays == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totalTicks / activeDays),
                LongestDay          = longestDay,
                Rows                = rows
            };
        }

        public void Dispose()
        {
            cancelSource?.Cancel();

            FinalizeSession(StandardTimeManager.Instance().UTCNow);

            if (cancelSource != null)
            {
                try
                {
                    backgroundTask?.Wait(50);
                }
                catch (Exception)
                {
                    // ignored
                }

                cancelSource.Dispose();
                cancelSource = null;
            }

            fileMutex.Dispose();
        }

        private async Task BackgroundLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(SnapshotInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var nowUTC = StandardTimeManager.Instance().UTCNow;

                    if (GameState.IsLoggedIn)
                    {
                        if (Volatile.Read(ref sessionActive) == 0)
                            ActivateSession(nowUTC);

                        if (nowUTC.Ticks - Volatile.Read(ref lastHeartbeatPersistedUTCTicks) >= HeartbeatInterval.Ticks)
                            PersistHeartbeat(nowUTC);
                    }
                    else if (Volatile.Read(ref sessionActive) != 0)
                        FinalizeSession(nowUTC);
                    else if (nowUTC.Ticks - Volatile.Read(ref lastHeartbeatPersistedUTCTicks) >= HeartbeatInterval.Ticks) 
                        RefreshStore(nowUTC, true);

                    RefreshSnapshot(nowUTC);
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                DLog.Error("游玩时长后台循环发生错误", ex);
            }
        }

        private void InitializeStore()
        {
            var nowUTC = StandardTimeManager.Instance().UTCNow;
            ExecuteWithFileLock
            (() =>
                {
                    PlaytimeStoreV2 store;

                    if (!File.Exists(storePath) && File.Exists(legacyLogPath))
                    {
                        store = RecoverStaleSessions(MigrateLegacyLog(), nowUTC, out _);
                        WriteStoreSynchronously(store);
                    }
                    else
                    {
                        store = RecoverStaleSessions(LoadStoreFromDisk(), nowUTC, out var changed);
                        if (changed)
                            WriteStoreSynchronously(store);
                    }

                    PublishStore(store);
                }
            );
        }

        private void ActivateSession(DateTime nowUTC)
        {
            if (Interlocked.CompareExchange(ref sessionActive, 1, 0) != 0)
                return;

            Volatile.Write(ref sessionStartUTCTicks,           nowUTC.Ticks);
            Volatile.Write(ref lastHeartbeatPersistedUTCTicks, 0L);
            PersistHeartbeat(nowUTC);
        }

        private void PersistHeartbeat(DateTime nowUTC)
        {
            if (Volatile.Read(ref sessionActive) == 0)
                return;

            var startUTCTicks = Volatile.Read(ref sessionStartUTCTicks);
            if (startUTCTicks <= 0)
                return;

            ExecuteWithFileLock
            (() =>
                {
                    var store = RecoverStaleSessions(LoadStoreFromDisk(), nowUTC, out _);
                    var next  = CloneStore(store);

                    next.ActiveSessions[sessionID] = new()
                    {
                        ProcessID             = processID,
                        StartUTCTicks         = startUTCTicks,
                        LastHeartbeatUTCTicks = nowUTC.Ticks
                    };

                    WriteStoreSynchronously(next);
                    PublishStore(next);
                }
            );

            Volatile.Write(ref lastHeartbeatPersistedUTCTicks, nowUTC.Ticks);
        }

        private void FinalizeSession(DateTime nowUTC)
        {
            if (Interlocked.Exchange(ref sessionActive, 0) == 0)
                return;

            var localStartUTCTicks = Interlocked.Exchange(ref sessionStartUTCTicks, 0);

            if (localStartUTCTicks <= 0)
            {
                Volatile.Write(ref lastHeartbeatPersistedUTCTicks, nowUTC.Ticks);
                return;
            }

            ExecuteWithFileLock
            (() =>
                {
                    var store = RecoverStaleSessions(LoadStoreFromDisk(), nowUTC, out _);
                    var next  = CloneStore(store);

                    if (next.ActiveSessions.Remove(sessionID, out var existingLease))
                        localStartUTCTicks = Math.Min(localStartUTCTicks, existingLease.StartUTCTicks);

                    AddRangeToDailyTotals(next.DailyTotals, UTCToLocalDateTime(localStartUTCTicks), nowUTC.ToLocalTime());

                    WriteStoreSynchronously(next);
                    PublishStore(next);
                }
            );

            Volatile.Write(ref lastHeartbeatPersistedUTCTicks, nowUTC.Ticks);
            RefreshSnapshot(nowUTC);
        }

        private void RefreshStore(DateTime nowUTC, bool persistChanges)
        {
            ExecuteWithFileLock
            (() =>
                {
                    var store = RecoverStaleSessions(LoadStoreFromDisk(), nowUTC, out var changed);
                    if (changed && persistChanges)
                        WriteStoreSynchronously(store);

                    PublishStore(store);
                }
            );

            Volatile.Write(ref lastHeartbeatPersistedUTCTicks, nowUTC.Ticks);
        }

        private void RefreshSnapshot(DateTime nowUTC)
        {
            var effectiveTotals = BuildEffectiveDailyTotals(nowUTC);
            var today           = nowUTC.ToLocalTime().Date;

            long totalTicks = 0;
            foreach (var ticks in effectiveTotals.Values)
                totalTicks += ticks;

            Volatile.Write
            (
                ref snapshot,
                new()
                {
                    Today      = GetRangeDuration(effectiveTotals, today,              today),
                    Yesterday  = GetRangeDuration(effectiveTotals, today.AddDays(-1),  today.AddDays(-1)),
                    Last7Days  = GetRangeDuration(effectiveTotals, today.AddDays(-6),  today),
                    Last30Days = GetRangeDuration(effectiveTotals, today.AddDays(-29), today),
                    Total      = TimeSpan.FromTicks(totalTicks)
                }
            );
        }

        private Dictionary<int, long> BuildEffectiveDailyTotals(DateTime nowUTC)
        {
            var store  = Volatile.Read(ref storeState);
            var totals = new Dictionary<int, long>(store.DailyTotals);

            foreach (var lease in store.ActiveSessions.Values)
                AddLeaseContribution(totals, lease, nowUTC);

            if (Volatile.Read(ref sessionActive) != 0 && !store.ActiveSessions.ContainsKey(sessionID))
            {
                var startUTCTicks = Volatile.Read(ref sessionStartUTCTicks);

                if (startUTCTicks > 0)
                {
                    AddLeaseContribution
                    (
                        totals,
                        new()
                        {
                            ProcessID             = processID,
                            StartUTCTicks         = startUTCTicks,
                            LastHeartbeatUTCTicks = nowUTC.Ticks
                        },
                        nowUTC
                    );
                }
            }

            return totals;
        }

        private static void AddLeaseContribution(Dictionary<int, long> totals, SessionLease lease, DateTime nowUTC)
        {
            var leaseEndUTCTicks = nowUTC.Ticks - lease.LastHeartbeatUTCTicks > StaleLeaseGrace.Ticks
                                       ? lease.LastHeartbeatUTCTicks + StaleLeaseGrace.Ticks
                                       : nowUTC.Ticks;

            if (leaseEndUTCTicks <= lease.StartUTCTicks)
                return;

            AddRangeToDailyTotals(totals, UTCToLocalDateTime(lease.StartUTCTicks), UTCToLocalDateTime(leaseEndUTCTicks));
        }

        private static TimeSpan GetRangeDuration(IReadOnlyDictionary<int, long> totals, DateTime startDate, DateTime endDate)
        {
            if (endDate < startDate)
                (startDate, endDate) = (endDate, startDate);

            long totalTicks = 0;

            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
                totalTicks += totals.GetValueOrDefault(ToDateKey(date));

            return TimeSpan.FromTicks(totalTicks);
        }

        private static void AddRangeToDailyTotals(Dictionary<int, long> dailyTotals, DateTime startLocal, DateTime endLocal)
        {
            if (endLocal <= startLocal)
                return;

            var current = startLocal;

            while (current.Date < endLocal.Date)
            {
                var next = current.Date.AddDays(1);
                AddDurationToDay(dailyTotals, current.Date, next - current);
                current = next;
            }

            AddDurationToDay(dailyTotals, current.Date, endLocal - current);
        }

        private static void AddDurationToDay(Dictionary<int, long> dailyTotals, DateTime date, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                return;

            var key = ToDateKey(date);
            dailyTotals[key] = dailyTotals.GetValueOrDefault(key) + duration.Ticks;
        }

        private PlaytimeStoreV2 RecoverStaleSessions(PlaytimeStoreV2 store, DateTime nowUTC, out bool changed)
        {
            changed = false;

            if (store.ActiveSessions.Count == 0)
                return store;

            var next       = CloneStore(store);
            var expiredIDs = new List<string>();

            foreach (var pair in next.ActiveSessions)
            {
                var lease = pair.Value;
                if (nowUTC.Ticks - lease.LastHeartbeatUTCTicks <= StaleLeaseGrace.Ticks)
                    continue;

                var endUTCTicks = Math.Max(lease.StartUTCTicks, lease.LastHeartbeatUTCTicks + StaleLeaseGrace.Ticks);
                AddRangeToDailyTotals(next.DailyTotals, UTCToLocalDateTime(lease.StartUTCTicks), UTCToLocalDateTime(endUTCTicks));
                expiredIDs.Add(pair.Key);
            }

            if (expiredIDs.Count == 0)
                return store;

            foreach (var expiredID in expiredIDs)
                next.ActiveSessions.Remove(expiredID);

            changed = true;
            return next;
        }

        private PlaytimeStoreV2 LoadStoreFromDisk()
        {
            if (!File.Exists(storePath))
                return PlaytimeStoreV2.Empty;

            try
            {
                var json  = File.ReadAllText(storePath, Encoding.UTF8);
                var store = JsonConvert.DeserializeObject<PlaytimeStoreV2>(json, JsonSerializerSettings.GetShared());
                return NormalizeStore(store);
            }
            catch (Exception ex)
            {
                DLog.Warning("读取游玩时长快照失败, 将使用空白数据继续运行", ex);
                return PlaytimeStoreV2.Empty;
            }
        }

        private PlaytimeStoreV2 MigrateLegacyLog()
        {
            if (!File.Exists(legacyLogPath))
                return PlaytimeStoreV2.Empty;

            try
            {
                var sessions  = new Dictionary<string, LegacySessionState>(StringComparer.Ordinal);
                var intervals = new List<TimeRange>();

                foreach (var line in File.ReadLines(legacyLogPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split('\t');
                    if (parts.Length < 4)
                        continue;

                    if (!DateTime.TryParseExact(parts[3], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                        continue;

                    if (!sessions.TryGetValue(parts[0], out var state))
                    {
                        state              = new();
                        sessions[parts[0]] = state;
                    }

                    var utcTimestamp = timestamp.ToUniversalTime();

                    switch (parts[2])
                    {
                        case "start":
                            if (state.StartUTC.HasValue && utcTimestamp > state.StartUTC.Value)
                                intervals.Add(new(state.StartUTC.Value, utcTimestamp));

                            state.StartUTC = utcTimestamp;
                            break;
                        case "stop":
                        case "autoClose":
                            if (state.StartUTC.HasValue)
                            {
                                var endUTC = utcTimestamp < state.StartUTC.Value ? state.StartUTC.Value : utcTimestamp;
                                intervals.Add(new(state.StartUTC.Value, endUTC));
                                state.StartUTC = null;
                            }

                            break;
                        case "heartbeat":
                            state.StartUTC ??= utcTimestamp;
                            break;
                    }

                    state.LastEventUTC = utcTimestamp;
                }

                foreach (var state in sessions.Values)
                {
                    if (!state.StartUTC.HasValue || state.LastEventUTC == default)
                        continue;

                    var endUTC = state.LastEventUTC.Add(StaleLeaseGrace);
                    if (endUTC < state.StartUTC.Value)
                        endUTC = state.StartUTC.Value;

                    intervals.Add(new(state.StartUTC.Value, endUTC));
                }

                var merged = MergeIntervals(intervals);
                var store  = CloneStore(PlaytimeStoreV2.Empty);

                foreach (var interval in merged)
                    AddRangeToDailyTotals(store.DailyTotals, interval.StartUTC.ToLocalTime(), interval.EndUTC.ToLocalTime());

                return store;
            }
            catch (Exception ex)
            {
                DLog.Warning("迁移旧版游玩时长日志失败, 将从空白数据开始记录", ex);
                return PlaytimeStoreV2.Empty;
            }
        }

        private static List<TimeRange> MergeIntervals(List<TimeRange> intervals)
        {
            if (intervals.Count == 0)
                return [];

            intervals.Sort(static (left, right) => left.StartUTC.CompareTo(right.StartUTC));

            var merged  = new List<TimeRange>(intervals.Count);
            var current = intervals[0];

            for (var index = 1; index < intervals.Count; index++)
            {
                var next = intervals[index];

                if (next.StartUTC <= current.EndUTC)
                {
                    current = current with { EndUTC = next.EndUTC > current.EndUTC ? next.EndUTC : current.EndUTC };
                    continue;
                }

                merged.Add(current);
                current = next;
            }

            merged.Add(current);
            return merged;
        }

        private void PublishStore(PlaytimeStoreV2 store) =>
            Volatile.Write(ref storeState, store);

        private void WriteStoreSynchronously(PlaytimeStoreV2 store)
        {
            var content = JsonConvert.SerializeObject(store, Formatting.Indented, JsonSerializerSettings.GetShared());

            var tempPath = $"{storePath}.{sessionID}.tmp";

            File.WriteAllText(tempPath, content, UTF8NoBOM);

            try
            {
                if (File.Exists(storePath))
                    File.Replace(tempPath, storePath, null);
                else
                    File.Move(tempPath, storePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private void ExecuteWithFileLock(Action action)
        {
            var lockTaken = false;

            try
            {
                lockTaken = fileMutex.WaitOne(3_000);

                if (!lockTaken)
                {
                    DLog.Warning("等待游玩时长文件锁超时");
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                DLog.Warning("处理游玩时长持久化时发生错误", ex);
            }
            finally
            {
                if (lockTaken)
                    fileMutex.ReleaseMutex();
            }
        }

        private static PlaytimeStoreV2 NormalizeStore(PlaytimeStoreV2? store)
        {
            if (store == null || store.Version != 2)
                return PlaytimeStoreV2.Empty;

            return new()
            {
                Version        = 2,
                DailyTotals    = store.DailyTotals    ?? [],
                ActiveSessions = store.ActiveSessions ?? new(StringComparer.Ordinal)
            };
        }

        private static PlaytimeStoreV2 CloneStore(PlaytimeStoreV2 store) =>
            new()
            {
                Version     = 2,
                DailyTotals = new(store.DailyTotals),
                ActiveSessions = store.ActiveSessions.ToDictionary
                (
                    static pair => pair.Key,
                    static pair => pair.Value with { },
                    StringComparer.Ordinal
                )
            };

        private static uint GetStableHashCode(string value)
        {
            var hash = 2166136261u;

            foreach (var character in value)
                hash = hash * 16777619u ^ character;

            return hash;
        }
    }
}
