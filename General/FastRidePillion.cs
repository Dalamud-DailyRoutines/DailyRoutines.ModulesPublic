using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastRidePillion : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastRidePillionTitle"),
        Description = GetLoc("FastRidePillionDescription"),
        Category    = ModuleCategories.General,
    };

    private static Hook<AgentReceiveEventDelegate>? AgentContextReceiveEventHook;

    protected override void Init()
    {
        AgentContextReceiveEventHook ??=
            DService.Hook.HookFromAddress<AgentReceiveEventDelegate>(GetVFuncByName(AgentContext.Instance()->VirtualTable, "ReceiveEvent"),
                                                                     AgentContextReceiveEventDetour);
        AgentContextReceiveEventHook.Enable();

        DService.Condition.ConditionChange += OnCondition;
    }

    private static void OnCondition(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.RidingPillion || !value) return;

        if (InfosOm.ContextMenuXIV != null && IsAddonAndNodesReady(InfosOm.ContextMenuXIV))
            InfosOm.ContextMenuXIV->Close(true);
    }

    private static AtkValue* AgentContextReceiveEventDetour(AgentInterface* agent, AtkValue* returnValues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        if (eventKind != 0 || values == null || values->Int != 1 || IsOnMount)
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

    protected override void Uninit() => 
        DService.Condition.ConditionChange -= OnCondition;
}
