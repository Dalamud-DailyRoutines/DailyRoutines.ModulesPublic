using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal unsafe class AutoDance : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDanceTitle"),
        Description = Lang.Get("AutoDanceDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoDance);

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };

        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);
    }

    protected override void Uninit() =>
        UseActionManager.Instance().Unreg(OnPostUseAction);

    private void OnPostUseAction
    (
        bool       result,
        ActionType actionType,
        uint       actionID,
        ulong      targetID,
        Vector3    location,
        uint       extraParam,
        byte       a7
    )
    {
        if (!result || actionType != ActionType.Action || !DanceActions.Contains(actionID)) return;

        var gauge = IJobGauges.Instance().Get<DNCGauge>();
        if (gauge.IsDancing) return;

        TaskHelper.Enqueue(() => gauge.IsDancing);
        TaskHelper.Enqueue(() => DanceStep(actionID == TECHNIQUE_STEP));
    }

    private bool DanceStep
    (
        bool isTechnicalStep
    )
    {
        var gauge = IJobGauges.Instance().Get<DNCGauge>();

        if (!gauge.IsDancing)
        {
            TaskHelper.Abort();
            return true;
        }

        if (gauge.CompletedSteps <
            (isTechnicalStep ?
                 4 :
                 2))
        {
            var nextStep = gauge.NextStep;

            if (ActionManager.Instance()->GetActionStatus(ActionType.Action, nextStep) != 0)
                return false;

            if (UseActionManager.Instance().UseActionLocation(ActionType.Action, nextStep))
            {
                TaskHelper.Enqueue(() => DanceStep(isTechnicalStep));
                return true;
            }
        }

        return false;
    }

    #region 常量

    private const uint STANDARD_STEP  = 15997; // 标准舞步
    private const uint TECHNIQUE_STEP = 15998; // 技巧舞步

    private static readonly uint[] DanceActions = [STANDARD_STEP, TECHNIQUE_STEP];

    #endregion
}
