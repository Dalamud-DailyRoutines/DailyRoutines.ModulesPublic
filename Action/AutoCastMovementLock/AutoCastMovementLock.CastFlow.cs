using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock
{
    private sealed class CastFlow
    {
        public bool StartedWhileMoving;

        private uint protectedActionID;
        private bool openingProtectedCast;

        public void GuardAction
        (
            uint actionID,
            bool fromQueue
        )
        {
            openingProtectedCast = fromQueue && IsProtectedAction(actionID);

            if (!fromQueue)
                protectedActionID = 0;
        }

        public void StartCast
        (
            bool movementRequested
        )
        {
            StartedWhileMoving   = movementRequested && !openingProtectedCast;
            protectedActionID    = 0;
            openingProtectedCast = false;
        }

        public void CompleteAction() => openingProtectedCast = false;

        public void RecordQueuedAction
        (
            uint actionID,
            bool protectedCast
        )
        {
            if (protectedCast)
                protectedActionID = actionID;
            else
                protectedActionID = 0;
        }

        public void Reset()
        {
            StartedWhileMoving   = false;
            protectedActionID    = 0;
            openingProtectedCast = false;
        }

        public bool IsProtectedAction
        (
            uint actionID
        ) =>
            actionID != 0 && actionID == protectedActionID;

        public static float GetRecastWindow()
        {
            var manager = ActionManager.Instance();
            if (manager == null) return 0;

            var recast = manager->GetRecastGroupDetail(GCD_RECAST_GROUP);
            if (recast == null || !recast->IsActive) return 0;

            return Math.Max(0, recast->Total - recast->Elapsed);
        }
    }
}
