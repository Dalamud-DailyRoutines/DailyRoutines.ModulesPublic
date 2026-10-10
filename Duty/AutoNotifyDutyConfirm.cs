using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class AutoNotifyDutyConfirm : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyDutyConfirmTitle"),
        Description = Lang.Get("AutoNotifyDutyConfirmDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<ContentsFinderQueueInfo.Delegates.OnQueuePop>? OnQueuePopHook;

    protected override void Init()
    {
        OnQueuePopHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ContentsFinderQueueInfo.MemberFunctionPointers),
            "OnQueuePop",
            (ContentsFinderQueueInfo.Delegates.OnQueuePop)OnQueuePopDetour
        );
        OnQueuePopHook.Enable();
    }

    private void OnQueuePopDetour
    (
        ContentsFinderQueueInfo* queueInfo,
        ContentsFinderQueueState state,
        uint                     contentFinderConditionID,
        nint                     a4,
        bool                     isInProgressParty,
        ContentsFinder.LootRule  lootRule,
        ulong                    inProgressPartyStartTimestamp,
        nint                     a8,
        bool                     isUnrestrictedParty,
        bool                     isMinimalIL,
        bool                     isSilenceEcho,
        bool                     isExplorerMode,
        bool                     isLevelSync,
        bool                     isLimitedLeveling
    )
    {
        var previousState = queueInfo->QueueState;

        OnQueuePopHook.Original
        (
            queueInfo,
            state,
            contentFinderConditionID,
            a4,
            isInProgressParty,
            lootRule,
            inProgressPartyStartTimestamp,
            a8,
            isUnrestrictedParty,
            isMinimalIL,
            isSilenceEcho,
            isExplorerMode,
            isLevelSync,
            isLimitedLeveling
        );

        if (state != ContentsFinderQueueState.Ready ||
            previousState is ContentsFinderQueueState.None or ContentsFinderQueueState.Ready)
            return;

        var entry = ContentsFinder.Instance()->QueueInfo.PoppedQueueEntry;
        if (entry.ContentType == ContentsType.None)
            return;

        var dutyName = entry.ContentType switch
        {
            ContentsType.Regular  => LuminaWrapper.GetContentName(entry.Id),
            ContentsType.Roulette => LuminaWrapper.GetContentRouletteName(entry.Id),
            _                     => string.Empty
        };
        if (string.IsNullOrEmpty(dutyName)) 
            return;

        NotifyHelper.Instance().TrayInfo
        (
            dutyName,
            Lang.Get("AutoNotifyDutyConfirm-Notification")
        );
        NotifyHelper.Instance().Chat
        (
            Lang.Get
            (
                "AutoNotifyDutyConfirm-Message",
                new Dictionary<string, object>
                {
                    ["duty"] = dutyName
                }
            ),
            false
        );
    }
}
