using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public unsafe class IgnoreWindowMinSizeLimit : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("IgnoreWindowMinSizeLimitTitle"),
        Description = Lang.Get("IgnoreWindowMinSizeLimitDescription"),
        Category    = ModuleCategory.System,
        Author      = ["Siren"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private int originalMinWidth  = 1024;
    private int originalMinHeight = 720;

    protected override void Init()
    {
        originalMinWidth  = GameWindow.Instance()->MinWidth;
        originalMinHeight = GameWindow.Instance()->MinHeight;

        GameWindow.Instance()->MinHeight = 1;
        GameWindow.Instance()->MinWidth  = 1;
    }

    protected override void Uninit()
    {
        if (!IsInitialized) return;

        GameWindow.Instance()->MinWidth  = originalMinWidth;
        GameWindow.Instance()->MinHeight = originalMinHeight;
    }
}
