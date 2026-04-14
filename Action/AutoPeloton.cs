using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Info.Game.Enums;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoPeloton : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPelotonTitle"),
        Description = Lang.Get("AutoPelotonDescription"),
        Category    = ModuleCategory.Action,
        Author      = ["yamiYori"]
    };
    
    private Config config = null!;

    protected override void Init()
    {
        TaskHelper   ??= new();
        config =   Config.Load(this) ?? new();

        LocalPlayerState.Instance().PlayerMoveStateChanged += OnMoveStateChanged;
        CharacterStatusManager.Instance().RegLose(OnLoseStatus);
    }

    protected override void Uninit()
    {
        LocalPlayerState.Instance().PlayerMoveStateChanged -= OnMoveStateChanged;
        CharacterStatusManager.Instance().Unreg(OnLoseStatus);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyInDuty"), ref config.OnlyInDuty))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("AutoPeloton-DisableInWalk"), ref config.DisableInWalk))
            config.Save(this);
    }

    private void OnLoseStatus(IBattleChara player, ushort id, ushort param, ushort stackCount, ulong sourceID)
    {
        if (player.Address != LocalPlayerState.Object?.Address) return;
        if (id != 1199 && id != 50) return;

        CheckAndUsePeloton();
    }

    private void OnMoveStateChanged(bool isMoving)
    {
        if (!isMoving) return;
        CheckAndUsePeloton();
    }

    private void CheckAndUsePeloton()
    {
        if (config.OnlyInDuty && GameState.ContentFinderCondition == 0) return;
        if (GameState.IsInPVPArea) return;
        if (DService.Instance().Condition[ConditionFlag.InCombat]) return;
        if (!UIModule.IsScreenReady()                       ||
            DService.Instance().Condition.IsOccupiedInEvent ||
            DService.Instance().ObjectTable.LocalPlayer is null)
            return;
        if (LocalPlayerState.ClassJobData.ToJobType() != ClassJobType.PhysicalRanged)
            return;
        if (!ActionManager.IsActionUnlocked(PELOTONING_ACTION_ID))
            return;
        if (config.DisableInWalk && LocalPlayerState.IsWalking)
            return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(UsePeloton, $"UseAction_{PELOTONING_ACTION_ID}", 5_000, weight: 1);
    }

    private bool UsePeloton()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
        var actionManager = ActionManager.Instance();
        var statusManager = localPlayer.ToStruct()->StatusManager;

        if (actionManager->GetActionStatus(ActionType.Action, PELOTONING_ACTION_ID) != 0) return false;
        if (statusManager.HasStatus(1199) || statusManager.HasStatus(50)) return true;
        if (!LocalPlayerState.Instance().IsMoving) return true;

        TaskHelper.Enqueue
        (
            () => UseActionManager.Instance().UseAction(ActionType.Action, PELOTONING_ACTION_ID),
            $"UseAction_{PELOTONING_ACTION_ID}",
            5_000,
            weight: 1
        );
        return true;
    }

    private class Config : ModuleConfig
    {
        public bool DisableInWalk = true;
        public bool OnlyInDuty    = true;
    }
    
    #region 常量
    
    private const uint PELOTONING_ACTION_ID = 7557;
    
    #endregion
}
