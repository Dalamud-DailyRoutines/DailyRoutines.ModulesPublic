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

public class MinimizeWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MinimizeWindowTitle"),
        Description = GetLoc("MinimizeWindowDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Rorinnn"]
    };

    private const string CommandMini = "mini";
    private const string CommandTray = "tray";

    private const int SwMinimize = 6;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private const ImGuiWindowFlags ButtonWindowFlags =
        ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoNavFocus |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoBringToFrontOnFocus |
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly Dictionary<ClickBehavior, string> BehaviorNames = new()
    {
        [ClickBehavior.None] = LuminaWrapper.GetAddonText(7),
        [ClickBehavior.MinimizeToTaskbar] = GetLoc("MinimizeWindow-MinimizeToTaskbar"),
        [ClickBehavior.MinimizeToTray] = GetLoc("MinimizeWindow-MinimizeToTray")
    };

    private static Config ModuleConfig = null!;
    private static NotifyIcon? TrayIcon;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        CommandManager.AddSubCommand(CommandMini, new(OnMinimizeCommand) { HelpMessage = GetLoc("MinimizeWindow-MinimizeToTaskbar") });
        CommandManager.AddSubCommand(CommandTray, new(OnTrayCommand) { HelpMessage = GetLoc("MinimizeWindow-MinimizeToTray") });

        WindowManager.Draw += DrawMinimizeButton;

        if (ModuleConfig.PermaTrayIcon)
            CreateTrayIcon();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Command"));
        using (ImRaii.PushIndent())
        {
            ImGui.Text($"/pdr {CommandMini} → {GetLoc("MinimizeWindow-MinimizeToTaskbar")}");
            ImGui.Text($"/pdr {CommandTray} → {GetLoc("MinimizeWindow-MinimizeToTray")}");
        }

        if (ImGui.Checkbox(GetLoc("MinimizeWindow-PermaTrayIcon"), ref ModuleConfig.PermaTrayIcon))
        {
            if (ModuleConfig.PermaTrayIcon)
                CreateTrayIcon();
            else
                DisposeTrayIcon();

            SaveConfig(ModuleConfig);
        }
        ImGui.SameLine();
        ImGuiOm.HelpMarker(GetLoc("MinimizeWindow-PermaTrayIconHelp"));

        if (ImGui.Checkbox(GetLoc("MinimizeWindow-DisplayButton"), ref ModuleConfig.DisplayButton))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.DisplayButton)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("MinimizeWindow-TransparentButton"), ref ModuleConfig.TransparentButton))
                    SaveConfig(ModuleConfig);

                ImGui.SetNextItemWidth(100f * GlobalFontScale);
                ImGui.SliderFloat(GetLoc("Scale"), ref ModuleConfig.Scale, 0.1f, 2f);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    ModuleConfig.Scale = Math.Clamp(ModuleConfig.Scale, 0.1f, 2f);
                    SaveConfig(ModuleConfig);
                }

                DrawBehaviorCombo(GetLoc("MinimizeWindow-LeftClickBehavior"), ref ModuleConfig.LeftClickBehavior);
                DrawBehaviorCombo(GetLoc("MinimizeWindow-RightClickBehavior"), ref ModuleConfig.RightClickBehavior);
            }
        }
    }

    private void DrawBehaviorCombo(string label, ref ClickBehavior behavior)
    {
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        using var combo = ImRaii.Combo(label, GetBehaviorName(behavior));
        if (!combo) return;

        if (ImGui.Selectable(LuminaWrapper.GetAddonText(7), behavior == ClickBehavior.None))
        {
            behavior = ClickBehavior.None;
            SaveConfig(ModuleConfig);
        }
        if (ImGui.Selectable(GetLoc("MinimizeWindow-MinimizeToTaskbar"), behavior == ClickBehavior.MinimizeToTaskbar))
        {
            behavior = ClickBehavior.MinimizeToTaskbar;
            SaveConfig(ModuleConfig);
        }
        if (ImGui.Selectable(GetLoc("MinimizeWindow-MinimizeToTray"), behavior == ClickBehavior.MinimizeToTray))
        {
            behavior = ClickBehavior.MinimizeToTray;
            SaveConfig(ModuleConfig);
        }
    }

    private static string GetBehaviorName(ClickBehavior behavior) =>
        BehaviorNames.GetValueOrDefault(behavior, LuminaWrapper.GetAddonText(7));

    private void DrawMinimizeButton()
    {
        if (!ModuleConfig.DisplayButton) return;

        var buttonSize = 20f * ModuleConfig.Scale;
        var windowPos = new Vector2(ImGuiHelpers.MainViewport.Size.X - buttonSize, 0f);

        using var style1 = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var style2 = ImRaii.PushStyle(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(windowPos);

        if (ImGui.Begin("##DRMinimizeButton", ButtonWindowFlags))
        {
            using var color = ModuleConfig.TransparentButton ? ImRaii.PushColor(ImGuiCol.Button, 0u) : null;
            if (ImGui.Button("##MinimizeBtn", new Vector2(buttonSize, buttonSize)))
                HandleClick(ModuleConfig.LeftClickBehavior);

            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                HandleClick(ModuleConfig.RightClickBehavior);

            ImGui.End();
        }
    }

    private static void HandleClick(ClickBehavior behavior)
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

    private static void OnTrayCommand(string command, string args) => TryMinimizeToTray();

    private static unsafe void TryMinimize()
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd != nint.Zero)
            ShowWindow(hwnd, SwMinimize);
    }

    private static unsafe void TryMinimizeToTray()
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

    private static bool CreateTrayIcon()
    {
        if (TrayIcon?.Visible is true) return true;

        DisposeTrayIcon();

        try
        {
            TrayIcon = new NotifyIcon
            {
                Icon = TryExtractGameIcon() ?? SystemIcons.Application,
                Text = GetLoc("MinimizeWindowTitle"),
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

    private static unsafe void OnTrayIconClick(object? sender, EventArgs e)
    {
        var hwnd = Framework.Instance()->GameWindow->WindowHandle;
        if (hwnd == nint.Zero) return;

        if (!IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SwShow);
            SetForegroundWindow(hwnd);

            if (!ModuleConfig.PermaTrayIcon)
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

    protected override void Uninit()
    {
        unsafe
        {
            var hwnd = Framework.Instance()->GameWindow->WindowHandle;
            if (hwnd != nint.Zero && !IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SwShow);
                SetForegroundWindow(hwnd);
            }
        }

        WindowManager.Draw -= DrawMinimizeButton;
        CommandManager.RemoveSubCommand(CommandMini);
        CommandManager.RemoveSubCommand(CommandTray);
        DisposeTrayIcon();
    }

    private enum ClickBehavior
    {
        None = 0,
        MinimizeToTaskbar = 1,
        MinimizeToTray = 2
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    private class Config : ModuleConfiguration
    {
        public bool DisplayButton = true;
        public float Scale = 0.5f;
        public bool TransparentButton;
        public ClickBehavior LeftClickBehavior = ClickBehavior.MinimizeToTaskbar;
        public ClickBehavior RightClickBehavior = ClickBehavior.MinimizeToTray;
        public bool PermaTrayIcon;
    }
}
