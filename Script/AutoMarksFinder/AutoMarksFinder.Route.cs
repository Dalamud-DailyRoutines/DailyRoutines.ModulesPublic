using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Newtonsoft.Json;
using OmenTools.Interop.Game.Helpers;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    private sealed class NotoriousMonsterList
    {
        public string Name          { get; set; }  = string.Empty;
        public long   GeneratedTime { get; init; } = Framework.GetServerTime();

        [JsonProperty]
        public List<NotoriousMonsterFound> MonstersFound { get; private set; } = [];

        public uint StartAetheryteID { get; set; }

        [JsonIgnore]
        public DateTime GeneratedDateTime => DateTimeOffset.FromUnixTimeSeconds(GeneratedTime).UtcDateTime;

        public event Action? Changed;

        public void RemoveAt(int index)
        {
            var monsters = MonstersFound.ToList();
            monsters.RemoveAt(index);
            MonstersFound = monsters;
            Changed?.Invoke();
        }

        public void Move(int sourceIndex, int destinationIndex)
        {
            var monsters = MonstersFound.ToList();
            var monster  = monsters[sourceIndex];
            monsters.RemoveAt(sourceIndex);
            monsters.Insert(destinationIndex, monster);
            MonstersFound = monsters;
            Changed?.Invoke();
        }

        public void Merge(IEnumerable<NotoriousMonsterFound> observed, bool overwrite)
        {
            var remaining = observed.ToHashSet();
            var monsters  = new List<NotoriousMonsterFound>();

            if (!overwrite)
            {
                foreach (var monster in MonstersFound)
                {
                    if (remaining.TryGetValue(monster, out var replacement))
                    {
                        monsters.Add(replacement.Copy());
                        remaining.Remove(replacement);
                    }
                    else
                        monsters.Add(monster);
                }
            }

            monsters.AddRange(remaining.Select(monster => monster.Copy()));
            MonstersFound = monsters;
            Changed?.Invoke();
        }

        public void Reorder()
        {
            var startRecord = AetheryteRecordManager.Instance().GameRecords.FirstOrDefault(record => record.RowID == StartAetheryteID);
            if (MonstersFound.Count == 0 ||
                startRecord         == null)
                return;

            var remainingZones = MonstersFound.GroupBy(monster => monster.ZoneID)
                                              .ToDictionary(group => group.Key, group => group.ToList());
            var currentZone     = startRecord.ZoneID;
            var currentPosition = startRecord.Position;
            var reordered       = new List<NotoriousMonsterFound>(MonstersFound.Count);

            while (remainingZones.Count > 0)
            {
                var costs = TeleportCostCalculator.GetRouteCosts(currentZone, remainingZones.Keys, applyTeleportDiscounts: false);
                var nextZone = remainingZones.ContainsKey(currentZone) ?
                                   currentZone :
                                   remainingZones.Keys.MinBy(zone => costs.GetValueOrDefault(zone, uint.MaxValue));
                var monsters = remainingZones[nextZone];

                if (nextZone != currentZone)
                {
                    var entry = monsters.MinBy
                    (monster => monster.NearestAetheryte is { } aetheryte ?
                                    TeleportCostCalculator.GetBaseTeleportCost(aetheryte, currentZone) :
                                    uint.MaxValue
                    );
                    currentPosition = entry.NearestAetherytePosition ?? entry.Position;
                }

                foreach (var group in monsters.GroupBy(monster => monster.Instance).OrderBy(group => group.Key))
                {
                    var pending = group.ToList();

                    while (pending.Count > 0)
                    {
                        var nearest = pending.MinBy(monster => Vector3.DistanceSquared(currentPosition, monster.Position));
                        reordered.Add(nearest);
                        currentPosition = nearest.Position;
                        pending.Remove(nearest);
                    }
                }

                currentZone = nextZone;
                remainingZones.Remove(nextZone);
            }

            MonstersFound = reordered;
            Changed?.Invoke();
        }
    }
}
