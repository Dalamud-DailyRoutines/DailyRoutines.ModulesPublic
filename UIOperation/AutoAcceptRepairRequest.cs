using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

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

    protected override unsafe void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RepairRequest", OnRepairRequest);
        if (RepairRequest is not null)
            OnRepairRequest(AddonEvent.PostSetup, null);
    }

    protected override void Uninit() => DService.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RepairRequest");

    private static unsafe void OnRepairRequest(AddonEvent type, AddonArgs args)
    {
        var addon = RepairRequest;
        if (!IsAddonAndNodesReady(addon)) return;

        var fullSelect = addon->GetComponentButtonById(6);
        if (fullSelect is not null) return;

        var repair = addon->GetComponentButtonById(33);
        if (repair is null) return;

        repair->ClickAddonButton(addon);
    }
}
