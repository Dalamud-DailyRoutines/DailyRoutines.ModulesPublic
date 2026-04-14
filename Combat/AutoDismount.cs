using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDismount : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDismountTitle"),
        Description = Lang.Get("AutoDismountDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 1_500 };

        UseActionManager.Instance().RegPostUseAction(OnUseAction);
    }
    
    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnUseAction);

    private void OnUseAction
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
        if (!DService.Instance().Condition.IsOnMount) return;

        var adjustedActionID = ActionManager.Instance()->GetAdjustedActionId(actionID);
        if (!IsNeedToDismount(actionType, adjustedActionID, targetID)) return;

        TaskHelper.Abort();

        MovementManager.Instance().Dismount();
        TaskHelper.Enqueue
        (() =>
            {
                if (MovementManager.Instance().IsManagerBusy || DService.Instance().Condition[ConditionFlag.Mounted]) return false;
                return UseActionManager.Instance().UseAction
                (
                    actionType,
                    actionID,
                    targetID,
                    extraParam,
                    queueState,
                    comboRouteID
                );
            }
        );
    }

    private static bool IsNeedToDismount(ActionType actionType, uint actionID, ulong actionTargetID)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
        if (!LuminaGetter.TryGetRow<Action>(actionID, out var actionRow)) return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null) return false;

        // 坐骑
        if (actionType == ActionType.Mount) return false;
        // 该技能无须下坐骑
        if (actionManager->GetActionStatus(actionType, actionID, actionTargetID, false, false) == 0) return false;
        // 必须下坐骑的技能类型
        if (MustDismountActionTypes.Contains(actionType)) return true;

        // 技能当前不可用
        if (!actionManager->IsActionOffCooldown(actionType, actionID)) return false;

        // 可以自身或地面为目标的技能
        if (actionRow is { CanTargetSelf: true } or { TargetArea: true }) return true;

        var actionObject = DService.Instance().ObjectTable.SearchByID(actionTargetID);

        // 技能必须要有目标
        if (actionRow.Range != 0)
        {
            // 对非自身的目标使用技能
            if (actionTargetID != 0xE0000000L && actionObject != null)
            {
                // 562 - 看不到目标; 566 - 目标在射程外
                if (ActionManager.GetActionInRangeOrLoS(actionID, (GameObject*)localPlayer.ToStruct(), actionObject.ToStruct()) is 562 or 566)
                    return false;

                // 无法对目标使用技能
                if (!ActionManager.CanUseActionOnTarget(actionID, actionObject.ToStruct())) return false;
            }
            else if (TargetManager.Target == null) return false;
        }

        return true;
    }
    
    #region 常量

    private static readonly FrozenSet<ActionType> MustDismountActionTypes = [ActionType.Item, ActionType.Ornament];

    #endregion
}
