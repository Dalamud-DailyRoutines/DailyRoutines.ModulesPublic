using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDance : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDanceTitle"),
        Description = Lang.Get("AutoDanceDescription"),
        Category    = ModuleCategory.Action
    };
    
    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };

        UseActionManager.Instance().RegPostUseActionLocation(OnPostUseAction);
    }
    
    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPostUseAction);

        TaskHelper?.Abort();
        TaskHelper = null;
    }

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

        var gauge = DService.Instance().JobGauges.Get<DNCGauge>();
        if (gauge.IsDancing) return;

        TaskHelper.Enqueue(() => gauge.IsDancing);
        TaskHelper.Enqueue(() => DanceStep(actionID != 15997));
    }

    private bool DanceStep(bool isTechnicalStep)
    {
        var gauge = DService.Instance().JobGauges.Get<DNCGauge>();

        if (!gauge.IsDancing)
        {
            TaskHelper.Abort();
            return true;
        }

        if (gauge.CompletedSteps < (isTechnicalStep ? 4 : 2))
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

    private static readonly FrozenSet<uint> DanceActions = [15997, 15998];

    #endregion
}
