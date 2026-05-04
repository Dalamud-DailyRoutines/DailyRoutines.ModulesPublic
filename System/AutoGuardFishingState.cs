using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoGuardFishingState : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGuardFishingStateTitle"),
        Description = Lang.Get("AutoGuardFishingStateDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        ExecuteCommandManager.Instance().RegPre(OnPreCommand);
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
    }
    
    protected override void Uninit()
    {
        ExecuteCommandManager.Instance().Unreg(OnPreCommand);
        UseActionManager.Instance().Unreg(OnPreUseAction);
    }

    protected override void ConfigUI() => 
        ImGuiOm.ConflictKeyText();

    private static void OnPreUseAction
    (
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID
    )
    {
        if (actionType != ActionType.Action || actionID != 299)
            return;

        FishingCommand.Quit();
        isPrevented = true;
    }

    private static void OnPreCommand
    (
        ref bool               isPrevented,
        ref ExecuteCommandFlag command,
        ref uint               param1,
        ref uint               param2,
        ref uint               param3,
        ref uint               param4
    )
    {
        if (command != ExecuteCommandFlag.Fishing) return;
        if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            return;

        if (param1 == 1)
            isPrevented = true;
    }
}
