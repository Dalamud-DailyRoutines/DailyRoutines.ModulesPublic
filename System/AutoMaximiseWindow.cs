using DailyRoutines.Abstracts;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DailyRoutines.ModulesPublic;

public class AutoMaximiseWindow : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMaximiseWindowTitle"),
        Description = GetLoc("AutoMaximiseWindowDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Bill", "ArcaneDisgea"]
    };
    
    private const int SW_SHOWMAXIMIZED = 3; // Maximise window

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void Init()
    {
        MaximiseGameWindow();
    }

    private static void MaximiseGameWindow()
    {
        try
        {
            ShowWindow(Process.GetCurrentProcess().MainWindowHandle, SW_SHOWMAXIMIZED);
        }
        catch
        {
            // Ignored
        }
    }
}
