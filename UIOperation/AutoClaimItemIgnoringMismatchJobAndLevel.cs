using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoClaimItemIgnoringMismatchJobAndLevel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoClaimItemIgnoringMismatchJobAndLevelTitle"),
        Description = GetLoc("AutoClaimItemIgnoringMismatchJobAndLevelDescription"),
        Category    = ModuleCategories.UIOperation
    };

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddon);
        if (IsAddonAndNodesReady(SelectYesno)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (!IsAddonAndNodesReady(SelectYesno)) return;
        
        ClickSelectYesnoYes
        ([
            LuminaWrapper.GetAddonText(1962), 
            LuminaWrapper.GetAddonText(2436), 
            LuminaWrapper.GetAddonText(11502), 
            LuminaWrapper.GetAddonText(11508)
        ]);
    }

    protected override void Uninit() => DService.AddonLifecycle.UnregisterListener(OnAddon);
}
