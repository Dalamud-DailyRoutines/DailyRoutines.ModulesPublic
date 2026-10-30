using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoGathererRoleActions : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoGathererRoleActionsTitle"),
        Description = Lang.Get("AutoGathererRoleActionsDescription"),
        Category    = ModuleCategory.CraftGather
    };

    protected override void Init()
    {
        TaskHelper = new()
        {
            TimeoutMS       = 5_000,
            RetryIntervalMS = 500
        };

        IClientState.Instance().ClassJobChanged += OnJobChanged;
        OnJobChanged(LocalPlayerState.ClassJob);
    }

    protected override void Uninit() =>
        IClientState.Instance().ClassJobChanged -= OnJobChanged;

    private unsafe void OnJobChanged
    (
        uint jobID
    )
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
                    if (HomeOnlyActions.Contains(action))
                    {
                        if (GameState.HomeWorld != GameState.CurrentWorld)
                            continue;
                    }
                    
                    if (localPlayer->StatusManager.HasStatus(status)) continue;

                    TaskHelper.Enqueue
                    (() =>
                        {
                            if (ICondition.Instance().IsBetweenAreas)
                            {
                                TaskHelper.Abort();
                                return true;
                            }

                            if (localPlayer->StatusManager.HasStatus(status) ||
                                !ActionManager.IsActionUnlocked(action))
                                return true;

                            UseActionManager.Instance().UseActionLocation(ActionType.Action, action);
                            return false;
                        }
                    );
                }
            }
        );
    }

    #region 常量

    private static readonly uint[] ValidJobs =
    [
        // 采矿工
        16,
        // 园艺工
        17,
        // 捕鱼人
        18
    ];

    // ActionID - StatusID
    private static readonly List<(uint Action, uint Status)> Actions =
    [
        // 矿脉勘探
        (227, 225),
        // 三角测量
        (210, 217),
        // 山岳之相
        (238, 222),
        // 丛林之相
        (221, 221),
        // 鱼群测定
        (7903, 1166),
        // 海洋之相
        (7911, 1173),
    ];

    private static readonly uint[] HomeOnlyActions =
    [
        // 丛林之相
        221,
        // 山岳之相
        238
    ];

    #endregion
}
