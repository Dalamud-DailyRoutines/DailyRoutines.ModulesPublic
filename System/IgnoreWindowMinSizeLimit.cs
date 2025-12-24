using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public unsafe class IgnoreWindowMinSizeLimit : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("IgnoreWindowMinSizeLimitTitle"),
        Description = GetLoc("IgnoreWindowMinSizeLimitDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Siren"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static int OriginalMinWidth  = 1024;
    private static int OriginalMinHeight = 720;

    protected override void Init()
    {
        OriginalMinWidth  = GameWindow.Instance()->MinWidth;
        OriginalMinHeight = GameWindow.Instance()->MinHeight;
        
        GameWindow.Instance()->MinHeight = 1;
        GameWindow.Instance()->MinWidth  = 1;
    }

    protected override void Uninit()
    {
        if (!Initialized) return;
        
        GameWindow.Instance()->MinWidth  = OriginalMinWidth;
        GameWindow.Instance()->MinHeight = OriginalMinHeight;
    }
}
