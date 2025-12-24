using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastMinimizeWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastMinimizeWindowTitle"),
        Description = GetLoc("FastMinimizeWindowDescription", CommandMini, CommandTray),
        Category    = ModuleCategories.General,
        Author      = ["Rorinnn"]
    };

    private static Config ModuleConfig = null!;
    
    private static NotifyIcon? TrayIcon;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        CommandManager.AddSubCommand(CommandMini, new(OnMinimizeCommand) { HelpMessage = GetLoc("FastMinimizeWindow-MinimizeToTaskbar") });
        CommandManager.AddSubCommand(CommandTray, new(OnTrayCommand) { HelpMessage = GetLoc("FastMinimizeWindow-MinimizeToTray") });

        WindowManager.Draw += DrawMinimizeButton;

        if (ModuleConfig.AlwaysAddTrayIcon)
            CreateTrayIcon();
    }
    
    protected override void Uninit()
    {
        WindowManager.Draw -= DrawMinimizeButton;
        CommandManager.RemoveSubCommand(CommandMini);
        CommandManager.RemoveSubCommand(CommandTray);
        
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd != nint.Zero && !IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SwShow);
            SetForegroundWindow(hwnd);
        }
        
        DisposeTrayIcon();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));
        using (ImRaii.PushIndent())
        {
            ImGui.Text($"/pdr {CommandMini} → {GetLoc("FastMinimizeWindow-MinimizeToTaskbar")}");
            ImGui.Text($"/pdr {CommandTray} → {GetLoc("FastMinimizeWindow-MinimizeToTray")}");
        }
        
        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("FastMinimizeWindow-AlwaysAddTrayIcon"), ref ModuleConfig.AlwaysAddTrayIcon))
        {
            if (ModuleConfig.AlwaysAddTrayIcon)
                CreateTrayIcon();
            else
                DisposeTrayIcon();

            SaveConfig(ModuleConfig);
        }
        ImGuiOm.HelpMarker(GetLoc("FastMinimizeWindow-AlwaysAddTrayIcon-Help"));
        
        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("FastMinimizeWindow-DrawButton"), ref ModuleConfig.DrawButton))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.DrawButton)
        {
            using (ImRaii.ItemWidth(250f * GlobalFontScale))
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("FastMinimizeWindow-IsTransparentWhenNotHovered"), ref ModuleConfig.IsTransparentWhenNotHovered))
                    SaveConfig(ModuleConfig);

                if (ImGui.SliderFloat(GetLoc("Scale"), ref ModuleConfig.Scale, 0.1f, 2f))
                    ModuleConfig.Scale = Math.Clamp(ModuleConfig.Scale, 0.1f, 2f);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SaveConfig(ModuleConfig);

                DrawBehaviorCombo(GetLoc("FastMinimizeWindow-LeftClickBehavior"), ref ModuleConfig.LeftClickBehavior);
                DrawBehaviorCombo(GetLoc("FastMinimizeWindow-RightClickBehavior"), ref ModuleConfig.RightClickBehavior);

                using (var combo = ImRaii.Combo(GetLoc("Position"), GetLoc($"{ModuleConfig.Position}")))
                {
                    if (combo)
                    {
                        foreach (var buttonPosition in Enum.GetValues<ButtonPosition>())
                        {
                            if (ImGui.Selectable(GetLoc($"{buttonPosition}", buttonPosition == ModuleConfig.Position)))
                            {
                                ModuleConfig.Position = buttonPosition;
                                ModuleConfig.Save(this);
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
                SaveConfig(ModuleConfig);
            }
        }
    }

    private void DrawMinimizeButton()
    {
        if (!ModuleConfig.DrawButton) return;

        var buttonSize = 20f * ModuleConfig.Scale;
        var windowPos = ModuleConfig.Position switch
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

        if (ImGui.Begin("##DRMinimizeButton", ButtonWindowFlags))
        {
            using var color = ModuleConfig.IsTransparentWhenNotHovered ? ImRaii.PushColor(ImGuiCol.Button, 0u) : null;
            if (ImGui.Button("##MinimizeBtn", new Vector2(buttonSize, buttonSize)))
                HandleClick(ModuleConfig.LeftClickBehavior);

            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                HandleClick(ModuleConfig.RightClickBehavior);

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
            ShowWindow(hwnd, SwMinimize);
    }

    private void TryMinimizeToTray()
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd == nint.Zero) return;

        if (TrayIcon?.Visible is not true && !CreateTrayIcon())
            return;

        ShowWindow(hwnd, SwHide);
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
        if (TrayIcon?.Visible is true) return true;

        DisposeTrayIcon();

        try
        {
            TrayIcon = new NotifyIcon
            {
                Icon = TryExtractGameIcon() ?? SystemIcons.Application,
                Text = Info.Title,
                Visible = true
            };

            TrayIcon.Click += OnTrayIconClick;
            return true;
        }
        catch
        {
            TrayIcon = null;
            return false;
        }
    }

    private static void OnTrayIconClick(object? sender, EventArgs e)
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd == nint.Zero) return;

        if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SwShow);
            SetForegroundWindow(hwnd);

            if (!ModuleConfig.AlwaysAddTrayIcon)
                DisposeTrayIcon();
        }
        else
        {
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
    }

    private static void DisposeTrayIcon()
    {
        if (TrayIcon is null) return;

        TrayIcon.Click -= OnTrayIconClick;
        TrayIcon.Visible = false;
        TrayIcon.Dispose();
        TrayIcon = null;
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

    private const string CommandMini = "mini";
    private const string CommandTray = "tray";

    private const int SwMinimize = 6;
    private const int SwHide     = 0;
    private const int SwShow     = 5;
    private const int SwRestore  = 9;

    private const ImGuiWindowFlags ButtonWindowFlags =
        ImGuiWindowFlags.AlwaysAutoResize      |
        ImGuiWindowFlags.NoNavFocus            |
        ImGuiWindowFlags.NoFocusOnAppearing    |
        ImGuiWindowFlags.NoBringToFrontOnFocus |
        ImGuiWindowFlags.NoTitleBar            |
        ImGuiWindowFlags.NoMove                |
        ImGuiWindowFlags.NoBackground          |
        ImGuiWindowFlags.NoScrollbar           |
        ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly Dictionary<ClickBehavior, string> BehaviorNames = new()
    {
        [ClickBehavior.None]              = LuminaWrapper.GetAddonText(7),
        [ClickBehavior.MinimizeToTaskbar] = GetLoc("FastMinimizeWindow-MinimizeToTaskbar"),
        [ClickBehavior.MinimizeToTray]    = GetLoc("FastMinimizeWindow-MinimizeToTray")
    };

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

    private class Config : ModuleConfiguration
    {
        public bool           DrawButton = true;
        public float          Scale      = 0.5f;
        public ButtonPosition Position   = ButtonPosition.TopRight;
        public bool           IsTransparentWhenNotHovered;
        public ClickBehavior  LeftClickBehavior  = ClickBehavior.MinimizeToTaskbar;
        public ClickBehavior  RightClickBehavior = ClickBehavior.MinimizeToTray;

        public bool AlwaysAddTrayIcon;
    }
}
