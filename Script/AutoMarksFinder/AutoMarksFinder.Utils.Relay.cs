using System.Numerics;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoMarksFinder
{
    private TaskHelper? relayTaskHelper;

    private bool Relay
    (
        RelayType              type,
        NotoriousMonsterFound? info    = null,
        bool                   preview = false
    )
    {
        if (type is RelayType.BeforeStart or RelayType.AfterEnd)
        {
            var text = type == RelayType.BeforeStart ?
                           config.AutoRelayBeforeStartPattern :
                           config.AutoRelayAfterEndPattern;
            if (string.IsNullOrWhiteSpace(text) ||
                (!preview && config.RelayCommands.Count == 0))
                return true;

            var lines = text.ReplaceLineEndings("\n").Split('\n');

            if (lines.Length > MAX_RELAY_LINES)
            {
                NotifyHelper.Instance().NotificationWarning
                (
                    Lang.Get
                    (
                        "AutoMarksFinder-Relay-TooManyLines",
                        new Dictionary<string, object>
                        {
                            ["count"] = MAX_RELAY_LINES
                        }
                    ),
                    Lang.Get("AutoMarksFinder-AutoBroadcast")
                );
                return false;
            }

            string[] commands = preview ?
                                    ["/e"] :
                                    [.. config.RelayCommands];
            var firstLine = true;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!firstLine)
                    relayTaskHelper.DelayNext(RELAY_LINE_INTERVAL_MS);

                relayTaskHelper.Enqueue
                (() =>
                    {
                        foreach (var command in commands)
                            ChatManager.Instance().SendMessage($"{command} {line}");
                    }
                );
                firstLine = false;
            }

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
                       true
                   );
        }

        if (info == null)
            return false;

        if (type == RelayType.Position)
            return RelayPosition(pattern, info.ZoneID, info.Position, info.GetBNPCName(), info.Instance);

        if (info.NearestAetherytePosition is not { } position ||
            info.NearestAetheryte is not { } aetheryte)
            return false;

        return RelayPosition(pattern, info.ZoneID, position, aetheryte.PlaceName.Value.Name.ToString(), info.Instance);
    }

    private bool RelayPosition
    (
        string  pattern,
        uint    territoryType,
        Vector3 worldPosition,
        string  name,
        uint    instance,
        bool    preview = false
    )
    {
        if (string.IsNullOrWhiteSpace(pattern) ||
            (!preview && config.RelayCommands.Count == 0))
            return true;

        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return false;

        var message = string.FormatICUText
        (
            pattern.Replace("{instance}", Lang.Get("AutoMarksFinder-Relay-InstancePattern"), StringComparison.Ordinal),
            new Dictionary<string, object>
            {
                ["name"]         = name,
                ["position"]     = "<flag>",
                ["instance"]     = instance,
                ["instanceText"] = instance.ToSESquareCount()
            }
        );

        agentMap->SetMapFlagByZoneAndOpen(territoryType, worldPosition, name);

        if (preview)
        {
            ChatManager.Instance().SendMessage($"/e {message}");
            return true;
        }

        foreach (var command in config.RelayCommands)
            ChatManager.Instance().SendMessage($"{command} {message}");

        return true;
    }

    private enum RelayType
    {
        Position,
        WaitAetheryte,
        BeforeStart,
        AfterEnd
    }

    #region 常量

    private const int MAX_RELAY_LINES        = 15;
    private const int RELAY_LINE_INTERVAL_MS = 1000;

    #endregion
}
