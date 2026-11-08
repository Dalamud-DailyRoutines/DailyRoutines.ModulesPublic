using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoUmbralSoul : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoUmbralSoulTitle"),
        Description = Lang.Get("AutoUmbralSoulDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoUmbralSoul);

    protected override void Init()
    {
        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas ||
                    ICondition.Instance().IsOccupiedInEvent)
                    return false;

                if (LocalPlayerState.ClassJob != CLASS_JOB_BLACK_MAGE ||
                    !GameState.IsInPVEActonZone                       ||
                    GameState.IsDutyCompleted                         ||
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

    private bool UseRelatedActions()
    {
        var gauge = IJobGauges.Instance().Get<BLMGauge>();

        // 六层灵极魂 → 耀星, 不把耀星打出来太亏了
        if (gauge.AstralSoulStacks == 6)
        {
            TaskHelper.Abort();
            return true;
        }

        var action = 0U;

        // 星极火状态 → 星灵移位转冰
        if (ActionManager.IsActionUnlocked(TRANSPOSE) &&
            gauge.InAstralFire)
            action = TRANSPOSE;
        // 灵极冰状态 → 灵极魂转满
        else if (ActionManager.IsActionUnlocked(UMBRAL_SOUL) &&
                 ((ActionManager.IsActionUnlocked(BLIZZARD4) && gauge.UmbralHearts != 3) ||
                  gauge.UmbralIceStacks != 3))
            action = UMBRAL_SOUL;

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

    private const uint CLASS_JOB_BLACK_MAGE = 25;
    private const uint UMBRAL_SOUL          = 16506; // 灵极魂
    private const uint TRANSPOSE            = 149;   // 星灵移位
    private const uint BLIZZARD4            = 3576;  // 冰澈

    #endregion
}
