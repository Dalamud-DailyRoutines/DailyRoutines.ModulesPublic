using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class BanEscToCancelCast : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BanEscToCancelCastTitle"),
        Description = Lang.Get("BanEscToCancelCastDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init() =>
        ExecuteCommandManager.Instance().RegPre(OnPreUseCommand);
    
    protected override void Uninit() =>
        ExecuteCommandManager.Instance().Unreg(OnPreUseCommand);

    private static void OnPreUseCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.CancelCast) return;
        isPrevented = true;
    }
}
