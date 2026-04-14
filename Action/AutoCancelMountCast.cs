using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoCancelMountCast : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title            = Lang.Get("AutoCancelMountCastTitle"),
        Description      = Lang.Get("AutoCancelMountCastDescription"),
        Category         = ModuleCategory.Action,
        Author           = ["Bill"],
        ModulesRecommend = ["BetterMountRoulette"]
    };
    
    private Config config = null!;

    private CancellationTokenSource? cancelSource;
    private bool                     isOnMountCasting;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
        UseActionManager.Instance().RegPreUseAction(OnPreUseAction);
    }
    
    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        UseActionManager.Instance().Unreg(OnPreUseAction);

        OnConditionChanged(ConditionFlag.Casting, false);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoCancelMountCast-CancelWhenUseAction"), ref config.CancelWhenUsection))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoCancelMountCast-CancelWhenMove"), ref config.CancelWhenMove))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoCancelMountCast-CancelWhenJump"), ref config.CancelWhenJump))
            config.Save(this);
    }
    
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        switch (flag)
        {
            case ConditionFlag.Casting:
                switch (value)
                {
                    case true:
                        if (DService.Instance().ObjectTable.LocalPlayer is { } localPlayer &&
                            (localPlayer.CastActionType == ActionType.Mount ||
                             localPlayer is { CastActionType: ActionType.GeneralAction, CastActionID: 9 }))
                        {
                            isOnMountCasting = true;

                            cancelSource = new();
                            DService.Instance().Framework.RunOnTick
                            (
                                async () =>
                                {
                                    while (config.CancelWhenMove && isOnMountCasting && !cancelSource.IsCancellationRequested)
                                    {
                                        if (LocalPlayerState.Instance().IsMoving)
                                            ExecuteCancelCast();

                                        await Task.Delay(10, cancelSource.Token);
                                    }
                                },
                                cancellationToken: cancelSource.Token
                            ).ContinueWith(t => t.Dispose());
                        }

                        break;
                    case false:
                        isOnMountCasting = false;

                        cancelSource?.Cancel();
                        cancelSource?.Dispose();
                        cancelSource = null;
                        break;
                }

                break;
            case ConditionFlag.Jumping:
                if (!config.CancelWhenJump || !value) return;

                ExecuteCancelCast();
                break;
        }
    }

    private void OnPreUseAction
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
        if (!config.CancelWhenUsection || !isOnMountCasting) return;

        ExecuteCancelCast();
    }

    private static void ExecuteCancelCast()
    {
        if (Throttler.Shared.Throttle("CancelMountCast-CancelCast", 100))
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.CancelCast);
    }

    private class Config : ModuleConfig
    {
        public bool CancelWhenJump;
        public bool CancelWhenMove;
        public bool CancelWhenUsection = true;
    }
}
