using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class AutoBlockEmptyXBMParty : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoBlockEmptyXBMPartyTitle"),
        Description = Lang.Get("AutoBlockEmptyXBMPartyDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<AgentReceiveEventDelegate>? AgentReceiveEventHook;

    protected override void Init()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.XBMStageDetailList);
        AgentReceiveEventHook = agent->VirtualTable->HookVFuncFromName
        (
            "ReceiveEvent",
            (AgentReceiveEventDelegate)AgentReceiveEventDetour
        );
        AgentReceiveEventHook.Enable();
    }

    private AtkValue* AgentReceiveEventDetour
    (
        AgentInterface* agent,
        AtkValue*       returnValues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind
    )
    {
        if (eventKind != 0 || valueCount != 1)
            return InvokeOriginal();

        var firstValue = values[0];
        if (firstValue.Type != AtkValueType.Int || firstValue.Int != 8)
            return InvokeOriginal();

        var agentPetParty = AgentModule.Instance()->GetAgentByInternalId(AgentId.XBMPetParty);
        if (agentPetParty == null)
            return InvokeOriginal();

        var agentPetPartyPtr = (nint)agentPetParty;

        var notification = *(uint*)((nint)agent + STAGE_MODE_OFFSET) switch
        {
            0 when GetElementCount(agentPetPartyPtr, PARTY_LIST_BEGIN_OFFSET, PARTY_LIST_END_OFFSET) < 3 =>
                "AutoBlockEmptyXBMParty-Notification-EmptyParty",
            2 when IsFluteAssignmentInsufficient(agentPetPartyPtr) =>
                "AutoBlockEmptyXBMParty-Notification-InsufficiantAssignment",
            _ => null
        };

        if (notification == null)
            return InvokeOriginal();

        NotifyHelper.ToastError(Lang.Get(notification));
        returnValues->SetBool(false);
        return returnValues;

        AtkValue* InvokeOriginal() =>
            AgentReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }

    private static long GetElementCount
    (
        nint agent,
        int  beginOffset,
        int  endOffset
    ) =>
        (*(nint*)(agent + endOffset) - *(nint*)(agent + beginOffset)) >> 3;

    // 存活魔兽不足三只时, 兽笛无法全部指派, 此时按存活数量放行
    private static bool IsFluteAssignmentInsufficient(nint agent)
    {
        var assignedCount = GetElementCount(agent, FLUTE_LIST_BEGIN_OFFSET, FLUTE_LIST_END_OFFSET);
        if (assignedCount >= 3)
            return false;

        var aliveCount = 0;
        for (var i = 0; i < PET_CURRENT_HEALTH_COUNT; i++)
        {
            if (*(uint*)(agent + PET_CURRENT_HEALTH_OFFSET + (i * sizeof(uint))) != 0)
                aliveCount++;
        }

        return assignedCount < aliveCount;
    }

    #region 常量

    // TODO：等待 FFCS 合并
    
    // AgentXBMStageDetailList: 当前所处阶段, 0 为调整编队, 2 为开始战斗
    private const int STAGE_MODE_OFFSET = 0x3C;

    // AgentXBMPetParty: 魔兽编队列表, 元素为 8 字节的 ID 记录
    private const int PARTY_LIST_BEGIN_OFFSET = 0x78;
    private const int PARTY_LIST_END_OFFSET   = 0x80;

    // AgentXBMPetParty: 兽笛列表, 元素为 8 字节的 ID 记录
    private const int FLUTE_LIST_BEGIN_OFFSET = 0x90;
    private const int FLUTE_LIST_END_OFFSET   = 0x98;

    // AgentXBMPetParty: 各魔兽当前生命值, 为 0 表示已阵亡
    private const int PET_CURRENT_HEALTH_OFFSET = 0xC0;
    private const int PET_CURRENT_HEALTH_COUNT  = 15;

    #endregion
}
