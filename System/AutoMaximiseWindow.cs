using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public class AutoMaximiseWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMaximiseWindowTitle"),
        Description = GetLoc("AutoMaximiseWindowDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Bill"]
    };

    private const string Command = "/pdrwin";

    private static readonly Dictionary<string, (Action EnqueueAction, int State)> CommandArgs = new()
    {
        ["hide"] = (() => ControlGameWindow(SW_HIDE), SW_HIDE),
        ["normal"] = (() => ControlGameWindow(SW_SHOWNORMAL), SW_SHOWNORMAL),
        ["min"] = (() => ControlGameWindow(SW_SHOWMINIMIZED), SW_SHOWMINIMIZED),
        ["max"] = (() => ControlGameWindow(SW_SHOWMAXIMIZED), SW_SHOWMAXIMIZED),
        ["noactive"] = (() => ControlGameWindow(SW_SHOWNOACTIVATE), SW_SHOWNOACTIVATE),
        ["show"] = (() => ControlGameWindow(SW_SHOW), SW_SHOW),
        ["minimize"] = (() => ControlGameWindow(SW_MINIMIZE), SW_MINIMIZE),
        ["minno"] = (() => ControlGameWindow(SW_SHOWMINNOACTIVE), SW_SHOWMINNOACTIVE),
        ["showna"] = (() => ControlGameWindow(SW_SHOWNA), SW_SHOWNA),
        ["restore"] = (() => ControlGameWindow(SW_RESTORE), SW_RESTORE),
        ["default"] = (() => ControlGameWindow(SW_SHOWDEFAULT), SW_SHOWDEFAULT),
        ["force"] = (() => ControlGameWindow(SW_FORCEMINIMIZE), SW_FORCEMINIMIZE),
    };

    // 窗口状态常量
    private const int SW_HIDE = 0;            // 隐藏窗口
    private const int SW_SHOWNORMAL = 1;      // 正常显示窗口
    private const int SW_SHOWMINIMIZED = 2;   // 最小化窗口
    private const int SW_SHOWMAXIMIZED = 3;   // 最大化窗口
    private const int SW_SHOWNOACTIVATE = 4;  // 显示窗口但不激活
    private const int SW_SHOW = 5;            // 显示窗口
    private const int SW_MINIMIZE = 6;        // 最小化窗口
    private const int SW_SHOWMINNOACTIVE = 7; // 最小化窗口但不激活
    private const int SW_SHOWNA = 8;          // 显示窗口但维持当前激活状态
    private const int SW_RESTORE = 9;         // 恢复窗口
    private const int SW_SHOWDEFAULT = 10;    // 根据程序的启动方式显示窗口
    private const int SW_FORCEMINIMIZE = 11;  // 最小化窗口

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void Init()
    {
        ControlGameWindow(SW_SHOWMAXIMIZED);

        CommandManager.AddCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoMaximiseWindow-CommandHelp") });
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("Command")}:");

        using var indent = ImRaii.PushIndent();
        using var table = ImRaii.Table("CommandsTable", 2, ImGuiTableFlags.Borders,
                                       (ImGui.GetContentRegionAvail() / 2) with { Y = 0 });
        if (!table) return;

        ImGui.TableSetupColumn(GetLoc("Argument"), ImGuiTableColumnFlags.WidthStretch, 10);
        ImGui.TableSetupColumn(GetLoc("Description"), ImGuiTableColumnFlags.WidthStretch, 20);

        ImGui.TableHeadersRow();

        foreach (var command in CommandArgs)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text(command.Key);

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(command.Key);
                NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {command.Key}");
            }

            ImGui.TableNextColumn();
            ImGui.Text(GetLoc($"AutoMaximiseWindow-{command.Key}"));
        }
    }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();

        foreach (var commandPair in CommandArgs)
        {
            if (args == commandPair.Key)
            {
                commandPair.Value.EnqueueAction();
                NotificationInfo(GetLoc($"AutoMaximiseWindow-Success-{commandPair.Key}"));
                return;
            }
        }
    }

    private static void ControlGameWindow(int nCmdShow)
    {
        try
        {
            ShowWindow(Process.GetCurrentProcess().MainWindowHandle, nCmdShow);
        }
        catch
        {
            NotificationError(GetLoc("AutoMaximiseWindow-CommandFailed"));
        }
    }

    protected override void Uninit()
    {
        CommandManager.RemoveCommand(Command);
    }
}
