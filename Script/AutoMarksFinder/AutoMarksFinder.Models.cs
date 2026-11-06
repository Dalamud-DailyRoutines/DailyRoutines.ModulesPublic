using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterFound : IEquatable<NotoriousMonsterFound>
    {
        public static Dictionary<uint, uint> NotoriousMonsters { get; }

        private AetheryteRecord? nearestAetheryteRecord;

        static NotoriousMonsterFound() =>
            NotoriousMonsters = LuminaGetter.Get<NotoriousMonster>()
                                            .DistinctBy(x => x.BNpcBase.RowId)
                                            .ToDictionary(x => x.BNpcBase.RowId, x => x.RowId);

        public NotoriousMonsterFound() { }

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
        public bool    IsAlive  { get; set; } = true;

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
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return DataID == other.DataID && ZoneID == other.ZoneID && Instance == other.Instance;
        }

        public NotoriousMonster GetData() =>
            LuminaGetter.GetRow<NotoriousMonster>(NotoriousMonsters[DataID]) ?? default;

        public TerritoryType GetZoneData() =>
            LuminaGetter.GetRow<TerritoryType>(ZoneID) ?? default;

        public uint GetMapID() => GetZoneData().Map.RowId;

        public string GetBNPCName() => GetData().BNpcName.Value.Singular.ToString();

        public string GetZoneName() => GetZoneData().ExtractPlaceName();

        public void SetState
        (
            bool isAlive
        ) => IsAlive = isAlive;

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
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((NotoriousMonsterFound)obj);
        }

        public override int GetHashCode() => HashCode.Combine(DataID, ZoneID, Instance);

        #region 常量

        private const uint DRAVANIAN_HINTERLANDS_ZONE_ID = 399;
        private const uint IDYLLSHIRE_ZONE_ID            = 478;
        private const uint RUBY_SEA_ZONE_ID              = 613;
        private const uint TEMPEST_ZONE_ID               = 818;
        private const uint ONOKORO_AETHERYTE_ID          = 106;
        private const uint ONDO_CUPS_AETHERYTE_ID        = 147;

        #endregion
    }

    private sealed class NotoriousMonsterList : IEquatable<NotoriousMonsterList>
    {
        [JsonConstructor]
        public NotoriousMonsterList() { }

        public NotoriousMonsterList
        (
            string name
        )
        {
            Name          = name;
            GeneratedTime = Framework.GetServerTime();
        }

        public string Name          { get; set; }
        public long   GeneratedTime { get; init; }

        [JsonIgnore]
        public DateTime? GeneratedDateTime { get; set; }

        public List<NotoriousMonsterFound> MonstersFound { get; set; } = [];

        [JsonIgnore]
        public Aetheryte? StartAetheryte { get; set; }

        public bool Equals
        (
            NotoriousMonsterList? other
        )
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;

            return GeneratedTime == other.GeneratedTime;
        }

        public void Reorder()
        {
            if (MonstersFound.Count <= 0 ||
                StartAetheryte      == null)
                return;

            var orderedMonsters = MonstersFound
                                  .OrderByDescending(x => x.ZoneID == StartAetheryte?.Map.Value.TerritoryType.RowId)
                                  .ThenBy(monster => monster.ZoneID)
                                  .ThenBy(monster => monster.Instance)
                                  .ToList();

            List<NotoriousMonsterFound> reorderedList    = [];
            var                         currentAetheryte = StartAetheryte;

            foreach (var group in orderedMonsters.GroupBy(monster => (Zone: monster.ZoneID, monster.Instance)))
            {
                var zoneInstanceMonsters = group.ToList();

                while (zoneInstanceMonsters.Count > 0)
                {
                    var nearestMonster = zoneInstanceMonsters
                                         .OrderBy
                                         (monster => Vector2.Distance
                                          (
                                              currentAetheryte?.GetPositionMap()         ?? new(),
                                              monster.NearestAetheryte?.GetPositionMap() ?? new()
                                          )
                                         )
                                         .First();

                    reorderedList.Add(nearestMonster);

                    currentAetheryte = nearestMonster.NearestAetheryte ?? currentAetheryte;

                    zoneInstanceMonsters.Remove(nearestMonster);
                }
            }

            MonstersFound = reorderedList;
        }

        public override bool Equals
        (
            object? obj
        )
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((NotoriousMonsterList)obj);
        }

        public override int GetHashCode() => GeneratedTime.GetHashCode();

        public override string ToString() => $"NotoriousMonsterList_{Name}_{GeneratedTime}";
    }

    private sealed class NotoriousMonsterScanPoint : IEquatable<NotoriousMonsterScanPoint>
    {
        public uint    Zone     { get; init; }
        public Vector3 Position { get; init; }

        public bool Equals
        (
            NotoriousMonsterScanPoint? other
        )
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Zone == other.Zone && Position.Equals(other.Position);
        }

        public override string ToString() => $"NotoriousMonsterScanPoint_{Zone}_{Position}";

        public override bool Equals
        (
            object? obj
        )
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((NotoriousMonsterScanPoint)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Zone, Position);
    }

    private sealed class AliveTrack
    {
        public bool ManualAlive { get; set; }
    }

    private sealed class AutoBroadcastSession
    {
        public bool                  On;
        public bool                  Departed;
        public int                   Index;
        public NotoriousMonsterList? Train;
        public bool                  FirstPointPending;
        public long                  FirstPointAt;
        public bool                  NextPending;
        public long                  NextAt;
        public bool                  EndPending;
        public long                  EndAt;
        public string                DepartTimeInput = string.Empty;
        public DateTime              DepartAt;
        public double                TimeCalibOffsetSec;
        public bool                  TimeCalibrated;
    }
}
