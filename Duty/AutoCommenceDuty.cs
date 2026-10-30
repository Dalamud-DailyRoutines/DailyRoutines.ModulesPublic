using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using AgentContentsFinder = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentContentsFinder;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;
using ContentsFinderQueueInfo = FFXIVClientStructs.Expansion.FFXIV.Client.Game.UI.ContentsFinderQueueInfo;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoCommenceDuty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCommenceDutyTitle"),
        Description = Lang.Get("AutoCommenceDutyDescription"),
        Category    = ModuleCategory.Duty,
        Author      = ["Cindy-Master"]
    };

    private Hook<ContentsFinderQueueInfo.Delegates.SetQueueState>? SetQueueStateHook;

    protected override void Init()
    {
        SetQueueStateHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ContentsFinderQueueInfo.MemberFunctionPointers),
            nameof(ContentsFinderQueueInfo.SetQueueState),
            (ContentsFinderQueueInfo.Delegates.SetQueueState)SetQueueStateDetour
        );
        SetQueueStateHook.Enable();

        IClientState.Instance().ClassJobChanged += OnClassJobChanged;
        ICondition.Instance().ConditionChange   += OnConditionChanged;

        TryCommence();
    }

    protected override void Uninit()
    {
        IClientState.Instance().ClassJobChanged -= OnClassJobChanged;
        ICondition.Instance().ConditionChange   -= OnConditionChanged;
    }

    private void SetQueueStateDetour
    (
        ContentsFinderQueueInfo* queueInfo,
        ContentsFinderQueueState newState
    )
    {
        var previousState = queueInfo->QueueState;

        SetQueueStateHook.Original(queueInfo, newState);

        if (newState      == ContentsFinderQueueState.Ready &&
            previousState != ContentsFinderQueueState.Ready)
            TryCommence();
    }

    private static void OnClassJobChanged
    (
        uint classJobID
    ) => TryCommence();

    private static void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (!value)
            TryCommence();
    }

    private static void TryCommence()
    {
        var contentsFinder = ContentsFinder.Instance();
        if (contentsFinder                       == null ||
            contentsFinder->QueueInfo.QueueState != ContentsFinderQueueState.Ready)
            return;

        var playerState = PlayerState.Instance();
        if (!playerState->IsLoaded                                                       ||
            playerState->CurrentClassJobId != contentsFinder->QueueInfo.QueuedClassJobId ||
            Conditions.Instance()->Dead)
            return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null)
            return;

        agent->RespondToDuty(true);
    }
}
