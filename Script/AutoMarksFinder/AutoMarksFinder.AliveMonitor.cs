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
using GameObjectId     = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using ObjectKindNative = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private readonly Dictionary<NotoriousMonsterList, Dictionary<NotoriousMonsterFound, AliveTrack>> aliveTracks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint, FrozenSet<uint>>                                              territoryIdentities = [];
    private readonly Dictionary<uint, NotoriousMonsterFound>                                        trackedMonsters = [];

    private Hook<PacketDispatcher.Delegates.HandleSpawnNpcPacket>?     onSpawnNPCHook;
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? onActorControlHook;

    private unsafe void InitAliveMonitor()
    {
        onSpawnNPCHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(PacketDispatcher.MemberFunctionPointers),
            nameof(PacketDispatcher.HandleSpawnNpcPacket),
            (PacketDispatcher.Delegates.HandleSpawnNpcPacket)OnSpawnNPCDetour
        );
        onSpawnNPCHook.Enable();

        onActorControlHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(PacketDispatcher.MemberFunctionPointers),
            nameof(PacketDispatcher.HandleActorControlPacket),
            (PacketDispatcher.Delegates.HandleActorControlPacket)OnActorControlDetour
        );
        onActorControlHook.Enable();

        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        GameState.Instance().WarpComplete        += OnWarpCompleted;
        GameState.Instance().Logout              += OnLogout;

        RefreshMonsterTracking();
    }

    private void UninitAliveMonitor()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        GameState.Instance().WarpComplete        -= OnWarpCompleted;
        GameState.Instance().Logout              -= OnLogout;

        if (onSpawnNPCHook != null)
        {
            onSpawnNPCHook.Disable();
            onSpawnNPCHook.Dispose();
            onSpawnNPCHook = null;
        }

        if (onActorControlHook != null)
        {
            onActorControlHook.Disable();
            onActorControlHook.Dispose();
            onActorControlHook = null;
        }

        trackedMonsters.Clear();
    }

    /// <summary>
    ///     区域切换会替换全部实体编号，先丢弃旧关联；新区域的实例编号在区域就绪后再取得。
    /// </summary>
    private void OnTerritoryChanged
    (
        uint territoryType
    ) => trackedMonsters.Clear();

    /// <summary>
    ///     区域就绪后对现有战斗角色补一次状态，站在原地的怪物不必等待重新生成即可被追踪。
    /// </summary>
    private void OnWarpCompleted
    (
        WarpType warpType
    ) => RefreshMonsterTracking();

    private void OnLogout() => trackedMonsters.Clear();

    /// <summary>
    ///     以客户端的明确观测结果补一次状态：建立实体与点位的对应关系，并按死亡标记与血量写入点位状态。
    /// </summary>
    private void RefreshMonsterTracking()
    {
        trackedMonsters.Clear();

        if (!GameState.IsLoggedIn)
            return;

        if (SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
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
            config.Save(this);
    }

    /// <summary>
    ///     生成通知：记录 entityID 与点位的对应关系，并以生成数据里的血量与角色模式初始化状态。
    ///     生成数据先读取再转发；HandleSpawnNpcPacket 会把生成数据入队，原函数返回时对象未必完成初始化，
    ///     因此这里只使用通知内的数据，不读取对象表。
    /// </summary>
    private unsafe void OnSpawnNPCDetour
    (
        uint            targetID,
        SpawnNpcPacket* packet
    )
    {
        if (packet == null)
        {
            onSpawnNPCHook.Original(targetID, packet);
            return;
        }

        var baseID   = packet->Common.BaseId;
        var position = packet->Common.Position;
        var mode     = packet->Common.CharacterMode;
        var health   = packet->Common.HealthPoints;

        onSpawnNPCHook.Original(targetID, packet);

        if (!TrackSpawnedMonster(targetID, baseID, position))
            return;

        if (!TryResolveMonster(targetID, out var monster, out var track))
            return;

        var isDead = mode == CharacterModes.Dead || health == 0;
        if (ApplyObservedState(monster, track, isDead))
            config.Save(this);
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
        onActorControlHook.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);

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

    /// <summary>
    ///     死亡通知：类别 14 在客户端侧就是死亡处理，直接按死亡写入。
    /// </summary>
    private void OnConfirmedDeath
    (
        uint entityID
    )
    {
        if (!TryResolveMonster(entityID, out var monster, out var track))
            return;

        if (ApplyObservedState(monster, track, true))
            config.Save(this);
    }

    /// <summary>
    ///     角色模式变更：类别 2 只给出模式编号，具体是死亡还是复位以该 entityID 当前的明确状态为准。
    /// </summary>
    private void OnCharacterModeChanged
    (
        uint entityID
    )
    {
        if (!TryResolveMonster(entityID, out var monster, out var track))
            return;

        if (!TryReadObservedState(entityID, out var isDead))
            return;

        if (ApplyObservedState(monster, track, isDead))
            config.Save(this);
    }

    /// <summary>
    ///     把生成通知里的实体与当前队列的点位建立对应关系；匹配依赖 DataID 与记录坐标。
    ///     生成通知绝大多数与恶名精英无关，先按区域身份表排除，区域未登记身份时交由点位匹配判定。
    /// </summary>
    private bool TrackSpawnedMonster
    (
        uint    entityID,
        uint    baseID,
        Vector3 position
    )
    {
        if (SelectedTrain is not { MonstersFound.Count: > 0 } activeTrain)
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

        if (SelectedTrain is not { } activeTrain)
            return false;

        if (!activeTrain.MonstersFound.Exists(entry => ReferenceEquals(entry, found)))
            return false;

        var tracks = GetTrainTracks(activeTrain);
        monster = found;
        track   = GetTrack(tracks, found);
        return true;
    }

    /// <summary>
    ///     以明确观测更新点位状态：死亡只认死亡标记或血量归零，存活观测解除手动覆盖。
    ///     返回点位状态是否发生变化，由调用方决定是否落盘。
    /// </summary>
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

    /// <summary>
    ///     按 EntityID 解析当前战斗对象并读取死亡标记与血量；对象不存在表示本次没有可观测目标，调用方保留原状态。
    /// </summary>
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

    /// <summary>
    ///     区域身份表：TerritoryType.NotoriousMonsterTerritory → NotoriousMonsterTerritory.NotoriousMonsters → NotoriousMonster.BNpcBase。
    /// </summary>
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
