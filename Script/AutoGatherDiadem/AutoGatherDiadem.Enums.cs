namespace DailyRoutines.ModulesPublic;

internal partial class AutoGatherDiadem
{
    private enum GatheringMode
    {
        练级,
        绿材,
        冲分
    }

    private enum GatheringState
    {
        可以采集,
        无法采集,
        采集完毕,
        等待加载
    }

    private enum GatheringNodeType
    {
        普通,
        园艺梦幻,
        采矿梦幻
    }

    private enum Phase
    {
        Idle,
        Registering,
        Leaving,
        Searching,
        Moving,
        Interacting,
        Gathering,
        Windmire,
        Auger,
        Equipping,
        Inspecting,
        Purchasing,
        Discarding
    }

    private enum ExchangeStep
    {
        Opening,
        Selecting,
        Processing,
        AwaitingInspection,
        Closing
    }
}
