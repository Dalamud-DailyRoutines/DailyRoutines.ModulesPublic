using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoHideNeedlessPopups : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideNeedlessPopupsTitle"),
        Description = Lang.Get("AutoHideNeedlessPopupsDescription"),
        Category    = ModuleCategory.Interface
    };

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreShow, AddonNames, OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreDraw, AddonNames, OnAddon);
    }

    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

    private static void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        if (type == AddonEvent.PreShow)
            args.PreventOriginal();
        else
            addon->Hide(true, true, 1);
    }

    #region 常量

    private static readonly FrozenSet<string> AddonNames =
    [
        "_NotificationCircleBook",
        "_NotificationAchieveLogIn",
        "_NotificationAchieveZoneIn",
        "AchievementInfo",
        "RecommendList",
        "PlayGuide",
        "HowTo",
        "WebLauncher",
        "LicenseViewer",
        "WKSEnterInfo"
    ];

    #endregion
}
