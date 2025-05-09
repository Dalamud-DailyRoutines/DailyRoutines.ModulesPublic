using DailyRoutines.Abstracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DailyRoutines.ModulesPublic.UIOptimization;

public class GameWindowLock : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("GameWindowLockTitle"),
        Description = GetLoc("GameWindowLockDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["status102"]
    };

    private bool _isLocked;
    private object _lock = new();

    public override unsafe void Init()
    {
        DService.Condition.ConditionChange += Condition_ConditionChange;
    }

    private void Condition_ConditionChange(Dalamud.Game.ClientState.Conditions.ConditionFlag flag, bool value)
    {
        if (flag == Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat)
        {
            Task.Run(() =>
            {
                lock (_lock)
                {
                    if (value && !_isLocked)
                    {
                        WindowLock.LockWindowByHandle(Process.GetCurrentProcess().MainWindowHandle);
                        _isLocked = true;
                    }
                    else if (!value && _isLocked)
                    {
                        WindowLock.UnlockWindow(Process.GetCurrentProcess().MainWindowHandle);
                        _isLocked = false;
                    }
                }
            });
        }
    }

    public override void Uninit()
    {
        DService.Condition.ConditionChange -= Condition_ConditionChange;
        WindowLock.Cleanup();
        base.Uninit();
    }

    public override void ConfigUI()
    {
        ImGui.Text(GetLoc("Locking"));
        ImGui.SameLine();
        ImGui.Text($": {_isLocked}");
    }

    private class WindowLock
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private const int GWL_WNDPROC = -4;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOMOVE = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private static readonly Dictionary<IntPtr, IntPtr> _windowProcMap = new();
        private static readonly Dictionary<IntPtr, WndProcDelegate> _wndProcDelegates = new();

        public static void LockWindowByHandle(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero)
            {
                SubclassWindow(hWnd);
            }
        }

        public static void UnlockWindow(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero && _windowProcMap.TryGetValue(hWnd, out IntPtr oldProc))
            {
                SetWindowLongPtr(hWnd, GWL_WNDPROC, oldProc);
                _windowProcMap.Remove(hWnd);
                _wndProcDelegates.Remove(hWnd);
            }
        }

        private static void SubclassWindow(IntPtr hWnd)
        {
            var newWndProc = new WndProcDelegate(NewWindowProc);
            IntPtr newProcPtr = Marshal.GetFunctionPointerForDelegate(newWndProc);

            if (!_windowProcMap.ContainsKey(hWnd))
            {
                IntPtr oldProc = SetWindowLongPtr(hWnd, GWL_WNDPROC, newProcPtr);
                if (oldProc == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to subclass window.");
                }
                _windowProcMap[hWnd] = oldProc;
                _wndProcDelegates[hWnd] = newWndProc;
            }
        }

        private static IntPtr NewWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            if (uMsg == WM_WINDOWPOSCHANGING)
            {
                WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOMOVE) == 0)
                {
                    GetWindowRect(hWnd, out RECT rect);
                    pos.x = rect.Left;
                    pos.y = rect.Top;
                    pos.flags |= SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, true);
                }
            }

            return CallWindowProc(_windowProcMap[hWnd], hWnd, uMsg, wParam, lParam);
        }

        public static void Cleanup()
        {
            foreach (var hWnd in _windowProcMap.Keys.ToList())
            {
                UnlockWindow(hWnd);
            }
        }
    }
}
