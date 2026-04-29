using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoSoulsow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSoulsowTitle"),
        Description = Lang.Get("AutoSoulsowDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

    // 重新挑战
    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 进入副本
    private void OnZoneChanged(uint u)
    {
        TaskHelper.Abort();

        if (GameState.ContentFinderCondition == 0) return;

        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 战斗状态
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat) return;

        TaskHelper.Abort();
        if (!value)
            TaskHelper.Enqueue(CheckCurrentJob);
    }

    private bool CheckCurrentJob()
    {
        if (DService.Instance().Condition.IsBetweenAreas || !UIModule.IsScreenReady() || DService.Instance().Condition.IsOccupiedInEvent) return false;

        if (DService.Instance().Condition[ConditionFlag.InCombat] || LocalPlayerState.ClassJob != 39 || !GameState.IsInPVEActonZone)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, weight: 1);
        return true;
    }

    private bool UseRelatedActions()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        // 播魂种
        if (localPlayer.StatusList.HasStatus(2594) || !ActionManager.IsActionUnlocked(24387))
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, 24387), $"UseAction_{24387}", 5_000, weight: 1);
        TaskHelper.DelayNext(2_000);
        TaskHelper.Enqueue(CheckCurrentJob, "二次检查", weight: 1);
        return true;
    }
}
