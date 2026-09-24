using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterFound : IEquatable<NotoriousMonsterFound>
    {
        public static readonly Dictionary<uint, uint> NotoriousMonsters;

        private static readonly Dictionary<uint, Vector3> AetheryteRedirection = new()
        {
            [818] = new(573f, 349.1f, -200f)
        };

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
            Rank     = (NotoriousMonsterRank)GetData().Rank;
        }

        public uint                 DataID   { get; set; }
        public uint                 ZoneID   { get; set; }
        public uint                 Instance { get; set; }
        public NotoriousMonsterRank Rank     { get; set; }

        [JsonIgnore]
        public Aetheryte? NearestAetheryte => LazyNearestAetheryte.Value;

        [JsonIgnore]
        public Vector3? NearestAetherytePosition => LazyNearestAetherytePosition.Value;

        private Lazy<Aetheryte?> LazyNearestAetheryte =>
            new
            (() => AetheryteRecordManager.Instance().GetNearestAetheryte
             (
                 ZoneID,
                 ZoneID == 818 ?
                     new(573f, 349.1f, -200f) :
                     Position
             )?.GetData()
            );

        private Lazy<Vector3?> LazyNearestAetherytePosition =>
            new
            (() => AetheryteRecordManager.Instance().GetNearestAetheryte
             (
                 ZoneID,
                 ZoneID == 818 ?
                     new(573f, 349.1f, -200f) :
                     Position
             )?.Position
            );

        public Vector3 Position { get; set; }
        public bool    IsAlive  { get; set; } = true;

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
    }

    private sealed class NotoriousMonsterList : IEquatable<NotoriousMonsterList>
    {
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
        public long   GeneratedTime { get; set; }

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
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return GeneratedTime == other.GeneratedTime;
        }

        public void Reorder()
        {
            if (MonstersFound.Count <= 0)
            {
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-CantSortEmptyList"), Lang.Get("AutoMarksFinderTitle"));
                return;
            }

            if (StartAetheryte == null)
            {
                NotifyHelper.Instance().NotificationWarning(Lang.Get("AutoMarksFinder-CantSortNullStartingPoint"), Lang.Get("AutoMarksFinderTitle"));
                return;
            }

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
        public uint    Zone            { get; set; }
        public Vector3 Position        { get; set; }
        public bool    CancelAnimation { get; set; }

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

    private sealed class ZoneScanProgress
    {
        public uint          ZoneID              { get; set; }
        public uint          Instance            { get; set; }
        public int           FoundARank          { get; set; }
        public int           FoundBRank          { get; set; }
        public HashSet<uint> ScannedPointIndices { get; set; } = [];

        public bool IsCompleted
        (
            HashSet<NotoriousMonsterRank> targetRanks,
            int                           expectedAmount
        )
        {
            if (targetRanks.Count == 1)
            {
                var rank = targetRanks.First();
                return rank           == NotoriousMonsterRank.A ?
                           FoundARank >= expectedAmount :
                           FoundBRank >= expectedAmount;
            }

            return FoundARank >= expectedAmount && FoundBRank >= expectedAmount;
        }
    }

    private sealed class PathDecision
    {
        public enum PathType
        {
            DirectNav,
            TeleportThenNav,
            SmartTP
        }

        public PathType         Type           { get; set; }
        public Vector3          TargetPosition { get; set; }
        public AetheryteRecord? Aetheryte      { get; set; }
        public float            EstimatedTime  { get; set; }
    }
}
