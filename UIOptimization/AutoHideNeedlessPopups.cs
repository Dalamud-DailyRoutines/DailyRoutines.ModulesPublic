using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

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
        "_NotificationCircleBook",
        "AchievementInfo",
        "RecommendList",
        "PlayGuide",
        "HowTo",
        "WebLauncher",
        "LicenseViewer"
    ];

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames, OnAddon);
    
    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;
        
        addon->RootNode->ToggleVisibility(false);
        addon->Close(false);
        addon->FireCloseCallback();
    }
}
