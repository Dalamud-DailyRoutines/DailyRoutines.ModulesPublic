using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

public class AutoRecordSubTimeLeft : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动记录剩余游戏时间",
        Description = "登录时, 自动记录保存当前账号剩余的游戏时间, 并显示在服务器信息栏",
        Category    = ModuleCategories.General,
        Author      = ["Due"]
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };

    private static readonly CompSig AgentLobbyOnLoginSig = new("E8 ?? ?? ?? ?? 41 C6 45 ?? ?? E9 ?? ?? ?? ?? 83 FB 03");

    private unsafe delegate nint AgentLobbyOnLoginDelegate(AgentLobby* agent);

    private static Hook<AgentLobbyOnLoginDelegate>? AgentLobbyOnLoginHook;

    private static Config        ModuleConfig = null!;
    private static IDtrBarEntry? Entry;
    private static Tracker?      PlaytimeTracker;
    private static Query?        PlaytimeQuery;

    protected override unsafe void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new();

        var path = Path.Join(ConfigDirectoryPath, "PlatimeData.log");
        PlaytimeTracker ??= new(path);
        PlaytimeQuery   ??= new(path);

        Entry         ??= DService.DtrBar.Get("DailyRoutines-GameTimeLeft");
        Entry.OnClick =   OnDTREntryClick;

        UpdateEntryAndTimeInfo();

        AgentLobbyOnLoginHook ??= AgentLobbyOnLoginSig.GetHook<AgentLobbyOnLoginDelegate>(AgentLobbyOnLoginDetour);
        AgentLobbyOnLoginHook.Enable();

        DService.ClientState.Login  += OnLogin;
        DService.ClientState.Logout += OnLogout;

        FrameworkManager.Reg(OnUpdate, throttleMS: 5_000);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "CharaSelect",        OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "CharaSelect",        OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_CharaSelectRemain", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_CharaSelectRemain", OnAddon);
    }

    protected override void ConfigUI()
    {
        var contentID = LocalPlayerState.ContentID;
        if (contentID == 0) return;

        if (!ModuleConfig.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            ImGui.TextColored(KnownColor.Orange.ToVector4(), "当前角色暂无数据, 请重新登录游戏以记录");
            return;
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "上次记录:");

        ImGui.SameLine();
        ImGui.Text($"{info.Record}");

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "月卡剩余时间:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftMonth == TimeSpan.MinValue ? TimeSpan.Zero : info.LeftMonth));

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "点卡剩余时间:");

        ImGui.SameLine();
        ImGui.Text(FormatTimeSpan(info.LeftTime));
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        FrameworkManager.Unreg(OnUpdate);

        Entry?.Remove();
        Entry = null;

        PlaytimeTracker?.Dispose();
        PlaytimeTracker = null;

        PlaytimeQuery = null;

        DService.ClientState.Login  -= OnLogin;
        DService.ClientState.Logout -= OnLogout;
    }

    private void OnLogin()
    {
        TaskHelper.Enqueue(() =>
        {
            var contentID = LocalPlayerState.ContentID;
            if (contentID == 0) return false;

            UpdateEntryAndTimeInfo(contentID);
            return true;
        });
    }

    private static void OnUpdate(IFramework _) =>
        UpdateEntryAndTimeInfo();

    private void OnLogout(int code, int type) =>
        TaskHelper?.Abort();

    private unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (CharaSelect == null) return;
        if (type == AddonEvent.PostDraw)
        {
            if (!Throttler.Throttle("AutoRecordSubTimeLeft-OnAddonDraw"))
                return;
        }

        var agent = AgentLobby.Instance();
        if (agent == null) return;

        var info = agent->LobbyData.LobbyUIClient.SubscriptionInfo;
        if (info == null) return;

        var contentID = agent->HoveredCharacterContentId;
        if (contentID == 0) return;

        if (agent->WorldIndex == -1) return;

        var timeInfo = GetLeftTimeSecond(*info);
        ModuleConfig.Infos[contentID]
            = new(DateTime.Now,
                  timeInfo.MonthTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.MonthTime),
                  timeInfo.PointTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.PointTime));
        ModuleConfig.Save(this);

        if (CharaSelectRemain != null)
        {
            var textNode = CharaSelectRemain->GetTextNodeById(7);
            if (textNode != null)
            {
                textNode->SetPositionFloat(-20, 40);
                textNode->SetText($"剩余天数: {FormatTimeSpan(TimeSpan.FromSeconds(timeInfo.MonthTime))}\n" +
                                  $"剩余时长: {FormatTimeSpan(TimeSpan.FromSeconds(timeInfo.PointTime))}");
            }
        }

        UpdateEntryAndTimeInfo(contentID);
    }

    private unsafe nint AgentLobbyOnLoginDetour(AgentLobby* agent)
    {
        var ret = AgentLobbyOnLoginHook.Original(agent);
        UpdateSubInfo(agent);
        return ret;
    }

    private unsafe void UpdateSubInfo(AgentLobby* agent)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            try
            {
                var info = agent->LobbyData.LobbyUIClient.SubscriptionInfo;
                if (info == null) return false;

                var contentID = agent->HoveredCharacterContentId;
                if (contentID == 0) return false;

                if (agent->WorldIndex == -1) return false;

                var timeInfo = GetLeftTimeSecond(*info);
                ModuleConfig.Infos[contentID]
                    = new(DateTime.Now,
                          timeInfo.MonthTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.MonthTime),
                          timeInfo.PointTime == 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(timeInfo.PointTime));
                ModuleConfig.Save(this);

                UpdateEntryAndTimeInfo(contentID);
            }
            catch (Exception ex)
            {
                Warning("更新游戏点月卡订阅信息失败", ex);
                NotificationWarning(ex.Message, "更新游戏点月卡订阅信息失败");
            }

            return true;
        }, "更新订阅信息");
    }

    private static (int MonthTime, int PointTime) GetLeftTimeSecond(LobbySubscriptionInfo info)
    {
        var size = Marshal.SizeOf(info);
        var arr  = new byte[size];
        var ptr  = nint.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
        } finally { Marshal.FreeHGlobal(ptr); }

        var month = string.Join(string.Empty, arr.Skip(16).Take(3).Reverse().Select(x => x.ToString("X2")));
        var point = string.Join(string.Empty, arr.Skip(24).Take(3).Reverse().Select(x => x.ToString("X2")));
        return (Convert.ToInt32(month, 16), Convert.ToInt32(point, 16));
    }

    private static void UpdateEntryAndTimeInfo(ulong contentID = 0)
    {
        if (DService.ClientState.IsLoggedIn)
            PlaytimeTracker.Start();
        else
            PlaytimeTracker.Stop();

        if (Entry == null) return;

        if (contentID == 0)
            contentID = LocalPlayerState.ContentID;

        if (contentID == 0                                           ||
            DService.Condition[ConditionFlag.InCombat]               ||
            !ModuleConfig.Infos.TryGetValue(contentID, out var info) ||
            info.Record == DateTime.MinValue                         ||
            (info.LeftMonth == TimeSpan.MinValue && info.LeftTime == TimeSpan.MinValue))
        {
            Entry.Shown = false;
            return;
        }

        var isMonth    = info.LeftMonth != TimeSpan.MinValue;
        var expireTime = info.Record + (isMonth ? info.LeftMonth : info.LeftTime);

        var textBuilder = new SeStringBuilder();
        textBuilder.AddUiForeground($"[{(isMonth ? "月卡" : "点卡")}] ", 25)
                   .AddText($"{expireTime:MM/dd HH:mm}");
        Entry.Text = textBuilder.Build();

        var tooltipBuilder = new SeStringBuilder();
        tooltipBuilder.AddUiForeground("[过期时间]", 28)
                      .Add(NewLinePayload.Payload)
                      .AddText($"{expireTime}")
                      .Add(NewLinePayload.Payload)
                      .AddUiForeground("[剩余时长]", 28)
                      .Add(NewLinePayload.Payload)
                      .AddText($"{FormatTimeSpan(expireTime - DateTime.Now)}")
                      .Add(NewLinePayload.Payload)
                      .Add(NewLinePayload.Payload)
                      .AddUiForeground("[本日游玩时长]", 28)
                      .Add(NewLinePayload.Payload)
                      .AddText($"{FormatTimeSpan(PlaytimeQuery.GetTotalUsageBetween(DateTime.Today, TimeSpan.FromDays(1)))}")
                      .Add(NewLinePayload.Payload)
                      .AddUiForeground("[昨日游玩时长]", 28)
                      .Add(NewLinePayload.Payload)
                      .AddText($"{FormatTimeSpan(PlaytimeQuery.GetTotalUsageBetween(DateTime.Today - TimeSpan.FromDays(1), TimeSpan.FromDays(1)))}")
                      .Add(NewLinePayload.Payload)
                      .AddUiForeground("[七日游玩时长]", 28)
                      .Add(NewLinePayload.Payload)
                      .AddText($"{FormatTimeSpan(PlaytimeQuery.GetTotalUsageBetween(DateTime.Today - TimeSpan.FromDays(6), TimeSpan.FromDays(7)))}")
                      .Add(NewLinePayload.Payload)
                      .Add(NewLinePayload.Payload)
                      .AddText("(左键: ")
                      .AddUiForeground("模块配置界面", 34)
                      .AddText(")")
                      .Add(NewLinePayload.Payload)
                      .AddText("(右键: ")
                      .AddUiForeground("时长充值页面", 34)
                      .AddText(")");
        Entry.Tooltip = tooltipBuilder.Build();

        Entry.Shown = true;
    }

    private static void OnDTREntryClick(DtrInteractionEvent eventData)
    {
        switch (eventData.ClickType)
        {
            case MouseClickType.Left:
                ChatManager.SendMessage($"/pdr search {nameof(AutoRecordSubTimeLeft)}");
                break;
            case MouseClickType.Right:
                Util.OpenLink("https://pay.sdo.com/item/GWPAY-100001900");
                break;
        }

    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        var parts = new List<string>();
    
        if (timeSpan.Days    > 0) 
            parts.Add($"{timeSpan.Days} 天");
        if (timeSpan.Hours   > 0) 
            parts.Add($"{timeSpan.Hours} 小时");
        if (timeSpan.Minutes > 0) 
            parts.Add($"{timeSpan.Minutes} 分");
        if (timeSpan.Seconds > 0) 
            parts.Add($"{timeSpan.Seconds} 秒");
    
        return parts.Count > 0 ? string.Join(" ", parts) : "0 秒";
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<ulong, (DateTime Record, TimeSpan LeftMonth, TimeSpan LeftTime)> Infos = [];
    }

    internal record UsageEvent(string SessionID, int ProcessID, string EventType, DateTime TimestampUtc);

    private static class UsageLogUtilities
    {
        public static string FormatEventLine(string sessionID, int processID, string eventType, DateTime timestampUtc) =>
            $"{sessionID}\t{processID}\t{eventType}\t{timestampUtc:o}";
    }

    private static class UsageLogCache
    {
        private static readonly ConcurrentDictionary<string, UsageLogCacheState> Cache = new(StringComparer.OrdinalIgnoreCase);

        internal static UsageLogSnapshot LoadSnapshot(string path, TimeSpan staleGrace, TimeSpan cacheValidity, DateTime nowUtc)
        {
            var state = Cache.GetOrAdd(path, _ => new UsageLogCacheState());
            return state.LoadSnapshot(path, staleGrace, cacheValidity, nowUtc);
        }

        private sealed class UsageLogCacheState
        {
            private readonly ReaderWriterLockSlim             cacheLock       = new();
            private readonly Dictionary<string, SessionState> sessions        = new(StringComparer.Ordinal);
            private readonly List<UsageInterval>              closedIntervals = [];
            private          long                             lastKnownLength;
            private          DateTime                         lastRefreshUtc;
            private          bool                             needsSorting;

            internal UsageLogSnapshot LoadSnapshot(string path, TimeSpan staleGrace, TimeSpan cacheValidity, DateTime nowUtc)
            {
                cacheLock.EnterUpgradeableReadLock();
                try
                {
                    if (!File.Exists(path)) return UsageLogSnapshot.Empty;

                    var fileLength = new FileInfo(path).Length;
                    if (fileLength == lastKnownLength && cacheValidity > TimeSpan.Zero && nowUtc - lastRefreshUtc < cacheValidity)
                        return CreateSnapshot(staleGrace, nowUtc);

                    cacheLock.EnterWriteLock();
                    try
                    {
                        if (!File.Exists(path))
                        {
                            ResetState();
                            return UsageLogSnapshot.Empty;
                        }

                        fileLength = new FileInfo(path).Length;
                        if (fileLength < lastKnownLength) 
                            ResetState();

                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        stream.Seek(lastKnownLength, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);

                        while (reader.ReadLine() is { } line)
                        {
                            if (TryParseEvent(line, out var usageEvent))
                                ApplyEvent(usageEvent);
                        }

                        lastKnownLength = stream.Position;
                        lastRefreshUtc  = nowUtc;

                        return CreateSnapshot(staleGrace, nowUtc);
                    }
                    finally 
                    {
                        cacheLock.ExitWriteLock();
                    }
                }
                finally 
                {
                    cacheLock.ExitUpgradeableReadLock();
                }
            }

            private UsageLogSnapshot CreateSnapshot(TimeSpan staleGrace, DateTime nowUtc)
            {
                if (needsSorting && closedIntervals.Count > 1)
                {
                    closedIntervals.Sort(static (left, right) => DateTime.Compare(left.BeginUtc, right.BeginUtc));
                    needsSorting = false;
                }

                var openSessions = new List<OpenSession>();
                var intervalList = new List<UsageInterval>(closedIntervals.Count + sessions.Count);

                intervalList.AddRange(closedIntervals);

                foreach (var (key, session) in sessions)
                {
                    if (!session.ActiveStartUTC.HasValue) continue;

                    var timeSinceLastEvent = nowUtc - session.LastEventUTC;
                    if (timeSinceLastEvent > staleGrace)
                    {
                        var staleEndTime = session.LastEventUTC + staleGrace;
                        if (staleEndTime > session.ActiveStartUTC.Value)
                            intervalList.Add(new(session.ActiveStartUTC.Value, staleEndTime));
                    }
                    else
                    {
                        if (nowUtc > session.ActiveStartUTC.Value)
                            intervalList.Add(new(session.ActiveStartUTC.Value, nowUtc));
                        openSessions.Add(new(key, session.ActiveStartUTC.Value, session.LastEventUTC));
                    }
                }

                if (intervalList.Count == 0 && openSessions.Count == 0) return UsageLogSnapshot.Empty;

                return new UsageLogSnapshot(intervalList.ToArray(), openSessions.ToArray());
            }

            private void ResetState()
            {
                sessions.Clear();
                closedIntervals.Clear();
                lastKnownLength = 0;
                lastRefreshUtc  = DateTime.MinValue;
                needsSorting    = false;
            }

            private static bool TryParseEvent(string line, out UsageEvent usageEvent)
            {
                usageEvent = null;
                if (string.IsNullOrEmpty(line)) return false;

                var span          = line.AsSpan();
                var firstTabIndex = span.IndexOf('\t');
                if (firstTabIndex <= 0) return false;

                var secondTabIndex = span.Slice(firstTabIndex + 1).IndexOf('\t');
                if (secondTabIndex < 0) return false;

                secondTabIndex += firstTabIndex + 1;

                var thirdTabIndex = span.Slice(secondTabIndex + 1).IndexOf('\t');
                if (thirdTabIndex < 0) return false;

                thirdTabIndex += secondTabIndex + 1;

                var sessionID   = span[..firstTabIndex].ToString();
                var processSpan = span.Slice(firstTabIndex + 1, secondTabIndex - firstTabIndex - 1);
                if (!int.TryParse(processSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId)) return false;

                var eventType = span.Slice(secondTabIndex + 1, thirdTabIndex - secondTabIndex - 1).ToString();
                var timeSpan  = span[(thirdTabIndex + 1)..];
                if (!DateTime.TryParseExact(timeSpan, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestampUtc)) return false;

                usageEvent = new UsageEvent(sessionID, processId, eventType, timestampUtc);
                return true;
            }

            private void ApplyEvent(UsageEvent usageEvent)
            {
                if (!sessions.TryGetValue(usageEvent.SessionID, out var sessionState)) 
                    sessionState = new();

                switch (usageEvent.EventType)
                {
                    case "start":
                        if (sessionState.ActiveStartUTC.HasValue && usageEvent.TimestampUtc > sessionState.ActiveStartUTC.Value)
                        {
                            closedIntervals.Add(new UsageInterval(sessionState.ActiveStartUTC.Value, usageEvent.TimestampUtc));
                            needsSorting = true;
                        }

                        sessionState.ActiveStartUTC = usageEvent.TimestampUtc;
                        break;

                    case "heartbeat":
                        if (!sessionState.ActiveStartUTC.HasValue || usageEvent.TimestampUtc < sessionState.ActiveStartUTC.Value)
                            sessionState.ActiveStartUTC = usageEvent.TimestampUtc;

                        break;

                    case "stop":
                    case "autoClose":
                        if (sessionState.ActiveStartUTC.HasValue && usageEvent.TimestampUtc > sessionState.ActiveStartUTC.Value)
                        {
                            closedIntervals.Add(new UsageInterval(sessionState.ActiveStartUTC.Value, usageEvent.TimestampUtc));
                            needsSorting = true;
                        }

                        sessionState.ActiveStartUTC = null;
                        break;

                    default:
                        return;
                }

                sessionState.LastEventUTC   = usageEvent.TimestampUtc;

                sessions[usageEvent.SessionID] = sessionState;
            }

            private sealed class SessionState
            {
                public DateTime? ActiveStartUTC { get; set; }
                public DateTime  LastEventUTC   { get; set; }
            }
        }
    }

    private sealed class UsageLogSnapshot(IReadOnlyList<UsageInterval> intervals, IReadOnlyList<OpenSession> openSessions)
    {
        public static readonly UsageLogSnapshot Empty = new([], []);

        public IReadOnlyList<UsageInterval> Intervals    { get; } = intervals;
        public IReadOnlyList<OpenSession>   OpenSessions { get; } = openSessions;
    }

    private readonly record struct UsageInterval(DateTime BeginUtc, DateTime EndUtc);

    private readonly record struct OpenSession(string SessionID, DateTime ActiveSinceUTC, DateTime LastEventUTC);

    private sealed class Tracker : IDisposable
    {
        private readonly string   logPath;
        private readonly string   sessionID         = Guid.NewGuid().ToString("N");
        private readonly int      processID         = Environment.ProcessId;
        private readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(30);
        private readonly TimeSpan staleGrace        = TimeSpan.FromSeconds(90);
        private readonly Mutex    fileMutex;

        private int                     runState;
        private CancellationTokenSource cancellation;
        private Task                    heartbeatTask;

        public Tracker(string path)
        {
            logPath = path ?? throw new ArgumentNullException(nameof(path));
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? throw new InvalidOperationException("Log directory cannot be resolved."));

            var mutexName = $@"Global\{nameof(Tracker)}-{GetStableHashCode(Path.GetFullPath(logPath).ToUpperInvariant())}";
            fileMutex = new Mutex(false, mutexName);

            AutoCloseStaleSessions();
        }

        private static uint GetStableHashCode(string value)
        {
            var hash = 2166136261;
            foreach (var character in value) 
                hash = (hash * 16777619) ^ character;

            return hash;
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref runState, 1, 0) != 0) return;

            cancellation = new CancellationTokenSource();
            WriteEvent("start", DateTime.UtcNow);

            var token = cancellation.Token;
            heartbeatTask = Task.Run(async () =>
            {
                try
                {
                    var timer = new PeriodicTimer(heartbeatInterval);
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) 
                        WriteEvent("heartbeat", DateTime.UtcNow);
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref runState, 0, 1) != 1) return;

            try
            {
                cancellation?.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                heartbeatTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }

            try
            {
                WriteEvent("stop", DateTime.UtcNow);
            }
            catch
            {
                // ignored
            }

            cancellation?.Dispose();
            cancellation  = null;
            heartbeatTask = null;
        }

        public void Dispose()
        {
            Stop();
            fileMutex.Dispose();
        }

        private void WriteEvent(string eventType, DateTime utc)
        {
            var line = UsageLogUtilities.FormatEventLine(sessionID, processID, eventType, utc);
            try
            {
                fileMutex.WaitOne();
                File.AppendAllText(logPath, line + Environment.NewLine);
            } finally { fileMutex.ReleaseMutex(); }
        }

        private void AutoCloseStaleSessions()
        {
            try
            {
                var nowUtc   = DateTime.UtcNow;
                var snapshot = UsageLogCache.LoadSnapshot(logPath, staleGrace, TimeSpan.Zero, nowUtc);

                if (snapshot.OpenSessions.Count == 0) return;

                var autoCloseLines = new List<string>();
                foreach (var session in snapshot.OpenSessions)
                {
                    var proposedEnd = session.LastEventUTC + staleGrace;
                    if (proposedEnd >= nowUtc) continue;

                    autoCloseLines.Add(UsageLogUtilities.FormatEventLine(session.SessionID, -1, "autoClose", proposedEnd));
                }

                if (autoCloseLines.Count == 0) return;

                fileMutex.WaitOne();
                try { File.AppendAllLines(logPath, autoCloseLines); } finally { fileMutex.ReleaseMutex(); }

                UsageLogCache.LoadSnapshot(logPath, staleGrace, TimeSpan.Zero, DateTime.UtcNow);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Query(string path)
    {
        private readonly string   logPath    = path ?? throw new ArgumentNullException(nameof(path));
        private readonly TimeSpan staleGrace = TimeSpan.FromSeconds(90);

        public TimeSpan GetTotalUsageSince(TimeSpan lookback)
        {
            if (lookback <= TimeSpan.Zero)
                return TimeSpan.Zero;

            var endLocal   = DateTime.Now;
            var startLocal = endLocal - lookback;
            return GetTotalUsageBetween(startLocal, lookback);
        }

        public TimeSpan GetTotalUsageBetween(DateTime startLocal, TimeSpan span)
        {
            if (span <= TimeSpan.Zero || !File.Exists(logPath))
                return TimeSpan.Zero;

            var timeZone = TimeZoneInfo.Local;
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Local), timeZone);
            var endLocal = startLocal + span;
            var endUtc   = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Local), timeZone);

            var intervals = BuildUsageIntervalsUtc();
            var result = IntersectSum(intervals, startUtc, endUtc);

            if (result > span)
                return span;

            return result;
        }

        private List<(DateTime BeginUtc, DateTime EndUtc)> BuildUsageIntervalsUtc()
        {
            var snapshot  = UsageLogCache.LoadSnapshot(logPath, staleGrace, TimeSpan.Zero, DateTime.UtcNow);
            var intervals = new List<(DateTime, DateTime)>(snapshot.Intervals.Count);

            foreach (var interval in snapshot.Intervals)
                intervals.Add((interval.BeginUtc, interval.EndUtc));

            return intervals;
        }

        private static TimeSpan IntersectSum(List<(DateTime BeginUtc, DateTime EndUtc)> intervals, DateTime windowBeginUtc, DateTime windowEndUtc)
        {
            if (windowEndUtc <= windowBeginUtc)
                return TimeSpan.Zero;

            var clippedIntervals = new List<(DateTime Start, DateTime End)>();
            foreach (var interval in intervals)
            {
                if (interval.EndUtc <= windowBeginUtc || interval.BeginUtc >= windowEndUtc)
                    continue;

                var segmentStart = interval.BeginUtc > windowBeginUtc ? interval.BeginUtc : windowBeginUtc;
                var segmentEnd   = interval.EndUtc   < windowEndUtc ? interval.EndUtc : windowEndUtc;
                if (segmentEnd > segmentStart)
                    clippedIntervals.Add((segmentStart, segmentEnd));
            }

            if (clippedIntervals.Count == 0)
                return TimeSpan.Zero;

            clippedIntervals.Sort((a, b) => DateTime.Compare(a.Start, b.Start));

            var mergedIntervals = new List<(DateTime Start, DateTime End)>();
            var current = clippedIntervals[0];

            for (int i = 1; i < clippedIntervals.Count; i++)
            {
                var next = clippedIntervals[i];
                if (next.Start <= current.End)
                    current = (current.Start, next.End > current.End ? next.End : current.End);
                else
                {
                    mergedIntervals.Add(current);
                    current = next;
                }
            }
            mergedIntervals.Add(current);

            long totalTicks = 0;
            foreach (var interval in mergedIntervals)
                totalTicks += (interval.End - interval.Start).Ticks;

            return new TimeSpan(totalTicks);
        }
    }
}
