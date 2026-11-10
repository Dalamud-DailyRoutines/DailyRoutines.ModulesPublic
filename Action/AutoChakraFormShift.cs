using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal class AutoChakraFormShift : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoChakraFormShiftTitle"),
        Description = Lang.Get("AutoChakraFormShiftDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoChakraFormShift);

    protected override void Init()
    {
        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas    ||
                    ICondition.Instance().IsOccupiedInEvent ||
                    LocalPlayerState.Object == null)
                    return false;

                if (LocalPlayerState.ClassJob != CLASS_JOB_MONK ||
                    !GameState.IsInPVEActonZone                 ||
                    GameState.IsDutyCompleted                   ||
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

    private unsafe bool UseRelatedActions()
    {
        var gauge = IJobGauges.Instance().Get<MNKGauge>();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return false;

        var statusManager = localPlayer->StatusManager;

        var action = 0U;
        // 铁山斗气
        if (ActionManager.IsActionUnlocked(STEELED_MEDITATION) &&
            gauge.Chakra != 5)
            action = STEELED_MEDITATION;
        // 演武
        else if (ActionManager.IsActionUnlocked(FORM_SHIFT)                 &&
                 !LocalPlayerState.HasStatus(STATUS_PERFECT_BALANCE, out _) &&
                 (!LocalPlayerState.HasStatus(STATUS_FORMLESS_FIST, out var statusIndex) || statusManager.GetRemainingTime(statusIndex) <= 27))
            action = FORM_SHIFT;

        if (action == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, action));
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(UseRelatedActions);
        return true;
    }

    // 脱战
    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (flag != ConditionFlag.InCombat) return;

        TaskHelper.Abort();
        if (!value)
            TaskHelper.Enqueue(UseRelatedActions);
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

    #region 常量

    private const uint STEELED_MEDITATION     = 36940; // 铁山斗气
    private const uint FORM_SHIFT             = 4262;  // 演武
    private const uint STATUS_PERFECT_BALANCE = 110;   // 震脚
    private const uint STATUS_FORMLESS_FIST   = 2513;  // 无相身形
    private const uint CLASS_JOB_MONK         = 20;

    #endregion
}
