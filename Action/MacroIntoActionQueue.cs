using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe class MacroIntoActionQueue : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("MacroIntoActionQueueTitle"),
        Description = GetLoc("MacroIntoActionQueueDescription"),
        Category    = ModuleCategories.Action,
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

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
        queueState                          = ActionManager.UseActionMode.Queue;
        ActionManager.Instance()->QueueType = ActionManager.UseActionMode.Queue;

        // 冲刺重定向
        if (actionType == ActionType.GeneralAction && actionID == 4)
        {
            actionType = ActionType.Action;
            actionID   = GetAdjustSprintActionID();
            targetID   = 0xE0000000;
        }
    }

    protected override void Uninit() => 
        UseActionManager.Unreg(OnPreUseAction);
}
