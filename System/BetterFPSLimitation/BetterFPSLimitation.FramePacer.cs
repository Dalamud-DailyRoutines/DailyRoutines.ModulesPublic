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

        private long vblankInterval;
        private long vblankPhase;
        private long nextCalibration;

        private long timerOvershoot;
        private nint timer;

        private long lastPresentTimestamp;
        private long accumulatedTicks;
        private int  accumulatedFrames;

        public unsafe FramePacer(BetterFPSLimitation inModule)
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
                _ = CloseHandle(timer);
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
                lastTargetFPS   = targetFPS;
                frameRemainder  = 0;
                nextTimestamp   = 0;
                vblankInterval  = 0;
                nextCalibration = 0;
            }

            frameRemainder += frequency % targetFPS;
            frameInterval   = frequency / targetFPS;

            if (frameRemainder >= targetFPS)
            {
                frameRemainder -= targetFPS;
                frameInterval  += 1;
            }

            if (frameInterval <= 0)
                return;

            var now = Stopwatch.GetTimestamp();

            if (nextCalibration <= now)
                CalibrateVBlank(frequency, now);

            if (now - nextTimestamp > frameInterval)
                nextTimestamp = ResolveTimestamp(now, frequency);

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

        private long ResolveTimestamp
        (
            long now,
            long frequency
        )
        {
            if (vblankInterval <= 0 || vblankPhase <= 0)
                return now + frameInterval;

            var cycles  = (double)frameInterval / vblankInterval;
            var rounded = Math.Round(cycles);

            if (rounded < 1 || Math.Abs(cycles - rounded) * 100 > PHASE_LOCK_PERCENT)
                return now + frameInterval;

            var elapsed = now - vblankPhase;

            if (elapsed < 0 || elapsed > frequency * PHASE_STALE_SECONDS)
                return now + frameInterval;

            var steps = (long)Math.Ceiling(elapsed / (double)frameInterval);
            if (steps < 1)
                steps = 1;

            return vblankPhase + steps * frameInterval;
        }

        private unsafe void CalibrateVBlank
        (
            long frequency,
            long now
        )
        {
            nextCalibration = now + frequency * CALIBRATION_INTERVAL_SECONDS;

            var device = Device.Instance();
            if (device == null)
                return;

            var output = device->DXGIOutput;
            if (output == null)
                return;

            var waitForVBlank = (delegate* unmanaged<void*, int>)(*(nint**)output)[10];

            if (waitForVBlank(output) < 0)
                return;

            var previous = Stopwatch.GetTimestamp();

            if (vblankInterval <= 0)
            {
                var total = 0L;

                for (var i = 0; i < VBLANK_CALIBRATION_SAMPLES; i++)
                {
                    if (waitForVBlank(output) < 0)
                        return;

                    var current = Stopwatch.GetTimestamp();
                    total   += current - previous;
                    previous = current;
                }

                vblankInterval = total / VBLANK_CALIBRATION_SAMPLES;
            }

            vblankPhase = previous;
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

                SetWaitableTimer(timer, ref dueTime, 0, 0, 0, 0);
                WaitForSingleObject(timer, INFINITE);
            }

            var overshoot = Stopwatch.GetTimestamp() - expected;

            if (overshoot > timerOvershoot)
                timerOvershoot = overshoot;
            else
                timerOvershoot -= timerOvershoot / OVERSHOOT_DECAY_DIVISOR;
        }

        #region 常量

        private const int SPIN_WAIT_ITERATIONS         = 256;
        private const int PHASE_LOCK_PERCENT           = 2;
        private const int PHASE_STALE_SECONDS          = 2;
        private const int CALIBRATION_INTERVAL_SECONDS = 30;
        private const int VBLANK_CALIBRATION_SAMPLES   = 4;
        private const int COOLDOWN_MARGIN_DIVISOR      = 10000;
        private const int OVERSHOOT_DECAY_DIVISOR      = 16;

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
