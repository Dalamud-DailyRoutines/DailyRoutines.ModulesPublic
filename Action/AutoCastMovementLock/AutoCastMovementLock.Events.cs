using FFXIVClientStructs.FFXIV.Client.Game;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock
{
    private void OnPreUseAction
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
        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer)
            return;

        if (isPrevented                                   ||
            actionType != ActionType.Action               ||
            !inputInterceptor.IsContentReady(localPlayer) ||
            inputInterceptor.IsMouseUnlocked())
            return;
        if (inputInterceptor.IsCastIgnored(actionType, actionID))
            return;

        var manager = ActionManager.Instance();
        if (manager == null)
            return;

        castFlow.GuardAction(manager->GetAdjustedActionId(actionID), queueState == ActionManager.UseActionMode.Queue);
    }

    private void OnPostUseAction
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
        castFlow.CompleteAction();

        var manager = ActionManager.Instance();
        if (manager == null) return;
        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer) 
            return;

        var protectedCast = inputInterceptor.IsContentReady(localPlayer)            &&
                            !inputInterceptor.IsMouseUnlocked()                     &&
                            localPlayer.IsCasting                                   &&
                            localPlayer.CurrentCastTime < localPlayer.TotalCastTime &&
                            !castFlow.StartedWhileMoving                            &&
                            !inputInterceptor.IsCastIgnored(localPlayer.CastActionType, localPlayer.CastActionID);
        var queuedActionID = manager->ActionQueued && manager->QueuedActionType == ActionType.Action ?
                                 manager->GetAdjustedActionId(manager->QueuedActionId) :
                                 0;

        castFlow.RecordQueuedAction(queuedActionID, protectedCast);
    }

    private void OnPreCharacterStartCast
    (
        ref bool         isPrevented,
        ref IBattleChara player,
        ref ActionType   type,
        ref uint         actionID,
        ref nint         a4,
        ref float        rotation,
        ref float        a6
    )
    {
        if (isPrevented ||
            player.ToStruct() != Control.GetLocalPlayer())
            return;

        castFlow.StartCast(IsMoveInputRequested());
    }

    private void OnZoneChanged
    (
        uint territory
    ) =>
        inputInterceptor.Reset();

    private void OnLogout
    (
    ) =>
        inputInterceptor.Reset();
}
