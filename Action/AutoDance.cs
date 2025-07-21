using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.Modules;

public unsafe class AutoDance : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDanceTitle"),
        Description = GetLoc("AutoDanceDescription"),
        Category    = ModuleCategories.Action,
    };

    private static HashSet<uint> DanceActions = [15997, 15998];

    protected override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 5_000 };

        UseActionManager.RegUseActionLocation(OnPostUseAction);
    }

    private void OnPostUseAction(
        bool result, ActionType actionType, uint actionID, ulong targetID, Vector3 location, uint extraParam)
    {
        if (!result || actionType != ActionType.Action || !DanceActions.Contains(actionID)) return;
        
        var gauge = DService.JobGauges.Get<DNCGauge>();
        if (gauge.IsDancing) return;
        
        TaskHelper.Enqueue(() => gauge.IsDancing);
        TaskHelper.Enqueue(() => DanceStep(actionID != 15997));
    }

    private bool? DanceStep(bool isTechnicalStep)
    {
        var gauge = DService.JobGauges.Get<DNCGauge>();
        if (!gauge.IsDancing)
        {
            TaskHelper.Abort();
            return true;
        }

        if (gauge.CompletedSteps < (isTechnicalStep ? 4 : 2))
        {
            var nextStep = gauge.NextStep;
            if (ActionManager.Instance()->GetActionStatus(ActionType.Action, nextStep) != 0) return false;
            if (UseActionManager.UseActionLocation(ActionType.Action, nextStep))
            {
                TaskHelper.Enqueue(() => DanceStep(isTechnicalStep));
                return true;
            }
        }

        return false;
    }

    protected override void Uninit()
    {
        UseActionManager.UnregUseActionLocation(OnPostUseAction);

        TaskHelper?.Abort();
        TaskHelper = null;
    }
}
