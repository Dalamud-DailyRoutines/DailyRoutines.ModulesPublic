using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideNeedlessPopups : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideNeedlessPopupsTitle"),
        Description = Lang.Get("AutoHideNeedlessPopupsDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    protected override void Init() =>
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames, OnAddon);

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        addon->RootNode->ToggleVisibility(false);
        addon->Close(false);
        addon->FireCloseCallback();
    }
    
    #region 常量

    private static readonly FrozenSet<string> AddonNames =
    [
        "_NotificationCircleBook",
        "AchievementInfo",
        "RecommendList",
        "PlayGuide",
        "HowTo",
        "WebLauncher",
        "LicenseViewer"
    ];

    #endregion
}
