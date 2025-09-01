using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DailyRoutines.ModulesPublic;

public class AutoTankStance : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoTankStanceTitle"),
        Description = GetLoc("AutoTankStanceDescription"),
        Category    = ModuleCategories.Action,
    };
    
    private static readonly HashSet<uint> InvalidContentTypes = [16, 17, 18, 19, 31, 32, 34, 35];

    private static readonly Dictionary<uint, (uint Action, uint Status)> TankStanceActions = new()
    {
        // 剑术师 / 骑士
        [1]  = (28, 79),
        [19] = (28, 79),
        // 斧术师 / 战士
        [3]  = (48, 91),
        [21] = (48, 91),
        // 暗黑骑士
        [32] = (3629, 743),
        // 绝枪战士
        [37] = (16142, 1833),
    };
    
    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new() { TimeLimitMS = 30_000 };

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoTankStance-OnlyAutoStanceWhenOneTank"), ref ModuleConfig.OnlyAutoStanceWhenOneTank))
            SaveConfig(ModuleConfig);

        ImGuiOm.HelpMarker(GetLoc("AutoTankStance-OnlyAutoStanceWhenOneTankHelp"));
    }

    private void OnZoneChanged(ushort zone)
    {
        TaskHelper.Abort();
        
        if (!IsValidPVEDuty()) return;
        
        // TODO: 表数据定义歪了, 所以
        if (ModuleConfig.OnlyAutoStanceWhenOneTank && 
            GameState.ContentFinderConditionData.ContentMemberType.Value.HealersPerParty != 1) return;
        
        TaskHelper.DelayNext(1000);
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    private static bool? CheckCurrentJob()
    {
        if (BetweenAreas || OccupiedInEvent || !IsScreenReady()) return false;

        if (DService.ObjectTable.LocalPlayer is not { ClassJob.RowId: var job, IsTargetable: true } || job == 0) 
            return false;

        if (!TankStanceActions.TryGetValue(job, out var info)) return true;
        if (LocalPlayerState.HasStatus(info.Status, out _)) return true;

        return UseActionManager.UseAction(ActionType.Action, info.Action);
    }

    private static bool IsValidPVEDuty() =>
        GameState.ContentFinderCondition != 0     &&
        !GameState.IsInPVPArea                    &&
        !GameState.ContentFinderConditionData.PvP &&
        !InvalidContentTypes.Contains(GameState.ContentFinderConditionData.ContentType.RowId);

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public bool OnlyAutoStanceWhenOneTank = true;
    }
}
