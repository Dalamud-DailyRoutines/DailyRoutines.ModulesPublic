using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyStart : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyStartTitle"),
        Description = Lang.Get("AutoNotifyDutyStartDescription"),
        Category    = ModuleCategory.Notice
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        DService.Instance().DutyState.DutyStarted += OnDutyStart;
    
    protected override void Uninit() =>
        DService.Instance().DutyState.DutyStarted -= OnDutyStart;

    private static void OnDutyStart(object? sender, ushort e)
    {
        var message = Lang.Get("AutoNotifyDutyStart-NotificationMessage");
        NotifyHelper.Instance().NotificationInfo(message);
        NotifyHelper.Speak(message);
    }
}
