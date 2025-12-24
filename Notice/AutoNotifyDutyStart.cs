using DailyRoutines.Abstracts;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyDutyStart : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoNotifyDutyStartTitle"),
        Description = GetLoc("AutoNotifyDutyStartDescription"),
        Category    = ModuleCategories.Notice,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() => 
        DService.DutyState.DutyStarted += OnDutyStart;

    private static void OnDutyStart(object? sender, ushort e)
    {
        var message = GetLoc("AutoNotifyDutyStart-NotificationMessage");
        NotificationInfo(message);
        Speak(message);
    }

    protected override void Uninit() => 
        DService.DutyState.DutyStarted -= OnDutyStart;
}
