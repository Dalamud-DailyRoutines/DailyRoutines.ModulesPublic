using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private const float DAMAGED_HP_PERCENT        = 80f;
    private const float MONSTER_MATCH_RADIUS      = 200f;
    private const float MISSING_INFERENCE_RADIUS  = 80f;
    private const long  MISSING_DEATH_GRACE_MS    = 2_000;
    private const uint  ALIVE_MONITOR_INTERVAL    = 500;

    private readonly Dictionary<NotoriousMonsterList, Dictionary<(uint DataID, uint ZoneID, uint Instance), AliveTrack>> aliveTracks = new(ReferenceEqualityComparer.Instance);
    private       (uint ZoneID, uint Instance)                                                                        lastAliveStay;
    private       NotoriousMonsterList?                                                                               lastActiveTrain;
    private       bool                                                                                                aliveMonitorRegistered;

    private sealed class AliveTrack
    {
        public bool HasBeenDamaged;
        public bool SeenThisStay;
        public bool AutoKilled;
        public bool ManualAlive;
        public long MissingSince;
    }

    private void RegisterAliveMonitor()
    {
        if (aliveMonitorRegistered) return;

        FrameworkManager.Instance().Reg(OnAliveMonitorUpdate, ALIVE_MONITOR_INTERVAL);
        aliveMonitorRegistered = true;
    }

    private void UnregisterAliveMonitor()
    {
        if (!aliveMonitorRegistered) return;

        FrameworkManager.Instance().Unreg(OnAliveMonitorUpdate);
        aliveMonitorRegistered = false;
        aliveTracks.Clear();
        lastActiveTrain = null;
        lastAliveStay   = default;
    }

    private void OnAliveMonitorUpdate
    (
        IFramework _
    )
    {
        UpdateTrainAliveStates();
        UpdateAutoPilot();
    }

    private void UpdateTrainAliveStates()
    {
        if (config?.Trains is not { Count: > 0 }) return;
        if (IObjectTable.Instance().LocalPlayer is not { } localPlayer ||
            !UIModule.IsScreenReady() || ICondition.Instance().IsBetweenAreas)
        {
            // 加载期间的对象消失不应参与死亡推断。
            foreach (var tracks in aliveTracks.Values)
            {
                foreach (var track in tracks.Values)
                {
                    track.SeenThisStay   = false;
                    track.HasBeenDamaged = false;
                    track.MissingSince   = 0;
                }
            }

            return;
        }

        var currentZone     = GameState.TerritoryType;
        var currentInstance = InstancesManager.CurrentInstance;
        var stayChanged     = lastAliveStay != (currentZone, currentInstance);

        if (stayChanged)
        {
            foreach (var trainTracks in aliveTracks.Values)
            {
                foreach (var track in trainTracks.Values)
                {
                    track.SeenThisStay   = false;
                    track.HasBeenDamaged = false;
                    track.MissingSince   = 0;
                }
            }

            lastAliveStay = (currentZone, currentInstance);
        }

        var activeTrain = selectedTrainIndex >= 0 && selectedTrainIndex < config.Trains.Count ?
                              config.Trains[selectedTrainIndex] :
                              null;

        if (!ReferenceEquals(activeTrain, lastActiveTrain))
        {
            foreach (var trainTracks in aliveTracks.Values)
            {
                foreach (var track in trainTracks.Values)
                {
                    track.SeenThisStay   = false;
                    track.HasBeenDamaged = false;
                    track.MissingSince   = 0;
                }
            }

            lastActiveTrain = activeTrain;
        }

        if (activeTrain is not { MonstersFound.Count: > 0 }) return;

        var changed     = false;
        var battleNPCs  = BuildBattleNPCIndex(IObjectTable.Instance());

        foreach (var train in config.Trains)
        {
            if (!ReferenceEquals(train, activeTrain)) continue;
            if (train.MonstersFound.Count == 0) continue;

            var trainTracks = GetTrainTracks(train);
            var observableMonsters = train.MonstersFound
                                           .Where(monster => IsMonsterObservable(monster, currentZone, currentInstance))
                                           .GroupBy(monster => (monster.DataID, monster.ZoneID, monster.Instance));

            foreach (var group in observableMonsters)
            {
                var monster = group.First();
                var key     = group.Key;
                if (!trainTracks.TryGetValue(key, out var track))
                {
                    track            = new AliveTrack();
                    trainTracks[key] = track;
                }

                if (FindNearestState(monster, battleNPCs, out var hpPercent, out var isDead))
                {
                    track.SeenThisStay = true;
                    track.MissingSince = 0;
                    if (hpPercent < DAMAGED_HP_PERCENT)
                        track.HasBeenDamaged = true;

                    if (!isDead)
                        track.ManualAlive = false;

                    if (isDead)
                    {
                        if (!track.ManualAlive && SetGroupState(group, false))
                        {
                            track.AutoKilled = true;
                            changed          = true;
                        }
                    }
                    else if (track.AutoKilled && SetGroupState(group, true))
                    {
                        track.AutoKilled = false;
                        changed          = true;
                    }
                }
                else if (track.SeenThisStay && track.HasBeenDamaged && !track.ManualAlive &&
                         Vector3.DistanceSquared(localPlayer.Position, monster.Position) <= MISSING_INFERENCE_RADIUS * MISSING_INFERENCE_RADIUS)
                {
                    if (track.MissingSince == 0)
                    {
                        track.MissingSince = Environment.TickCount64;
                    }
                    else if (Environment.TickCount64 - track.MissingSince >= MISSING_DEATH_GRACE_MS && SetGroupState(group, false))
                    {
                        track.AutoKilled = true;
                        changed          = true;
                    }
                }
                else
                    track.MissingSince = 0;
            }
        }

        if (changed)
            config.Save(this);
    }

    private Dictionary<(uint DataID, uint ZoneID, uint Instance), AliveTrack> GetTrainTracks
    (
        NotoriousMonsterList train
    )
    {
        if (!aliveTracks.TryGetValue(train, out var tracks))
        {
            tracks            = [];
            aliveTracks[train] = tracks;
        }

        return tracks;
    }

    private void ForgetTrainTracks
    (
        NotoriousMonsterList train
    ) => aliveTracks.Remove(train);


    private static bool IsMonsterObservable
    (
        NotoriousMonsterFound monster,
        uint                  zoneID,
        uint                  instance
    )
    {
        if (monster.ZoneID != zoneID) return false;

        if (InstancesManager.IsInstancedArea &&
            monster.Instance != 0            &&
            monster.Instance != instance)
            return false;

        return true;
    }

    private static Dictionary<uint, List<IBattleChara>> BuildBattleNPCIndex
    (
        IEnumerable<IGameObject> objects
    )
    {
        var result = new Dictionary<uint, List<IBattleChara>>();

        foreach (var obj in objects)
        {
            if (obj is not { ObjectKind: ObjectKind.BattleNpc } || obj is not IBattleChara chara)
                continue;

            if (!result.TryGetValue(obj.DataID, out var entries))
            {
                entries           = [];
                result[obj.DataID] = entries;
            }

            entries.Add(chara);
        }

        return result;
    }

    private static bool FindNearestState
    (
        NotoriousMonsterFound                       monster,
        IReadOnlyDictionary<uint, List<IBattleChara>> battleNPCs,
        out float                                   hpPercent,
        out bool                                    isDead
    )
    {
        hpPercent = 0f;
        isDead    = false;

        if (!battleNPCs.TryGetValue(monster.DataID, out var candidates))
            return false;

        var found           = false;
        var bestDistSquared = MONSTER_MATCH_RADIUS * MONSTER_MATCH_RADIUS;

        foreach (var chara in candidates)
        {
            var distSquared = Vector3.DistanceSquared(chara.Position, monster.Position);
            if (distSquared >= bestDistSquared) continue;

            isDead    = chara.IsDead || chara.CurrentHp == 0;
            hpPercent = chara.MaxHp > 0 ? chara.CurrentHp / (float)chara.MaxHp * 100f : 100f;

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
            if (monster.IsAlive == isAlive) continue;

            monster.SetState(isAlive);
            changed = true;
        }

        return changed;
    }
}
