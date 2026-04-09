using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyConfirm : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyConfirmTitle"),
        Description = Lang.Get("AutoNotifyDutyConfirmDescription"),
        Category    = ModuleCategory.Notice
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinderConfirm", OnAddonSetup);
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

    private static unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        var dutyName = MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[1].String.Value);
        if (string.IsNullOrWhiteSpace(dutyName)) return;

        var loc = Lang.Get("AutoNotifyDutyConfirm-NoticeMessage", dutyName);
        NotifyHelper.Instance().NotificationInfo(loc);
        NotifyHelper.Speak(loc);
    }
}
