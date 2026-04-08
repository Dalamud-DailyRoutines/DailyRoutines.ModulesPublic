using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using AgentReceiveEventDelegate = OmenTools.Interop.Game.Models.Native.AgentReceiveEventDelegate;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastRidePillion : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FastRidePillionTitle"),
        Description = Lang.Get("FastRidePillionDescription"),
        Category    = ModuleCategory.General
    };

    private Hook<AgentReceiveEventDelegate>? AgentContextReceiveEventHook;

    protected override void Init()
    {
        AgentContextReceiveEventHook ??=
            DService.Instance().Hook.HookFromAddress<AgentReceiveEventDelegate>
            (
                AgentContext.Instance()->VirtualTable->GetVFuncByName("ReceiveEvent"),
                AgentContextReceiveEventDetour
            );
        AgentContextReceiveEventHook.Enable();

        DService.Instance().Condition.ConditionChange += OnCondition;
    }
    
    protected override void Uninit() =>
        DService.Instance().Condition.ConditionChange -= OnCondition;

    private static void OnCondition(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.RidingPillion || !value) return;

        if (ContextMenuAddon != null && ContextMenuAddon->IsAddonAndNodesReady())
            ContextMenuAddon->Close(true);
    }

    private AtkValue* AgentContextReceiveEventDetour
    (
        AgentInterface* agent,
        AtkValue*       returnValues,
        AtkValue*       values,
        uint            valueCount,
        ulong           eventKind
    )
    {
        if (eventKind != 0 || values == null || values->Int != 1 || DService.Instance().Condition.IsOnMount)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        var targetObjectIDGame = ((AgentContext*)agent)->TargetObjectId;
        if (targetObjectIDGame.ObjectId == 0 || targetObjectIDGame.Type != 0)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        var isInParty = GroupManager.Instance()->GetGroup()->IsEntityIdInParty(targetObjectIDGame.ObjectId);
        if (!isInParty)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        var chara = CharacterManager.Instance()->LookupBattleCharaByEntityId(targetObjectIDGame.ObjectId);
        if (chara == null)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        var mount = chara->Character.Mount;
        if (mount.MountObject == null || mount.MountId == 0)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        if (!LuminaGetter.TryGetRow<Mount>(mount.MountId, out var mountRow) || mountRow.ExtraSeats <= 0)
            return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);

        chara->RidePillion(10);
        return AgentContextReceiveEventHook.Original(agent, returnValues, values, valueCount, eventKind);
    }
}
