using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyConfirm : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyDutyConfirmTitle"),
        Description = GetLoc("AutoNotifyDutyConfirmDescription"),
        Category    = ModuleCategories.Notice,
    };

    protected override void Init() => 
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinderConfirm", OnAddonSetup);

    private static unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        var dutyName = MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[1].String.Value);
        if (string.IsNullOrWhiteSpace(dutyName)) return;

        var loc = GetLoc("AutoNotifyDutyConfirm-NoticeMessage", dutyName);
        NotificationInfo(loc);
        Speak(loc);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddonSetup);
}
