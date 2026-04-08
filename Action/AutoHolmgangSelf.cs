using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoHolmgangSelf : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHolmgangSelfTitle"),
        Description = Lang.Get("AutoHolmgangSelfDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init() =>
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
    
    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnPreUseAction);

    private static void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType is not ActionType.Action || actionID is not 43) return;
        targetID = 0xE0000000;
    }
}
