using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoMaximiseWindow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMaximiseWindowTitle"),
        Description = Lang.Get("AutoMaximiseWindowDescription", COMMAND),
        Category    = ModuleCategory.System,
        Author      = ["Bill"]
    };

    protected override void Init()
    {
        ControlGameWindow(SW_SHOWMAXIMIZED);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoMaximiseWindow-CommandHelp") });
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        using var indent = ImRaii.PushIndent();

        ImGui.TextUnformatted($"/pdr {COMMAND} → {Lang.Get("AutoMaximiseWindow-CommandHelp")}");
    }

    private static void OnCommand(string command, string args) =>
        ControlGameWindow(SW_SHOWMAXIMIZED);

    private static unsafe void ControlGameWindow(int nCmdShow)
    {
        try
        {
            ShowWindow(Framework.Instance()->GameWindow->WindowHandle, nCmdShow);
        }
        catch
        {
            // ignored
        }
    }

    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    
    #region 常量

    private const string COMMAND = "maxwin";

    private const int SW_SHOWMAXIMIZED = 3; // 最大化窗口

    #endregion
}
