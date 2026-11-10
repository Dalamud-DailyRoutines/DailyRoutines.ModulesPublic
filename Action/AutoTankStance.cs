using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoTankStance : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoTankStanceTitle"),
        Description = Lang.Get("AutoTankStanceDescription"),
        Category    = ModuleCategory.Action
    };

    public override string ModuleName => nameof(AutoTankStance);

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        TaskHelper ??= new()
        {
            TimeoutMS = 5_000,
            MoveToNextCheckFunc = () =>
            {
                if (ICondition.Instance().IsBetweenAreas    ||
                    ICondition.Instance().IsOccupiedInEvent ||
                    !UIModule.IsScreenReady()               ||
                    LocalPlayerState.Object is not { IsTargetable: true })
                    return false;

                var tankStance = TankStanceActions.FirstOrDefault(x => x.ClassJob == LocalPlayerState.ClassJob);

                if (GameState.IsDutyCompleted ||
                    tankStance.Action == 0)
                {
                    TaskHelper.Abort();
                    return false;
                }

                return true;
            }
        };

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        IDutyState.Instance().DutyRecommenced    += OnDutyRecommenced;
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        IDutyState.Instance().DutyRecommenced    -= OnDutyRecommenced;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoTankStance-OnlyAutoStanceWhenOneTank"), ref config.OnlyAutoStanceWhenOneTank))
            config.Save(this);

        ImGuiOm.HelpMarker(Lang.Get("AutoTankStance-OnlyAutoStanceWhenOneTankHelp"));
    }

    private void OnZoneChanged
    (
        uint zone
    )
    {
        if (!GameState.IsInPVEActonZone) return;

        if (config.OnlyAutoStanceWhenOneTank &&
            GameState.ContentFinderConditionData.ContentMemberType.Value.TanksPerParty != 1)
            return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(1_000);
        TaskHelper.Enqueue(UseTankStance);
    }

    private void OnDutyRecommenced
    (
        IDutyStateEventArgs args
    )
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(UseTankStance);
    }

    private static bool UseTankStance()
    {
        var tankStance = TankStanceActions.FirstOrDefault(x => x.ClassJob == LocalPlayerState.ClassJob);

        if (tankStance.Action == 0)
            return true;

        return LocalPlayerState.HasStatus(tankStance.Status, out _) ||
               UseActionManager.Instance().UseAction(ActionType.Action, tankStance.Action);
    }

    private class Config : ModuleConfig
    {
        public bool OnlyAutoStanceWhenOneTank = true;
    }

    #region 常量

    private static readonly (uint ClassJob, uint Action, uint Status)[] TankStanceActions =
    [
        // 剑术师 / 骑士
        (1, 28, 79),
        (19, 28, 79),
        // 斧术师 / 战士
        (3, 48, 91),
        (21, 48, 91),
        // 暗黑骑士
        (32, 3629, 743),
        // 绝枪战士
        (37, 16142, 1833)
    ];

    #endregion
}
