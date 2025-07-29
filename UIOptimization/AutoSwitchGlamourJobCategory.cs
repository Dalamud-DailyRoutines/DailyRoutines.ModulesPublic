using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class AutoSwitchGlamourJobCategory : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoSwitchGlamourJobCategoryTitle"),
        Description = GetLoc("AutoSwitchGlamourJobCategoryDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["ECSS11"]
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "MiragePrismPrismBox", OnMiragePrismPrismBox);

    private static unsafe void OnMiragePrismPrismBox(AddonEvent type, AddonArgs args)
    {
        // 获取当前职业和幻化台 Addon
        var job = LocalPlayerState.ClassJob;
        var addon = (AddonMiragePrismPrismBox*)args.Addon;
        addon->Param = (int)job;
    }

    protected override void Uninit() =>
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, "MiragePrismPrismBox");
}
