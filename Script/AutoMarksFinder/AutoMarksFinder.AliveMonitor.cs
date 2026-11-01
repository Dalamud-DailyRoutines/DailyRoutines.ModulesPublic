using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private readonly Dictionary<NotoriousMonsterList, Dictionary<NotoriousMonsterFound, AliveTrack>> aliveTracks = new(ReferenceEqualityComparer.Instance);

    private void OnAliveMonitorUpdate
    (
        IFramework _
    )
    {
        UpdateTrainAliveStates();
        UpdateAutoBroadcast();
    }

    private void UpdateTrainAliveStates()
    {
        if (config.Trains.Count == 0)
            return;

        if (LocalPlayerState.Object is null ||
            !UIModule.IsScreenReady() ||
            ICondition.Instance().IsBetweenAreas)
            return;

        if (SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
            return;

        var currentZone     = GameState.TerritoryType;
        var currentInstance = InstancesManager.CurrentInstance;
        var observable      = activeTrain.MonstersFound
                                         .Where(monster => IsMonsterObservable(monster, currentZone, currentInstance))
                                         .ToList();

        if (observable.Count == 0)
            return;

        var trainTracks = GetTrainTracks(activeTrain);
        var battleNPCs  = BuildBattleNPCIndex(observable.Select(monster => monster.DataID));
        var changed     = false;

        foreach (var group in observable.GroupBy(monster => monster))
        {
            if (!trainTracks.TryGetValue(group.Key, out var track))
            {
                track                  = new AliveTrack();
                trainTracks[group.Key] = track;
            }

            if (!FindNearestState(group.Key, battleNPCs, out var isDead))
                continue;

            if (isDead)
            {
                if (track.ManualAlive)
                    continue;

                if (SetGroupState(group, false))
                {
                    track.AutoKilled = true;
                    changed          = true;
                }

                continue;
            }

            track.ManualAlive = false;

            if (track.AutoKilled && SetGroupState(group, true))
            {
                track.AutoKilled = false;
                changed          = true;
            }
        }

        if (changed)
            config.Save(this);
    }

    private Dictionary<NotoriousMonsterFound, AliveTrack> GetTrainTracks
    (
        NotoriousMonsterList train
    )
    {
        if (!aliveTracks.TryGetValue(train, out var tracks))
        {
            tracks             = [];
            aliveTracks[train] = tracks;
        }

        return tracks;
    }

    private static bool IsMonsterObservable
    (
        NotoriousMonsterFound monster,
        uint                  zoneID,
        uint                  instance
    )
    {
        if (monster.ZoneID != zoneID)
            return false;

        if (InstancesManager.IsInstancedArea &&
            monster.Instance != 0            &&
            monster.Instance != instance)
            return false;

        return true;
    }

    private static Dictionary<uint, List<IBattleChara>> BuildBattleNPCIndex
    (
        IEnumerable<uint> dataIDs
    )
    {
        var wanted = new HashSet<uint>(dataIDs);
        var result = new Dictionary<uint, List<IBattleChara>>();

        foreach (var obj in IObjectTable.Instance())
        {
            if (obj is not IBattleChara { ObjectKind: ObjectKind.BattleNpc } chara)
                continue;

            if (!wanted.Contains(obj.DataID))
                continue;

            if (!result.TryGetValue(obj.DataID, out var entries))
            {
                entries             = [];
                result[obj.DataID]  = entries;
            }

            entries.Add(chara);
        }

        return result;
    }

    private static bool FindNearestState
    (
        NotoriousMonsterFound                         monster,
        IReadOnlyDictionary<uint, List<IBattleChara>> battleNPCs,
        out bool                                      isDead
    )
    {
        isDead = false;

        if (!battleNPCs.TryGetValue(monster.DataID, out var candidates))
            return false;

        var found           = false;
        var bestDistSquared = MONSTER_MATCH_RADIUS * MONSTER_MATCH_RADIUS;

        foreach (var chara in candidates)
        {
            var distSquared = Vector3.DistanceSquared(chara.Position, monster.Position);
            if (distSquared >= bestDistSquared)
                continue;

            isDead          = chara.IsDead || chara.CurrentHp == 0;
            bestDistSquared = distSquared;
            found           = true;
        }

        return found;
    }

    private static bool SetGroupState
    (
        IEnumerable<NotoriousMonsterFound> monsters,
        bool                               isAlive
    )
    {
        var changed = false;

        foreach (var monster in monsters)
        {
            if (monster.IsAlive == isAlive)
                continue;

            monster.SetState(isAlive);
            changed = true;
        }

        return changed;
    }

    #region 常量

    private const float MONSTER_MATCH_RADIUS   = 200f;
    private const uint  ALIVE_MONITOR_INTERVAL = 500;

    #endregion
}
