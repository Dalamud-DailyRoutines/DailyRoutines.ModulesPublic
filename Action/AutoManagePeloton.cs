using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoManagePeloton : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoManagePelotonTitle"),
        Description = GetLoc("AutoManagePelotonDescription"),
        Category    = ModuleCategories.Action
    };
    
    protected override void Init() => 
        UseActionManager.RegPreUseAction(OnPreUseAction);

    protected override void Uninit() => 
        UseActionManager.UnregPreUseAction(OnPreUseAction);

    private static void OnPreUseAction(ref bool isPrevented, ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam, ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        if (actionType != ActionType.Action || actionID != 7557) return;
        isPrevented = DService.Condition[ConditionFlag.InCombat];
    }
}
