using System.Collections.Frozen;
using System.Numerics;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterFound : IEquatable<NotoriousMonsterFound>
    {
        private AetheryteRecord? nearestAetheryteRecord;
        private bool             manualAlive;

        public NotoriousMonsterFound()
        {
        }

        public NotoriousMonsterFound
        (
            uint    dataID,
            Vector3 position,
            uint    zoneID,
            uint    instance
        )
        {
            DataID   = dataID;
            Position = position;
            ZoneID   = zoneID;
            Instance = instance;
        }

        public uint DataID   { get; init; }
        public uint ZoneID   { get; init; }
        public uint Instance { get; init; }

        public uint NearestAetheryteID
        {
            get
            {
                if (field == 0)
                    field = GetInitialNearestAetheryteID();

                return field;
            }
            set
            {
                field                  = value;
                nearestAetheryteRecord = null;
            }
        }

        [JsonIgnore]
        public uint AetheryteZoneID => ZoneID == DRAVANIAN_HINTERLANDS_ZONE_ID ?
                                           IDYLLSHIRE_ZONE_ID :
                                           ZoneID;

        [JsonIgnore]
        public AetheryteRecord? NearestAetheryteRecord =>
            nearestAetheryteRecord ??= LuminaGetter.GetRow<Aetheryte>(NearestAetheryteID) is { } aetheryte ?
                                           AetheryteRecord.Parse(aetheryte) :
                                           null;

        [JsonIgnore]
        public Aetheryte? NearestAetheryte => NearestAetheryteRecord?.GetData();

        [JsonIgnore]
        public Vector3? NearestAetherytePosition => NearestAetheryteRecord?.Position;

        public Vector3 Position { get; init; }

        [JsonProperty]
        public bool IsAlive { get; private set; } = true;

        private uint GetInitialNearestAetheryteID() =>
            ZoneID switch
            {
                RUBY_SEA_ZONE_ID => ONOKORO_AETHERYTE_ID,
                TEMPEST_ZONE_ID  => ONDO_CUPS_AETHERYTE_ID,
                _                => AetheryteRecordManager.Instance().GetNearestAetheryte(AetheryteZoneID, Position, true)?.RowID ?? 0
            };

        public bool Equals
        (
            NotoriousMonsterFound? other
        )
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;
            return DataID == other.DataID && ZoneID == other.ZoneID && Instance == other.Instance;
        }

        public NotoriousMonster GetData() =>
            LuminaGetter.GetRow<NotoriousMonster>(NOTORIOUS_MONSTERS[DataID]) ?? default;

        public TerritoryType GetZoneData() =>
            LuminaGetter.GetRow<TerritoryType>(ZoneID) ?? default;

        public uint GetMapID() => GetZoneData().Map.RowId;

        public string GetBNPCName() => GetData().BNpcName.Value.Singular.ToString();

        public string GetZoneName() => GetZoneData().ExtractPlaceName();

        public void SetState
        (
            bool isAlive
        )
        {
            manualAlive = isAlive;
            IsAlive     = isAlive;
        }

        public bool Observe(bool isDead, bool confirmed)
        {
            if (confirmed ||
                !isDead)
                manualAlive = false;

            if (isDead &&
                manualAlive)
                return false;

            var isAlive = !isDead;
            if (IsAlive == isAlive)
                return false;

            IsAlive = isAlive;
            return true;
        }

        public NotoriousMonsterFound Copy() =>
            new()
            {
                DataID             = DataID,
                Position           = Position,
                ZoneID             = ZoneID,
                Instance           = Instance,
                IsAlive            = IsAlive,
                NearestAetheryteID = NearestAetheryteID
            };

        public override string ToString() => $"NotoriousMonsterFound_{DataID}_{Position}";

        public override bool Equals
        (
            object? obj
        )
        {
            if (ReferenceEquals(null, obj))
                return false;

            if (ReferenceEquals(this, obj))
                return true;
            return obj.GetType() == GetType() && Equals((NotoriousMonsterFound)obj);
        }

        public override int GetHashCode() => HashCode.Combine(DataID, ZoneID, Instance);

        #region 常量

        public static readonly FrozenDictionary<uint, uint> NOTORIOUS_MONSTERS = LuminaGetter.Get<NotoriousMonster>()
                                                                                             .DistinctBy(monster => monster.BNpcBase.RowId)
                                                                                             .ToFrozenDictionary
                                                                                                 (monster => monster.BNpcBase.RowId, monster => monster.RowId);

        private const uint DRAVANIAN_HINTERLANDS_ZONE_ID = 399;
        private const uint IDYLLSHIRE_ZONE_ID            = 478;
        private const uint RUBY_SEA_ZONE_ID              = 613;
        private const uint TEMPEST_ZONE_ID               = 818;
        private const uint ONOKORO_AETHERYTE_ID          = 106;
        private const uint ONDO_CUPS_AETHERYTE_ID        = 147;

        #endregion
    }
}
