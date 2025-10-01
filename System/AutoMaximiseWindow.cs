using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public class AutoMaximiseWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMaximiseWindowTitle"),
        Description = GetLoc("AutoMaximiseWindowDescription", Command),
        Category    = ModuleCategories.System,
        Author      = ["Bill"]
    };

    private const string Command = "maxwin";

    private const int SW_SHOWMAXIMIZED = 3; // 最大化窗口

    protected override void Init()
    {
        ControlGameWindow(SW_SHOWMAXIMIZED);

        CommandManager.AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("AutoMaximiseWindow-CommandHelp") });
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");

        using var indent = ImRaii.PushIndent();
        
        ImGui.Text($"/pdr {Command} → {GetLoc("AutoMaximiseWindow-CommandHelp")}");
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
        CommandManager.RemoveSubCommand(Command);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
