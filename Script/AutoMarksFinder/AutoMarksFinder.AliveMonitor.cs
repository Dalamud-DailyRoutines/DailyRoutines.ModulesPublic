using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using ObjectKindNative = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoMarksFinder
{
    private sealed class MonsterAliveMonitor : IDisposable
    {
        private readonly AutoMarksFinder module;

        private readonly Dictionary<uint, FrozenSet<uint>>       territoryIdentities = [];
        private readonly Dictionary<uint, NotoriousMonsterFound> trackedMonsters     = [];

        public MonsterAliveMonitor
        (
            AutoMarksFinder inModule
        )
        {
            module = inModule;

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

            territoryIdentities.Clear();
            trackedMonsters.Clear();
        }

        public void RefreshTracking()
        {
            trackedMonsters.Clear();

            if (!GameState.IsLoggedIn)
                return;

            if (module.SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
                return;

            var zoneID     = GameState.TerritoryType;
            var instanceID = InstancesManager.CurrentInstance;
            var changed    = false;

            foreach (var obj in IObjectTable.Instance())
            {
                if (obj is not IBattleChara { ObjectKind: ObjectKind.BattleNpc } chara)
                    continue;

                var monster = MatchMonster(activeTrain, chara.DataID, chara.Position, zoneID, instanceID);
                if (monster == null)
                    continue;

                trackedMonsters[chara.EntityID] =  monster;
                changed                         |= monster.Observe(chara.IsDead || chara.CurrentHp == 0, false);
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

        public void ObserveMonster
        (
            uint    entityID,
            uint    baseID,
            Vector3 position,
            bool    isDead
        )
        {
            if (!TrackSpawnedMonster(entityID, baseID, position))
                return;

            if (!TryResolveMonster(entityID, out var monster))
                return;

            if (monster.Observe(isDead, true))
                module.config.Save(module);
        }

        public void OnConfirmedDeath
        (
            uint entityID
        )
        {
            if (!TryResolveMonster(entityID, out var monster))
                return;

            if (monster.Observe(true, true))
                module.config.Save(module);
        }

        public void OnCharacterModeChanged
        (
            uint entityID
        )
        {
            if (!TryResolveMonster(entityID, out var monster))
                return;

            if (!TryReadObservedState(entityID, out var isDead))
                return;

            if (monster.Observe(isDead, true))
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
            out NotoriousMonsterFound monster
        )
        {
            monster = null!;

            if (!trackedMonsters.TryGetValue(entityID, out var found))
                return false;

            if (module.SelectedTrain is not { } activeTrain)
                return false;

            if (!activeTrain.MonstersFound.Any(entry => ReferenceEquals(entry, found)))
                return false;

            monster = found;
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

        private static bool IsMonsterObservable
        (
            NotoriousMonsterFound monster,
            uint                  zoneID,
            uint                  instance
        )
        {
            if (monster.ZoneID != zoneID)
                return false;

            return !InstancesManager.IsInstancedArea ||
                   monster.Instance == 0             ||
                   monster.Instance == instance;
        }

        #region 常量

        private const float MONSTER_MATCH_RADIUS = 200f;

        #endregion
    }
}
