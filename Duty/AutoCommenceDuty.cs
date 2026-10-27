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
        Category    = ModuleCategory.Duty,
        Author      = ["Cindy-Master"]
    };

    protected override void Init()
    {
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "ContentsFinderConfirm", OnAddonSetup);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreDraw,   "ContentsFinderConfirm", OnAddonSetup);
    }

    protected override void Uninit() =>
        IAddonLifecycle.Instance().UnregisterListener(OnAddonSetup);

    private static unsafe void OnAddonSetup
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (args.Addon == nint.Zero) return;

        var addon = args.Addon.ToStruct();
        if (addon->AtkValues[7].UInt != 0)
            return;

        ((AddonContentsFinderConfirm*)addon)->CommenceButton->Click();
    }
}
