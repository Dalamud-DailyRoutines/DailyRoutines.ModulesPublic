using DailyRoutines.Abstracts;

namespace DailyRoutines.Modules;

public class AutoNotifyDutyStart : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyDutyStartTitle"),
        Description = GetLoc("AutoNotifyDutyStartDescription"),
        Category = ModuleCategories.Notice,
    };

    public override void Init()
    {
        DService.DutyState.DutyStarted += OnDutyStart;
    }

    private static void OnDutyStart(object? sender, ushort e)
    {
        var message = Lang.Get("AutoNotifyDutyStart-NotificationMessage");
        NotificationInfo(message);
        Speak(message);
    }

    public override void Uninit()
    {
        DService.DutyState.DutyStarted -= OnDutyStart;
    }
}
