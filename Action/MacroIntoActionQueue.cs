using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class MacroIntoActionQueue : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("MacroIntoActionQueueTitle"),
        Description = Lang.Get("MacroIntoActionQueueDescription"),
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
        queueState                          = ActionManager.UseActionMode.Queue;
        ActionManager.Instance()->QueueType = ActionManager.UseActionMode.Queue;

        // 冲刺重定向
        if (actionType == ActionType.GeneralAction && actionID == 4)
        {
            actionType = ActionType.Action;
            actionID   = ActionManager.GetAdjustSprintActionID();
            targetID   = 0xE0000000;
        }
    }
}
