using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace DailyRoutines.ModulesPublic;

public partial class BetterFPSLimitation
{
    private sealed class FramePacer : IDisposable
    {
        public float FPS { get; private set; }

        private readonly BetterFPSLimitation module;

        private Hook<SwapChain.Delegates.Present>? SwapChainPresentHook;

        private long frameInterval;
        private long frameRemainder;
        private long nextTimestamp;
        private int  lastTargetFPS;

        private long timerOvershoot;
        private nint timer;

        private long lastPresentTimestamp;
        private long accumulatedTicks;
        private int  accumulatedFrames;

        public unsafe FramePacer
        (
            BetterFPSLimitation inModule
        )
        {
            module = inModule;

            timer          = CreateWaitableTimerExW(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            timerOvershoot = Stopwatch.Frequency / 2000;

            SwapChainPresentHook = IGameInteropProvider.Instance().HookFromMemberFunction
            (
                typeof(SwapChain.MemberFunctionPointers),
                "Present",
                (SwapChain.Delegates.Present)SwapChainPresentDetour
            );
            SwapChainPresentHook.Enable();
        }

        public void Dispose()
        {
            if (SwapChainPresentHook != null)
            {
                SwapChainPresentHook.Dispose();
                SwapChainPresentHook = null;
            }

            if (timer != 0)
            {
                _     = CloseHandle(timer);
                timer = 0;
            }
        }

        private unsafe void SwapChainPresentDetour
        (
            SwapChain* thisPtr
        )
        {
            if (module.config.IsEnabled)
                Wait(module.config.Limitation);

            RecordFrame();

            Device.Instance()->IsFrameRateLimited = false;
            SwapChainPresentHook.Original(thisPtr);
        }

        public void Wait
        (
            int targetFPS
        )
        {
            if (targetFPS <= 0)
                return;

            var frequency = Stopwatch.Frequency;

            if (targetFPS != lastTargetFPS)
            {
                lastTargetFPS  = targetFPS;
                frameRemainder = 0;
                nextTimestamp  = 0;
            }

            frameRemainder += frequency % targetFPS;
            frameInterval  =  frequency / targetFPS;

            if (frameRemainder >= targetFPS)
            {
                frameRemainder -= targetFPS;
                frameInterval  += 1;
            }

            if (frameInterval <= 0)
                return;

            var now = Stopwatch.GetTimestamp();

            if (nextTimestamp <= 0 || now - nextTimestamp > frameInterval * MAX_DRIFT_INTERVALS)
                nextTimestamp = now;
            else if (now < nextTimestamp)
                WaitUntil(nextTimestamp, frequency);

            nextTimestamp += frameInterval;
        }

        public void RecordFrame()
        {
            var now = Stopwatch.GetTimestamp();

            if (lastPresentTimestamp != 0)
            {
                accumulatedTicks += now - lastPresentTimestamp;
                accumulatedFrames++;

                if (accumulatedTicks >= Stopwatch.Frequency)
                {
                    FPS = (float)(accumulatedFrames * (double)Stopwatch.Frequency / accumulatedTicks);

                    accumulatedTicks  = 0;
                    accumulatedFrames = 0;
                }
            }

            lastPresentTimestamp = now;
        }

        private void WaitUntil
        (
            long target,
            long frequency
        )
        {
            var cooldown = timer != 0 ?
                               timerOvershoot + frequency / COOLDOWN_MARGIN_DIVISOR :
                               frequency / 1000;

            cooldown = Math.Min(cooldown, frameInterval / 2);

            while (true)
            {
                var remaining = target - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                    break;

                if (remaining <= cooldown)
                {
                    Thread.SpinWait(SPIN_WAIT_ITERATIONS);
                    continue;
                }

                SleepPrecise(remaining - cooldown, frequency);
            }
        }

        private void SleepPrecise
        (
            long ticks,
            long frequency
        )
        {
            var expected = Stopwatch.GetTimestamp() + ticks;

            if (timer == 0)
                Thread.Sleep((int)(ticks * 1000 / frequency));
            else
            {
                var dueTime = -(ticks * 10000000 / frequency);

                _ = SetWaitableTimer(timer, ref dueTime, 0, 0, 0, 0);
                _ = WaitForSingleObject(timer, INFINITE);
            }

            var overshoot = Stopwatch.GetTimestamp() - expected;

            if (overshoot > timerOvershoot)
                timerOvershoot = overshoot;
            else
                timerOvershoot -= timerOvershoot / OVERSHOOT_DECAY_DIVISOR;
        }

        #region 常量

        private const int SPIN_WAIT_ITERATIONS    = 256;
        private const int MAX_DRIFT_INTERVALS     = 4;
        private const int COOLDOWN_MARGIN_DIVISOR = 10000;
        private const int OVERSHOOT_DECAY_DIVISOR = 16;

        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS                      = 0x001F0003;
        private const uint INFINITE                              = 0xFFFFFFFF;

        #endregion

        #region 原生

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint CreateWaitableTimerExW
        (
            nint lpTimerAttributes,
            nint lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SetWaitableTimer
        (
            nint     hTimer,
            ref long lpDueTime,
            int      lPeriod,
            nint     pfnCompletionRoutine,
            nint     lpArgToCompletionRoutine,
            int      fResume
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WaitForSingleObject
        (
            nint hHandle,
            uint dwMilliseconds
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CloseHandle
        (
            nint hObject
        );

        #endregion
    }
}
