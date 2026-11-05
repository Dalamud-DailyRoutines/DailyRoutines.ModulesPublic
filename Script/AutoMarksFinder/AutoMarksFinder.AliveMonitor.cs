using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Network;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using GameObjectId      = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using ObjectKindNative  = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private sealed class MonsterAliveMonitor : IDisposable
    {
        private readonly AutoMarksFinder module;

        private readonly Dictionary<NotoriousMonsterList, Dictionary<NotoriousMonsterFound, AliveTrack>> aliveTracks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, FrozenSet<uint>>                                              territoryIdentities = [];
        private readonly Dictionary<uint, NotoriousMonsterFound>                                        trackedMonsters = [];

        private Hook<PacketDispatcher.Delegates.HandleSpawnNpcPacket>?     OnSpawnNPCHook;
        private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? OnActorControlHook;

        public unsafe MonsterAliveMonitor
        (
            AutoMarksFinder module
        )
        {
            this.module = module;

            OnSpawnNPCHook = IGameInteropProvider.Instance().HookFromMemberFunction
            (
                typeof(PacketDispatcher.MemberFunctionPointers),
                nameof(PacketDispatcher.HandleSpawnNpcPacket),
                (PacketDispatcher.Delegates.HandleSpawnNpcPacket)OnSpawnNPCDetour
            );
            OnSpawnNPCHook.Enable();

            OnActorControlHook = IGameInteropProvider.Instance().HookFromMemberFunction
            (
                typeof(PacketDispatcher.MemberFunctionPointers),
                nameof(PacketDispatcher.HandleActorControlPacket),
                (PacketDispatcher.Delegates.HandleActorControlPacket)OnActorControlDetour
            );
            OnActorControlHook.Enable();

            IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
            GameState.Instance().WarpComplete        += OnWarpCompleted;
            GameState.Instance().Logout              += OnLogout;

            RefreshTracking();
        }

        public void Dispose()
        {
            IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
            GameState.Instance().WarpComplete        -= OnWarpCompleted;
            GameState.Instance().Logout              -= OnLogout;

            if (OnSpawnNPCHook != null)
            {
                OnSpawnNPCHook.Dispose();
                OnSpawnNPCHook = null;
            }

            if (OnActorControlHook != null)
            {
                OnActorControlHook.Dispose();
                OnActorControlHook = null;
            }

            aliveTracks.Clear();
            territoryIdentities.Clear();
            trackedMonsters.Clear();
        }

        public void RemoveTrain
        (
            NotoriousMonsterList train
        ) => aliveTracks.Remove(train);

        public void InitializeTracks
        (
            NotoriousMonsterList train
        )
        {
            var tracks = GetTrainTracks(train);

            foreach (var monster in train.MonstersFound.Distinct())
                tracks[monster] = new AliveTrack();
        }

        public void RemoveTrack
        (
            NotoriousMonsterList train,
            NotoriousMonsterFound monster
        ) => GetTrainTracks(train).Remove(monster);

        public void SetManualAlive
        (
            NotoriousMonsterList train,
            NotoriousMonsterFound monster,
            bool                  manualAlive
        ) => GetTrack(GetTrainTracks(train), monster).ManualAlive = manualAlive;

        public void RefreshTracking()
        {
            trackedMonsters.Clear();

            if (!GameState.IsLoggedIn)
                return;

            if (module.SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
                return;

            var zoneID     = GameState.TerritoryType;
            var instanceID = InstancesManager.CurrentInstance;
            var tracks     = GetTrainTracks(activeTrain);
            var changed    = false;

            foreach (var obj in IObjectTable.Instance())
            {
                if (obj is not IBattleChara { ObjectKind: ObjectKind.BattleNpc } chara)
                    continue;

                var monster = MatchMonster(activeTrain, chara.DataID, chara.Position, zoneID, instanceID);
                if (monster == null)
                    continue;

                trackedMonsters[chara.EntityID] = monster;
                changed |= ApplyObservedState(monster, GetTrack(tracks, monster), chara.IsDead || chara.CurrentHp == 0);
            }

            if (changed)
                module.config.Save(module);
        }

        private void OnTerritoryChanged
        (
            uint territoryType
        ) => trackedMonsters.Clear();

        private void OnWarpCompleted
        (
            WarpType warpType
        ) => RefreshTracking();

        private void OnLogout() => trackedMonsters.Clear();

        private unsafe void OnSpawnNPCDetour
        (
            uint            targetID,
            SpawnNpcPacket* packet
        )
        {
            if (packet == null)
            {
                OnSpawnNPCHook.Original(targetID, packet);
                return;
            }

            var baseID   = packet->Common.BaseId;
            var position = packet->Common.Position;
            var mode     = packet->Common.CharacterMode;
            var health   = packet->Common.HealthPoints;

            OnSpawnNPCHook.Original(targetID, packet);

            if (!TrackSpawnedMonster(targetID, baseID, position))
                return;

            if (!TryResolveMonster(targetID, out var monster, out var track))
                return;

            var isDead = mode == CharacterModes.Dead || health == 0;
            if (ApplyNotifiedState(monster, track, isDead))
                module.config.Save(module);
        }

        private unsafe void OnActorControlDetour
        (
            uint         entityID,
            uint         category,
            uint         arg1,
            uint         arg2,
            uint         arg3,
            uint         arg4,
            uint         arg5,
            uint         arg6,
            uint         arg7,
            uint         arg8,
            GameObjectId targetID,
            bool         isRecorded
        )
        {
            OnActorControlHook.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);

            switch (category)
            {
                case ACTOR_CONTROL_DEATH:
                    OnConfirmedDeath(entityID);
                    break;
                case ACTOR_CONTROL_SET_MODE:
                    OnCharacterModeChanged(entityID);
                    break;
            }
        }

        private void OnConfirmedDeath
        (
            uint entityID
        )
        {
            if (!TryResolveMonster(entityID, out var monster, out var track))
                return;

            if (ApplyNotifiedState(monster, track, true))
                module.config.Save(module);
        }

        private void OnCharacterModeChanged
        (
            uint entityID
        )
        {
            if (!TryResolveMonster(entityID, out var monster, out var track))
                return;

            if (!TryReadObservedState(entityID, out var isDead))
                return;

            if (ApplyNotifiedState(monster, track, isDead))
                module.config.Save(module);
        }

        private bool TrackSpawnedMonster
        (
            uint    entityID,
            uint    baseID,
            Vector3 position
        )
        {
            if (module.SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
                return false;

            var zoneID     = GameState.TerritoryType;
            var identities = GetTerritoryIdentities(zoneID);

            if (identities.Count > 0 &&
                !identities.Contains(baseID))
                return false;

            var monster = MatchMonster(activeTrain, baseID, position, zoneID, InstancesManager.CurrentInstance);
            if (monster == null)
                return false;

            trackedMonsters[entityID] = monster;
            return true;
        }

        private bool TryResolveMonster
        (
            uint                      entityID,
            out NotoriousMonsterFound monster,
            out AliveTrack            track
        )
        {
            monster = null!;
            track   = null!;

            if (!trackedMonsters.TryGetValue(entityID, out var found))
                return false;

            if (module.SelectedTrain is not { } activeTrain)
                return false;

            if (!activeTrain.MonstersFound.Exists(entry => ReferenceEquals(entry, found)))
                return false;

            var tracks = GetTrainTracks(activeTrain);
            monster = found;
            track   = GetTrack(tracks, found);
            return true;
        }

        private static bool ApplyNotifiedState
        (
            NotoriousMonsterFound monster,
            AliveTrack            track,
            bool                  isDead
        )
        {
            track.ManualAlive = false;
            return ApplyObservedState(monster, track, isDead);
        }

        private static bool ApplyObservedState
        (
            NotoriousMonsterFound monster,
            AliveTrack            track,
            bool                  isDead
        )
        {
            if (isDead)
            {
                if (track.ManualAlive)
                    return false;

                if (!monster.IsAlive)
                    return false;

                monster.SetState(false);
                return true;
            }

            track.ManualAlive = false;

            if (monster.IsAlive)
                return false;

            monster.SetState(true);
            return true;
        }

        private static unsafe bool TryReadObservedState
        (
            uint     entityID,
            out bool isDead
        )
        {
            isDead = false;

            var obj = GameObjectManager.Instance()->Objects.GetObjectByEntityId(entityID);
            if (obj == null)
                return false;

            if (obj->GetObjectKind() != ObjectKindNative.BattleNpc)
                return false;

            var chara = (Character*)obj;
            isDead = chara->IsDead() || chara->Health == 0;
            return true;
        }

        private static NotoriousMonsterFound? MatchMonster
        (
            NotoriousMonsterList train,
            uint                 dataID,
            Vector3              position,
            uint                 zoneID,
            uint                 instanceID
        )
        {
            foreach (var monster in train.MonstersFound)
            {
                if (monster.DataID != dataID)
                    continue;

                if (!IsMonsterObservable(monster, zoneID, instanceID))
                    continue;

                if (Vector3.DistanceSquared(monster.Position, position) > MONSTER_MATCH_RADIUS * MONSTER_MATCH_RADIUS)
                    continue;

                return monster;
            }

            return null;
        }

        private FrozenSet<uint> GetTerritoryIdentities
        (
            uint zoneID
        )
        {
            if (territoryIdentities.TryGetValue(zoneID, out var cached))
                return cached;

            FrozenSet<uint> identities = [];

            if (LuminaGetter.GetRow<TerritoryType>(zoneID) is { } territory)
            {
                var territoryRowID = territory.NotoriousMonsterTerritory.RowId;

                if (territoryRowID != 0 &&
                    LuminaGetter.GetRow<NotoriousMonsterTerritory>(territoryRowID) is { } link)
                {
                    identities = link.NotoriousMonsters
                                     .Select(monster => LuminaGetter.GetRow<NotoriousMonster>(monster.RowId))
                                     .OfType<NotoriousMonster>()
                                     .Select(monster => monster.BNpcBase.RowId)
                                     .ToFrozenSet();
                }
            }

            territoryIdentities[zoneID] = identities;
            return identities;
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

        private static AliveTrack GetTrack
        (
            Dictionary<NotoriousMonsterFound, AliveTrack> tracks,
            NotoriousMonsterFound                         monster
        )
        {
            if (tracks.TryGetValue(monster, out var track))
                return track;

            track = new();
            tracks[monster] = track;
            return track;
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

        #region 常量

        private const uint  ACTOR_CONTROL_SET_MODE = 2;
        private const uint  ACTOR_CONTROL_DEATH    = 14;
        private const float MONSTER_MATCH_RADIUS   = 200f;

        #endregion
    }
}
