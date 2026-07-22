using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoUmbralSoul : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoUmbralSoulTitle"),
        Description = Lang.Get
        (
            "AutoUmbralSoulDescription",
            LuminaWrapper.GetJobName(CLASS_JOB),     // 黑魔法师
            LuminaWrapper.GetActionName(UMBRAL_SOUL) // 灵极魂
        ),
        Category = ModuleCategory.Action
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

    private bool CheckCurrentJob()
    {
        if (ICondition.Instance().IsBetweenAreas ||
            ICondition.Instance().IsOccupiedInEvent)
            return false;

        if (LocalPlayerState.ClassJob != CLASS_JOB || !GameState.IsInPVEActonZone)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, weight: 1);
        return true;
    }

    private unsafe bool UseRelatedActions()
    {
        if (ICondition.Instance()[ConditionFlag.InCombat])
        {
            TaskHelper.Abort();
            return true;
        }
        
        var gauge = DService.Instance().JobGauges.Get<BLMGauge>();

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

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
                 (gauge.UmbralHearts != 3 || gauge.UmbralIceStacks != 3))
            action = UMBRAL_SOUL;
        
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
        if (GameState.ContentFinderCondition == 0) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    #region 常量

    private const uint CLASS_JOB = 25;
    
    private const uint UMBRAL_SOUL = 16506;
    private const uint TRANSPOSE   = 149;
    
    #endregion
}
