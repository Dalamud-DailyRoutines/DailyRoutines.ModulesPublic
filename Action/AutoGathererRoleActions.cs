using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;
using OmenTools.Threading;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoGathererRoleActions : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGathererRoleActionsTitle"),
        Description = Lang.Get("AutoGathererRoleActionsDescription"),
        Category    = ModuleCategory.Action
    };

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };

        DService.Instance().ClientState.ClassJobChanged += OnJobChanged;
        OnJobChanged(LocalPlayerState.ClassJob);
    }
    
    protected override void Uninit() =>
        DService.Instance().ClientState.ClassJobChanged -= OnJobChanged;

    private unsafe void OnJobChanged(uint jobID)
    {
        TaskHelper.Abort();
        if (!ValidJobs.Contains(jobID)) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        TaskHelper.DelayNext(5_00);
        TaskHelper.Enqueue
        (() =>
            {
                foreach (var (action, status) in Actions)
                {
                    if (localPlayer->StatusManager.HasStatus(status)) continue;

                    TaskHelper.Enqueue
                    (() =>
                        {
                            if (!Throttler.Shared.Throttle("AutoGathererRoleActions-UseAction")) return false;

                            if (DService.Instance().Condition.IsBetweenAreas)
                            {
                                TaskHelper.Abort();
                                return true;
                            }

                            if (localPlayer->StatusManager.HasStatus(status) || !ActionManager.IsActionUnlocked(action)) return true;

                            UseActionManager.Instance().UseActionLocation(ActionType.Action, action);
                            return localPlayer->StatusManager.HasStatus(status);
                        }
                    );
                }
            }
        );
    }

    #region 常量

    private static readonly FrozenSet<uint> ValidJobs = [16, 17, 18];

    // ActionID - StatusID
    private static readonly FrozenDictionary<uint, uint> Actions = new Dictionary<uint, uint>()
    {
        // 矿脉勘探
        [227] = 225,
        // 三角测量
        [210] = 217,
        // 山岳之相
        [238] = 222,
        // 丛林之相
        [221] = 221,
        // 鱼群测定
        [7903] = 1166,
        // 海洋之相
        [7911] = 1173
    }.ToFrozenDictionary();

    #endregion
}
