using System.Collections.Concurrent;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private sealed class RateLimiter
    (
        int maxConcurrency = 3
    ) : IDisposable
    {
        private readonly SemaphoreSlim                          globalSemaphore = new(maxConcurrency, maxConcurrency);
        private readonly ConcurrentDictionary<string, DateTime> lastReplyTimes  = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose() => globalSemaphore.Dispose();

        public bool CanProceed(string key, int cooldownSeconds)
        {
            if (!lastReplyTimes.TryGetValue(key, out var lastTime))
                return true;

            var cooldown = TimeSpan.FromSeconds(Math.Max(5, cooldownSeconds));
            return StandardTimeManager.Instance().UTCNow - lastTime >= cooldown;
        }

        public void MarkUsed(string key) =>
            lastReplyTimes[key] = StandardTimeManager.Instance().UTCNow;

        public async Task<Ticket> AcquireAsync(string key, CancellationToken ct = default)
        {
            await globalSemaphore.WaitAsync(ct).ConfigureAwait(false);
            lastReplyTimes[key] = StandardTimeManager.Instance().UTCNow;
            return new Ticket(globalSemaphore);
        }

        public sealed class Ticket : IDisposable
        {
            private SemaphoreSlim? semaphore;

            internal Ticket(SemaphoreSlim semaphore) => this.semaphore = semaphore;

            public void Dispose()
            {
                var s = Interlocked.Exchange(ref semaphore, null);
                s?.Release();
            }
        }
    }
}
