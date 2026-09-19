using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.ModulesPublic.CraftGather;

public unsafe class AutoCheckQuickGathering : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCheckQuickGatheringTitle"),
        Description = Lang.Get("AutoCheckQuickGatheringDescription"),
        Category    = ModuleCategory.CraftGather
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "Gathering", OnAddon);
    
    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);

    private static void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;
        
        addon->Callback(130, true);
    }
}
