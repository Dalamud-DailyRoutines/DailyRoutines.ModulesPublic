using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoChakraFormShift : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoChakraFormShiftTitle"),
        Description = Lang.Get("AutoChakraFormShiftDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
    }

    private bool CheckCurrentJob()
    {
        if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition.IsOccupiedInEvent) return false;

        if (LocalPlayerState.ClassJob != 20 || !GameState.IsInPVEActonZone)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, weight: 1);
        return true;
    }

    private unsafe bool UseRelatedActions()
    {
        var gauge = DService.Instance().JobGauges.Get<MNKGauge>();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var statusManager = localPlayer->StatusManager;

        var action = 0U;
        // 铁山斗气
        if (ActionManager.IsActionUnlocked(STEELED_MEDITATION) &&
            gauge.Chakra != 5)
            action = STEELED_MEDITATION;
        // 演武
        else if (ActionManager.IsActionUnlocked(FORM_SHIFT) &&
                 !LocalPlayerState.HasStatus(110, out _)    &&
                 (!LocalPlayerState.HasStatus(2513, out var statusIndex) || statusManager.GetRemainingTime(statusIndex) <= 27))
            action = 4262;

        if (action == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, action), $"UseAction_{action}", 2_000, weight: 1);
        TaskHelper.DelayNext(500, $"Delay_Use{action}", 1);
        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, weight: 1);
        return true;
    }

    // 脱战
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        TaskHelper.Abort();
        if (!value)
            TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 重新挑战
    private void OnDutyRecommenced(IDutyStateEventArgs args)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 进入副本
    private void OnZoneChanged(uint zone)
    {
        if (LuminaGetter.GetRow<TerritoryType>(zone) is not { ContentFinderCondition.RowId: > 0 }) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
    }

    #region 常量

    private const uint STEELED_MEDITATION = 36940;
    private const uint FORM_SHIFT         = 4262;
    
    #endregion
}
