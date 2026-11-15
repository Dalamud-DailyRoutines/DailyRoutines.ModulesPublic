using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    private void OnZoneChanged
    (
        uint zone
    )
    {
        if (machine.CurrentPhase == Phase.Idle)
            return;

        StopActivePathfind();

        machine.AugerAttempts = 0;

        if (zone == ZONE_DIADEM && isOnAutoGather)
            machine.SetPhase(Phase.Searching);
    }

    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (!isOnAutoGather ||
            GameState.TerritoryType != ZONE_DIADEM)
            return;

        switch (flag)
        {
            case ConditionFlag.Gathering:
                if (value)
                    return;

                machine.GatherActions.Clear();

                if (machine.CurrentPhase != Phase.Gathering)
                    return;

                if (config.Mode == GatheringMode.绿材)
                {
                    BeginLeave(false, true);
                    return;
                }

                var level = AgentHUD.Instance()->ExpLevel;

                if (config.Mode == GatheringMode.练级 &&
                    lastLevel   != level)
                    machine.SetPhase(Phase.Equipping);
                else
                    machine.SetPhase(Phase.Searching);

                lastLevel = level;
                break;

            case ConditionFlag.Jumping61:
                StopActivePathfind();
                machine.SetPhase(Phase.Windmire);
                machine.ResumeTick = Environment.TickCount64 + 1000;
                break;
        }
    }

    private void OnGatheringAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!isOnAutoGather ||
            GameState.TerritoryType != ZONE_DIADEM)
            return;

        StopActivePathfind();
        machine.SetPhase(Phase.Gathering);

        machine.GatherActions.Clear();
        machine.GatherActionsPrepared = false;
    }

    private void OnLogin()
    {
        if (!isOnAutoGather)
            return;

        StopActivePathfind();
        machine.Reset();

        machine.SetPhase(Phase.Registering);
    }
}
