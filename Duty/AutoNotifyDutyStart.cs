using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.DutyState;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public class AutoNotifyDutyStart : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyStartTitle"),
        Description = Lang.Get("AutoNotifyDutyStartDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override void Init() =>
        IDutyState.Instance().DutyStarted += OnDutyStart;

    protected override void Uninit() =>
        IDutyState.Instance().DutyStarted -= OnDutyStart;

    private static void OnDutyStart
    (
        IDutyStateEventArgs args
    ) =>
        NotifyHelper.Instance().TrayInfo
        (
            GameState.ContentFinderConditionData.Name.ToString(),
            Lang.Get("AutoNotifyDutyStart-NotificationMessage")
        );
}
