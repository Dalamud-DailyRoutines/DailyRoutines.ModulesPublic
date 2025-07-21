using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;

namespace DailyRoutines.ModulesPublic;

public class AutoLockGameWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoLockGameWindowTitle"),
        Description = GetLoc("AutoLockGameWindowDescription"),
        Category    = ModuleCategories.System,
        Author      = ["status102"]
    };

    private static         bool   IsLocked;
    private static readonly object ObjectLock = new();

    protected override void Init() => DService.Condition.ConditionChange += OnConditionChange;
    
    private static void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;
        
        Task.Run(() =>
        {
            lock (ObjectLock)
            {
                switch (value)
                {
                    case true when !IsLocked:
                        WindowLock.LockWindowByHandle(Process.GetCurrentProcess().MainWindowHandle);
                        IsLocked = true;
                        break;
                    case false when IsLocked:
                        WindowLock.UnlockWindow(Process.GetCurrentProcess().MainWindowHandle);
                        IsLocked = false;
                        break;
                }
            }
        });
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChange;
        WindowLock.Cleanup();
        
        base.Uninit();
    }
    
    private class WindowLock
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint newProc);

        [DllImport("user32.dll")]
        private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint uMsg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        private const int  GWL_WNDPROC          = -4;
        private const int  WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOMOVE           = 0x0002;

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
            public nint hwnd;
            public nint hwndInsertAfter;
            public int  x;
            public int  y;
            public int  cx;
            public int  cy;
            public uint flags;
        }

        private delegate nint WndProcDelegate(nint hWnd, uint uMsg, nint wParam, nint lParam);

        private static readonly Dictionary<nint, nint>            windowProcMap    = [];
        private static readonly Dictionary<nint, WndProcDelegate> wndProcDelegates = [];

        public static void LockWindowByHandle(nint hWnd)
        {
            if (hWnd == nint.Zero) return;
            SubclassWindow(hWnd);
        }

        public static void UnlockWindow(nint hWnd)
        {
            if (hWnd != nint.Zero && windowProcMap.TryGetValue(hWnd, out var oldProc))
            {
                SetWindowLongPtr(hWnd, GWL_WNDPROC, oldProc);
                windowProcMap.Remove(hWnd);
                wndProcDelegates.Remove(hWnd);
            }
        }

        private static void SubclassWindow(nint hWnd)
        {
            var newWndProc = new WndProcDelegate(NewWindowProc);
            var newProcPtr = Marshal.GetFunctionPointerForDelegate(newWndProc);

            if (!windowProcMap.ContainsKey(hWnd))
            {
                var oldProc = SetWindowLongPtr(hWnd, GWL_WNDPROC, newProcPtr);
                if (oldProc == nint.Zero && Marshal.GetLastWin32Error() != 0) 
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to subclass window.");
                
                windowProcMap[hWnd]    = oldProc;
                wndProcDelegates[hWnd] = newWndProc;
            }
        }

        private static nint NewWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam)
        {
            if (uMsg == WM_WINDOWPOSCHANGING)
            {
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & SWP_NOMOVE) == 0)
                {
                    GetWindowRect(hWnd, out var rect);
                    pos.x     =  rect.Left;
                    pos.y     =  rect.Top;
                    pos.flags |= SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, true);
                }
            }

            return CallWindowProc(windowProcMap[hWnd], hWnd, uMsg, wParam, lParam);
        }

        public static void Cleanup()
        {
            foreach (var hWnd in windowProcMap.Keys.ToList()) 
                UnlockWindow(hWnd);
        }
    }
}
