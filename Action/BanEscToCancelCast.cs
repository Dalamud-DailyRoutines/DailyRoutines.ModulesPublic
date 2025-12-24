using DailyRoutines.Abstracts;
using DailyRoutines.Managers;

namespace DailyRoutines.ModulesPublic;

public class BanEscToCancelCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BanEscToCancelCastTitle"),
        Description = GetLoc("BanEscToCancelCastDescription"),
        Category    = ModuleCategories.Action,
    };

    protected override void Init() => 
        ExecuteCommandManager.RegPre(OnPreUseCommand);

    private static void OnPreUseCommand(
        ref bool isPrevented, ref ExecuteCommandFlag command, ref uint param1, ref uint param2, ref uint param3, ref uint param4)
    {
        if (command != ExecuteCommandFlag.CancelCast) return;
        isPrevented = true;
    }

    protected override void Uninit() => 
        ExecuteCommandManager.Unreg(OnPreUseCommand);
}
