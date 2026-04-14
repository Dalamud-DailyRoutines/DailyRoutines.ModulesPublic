using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoClaimItemIgnoringMismatchJobAndLevel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoClaimItemIgnoringMismatchJobAndLevelTitle"),
        Description = Lang.Get("AutoClaimItemIgnoringMismatchJobAndLevelDescription"),
        Category    = ModuleCategory.UIOperation
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddon);
        if (SelectYesno->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit() => 
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs? args)
    {
        if (!SelectYesno->IsAddonAndNodesReady()) return;

        AddonSelectYesnoEvent.ClickYes
        (
            [
                LuminaWrapper.GetAddonText(1962),
                LuminaWrapper.GetAddonText(2436),
                LuminaWrapper.GetAddonText(11502),
                LuminaWrapper.GetAddonText(11508)
            ]
        );
    }
}
