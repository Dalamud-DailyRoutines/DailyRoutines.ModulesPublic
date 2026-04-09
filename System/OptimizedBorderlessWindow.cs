using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedBorderlessWindow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedBorderlessWindowTitle"),
        Description = Lang.Get("OptimizedBorderlessWindowDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig WindowProcessSig =
        new("40 55 53 56 57 41 54 41 56 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 E0");
    private delegate nint                        WindowProcessDelegate(ulong hWnd, uint uMsg, ulong wParam, long lParam);
    private          Hook<WindowProcessDelegate> WindowProcessHook = null!;

    private static readonly CompSig SetMainWindowBorderlessSig =
        new("40 53 48 83 EC 60 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 48 8B D9 48 8B 49 18");
    private delegate void                                  SetMainWindowBorderlessDelegate(GameWindow* self, bool borderless);
    private          Hook<SetMainWindowBorderlessDelegate> SetMainWindowBorderlessHook = null!;

    protected override void Init()
    {
        WindowProcessHook           ??= WindowProcessSig.GetHook<WindowProcessDelegate>(WindowProcessDetour);
        SetMainWindowBorderlessHook ??= SetMainWindowBorderlessSig.GetHook<SetMainWindowBorderlessDelegate>(SetMainWindowBorderlessDetour);

        WindowProcessHook.Enable();
        SetMainWindowBorderlessHook.Enable();

        if (Framework.Instance()->GameWindow->Borderless)
            MakeBorderless();
    }

    protected override void Uninit()
    {
        if (IsEnabled && Framework.Instance()->GameWindow->Borderless)
        {
            var windowHandle = Framework.Instance()->GameWindow->WindowHandle;
            WinAPI.SetWindowLongPtrW(windowHandle, WinAPI.GWLP_STYLE, 0x80000000); // WS_POPUP
            WinAPI.ShowWindow(windowHandle, WinAPI.SW_SHOW_MAXIMIZED);
            WinAPI.SetWindowPos(windowHandle, 0, 0, 0, 0, 0, WinAPI.SWP_NO_SIZE | WinAPI.SWP_NO_MOVE | WinAPI.SWP_NO_Z_ORDER | WinAPI.SWP_FRAME_CHANGED);
        }
    }

    private nint WindowProcessDetour(ulong hWnd, uint uMsg, ulong wParam, long lParam)
    {
        switch (uMsg)
        {
            case WinAPI.WM_WINDOW_POS_CHANGING:
                if (Framework.Instance()->GameWindow->Borderless)
                {
                    var windowPos = (WinAPI.WindowPos*)lParam;

                    if ((windowPos->Flags & WinAPI.SWP_NO_SIZE) == 0)
                    {
                        // 调整无边框窗口大小以覆盖整个显示器
                        WinAPI.Rect rect = new()
                            { Left = windowPos->X, Top = windowPos->Y, Right = windowPos->X + windowPos->CX, Bottom = windowPos->Y + windowPos->CY };
                        ConvertToBorderlessRect(ref rect);
                        windowPos->X  = rect.Left;
                        windowPos->Y  = rect.Top;
                        windowPos->CX = rect.Right  - windowPos->X;
                        windowPos->CY = rect.Bottom - windowPos->Y;
                        SyncSwapchainResolution(windowPos->CX, windowPos->CY);
                    }

                    return 1;
                }

                break;

            case WinAPI.WM_NC_CALC_SIZE:
                if (wParam != 0 && Framework.Instance()->GameWindow->Borderless)
                    return 0;
                break;
        }

        return WindowProcessHook.Original(hWnd, uMsg, wParam, lParam);
    }

    private void SetMainWindowBorderlessDetour(GameWindow* self, bool borderless)
    {
        if (borderless)
        {
            self->Borderless = true;
            MakeBorderless();
        }
        else
            SetMainWindowBorderlessHook.Original(self, borderless);
    }

    private static void MakeBorderless()
    {
        var windowHandle = Framework.Instance()->GameWindow->WindowHandle;

        WinAPI.Rect rect;
        WinAPI.GetWindowRect(windowHandle, &rect);
        ConvertToBorderlessRect(ref rect);

        WinAPI.SetWindowLongPtrW(windowHandle, WinAPI.GWLP_STYLE, 0x80CE0000); // WS_POPUP | WS_OVERLAPPEDWINDOW & ~WS_MAXIMIZEBOX
        WinAPI.ShowWindow(windowHandle, WinAPI.SW_RESTORE);
        WinAPI.SetWindowPos(windowHandle, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, WinAPI.SWP_NO_Z_ORDER | WinAPI.SWP_FRAME_CHANGED);
    }

    private static void SyncSwapchainResolution(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        var device = Device.Instance();
        if (device->NewWidth == width && device->NewHeight == height) return;

        device->NewWidth                = (uint)width;
        device->NewHeight               = (uint)height;
        device->RequestResolutionChange = 1;
    }

    private static void ConvertToBorderlessRect(ref WinAPI.Rect rect)
    {
        var originalRect = rect;
        var monitor      = WinAPI.MonitorFromRect(&originalRect, WinAPI.MONITOR_DEFAULT_TO_PRIMARY);

        if (monitor != 0)
        {
            var monitorInfo = new WinAPI.MonitorInfo { CbSize = sizeof(WinAPI.MonitorInfo) };
            if (WinAPI.GetMonitorInfoW(monitor, &monitorInfo))
                rect = monitorInfo.RCMonitor;
        }
    }

    private static class WinAPI
    {
        public const int  GWLP_STYLE                 = -16;
        public const int  SW_SHOW_MAXIMIZED          = 3;
        public const int  SW_RESTORE                 = 9;
        public const uint SWP_NO_SIZE                = 0x01;
        public const uint SWP_NO_MOVE                = 0x02;
        public const uint SWP_NO_Z_ORDER             = 0x04;
        public const uint SWP_FRAME_CHANGED          = 0x20;
        public const uint WM_WINDOW_POS_CHANGING     = 0x46;
        public const uint WM_NC_CALC_SIZE            = 0x83;
        public const uint MONITOR_DEFAULT_TO_PRIMARY = 1;

        [DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
        public static extern bool GetWindowRect(nint hWnd, Rect* lpRect);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
        public static extern ulong SetWindowLongPtrW(nint hWnd, int nIndex, ulong dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", ExactSpelling = true)]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "ShowWindow", ExactSpelling = true)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "MonitorFromRect", ExactSpelling = true)]
        public static extern ulong MonitorFromRect(Rect* lprc, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
        public static extern bool GetMonitorInfoW(ulong hmonitor, MonitorInfo* lpmi);

        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public struct MonitorInfo
        {
            public int  CbSize;
            public Rect RCMonitor;
            public Rect RCWork;
            public uint DwFlags;
        }

        public struct WindowPos
        {
            public nint Hwnd;
            public nint HwndInsertAfter;
            public int  X;
            public int  Y;
            public int  CX;
            public int  CY;
            public uint Flags;
        }
    }
}
