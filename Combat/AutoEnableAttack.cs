using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoEnableAttack : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoEnableAttackTitle"),
        Description = Lang.Get("AutoEnableAttackDescription"),
        Category    = ModuleCategory.Combat
    };

    protected override void Init() =>
        UseActionManager.Instance().RegPostUseAction(OnPostUseAction);
    
    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnPostUseAction);

    private static void OnPostUseAction
    (
        bool                        result,
        ActionType                  actionType,
        uint                        actionID,
        ulong                       targetID,
        uint                        extraParam,
        ActionManager.UseActionMode queueState,
        uint                        comboRouteID
    )
    {
        if (actionType != ActionType.Action || targetID == 0xE000_0000 || InvalidActions.Contains(actionID)) return;


        if (GameState.IsInPVPArea                                  ||
            !DService.Instance().Condition[ConditionFlag.InCombat] ||
            DService.Instance().Condition[ConditionFlag.Casting])
            return;

        if (UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking) return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.AutoAttack, 1, (uint)targetID);
    }

    #region 常量

    private static readonly FrozenSet<uint> InvalidActions = [7385, 7418, 23288, 23289, 34581, 23273];

    #endregion
}
