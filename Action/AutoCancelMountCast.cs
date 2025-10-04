using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoCancelMountCast : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = GetLoc("AutoCancelMountCastTitle"),
        Description      = GetLoc("AutoCancelMountCastDescription"),
        Category         = ModuleCategories.Action,
        Author           = ["Bill"],
        ModulesRecommend = ["BetterMountRoulette"]
    };

    private static Config ModuleConfig = null!;

    private static CancellationTokenSource? CancelWhenMoveCancelSource;
    
    private static bool IsOnMountCasting;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Condition.ConditionChange += OnConditionChanged;
        UseActionManager.RegPreUseAction(OnPreUseAction);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoCancelMountCast-CancelWhenUseAction"), ref ModuleConfig.CancelWhenUsection))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("AutoCancelMountCast-CancelWhenMove"), ref ModuleConfig.CancelWhenMove))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("AutoCancelMountCast-CancelWhenJump"), ref ModuleConfig.CancelWhenJump))
            SaveConfig(ModuleConfig);
    }
    
    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        UseActionManager.Unreg(OnPreUseAction);

        OnConditionChanged(ConditionFlag.Casting, false);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        switch (flag)
        {
            case ConditionFlag.Casting:
                switch (value)
                {
                    case true:
                        if (DService.ObjectTable.LocalPlayer is { } localPlayer &&
                            (localPlayer.CastActionType == ActionType.Mount ||
                             localPlayer is { CastActionType: ActionType.GeneralAction, CastActionId: 9 }))
                        {
                            IsOnMountCasting = true;

                            CancelWhenMoveCancelSource = new();
                            DService.Framework.RunOnTick(async () =>
                            {
                                while (ModuleConfig.CancelWhenMove && IsOnMountCasting && !CancelWhenMoveCancelSource.IsCancellationRequested)
                                {
                                    if (!LocalPlayerState.IsMoving) continue;

                                    ExecuteCancelCast();

                                    await Task.Delay(10, CancelWhenMoveCancelSource.Token);
                                }
                            }, cancellationToken: CancelWhenMoveCancelSource.Token).ContinueWith(t => t.Dispose());
                        }
                        break;
                    case false:
                        IsOnMountCasting = false;
                    
                        CancelWhenMoveCancelSource?.Cancel();
                        CancelWhenMoveCancelSource?.Dispose();
                        CancelWhenMoveCancelSource = null;
                        break;
                }

                break;
            case ConditionFlag.Jumping:
                if (!ModuleConfig.CancelWhenJump || !value) return;
                
                ExecuteCancelCast();
                break;
        }
    }

    private static void OnPreUseAction(
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID)
    {
        if (!ModuleConfig.CancelWhenUsection || !IsOnMountCasting) return;

        ExecuteCancelCast();
    }
    
    private static void ExecuteCancelCast()
    {
        if (Throttler.Throttle("CancelMountCast-CancelCast", 100))
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    private class Config : ModuleConfiguration
    {
        public bool CancelWhenUsection = true;
        public bool CancelWhenMove;
        public bool CancelWhenJump;
    }
}
