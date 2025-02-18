using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public class AutoChakraFormShift : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoChakraFormShiftTitle"),
        Description = GetLoc("AutoChakraFormShiftDescription"),
        Category = ModuleCategories.Action,
    };

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.DutyState.DutyRecommenced += OnDutyRecommenced;
        DService.Condition.ConditionChange += OnConditionChanged;
    }

    private bool? CheckCurrentJob()
    {
        if (BetweenAreas || OccupiedInEvent) return false;
        if (DService.ClientState.LocalPlayer is not { ClassJob.RowId: 20 } || !IsValidPVEDuty())
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, true, 1);
        return true;
    }

    private static unsafe bool IsValidPVEDuty()
    {
        HashSet<uint> InvalidContentTypes = [16, 17, 18, 19, 31, 32, 34, 35];

        var isPVP = GameMain.IsInPvPArea() || GameMain.IsInPvPInstance();
        var contentData = LuminaCache.GetRow<ContentFinderCondition>(GameMain.Instance()->CurrentContentFinderConditionId);
        
        return !isPVP && (contentData == null || !InvalidContentTypes.Contains(contentData.Value.ContentType.RowId));
    }

    private unsafe bool? UseRelatedActions()
    {
        var gauge = DService.JobGauges.Get<MNKGauge>();
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        var statusManager = localPlayer.ToBCStruct()->StatusManager;

        const uint SteeledMeditation = 36940;
        const uint FormShift = 4262;

        var action = 0U;
        // 铁山斗气
        if (gauge.Chakra != 5 && IsActionUnlocked(SteeledMeditation))
            action = SteeledMeditation;
        // 演武
        else if (!statusManager.HasStatus(110) &&
                 (!statusManager.HasStatus(2513) ||
                  statusManager.GetRemainingTime(statusManager.GetStatusIndex(2513)) <= 27) && 
                 IsActionUnlocked(FormShift))
            action = 4262;

        if (action == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.UseAction(ActionType.Action, action), $"UseAction_{action}", 2_000, true, 1);
        TaskHelper.DelayNext(500, $"Delay_Use{action}", false, 1);
        TaskHelper.Enqueue(UseRelatedActions, "UseRelatedActions", 5_000, true, 1);
        return true;
    }

    // 脱战
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat) return;
        TaskHelper.Abort();

        if (!value) TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 重新挑战
    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 进入副本
    private void OnZoneChanged(ushort zone)
    {
        if (LuminaCache.GetRow<TerritoryType>(zone) is not { ContentFinderCondition.RowId: > 0 }) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;
        DService.Condition.ConditionChange -= OnConditionChanged;

        base.Uninit();
    }
}
