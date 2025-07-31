using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoAcceptRepairRequest : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAcceptRepairRequestTitle"),
        Description = GetLoc("AutoAcceptRepairRequestDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["ECSS11"]
    };

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RepairRequest", OnRepairRequest);
    }
    
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RepairRequest");
    }

    private unsafe void OnRepairRequest(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon;
        if (!IsAddonAndNodesReady(addon)) return;

        var button = addon->GetComponentButtonById(33);
        if (button is null) return;
        button->ClickAddonButton(addon);
    }
}
