using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoDrawMotifs : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDrawMotifsTitle"),
        Description = Lang.Get("AutoDrawMotifsDescription"),
        Category    = ModuleCategory.Action
    };
    
    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
        DService.Instance().DutyState.DutyCompleted      += OnDutyCompleted;
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
        DService.Instance().DutyState.DutyCompleted      -= OnDutyCompleted;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoDrawMotifs-DrawWhenOutOfCombat"), ref config.DrawWhenOutOfCombat))
            config.Save(this);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        TaskHelper.Abort();

        if (value || !config.DrawWhenOutOfCombat) return;

        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 重新挑战
    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    // 完成副本
    private void OnDutyCompleted(object? sender, ushort e) =>
        TaskHelper.Abort();

    // 进入副本
    private void OnZoneChanged(ushort zone)
    {
        TaskHelper.Abort();

        if (!Sheets.Contents.ContainsKey(zone)) return;
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    private bool CheckCurrentJob()
    {
        if (DService.Instance().Condition.IsBetweenAreas || DService.Instance().Condition.IsOccupiedInEvent) return false;

        if (DService.Instance().ObjectTable.LocalPlayer is not { ClassJob.RowId: 42, Level: >= 30 } || !GameState.IsInPVEActonZone)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(DrawNeededMotif, "DrawNeededMotif", 5_000, weight: 1);
        return true;
    }

    private bool DrawNeededMotif()
    {
        var gauge = DService.Instance().JobGauges.Get<PCTGauge>();

        if (DService.Instance().ObjectTable.LocalPlayer == null ||
            DService.Instance().Condition.IsBetweenAreas        ||
            DService.Instance().Condition[ConditionFlag.Casting]) return false;

        if (DService.Instance().Condition.Any(ConditionFlag.InCombat, ConditionFlag.Mounted, ConditionFlag.Mounting, ConditionFlag.InFlight))
        {
            TaskHelper.Abort();
            return true;
        }

        var motifAction = 0U;
        if (!gauge.CreatureMotifDrawn && ActionManager.IsActionUnlocked(34689))
            motifAction = 34689;
        else if (!gauge.WeaponMotifDrawn && ActionManager.IsActionUnlocked(34690) && !LocalPlayerState.HasStatus(3680, out _))
            motifAction = 34690;
        else if (!gauge.LandscapeMotifDrawn && ActionManager.IsActionUnlocked(34691))
            motifAction = 34691;

        if (motifAction == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(() => UseActionManager.Instance().UseAction(ActionType.Action, motifAction), $"UseAction_{motifAction}", 2_000, weight: 1);
        TaskHelper.DelayNext(500, $"DrawMotif_{motifAction}", 1);
        TaskHelper.Enqueue(DrawNeededMotif, "DrawNeededMotif", 5_000, weight: 1);
        return true;
    }
    
    private class Config : ModuleConfig
    {
        public bool DrawWhenOutOfCombat;
    }
}
