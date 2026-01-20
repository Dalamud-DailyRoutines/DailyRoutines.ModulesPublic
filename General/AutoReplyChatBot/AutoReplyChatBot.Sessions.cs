using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private static readonly ConcurrentDictionary<string, ChatSession> Sessions = [];

    private static ChatSession GetSession(string key) =>
        Sessions.GetOrAdd(key, static _ => new ChatSession());

    private static void DisposeAllSessions()
    {
        foreach (var session in Sessions.Values)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        Sessions.Clear();
    }

    private sealed class ChatSession : IDisposable
    {
        private long lastReplyUTCTicks;

        public TaskHelper TaskHelper { get; } = new()
        {
            TimeoutMS          = 30_000,
            TimeoutBehaviour   = TaskAbortBehaviour.AbortCurrent,
            ExceptionBehaviour = TaskAbortBehaviour.AbortCurrent
        };

        public void Dispose() => TaskHelper.Dispose();

        public bool IsCooldownReady(int cooldownSeconds)
        {
            var cdTicks   = TimeSpan.FromSeconds(Math.Max(5, cooldownSeconds)).Ticks;
            var nowTicks  = StandardTimeManager.Instance().UTCNow.Ticks;
            var lastTicks = Interlocked.Read(ref lastReplyUTCTicks);
            return lastTicks == 0 || nowTicks - lastTicks >= cdTicks;
        }

        public void SetCooldown()
        {
            var nowTicks = StandardTimeManager.Instance().UTCNow.Ticks;
            Interlocked.Exchange(ref lastReplyUTCTicks, nowTicks);
        }
    }
}
