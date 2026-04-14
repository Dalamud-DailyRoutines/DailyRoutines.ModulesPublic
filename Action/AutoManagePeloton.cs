using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoManagePeloton : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoManagePelotonTitle"),
        Description = Lang.Get("AutoManagePelotonDescription"),
        Category    = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

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
        if (actionType != ActionType.Action || actionID != 7557) return;
        isPrevented = DService.Instance().Condition[ConditionFlag.InCombat];
    }
}
