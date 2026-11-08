using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoSoulsow : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoSoulsowTitle"),
        Description = Lang.Get("AutoSoulsowDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoSoulsow);

    protected override void Init()
    {
        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas    ||
                    ICondition.Instance().IsOccupiedInEvent ||
                    !UIModule.IsScreenReady()               ||
                    LocalPlayerState.Object == null)
                    return false;

                if (LocalPlayerState.ClassJob != CLASS_JOB_REAPER ||
                    !GameState.IsInPVEActonZone                   ||
                    GameState.IsDutyCompleted                     ||
                    ICondition.Instance()
                              .Any(ConditionFlag.InCombat, ConditionFlag.Mounted, ConditionFlag.Mounting, ConditionFlag.InFlight))
                {
                    TaskHelper.Abort();
                    return false;
                }

                return true;
            }
        };

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        IDutyState.Instance().DutyRecommenced    += OnDutyRecommenced;
        ICondition.Instance().ConditionChange    += OnConditionChanged;
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        IDutyState.Instance().DutyRecommenced    -= OnDutyRecommenced;
        ICondition.Instance().ConditionChange    -= OnConditionChanged;
    }

    // 重新挑战
    private void OnDutyRecommenced
    (
        IDutyStateEventArgs args
    )
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(UseRelatedActions);
    }

    // 进入副本
    private void OnZoneChanged
    (
        uint zone
    )
    {
        if (GameState.ContentFinderCondition == 0)
            return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(UseRelatedActions);
    }

    // 战斗状态
    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (flag is not ConditionFlag.InCombat) return;

        TaskHelper.Abort();
        if (!value)
            TaskHelper.Enqueue(UseRelatedActions);
    }

    private bool UseRelatedActions()
    {
        if (LocalPlayerState.HasStatus(STATUS_SOULSOW, out _) ||
            !ActionManager.IsActionUnlocked(SOULSOW))
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, SOULSOW));
        TaskHelper.DelayNext(2_000);
        TaskHelper.Enqueue(UseRelatedActions);
        return true;
    }

    #region 常量

    private const uint CLASS_JOB_REAPER = 39;
    private const uint SOULSOW          = 24387; // 播魂种
    private const uint STATUS_SOULSOW   = 2594;  // 播魂种

    #endregion
}
