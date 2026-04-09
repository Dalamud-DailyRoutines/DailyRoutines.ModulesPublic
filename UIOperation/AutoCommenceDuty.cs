using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic;

public class AutoCommenceDuty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCommenceDutyTitle"),
        Description = Lang.Get("AutoCommenceDutyDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["Cindy-Master"]
    };

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinderConfirm", OnAddonSetup);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,   "ContentsFinderConfirm", OnAddonSetup);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

    private static unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (args.Addon == nint.Zero) return;

        var addon = args.Addon.ToStruct();
        if (addon->AtkValues[7].UInt != 0)
            return;

        ((AddonContentsFinderConfirm*)addon)->CommenceButton->Click();
    }
}
