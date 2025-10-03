using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class CancelMountCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("CancelMountCastTitle"),
        Description = GetLoc("CancelMountCastDescription"),
        Category = ModuleCategories.Action,
        Author = ["Bill"],
        ModulesRecommend = ["BetterMountRoulette"]
    };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Condition.ConditionChange += OnConditionChanged;
        UseActionManager.RegPreUseAction(OnPreUseAction);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("CancelMountCast-ClickToCancel"), ref ModuleConfig.ClickToCancel))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("CancelMountCast-MoveToCancel"), ref ModuleConfig.MoveToCancel))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("CancelMountCast-JumpToCancel"), ref ModuleConfig.JumpToCancel))
            SaveConfig(ModuleConfig);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.Casting) return;
        
        if (value && 
            (ModuleConfig.MoveToCancel || ModuleConfig.JumpToCancel))
        {
            FrameworkManager.Unregister(OnUpdate);
            FrameworkManager.Register(OnUpdate);
        }
        else
            FrameworkManager.Unregister(OnUpdate);
    }

    private static void OnPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType,
        ref uint actionID,
        ref ulong targetID,
        ref uint extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint comboRouteID)
    {
        if (!ModuleConfig.ClickToCancel || !IsCasting) return;
        
        var player = DService.ObjectTable.LocalPlayer;
        if (player.CastActionType != ActionType.Mount ||
            (player.CastActionType == ActionType.GeneralAction && player.CastActionId != 9)) return;
        
        ExecuteCancelCast();
    }

    private void OnUpdate(IFramework _)
    {
        if (!(ModuleConfig.MoveToCancel && LocalPlayerState.IsMoving) &&
            !(ModuleConfig.JumpToCancel && 
              DService.Condition.Any(ConditionFlag.Jumping, ConditionFlag.Jumping61)))
            return;

        var player = DService.ObjectTable.LocalPlayer;
        if (player.CastActionType != ActionType.Mount ||
            (player.CastActionType == ActionType.GeneralAction && player.CastActionId != 9)) return;

        ExecuteCancelCast();
    }

    private static void ExecuteCancelCast()
    {
        if (Throttler.Throttle("CancelMountCast-CancelCast", 100))
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        UseActionManager.Unreg(OnPreUseAction);
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool ClickToCancel = true;
        public bool MoveToCancel;
        public bool JumpToCancel;
    }
}
