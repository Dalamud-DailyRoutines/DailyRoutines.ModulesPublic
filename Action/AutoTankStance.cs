using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public class AutoTankStance : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoTankStanceTitle"),
        Description = GetLoc("AutoTankStanceDescription"),
        Category = ModuleCategories.Action,
    };

    private static bool ConfigOnlyAutoStanceWhenOneTank = true;

    private static HashSet<uint>? ContentsWithOneTank;
    private static readonly uint[] TankStanceStatuses = [79, 91, 743, 1833];

    private static readonly Dictionary<uint, uint> TankStanceActions = new()
    {
        // 剑术师 / 骑士
        { 1, 28 },
        { 19, 28 },
        // 斧术师 / 战士
        { 3, 48 },
        { 21, 48 },
        // 暗黑骑士
        { 32, 3629 },
        // 绝枪战士
        { 37, 16142 },
    };

    public override void Init()
    {
        AddConfig("OnlyAutoStanceWhenOneTank", true);
        ConfigOnlyAutoStanceWhenOneTank = GetConfig<bool>("OnlyAutoStanceWhenOneTank");

        TaskHelper ??= new TaskHelper { AbortOnTimeout = true, TimeLimitMS = 30000, ShowDebug = false };

        ContentsWithOneTank ??= PresetSheet.Contents
                                          .Where(x => (uint)x.Value.ContentMemberType.Value.TanksPerParty == 1)
                                          .Select(x => x.Key)
                                          .ToHashSet();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.DutyState.DutyRecommenced += OnDutyRecommenced;
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("AutoTankStance-OnlyAutoStanceWhenOneTank"),
                           ref ConfigOnlyAutoStanceWhenOneTank))
            UpdateConfig("OnlyAutoStanceWhenOneTank", ConfigOnlyAutoStanceWhenOneTank);

        ImGuiOm.HelpMarker(Lang.Get("AutoTankStance-OnlyAutoStanceWhenOneTankHelp"));
    }

    private void OnZoneChanged(ushort zone)
    {
        if (!IsValidPVEDuty()) return;
        if ((ConfigOnlyAutoStanceWhenOneTank && ContentsWithOneTank.Contains(zone)) ||
            (!ConfigOnlyAutoStanceWhenOneTank && PresetSheet.Contents.ContainsKey(zone)))
        {
            TaskHelper.Abort();
            TaskHelper.DelayNext(1000);
            TaskHelper.Enqueue(CheckCurrentJob);
        }
    }

    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCurrentJob);
    }

    private static unsafe bool? CheckCurrentJob()
    {
        if (BetweenAreas) return false;
        if (!IsScreenReady()) return false;

        var player = DService.ObjectTable.LocalPlayer;
        if (player == null || player.ClassJob.RowId == 0 || !player.IsTargetable) return false;

        var job = player.ClassJob.RowId;
        if (!TankStanceActions.TryGetValue(job, out var actionID)) return true;

        if (OccupiedInEvent) return false;

        var battlePlayer = (BattleChara*)player.Address;
        foreach (var status in TankStanceStatuses)
        {
            if (battlePlayer->GetStatusManager()->HasStatus(status))
                return true;
        }

        return ActionManager.Instance()->UseAction(ActionType.Action, actionID);
    }

    private static bool IsValidPVEDuty()
    {
        HashSet<uint> InvalidContentTypes = [16, 17, 18, 19, 31, 32, 34, 35];

        return PresetSheet.Contents.TryGetValue(DService.ClientState.TerritoryType, out var zoneData) &&
               !DService.ClientState.IsPvP && !zoneData.PvP && !InvalidContentTypes.Contains(zoneData.ContentType.RowId);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;

        base.Uninit();
    }
}
