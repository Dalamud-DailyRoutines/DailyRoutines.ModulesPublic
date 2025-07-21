using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoEnableAttack : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoEnableAttackTitle"),
        Description = GetLoc("AutoEnableAttackDescription"),
        Category    = ModuleCategories.Combat,
    };

    protected override void Init() => 
        UseActionManager.RegUseAction(OnPostUseAction);

    private static void OnPostUseAction(
        bool result, ActionType actionType, uint actionID, ulong targetID, uint extraParam,
        ActionManager.UseActionMode queueState, uint comboRouteID)
    {
        if (actionType is not ActionType.Action || targetID == 0xE000_0000) return;
        if (DService.ClientState.IsPvP || !DService.Condition[ConditionFlag.InCombat] ||
            DService.Condition[ConditionFlag.Casting]) return;
        if (UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking) return;
        
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.AutoAttack, 1, (uint)targetID);
    }

    protected override void Uninit() => 
        UseActionManager.UnregUseAction(OnPostUseAction);
}
