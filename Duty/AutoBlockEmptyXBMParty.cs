using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AgentReceiveEventDelegate = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentReceiveEventDelegate;
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

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AgentReceiveEventDelegate>? AgentReceiveEventHook;

    protected override void Init()
    {
        AgentReceiveEventHook = AgentXBMStageDetailList.Instance()->VirtualTable->HookVFuncFromName
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

        var agentPetParty = AgentXBMPetParty.Instance();
        if (agentPetParty == null)
            return InvokeOriginal();

        var notification = ((AgentXBMStageDetailList*)agent)->Mode switch
        {
            0 when agentPetParty->SelectedPets.Count < 3 =>
                "AutoBlockEmptyXBMParty-Notification-EmptyParty",
            2 when IsFluteAssignmentInsufficient(agentPetParty) =>
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

    private static bool IsFluteAssignmentInsufficient
    (
        AgentXBMPetParty* agentPetParty
    )
    {
        var assignedCount = agentPetParty->SelectedPetIds.Count;
        if (assignedCount >= 3)
            return false;

        var aliveCount = 0;

        foreach (var currentHealth in agentPetParty->PetCurrentHealth)
        {
            if (currentHealth != 0)
                aliveCount++;
        }

        return assignedCount < aliveCount;
    }
}
