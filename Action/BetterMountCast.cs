using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using InputData = FFXIVClientStructs.FFXIV.Client.System.Input.InputData;

namespace DailyRoutines.ModulesPublic;

public class BetterMountCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("BetterMountCastTitle"),
        Description = GetLoc("BetterMountCastDescription"),
        Category = ModuleCategories.Action,
        Author = ["Bill"],
        ModulesRecommend = ["BetterMountRoulette"]
    };

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        UseActionManager.RegPreUseAction(OnPreUseAction);
        FrameworkManager.Register(OnUpdate);
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

        if (actionType == ActionType.Mount || actionID == 9)
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    private void OnUpdate(IFramework _)
    {
        if (!IsCasting || !ModuleConfig.MoveToCancel) return;

        var player = DService.ObjectTable.LocalPlayer;
        if (LocalPlayerState.IsMoving && (player.CastActionType == ActionType.Mount || player.CastActionId == 9))
            ExecuteCancelCast();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("BetterMountCast-ClickToCancel"), ref ModuleConfig.ClickToCancel))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("BetterMountCast-MoveToCancel"),ref ModuleConfig.MoveToCancel))
            SaveConfig(ModuleConfig);
    }
    private static void ExecuteCancelCast()
    {
        if (Throttler.Throttle("BetterMountCast-CancelCast", 100))
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool ClickToCancel = true;
        public bool MoveToCancel;
    }
}
