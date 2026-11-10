using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoDrawMotifs : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDrawMotifsTitle"),
        Description = Lang.Get("AutoDrawMotifsDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoDrawMotifs);

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas         ||
                    ICondition.Instance().IsOccupiedInEvent      ||
                    ICondition.Instance()[ConditionFlag.Casting] ||
                    LocalPlayerState.Object == null)
                    return false;

                if (LocalPlayerState.ClassJob     != CLASS_JOB_PICTOMANCER ||
                    LocalPlayerState.CurrentLevel < 30                     ||
                    !GameState.IsInPVEActonZone                            ||
                    GameState.IsDutyCompleted                              ||
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

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoDrawMotifs-DrawWhenOutOfCombat"), ref config.DrawWhenOutOfCombat))
            config.Save(this);
    }

    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (flag != ConditionFlag.InCombat) return;

        TaskHelper.Abort();

        if (value || !config.DrawWhenOutOfCombat) return;

        TaskHelper.Enqueue(DrawNeededMotif);
    }

    // 重新挑战
    private void OnDutyRecommenced
    (
        IDutyStateEventArgs args
    )
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(DrawNeededMotif);
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
        TaskHelper.Enqueue(DrawNeededMotif);
    }

    private bool DrawNeededMotif()
    {
        var gauge = IJobGauges.Instance().Get<PCTGauge>();

        var motifAction = 0U;
        if (!gauge.CreatureMotifDrawn && ActionManager.IsActionUnlocked(CREATURE_MOTIF))
            motifAction = CREATURE_MOTIF;
        else if (!gauge.WeaponMotifDrawn                      &&
                 ActionManager.IsActionUnlocked(WEAPON_MOTIF) &&
                 !LocalPlayerState.HasStatus(STATUS_HAMMER_COMBO, out _))
            motifAction = WEAPON_MOTIF;
        else if (!gauge.LandscapeMotifDrawn && ActionManager.IsActionUnlocked(LANDSCAPE_MOTIF))
            motifAction = LANDSCAPE_MOTIF;

        if (motifAction == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, motifAction));
        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(DrawNeededMotif);
        return true;
    }

    private class Config : ModuleConfig
    {
        public bool DrawWhenOutOfCombat;
    }

    #region 常量

    private const uint CLASS_JOB_PICTOMANCER = 42;
    private const uint CREATURE_MOTIF        = 34689; // 动物彩绘
    private const uint WEAPON_MOTIF          = 34690; // 武器彩绘
    private const uint LANDSCAPE_MOTIF       = 34691; // 风景彩绘
    private const uint STATUS_HAMMER_COMBO   = 3680;  // 重锤连击

    #endregion
}
