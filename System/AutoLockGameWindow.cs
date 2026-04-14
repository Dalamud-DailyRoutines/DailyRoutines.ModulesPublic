using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;

namespace DailyRoutines.ModulesPublic;

public class AutoLockGameWindow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoLockGameWindowTitle"),
        Description = Lang.Get("AutoLockGameWindowDescription"),
        Category    = ModuleCategory.System,
        Author      = ["status102"]
    };
    
    private          bool isLocked;
    private readonly Lock objectLock = new();

    protected override void Init() => 
        DService.Instance().Condition.ConditionChange += OnConditionChange;
    
    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChange;
        WindowLock.Cleanup();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        Task.Run
        (() =>
            {
                lock (objectLock)
                {
                    switch (value)
                    {
                        case true when !isLocked:
                            WindowLock.LockWindowByHandle(Process.GetCurrentProcess().MainWindowHandle);
                            isLocked = true;
                            break;
                        case false when isLocked:
                            WindowLock.UnlockWindow(Process.GetCurrentProcess().MainWindowHandle);
                            isLocked = false;
                            break;
                    }
                }
            }
        );
    }

    private static class WindowLock
    {
        private const int  GWL_WNDPROC          = -4;
        private const int  WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOMOVE           = 0x0002;

        private static readonly Dictionary<nint, nint>            WindowProcMap    = [];
        private static readonly Dictionary<nint, WndProcDelegate> WndProcDelegates = [];

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

        [DllImport("user32.dll")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint uMsg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

        public static void LockWindowByHandle(nint hWnd)
        {
            if (hWnd == nint.Zero) return;
            SubclassWindow(hWnd);
        }

        public static void UnlockWindow(nint hWnd)
        {
            if (hWnd != nint.Zero && WindowProcMap.TryGetValue(hWnd, out var oldProc))
            {
                SetWindowLongPtr(hWnd, GWL_WNDPROC, oldProc);
                WindowProcMap.Remove(hWnd);
                WndProcDelegates.Remove(hWnd);
            }
        }

        private static void SubclassWindow(nint hWnd)
        {
            var newWndProc = new WndProcDelegate(NewWindowProc);
            var newProcPtr = Marshal.GetFunctionPointerForDelegate(newWndProc);

            if (!WindowProcMap.ContainsKey(hWnd))
            {
                var oldProc = SetWindowLongPtr(hWnd, GWL_WNDPROC, newProcPtr);
                if (oldProc == nint.Zero && Marshal.GetLastWin32Error() != 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to subclass window.");

                WindowProcMap[hWnd]    = oldProc;
                WndProcDelegates[hWnd] = newWndProc;
            }
        }

        private static nint NewWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
        {
            if (uMsg == WM_WINDOWPOSCHANGING)
            {
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);

                if ((pos.flags & SWP_NOMOVE) == 0)
                {
                    GetWindowRect(hWnd, out var rect);
                    pos.x     =  rect.Left;
                    pos.y     =  rect.Top;
                    pos.flags |= SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, true);
                }
            }

            return CallWindowProc(WindowProcMap[hWnd], hWnd, uMsg, wParam, lParam);
        }

        public static void Cleanup()
        {
            foreach (var hWnd in WindowProcMap.Keys.ToList())
                UnlockWindow(hWnd);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPos
        {
            public nint hwnd;
            public nint hwndInsertAfter;
            public int  x;
            public int  y;
            public int  cx;
            public int  cy;
            public uint flags;
        }

        private delegate nint WndProcDelegate(nint hWnd, uint uMsg, nint wParam, nint lParam);
    }
}
