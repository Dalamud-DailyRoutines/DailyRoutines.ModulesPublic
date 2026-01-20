using System;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Managers;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private static int                      PendingSaveConfig;
    private static CancellationTokenSource? SaveConfigTokenSource;

    private static void RequestSaveConfig(int delayMs = 800)
    {
        if (delayMs < 0) delayMs = 0;

        Interlocked.Exchange(ref PendingSaveConfig, 1);

        var tokenSource    = new CancellationTokenSource();
        var oldTokenSource = Interlocked.Exchange(ref SaveConfigTokenSource, tokenSource);

        try
        {
            oldTokenSource?.Cancel();
        }
        catch
        {
            // ignored
        }

        oldTokenSource?.Dispose();

        _ = SaveConfigAfterDelayAsync(delayMs, tokenSource.Token);
    }

    private static async Task SaveConfigAfterDelayAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        FlushSaveConfig();
    }

    private static void FlushSaveConfig()
    {
        if (Interlocked.Exchange(ref PendingSaveConfig, 0) == 0)
            return;

        try
        {
            ModuleConfig.Save(ModuleManager.GetModule<AutoReplyChatBot>());
        }
        catch
        {
            // ignored
        }
    }

    private static void DisposeSaveConfigScheduler()
    {
        var tokenSource = Interlocked.Exchange(ref SaveConfigTokenSource, null);
        if (tokenSource == null)
            return;

        try
        {
            tokenSource.Cancel();
        }
        catch
        {
            // ignored
        }

        tokenSource.Dispose();
    }
}
