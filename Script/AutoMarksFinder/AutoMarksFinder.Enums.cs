namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private enum HuntNavigationMode
    {
        Exact,
        Nearby
    }

    private enum RelayType
    {
        Position,
        WaitAetheryte,
        BeforeStart,
        AfterEnd
    }

    private enum MovementStage
    {
        Idle,
        Travelling,
        SwitchingZone,
        SwitchingInstance,
        Arrived
    }

    private enum BroadcastStage
    {
        Stopped,
        WaitingForDeparture,
        Starting,
        WaitingForPoint,
        WaitingForDeath,
        WaitingForEnd,
        Finishing
    }
}
