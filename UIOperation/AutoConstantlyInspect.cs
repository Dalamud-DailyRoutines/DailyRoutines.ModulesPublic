using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoConstantlyInspect : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoConstantlyInspectTitle"),
        Description = GetLoc("AutoConstantlyInspectDescription"),
        Category = ModuleCategories.UIOperation,
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemInspectionResult", OnAddon);

    protected override void ConfigUI() => ConflictKeyText();

    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (IsConflictKeyPressed())
        {
            NotificationSuccess(GetLoc("ConflictKey-InterruptMessage"));
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        var nextButton = addon->GetComponentButtonById(74);
        if (nextButton == null || !nextButton->IsEnabled) return;
        
        SendEvent(AgentId.ItemInspection, 3, 0);
        addon->Close(true);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);
}
