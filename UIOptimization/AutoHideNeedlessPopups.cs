using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.Modules;

public unsafe class AutoHideNeedlessPopups : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHideNeedlessPopupsTitle"),
        Description = GetLoc("AutoHideNeedlessPopupsDescription"),
        Category    = ModuleCategories.UIOptimization,
    };

    private static readonly HashSet<string> AddonNames = 
    [
        "_NotificationCircleBook", "AchievementInfo", "RecommendList", "PlayGuide", "HowTo", "WebLauncher",
        "LicenseViewer"
    ];

    private static readonly CompSig AtkUnitBaseDrawSig = new("48 83 EC ?? F6 81 ?? ?? ?? ?? ?? 4C 8B C1 0F 84");
    private delegate void AtkUnitBaseDrawDelegate(AtkUnitBase* addon);
    private static Hook<AtkUnitBaseDrawDelegate>? AtkUnitBaseDrawHook;

    protected override void Init()
    {
        AtkUnitBaseDrawHook ??= AtkUnitBaseDrawSig.GetHook<AtkUnitBaseDrawDelegate>(AtkUnitBaseDrawDetour);
        AtkUnitBaseDrawHook.Enable();
    }

    private static void AtkUnitBaseDrawDetour(AtkUnitBase* addon)
    {
        if (addon == null) return;
        if (!AddonNames.Contains(addon->NameString))
        {
            AtkUnitBaseDrawHook.Original(addon);
            return;
        }
        
        addon->Close(false);
        addon->FireCloseCallback();
    }
}
