using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoHolmgangSelf : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHolmgangSelfTitle"),
        Description = GetLoc("AutoHolmgangSelfDescription"),
        Category    = ModuleCategories.Action,
    };

    protected override void Init() => 
        UseActionManager.RegPreUseAction(OnPreUseAction);

    private static void OnPreUseAction(
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID)
    {
        if (actionType is not ActionType.Action || actionID is not 43) return;
        targetID = 0xE0000000;
    }

    protected override void Uninit() => 
        UseActionManager.Unreg(OnPreUseAction);
}
