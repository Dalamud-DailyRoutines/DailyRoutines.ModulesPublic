using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.Modules;

public class AutoGathererRoleActions : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoGathererRoleActionsTitle"),
        Description = GetLoc("AutoGathererRoleActionsDescription"),
        Category = ModuleCategories.Action,
    };

    private static readonly HashSet<uint> ValidJobs = [16, 17, 18];

    // ActionID - StatusID
    private static readonly Dictionary<uint, uint> Actions = new()
    {
        // 矿脉勘探
        { 227, 225 },
        // 三角测量
        { 210, 217 },
        // 山岳之相
        { 238, 222 },
        // 丛林之相
        { 221, 221 },
        // 鱼群测定
        { 7903, 1166 },
        // 海洋之相
        { 7911, 1173 },
    };

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };

        DService.ClientState.ClassJobChanged += OnJobChanged;
        if (DService.ClientState.LocalPlayer != null)
            OnJobChanged(DService.ClientState.LocalPlayer.ClassJob.RowId);
    }

    private unsafe void OnJobChanged(uint jobID)
    {
        TaskHelper.Abort();
        if (!ValidJobs.Contains(jobID)) return;

        var localPlayer = DService.ClientState.LocalPlayer.ToBCStruct();
        if (localPlayer == null) return;

        TaskHelper.DelayNext(5_00);
        TaskHelper.Enqueue(() =>
        {
            foreach (var (action, status) in Actions)
            {
                if (localPlayer->StatusManager.HasStatus(status)) continue;

                TaskHelper.Enqueue(() =>
                {
                    if (!Throttler.Throttle("AutoGathererRoleActions-UseAction", 100)) return false;
                    if (localPlayer->StatusManager.HasStatus(status) || !IsActionUnlocked(action)) return true;
                    UseActionManager.UseActionLocation(ActionType.Action, action);
                    return localPlayer->StatusManager.HasStatus(status);
                });
            }
        });
    }

    public override void Uninit()
    {
        DService.ClientState.ClassJobChanged -= OnJobChanged;

        base.Uninit();
    }
}
