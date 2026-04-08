using System.Collections.Frozen;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastMinimizeWindow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastMinimizeWindowTitle"),
        Description = Lang.Get("FastMinimizeWindowDescription", COMMAND_MINI, COMMAND_TRAY),
        Category    = ModuleCategory.General,
        Author      = ["Rorinnn"]
    };
    
    private Config      config = null!;
    private NotifyIcon? trayIcon;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        CommandManager.Instance().AddSubCommand(COMMAND_MINI, new(OnMinimizeCommand) { HelpMessage = Lang.Get("FastMinimizeWindow-MinimizeToTaskbar") });
        CommandManager.Instance().AddSubCommand(COMMAND_TRAY, new(OnTrayCommand) { HelpMessage     = Lang.Get("FastMinimizeWindow-MinimizeToTray") });

        WindowManager.Instance().PostDraw += DrawMinimizeButton;

        if (config.AlwaysAddTrayIcon)
            CreateTrayIcon();
    }

    protected override void Uninit()
    {
        WindowManager.Instance().PostDraw -= DrawMinimizeButton;
        CommandManager.Instance().RemoveSubCommand(COMMAND_MINI);
        CommandManager.Instance().RemoveSubCommand(COMMAND_TRAY);

        if (IsEnabled)
        {
            var hwnd = Framework.Instance()->GameWindow->WindowHandle;

            if (hwnd != nint.Zero && !IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            }
        }

        DisposeTrayIcon();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Command"));

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"/pdr {COMMAND_MINI} → {Lang.Get("FastMinimizeWindow-MinimizeToTaskbar")}");
            ImGui.TextUnformatted($"/pdr {COMMAND_TRAY} → {Lang.Get("FastMinimizeWindow-MinimizeToTray")}");
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastMinimizeWindow-AlwaysAddTrayIcon"), ref config.AlwaysAddTrayIcon))
        {
            if (config.AlwaysAddTrayIcon)
                CreateTrayIcon();
            else
                DisposeTrayIcon();

            config.Save(this);
        }

        ImGuiOm.HelpMarker(Lang.Get("FastMinimizeWindow-AlwaysAddTrayIcon-Help"));

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("FastMinimizeWindow-DrawButton"), ref config.DrawButton))
            config.Save(this);

        if (config.DrawButton)
        {
            using (ImRaii.ItemWidth(250f * GlobalUIScale))
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(Lang.Get("FastMinimizeWindow-IsTransparentWhenNotHovered"), ref config.IsTransparentWhenNotHovered))
                    config.Save(this);

                if (ImGui.SliderFloat(Lang.Get("Scale"), ref config.Scale, 0.1f, 2f))
                    config.Scale = Math.Clamp(config.Scale, 0.1f, 2f);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                DrawBehaviorCombo(Lang.Get("FastMinimizeWindow-LeftClickBehavior"),  ref config.LeftClickBehavior);
                DrawBehaviorCombo(Lang.Get("FastMinimizeWindow-RightClickBehavior"), ref config.RightClickBehavior);

                using (var combo = ImRaii.Combo(Lang.Get("Position"), Lang.Get($"{config.Position}")))
                {
                    if (combo)
                    {
                        foreach (var buttonPosition in Enum.GetValues<ButtonPosition>())
                        {
                            if (ImGui.Selectable(Lang.Get($"{buttonPosition}", buttonPosition == config.Position)))
                            {
                                config.Position = buttonPosition;
                                config.Save(this);
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawBehaviorCombo(string label, ref ClickBehavior behavior)
    {
        using var combo = ImRaii.Combo(label, BehaviorNames.GetValueOrDefault(behavior, LuminaWrapper.GetAddonText(7)));
        if (!combo) return;

        foreach (var (behaviour, name) in BehaviorNames)
        {
            if (ImGui.Selectable(name, behavior == behaviour))
            {
                behavior = behaviour;
                config.Save(this);
            }
        }
    }

    private void DrawMinimizeButton()
    {
        if (!config.DrawButton) return;

        var buttonSize = 20f * config.Scale;
        var windowPos = config.Position switch
        {
            ButtonPosition.TopLeft     => new Vector2(0f,                                            0f),
            ButtonPosition.TopRight    => new Vector2(ImGuiHelpers.MainViewport.Size.X - buttonSize, 0f),
            ButtonPosition.BottomLeft  => new Vector2(0f,                                            ImGuiHelpers.MainViewport.Size.Y - buttonSize),
            ButtonPosition.BottomRight => ImGuiHelpers.MainViewport.Size - new Vector2(buttonSize),
            _                          => Vector2.Zero
        };

        using var style1 = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var style2 = ImRaii.PushStyle(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(windowPos);

        if (ImGui.Begin("##DRMinimizeButton", BUTTON_WINDOW_FLAGS))
        {
            using var color = config.IsTransparentWhenNotHovered ? ImRaii.PushColor(ImGuiCol.Button, 0u) : null;
            if (ImGui.Button("##MinimizeBtn", new Vector2(buttonSize, buttonSize)))
                HandleClick(config.LeftClickBehavior);

            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                HandleClick(config.RightClickBehavior);

            ImGui.End();
        }
    }

    private void HandleClick(ClickBehavior behavior)
    {
        switch (behavior)
        {
            case ClickBehavior.MinimizeToTaskbar:
                TryMinimize();
                break;
            case ClickBehavior.MinimizeToTray:
                TryMinimizeToTray();
                break;
        }
    }

    private static void OnMinimizeCommand(string command, string args) => TryMinimize();

    private void OnTrayCommand(string command, string args) => TryMinimizeToTray();

    private static void TryMinimize()
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd != nint.Zero)
            ShowWindow(hwnd, SW_MINIMIZE);
    }

    private void TryMinimizeToTray()
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd == nint.Zero) return;

        if (trayIcon?.Visible is not true && !CreateTrayIcon())
            return;

        ShowWindow(hwnd, SW_HIDE);
    }

    private static Icon? TryExtractGameIcon()
    {
        var mainModule = Process.GetCurrentProcess().MainModule;
        return mainModule?.FileName is { } fileName
                   ? Icon.ExtractAssociatedIcon(fileName)
                   : null;
    }

    private bool CreateTrayIcon()
    {
        if (trayIcon?.Visible is true) return true;

        DisposeTrayIcon();

        try
        {
            trayIcon = new NotifyIcon
            {
                Icon    = TryExtractGameIcon() ?? SystemIcons.Application,
                Text    = Info.Title,
                Visible = true
            };

            trayIcon.Click += OnTrayIconClick;
            return true;
        }
        catch
        {
            trayIcon = null;
            return false;
        }
    }

    private void OnTrayIconClick(object? sender, EventArgs e)
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd == nint.Zero) return;

        if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);

            if (!config.AlwaysAddTrayIcon)
                DisposeTrayIcon();
        }
        else
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }

    private void DisposeTrayIcon()
    {
        if (trayIcon is null) return;

        trayIcon.Click   -= OnTrayIconClick;
        trayIcon.Visible =  false;
        trayIcon.Dispose();
        trayIcon = null;
    }

    private class Config : ModuleConfig
    {
        public bool           AlwaysAddTrayIcon;
        public bool           DrawButton = true;
        public bool           IsTransparentWhenNotHovered;
        public ClickBehavior  LeftClickBehavior  = ClickBehavior.MinimizeToTaskbar;
        public ButtonPosition Position           = ButtonPosition.TopRight;
        public ClickBehavior  RightClickBehavior = ClickBehavior.MinimizeToTray;
        public float          Scale              = 0.5f;
    }

    #region 预定义

    private enum ClickBehavior
    {
        None,
        MinimizeToTaskbar,
        MinimizeToTray
    }

    private enum ButtonPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    #endregion

    #region 数据

    private const string COMMAND_MINI = "mini";
    private const string COMMAND_TRAY = "tray";

    private const int SW_MINIMIZE = 6;
    private const int SW_HIDE     = 0;
    private const int SW_SHOW     = 5;
    private const int SW_RESTORE  = 9;

    private const ImGuiWindowFlags BUTTON_WINDOW_FLAGS =
        ImGuiWindowFlags.AlwaysAutoResize      |
        ImGuiWindowFlags.NoNavFocus            |
        ImGuiWindowFlags.NoFocusOnAppearing    |
        ImGuiWindowFlags.NoBringToFrontOnFocus |
        ImGuiWindowFlags.NoTitleBar            |
        ImGuiWindowFlags.NoMove                |
        ImGuiWindowFlags.NoBackground          |
        ImGuiWindowFlags.NoScrollbar           |
        ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly FrozenDictionary<ClickBehavior, string> BehaviorNames = new Dictionary<ClickBehavior, string>
    {
        [ClickBehavior.None]              = LuminaWrapper.GetAddonText(7),
        [ClickBehavior.MinimizeToTaskbar] = Lang.Get("FastMinimizeWindow-MinimizeToTaskbar"),
        [ClickBehavior.MinimizeToTray]    = Lang.Get("FastMinimizeWindow-MinimizeToTray")
    }.ToFrozenDictionary();

    #endregion

    #region Win32

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    #endregion
}
