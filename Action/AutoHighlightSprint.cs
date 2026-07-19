using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHighlightSprint : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoHighlightSprintTitle"),
        Description = Lang.Get
        (
            "AutoHighlightSprintDescription",
            LuminaWrapper.GetGeneralActionName(4) // 冲刺
        ),
        Category = ModuleCategory.Action
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<ActionManager.Delegates.IsActionHighlighted>? IsActionHighlightedHook;

    protected override void Init()
    {
        IsActionHighlightedHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ActionManager.MemberFunctionPointers),
            "IsActionHighlighted",
            (ActionManager.Delegates.IsActionHighlighted)IsActionHighlightedDetour
        );
        IsActionHighlightedHook.Enable();
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private bool IsActionHighlightedDetour(ActionManager* manager, ActionType actionType, uint actionID)
    {
        if (GameState.ContentFinderCondition != 0 &&
            !ICondition.Instance()[ConditionFlag.InCombat] &&
            ((actionType == ActionType.GeneralAction && actionID == 4) ||
             (actionType == ActionType.Action        && actionID == 3)))
            return true;

        return IsActionHighlightedHook.Original(manager, actionType, actionID);
    }
}
