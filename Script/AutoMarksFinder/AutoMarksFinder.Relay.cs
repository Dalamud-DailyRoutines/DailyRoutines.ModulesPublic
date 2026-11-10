using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoMarksFinder
{
    private sealed unsafe class HuntRelay
    (
        Config       config,
        HuntMovement movement
    ) : IDisposable
    {
        private readonly TaskHelper tasks = new();

        public bool IsBusy => tasks.IsBusy;

        public void Cancel() => tasks.Abort();

        public void Dispose() => tasks.Dispose();

        public bool Send
        (
            RelayType              type,
            NotoriousMonsterList?  train,
            NotoriousMonsterFound? info                   = null,
            bool                   preview                = false,
            bool                   automatic              = false,
            bool                   arriveBeforeNavigation = false
        )
        {
            if (IsBusy)
                return false;

            if (!preview                                            &&
                type is RelayType.BeforeStart or RelayType.AfterEnd &&
                train is { MonstersFound.Count: > 0 })
            {
                var firstAlive = train.MonstersFound.Find(monster => monster.IsAlive);

                if (firstAlive == null)
                    type = RelayType.AfterEnd;
                else if (type == RelayType.AfterEnd ||
                         train.MonstersFound.Any(monster => !monster.IsAlive))
                {
                    type = RelayType.Position;
                    info = firstAlive;
                }
            }

            if (type is RelayType.BeforeStart or RelayType.AfterEnd)
            {
                var text = type == RelayType.BeforeStart ?
                               config.AutoRelayBeforeStartPattern :
                               config.AutoRelayAfterEndPattern;
                if (string.IsNullOrWhiteSpace(text) ||
                    (!preview && config.RelayCommands.Count == 0))
                    return true;

                EnqueueMessage(FormatRelayMessage(text, train), preview);
                return true;
            }

            var pattern = type == RelayType.Position ?
                              config.RelayPositionPattern :
                              config.RelayWaitAetherytePattern;

            if (preview)
            {
                return LocalPlayerState.Object is { } player &&
                       RelayPosition
                       (
                           pattern,
                           GameState.TerritoryType,
                           player.Position,
                           player.Name,
                           InstancesManager.IsInstancedArea ?
                               InstancesManager.CurrentInstance :
                               0,
                           train,
                           true
                       );
            }

            if (info == null)
                return false;

            if (type == RelayType.Position)
            {
                info.OpenMap();
                var sent = RelayPosition(pattern, info.ZoneID, info.Position, info.GetBNPCName(), info.Instance, train, markMap: false);

                if (automatic                                                        &&
                    sent                                                             &&
                    config is { MoveAfterBroadcast: true, RelayCommands.Count: > 0 } &&
                    !string.IsNullOrWhiteSpace(pattern))
                {
                    if (arriveBeforeNavigation)
                    {
                        var entry = info.NearestAetheryteRecord;
                        tasks.Enqueue
                        (() => movement.Travel
                         (
                             entry?.ZoneID ?? info.ZoneID,
                             entry == null || entry.ZoneID == info.ZoneID ?
                                 info.Instance :
                                 0,
                             entry,
                             afterBroadcast: true
                         )
                        );
                    }
                    else
                        tasks.Enqueue(() => movement.NavigateToMonster(info, afterBroadcast: true));
                }

                return sent;
            }

            if (info.NearestAetherytePosition is not { } position ||
                info.NearestAetheryte is not { } aetheryte)
                return false;

            var relayed = RelayPosition(pattern, aetheryte.Territory.RowId, position, aetheryte.PlaceName.Value.Name.ToString(), info.Instance, train);
            if (automatic                                                        &&
                relayed                                                          &&
                config is { MoveAfterBroadcast: true, RelayCommands.Count: > 0 } &&
                !string.IsNullOrWhiteSpace(pattern))
                tasks.Enqueue
                (() => movement.Travel
                 (
                     aetheryte.Territory.RowId,
                     aetheryte.Territory.RowId == info.ZoneID ?
                         info.Instance :
                         0,
                     info.NearestAetheryteRecord,
                     afterBroadcast: true
                 )
                );

            return relayed;
        }

        private bool RelayPosition
        (
            string                pattern,
            uint                  territoryType,
            Vector3               worldPosition,
            string                name,
            uint                  instance,
            NotoriousMonsterList? train,
            bool                  preview = false,
            bool                  markMap = true
        )
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
                return false;

            if (markMap)
                agentMap->SetMapFlagByZoneAndOpen(territoryType, worldPosition, name);

            if (string.IsNullOrWhiteSpace(pattern) ||
                (!preview && config.RelayCommands.Count == 0))
                return true;

            var message = FormatRelayMessage(pattern, train, name, instance);

            EnqueueMessage(message, preview);
            return true;
        }

        private void EnqueueMessage(string message, bool preview)
        {
            string[] commands = preview ?
                                    ["/e"] :
                                    [.. config.RelayCommands];
            var firstLine = true;

            foreach (var line in message.ReplaceLineEndings("\n").Split('\n').Take(MAX_RELAY_LINES))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!firstLine)
                    tasks.DelayNext(RELAY_LINE_INTERVAL_MS);

                tasks.Enqueue
                (() =>
                    {
                        foreach (var command in commands)
                            ChatManager.Instance().SendMessage($"{command} {line}");
                    }
                );
                firstLine = false;
            }
        }

        private static string FormatRelayMessage(string pattern, NotoriousMonsterList? train, string name = "", uint instance = 0)
            => string.FormatICUText
            (
                pattern.Replace("{instance}", Lang.Get("AutoMarksFinder-Relay-InstancePattern"), StringComparison.Ordinal),
                new Dictionary<string, object>
                {
                    ["name"]              = name,
                    ["position"]          = "<flag>",
                    ["instance"]          = instance,
                    ["instanceText"]      = instance.ToSESquareCount(),
                    ["routeName"]         = train?.Name                ?? string.Empty,
                    ["routeMonsterCount"] = train?.MonstersFound.Count ?? 0
                }
            );

        #region 常量

        public const  int MAX_RELAY_LINES        = 15;
        private const int RELAY_LINE_INTERVAL_MS = 500;

        #endregion
    }
}
