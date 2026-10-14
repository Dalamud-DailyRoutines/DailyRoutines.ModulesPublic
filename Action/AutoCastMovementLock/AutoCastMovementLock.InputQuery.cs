using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock
{
    private sealed class InputInterceptor
    (
        Config   config,
        CastFlow castFlow
    )
    {
        private long lastInputFrame = -1;

        private bool mouseUnlocked;

        public void Reset()
        {
            castFlow.Reset();
            lastInputFrame = -1;
            mouseUnlocked  = false;
        }

        public bool ShouldBlockMovement
        (
            IPlayerCharacter player
        )
        {
            if (!IsContentReady(player) || IsMouseUnlocked())
                return false;

            var combatLockActive = config.InCombatOnlyMoveWhenSlideCast && ICondition.Instance()[ConditionFlag.InCombat];

            if (InputQuery.IsKeyPressed(config.KeyToTempLock) || InputQuery.IsButtonPressed(config.ButtonToTempLock))
                return true;

            if (!combatLockActive && castFlow.StartedWhileMoving && (player.IsCasting || ICondition.Instance().IsCasting))
                return false;

            if (IsCastUnderProtection(player)) return true;

            var castRemaining = 0f;

            if (player.IsCasting)
                castRemaining = Math.Max(0, player.TotalCastTime - player.CurrentCastTime);

            return Math.Max(castRemaining, CastFlow.GetRecastWindow()) < GCD_CUTOFF && IsQueuedCastHandled(combatLockActive);
        }

        public bool IsContentReady
        (
            IPlayerCharacter player
        )
        {
            if (player.CurrentHp == 0) return false;

            if (ICondition.Instance().IsBetweenAreas) return false;
            if (GameState.IsInPVPArea && !config.IsEnabledPVP) return false;

            return config.EnabledJobs.Contains(player.ClassJob.RowId);
        }

        public bool IsCastIgnored
        (
            ActionType type,
            uint       actionID
        ) =>
            type switch
            {
                ActionType.Mount => true,
                ActionType.Action => actionID is WALKABLE_ACTION_ONE or WALKABLE_ACTION_TWO    ||
                                     (actionID == TELEPORT_ACTION && !config.LockTeleportCast) ||
                                     (actionID == RETURN_ACTION   && !config.LockReturnCast),
                _ => false
            };

        public bool IsMouseUnlocked()
        {
            var frame = Framework.Instance()->FrameCounter;

            if (lastInputFrame == frame)
                return mouseUnlocked;

            lastInputFrame = frame;
            mouseUnlocked = GameState.IsForeground                            &&
                            config.MouseButtonsToTempUnlock                   &&
                            InputQuery.IsKeyPressed(VirtualKey.LBUTTON) &&
                            InputQuery.IsKeyPressed(VirtualKey.RBUTTON);

            return mouseUnlocked;
        }

        private bool IsCastUnderProtection
        (
            IPlayerCharacter player
        )
        {
            if (player.IsCasting && IsCastIgnored(player.CastActionType, player.CastActionID))
                return false;

            return ICondition.Instance().IsCasting;
        }

        private bool IsQueuedCastHandled
        (
            bool combatLockActive
        )
        {
            var manager = ActionManager.Instance();
            if (manager == null) return false;

            var queued = manager->ActionQueued && manager->QueuedActionId != 0;
            var castable = queued                                         &&
                           manager->QueuedActionType == ActionType.Action &&
                           IsActionCastable(manager->QueuedActionId);
            var canStopForQueuedCast = castable &&
                                       (combatLockActive                                                                  ||
                                        castFlow.IsProtectedAction(manager->GetAdjustedActionId(manager->QueuedActionId)) ||
                                        !IsMoveInputRequested());

            return canStopForQueuedCast || (combatLockActive && (!queued || castable));
        }

        private bool IsActionCastable
        (
            uint actionID
        )
        {
            var manager = ActionManager.Instance();
            if (manager == null) return false;

            actionID = manager->GetAdjustedActionId(actionID);

            if (IsCastIgnored(ActionType.Action, actionID)) return false;

            return LuminaGetter.TryGetRow<LuminaAction>(actionID, out var row)        &&
                   row.Cast100ms                                                  > 0 &&
                   ActionManager.GetAdjustedCastTime(ActionType.Action, actionID) > 0;
        }
    }
    
    private static partial class InputQuery
    {
        public static bool IsKeyPressed
        (
            VirtualKey key
        ) =>
            GameState.IsForeground                                            &&
            key                                          != VirtualKey.NO_KEY &&
            (GetAsyncKeyState((int)key) & KEY_HELD_MASK) != 0;

        public static bool IsButtonPressed
        (
            GamepadButtons button
        ) =>
            GameState.IsForeground                                      &&
            button                               != GamepadButtons.None &&
            IGamepadState.Instance().Raw(button) != 0;

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState
        (
            int key
        );

        #region 常量

        private const int KEY_HELD_MASK = 32768;

        #endregion
    }
}
