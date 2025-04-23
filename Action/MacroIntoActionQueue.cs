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

    public override void Init() => UseActionManager.Register(OnPreUseAction);

    private static void OnPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam,
        ref ActionManager.UseActionMode queueState, ref uint comboRouteID, ref bool* outOptAreaTargeted)
    {
        queueState = ActionManager.UseActionMode.Queue;
        ActionManager.Instance()->QueueType = ActionManager.UseActionMode.Queue;
        
        // 冲刺重定向
        if (actionType == ActionType.GeneralAction && actionID == 4)
        {
            actionType = ActionType.Action;
            actionID   = GetAdjustSprintActionID();
            targetID   = 0xE0000000;
        }
    }

    public override void Uninit() => UseActionManager.Unregister(OnPreUseAction);
}
