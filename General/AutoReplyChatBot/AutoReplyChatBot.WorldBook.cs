using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private static void UpdateGameContextInWorldBook()
    {
        if (ModuleConfig is not { EnableGameContext: true }) return;

        var contextParts = new List<string>();

        foreach (var contextType in Enum.GetValues<GameContextType>())
        {
            if (ModuleConfig.GameContextSettings.TryGetValue(contextType, out var enabled) && enabled)
            {
                var value = GameContextValueMap[contextType]();
                contextParts.Add($"{contextType}:{value}");
            }
        }

        var context = string.Join(", ", contextParts);
        ModuleConfig.WorldBookEntry["GameContext"] = context;
    }

    private static class WorldBookManager
    {
        public static List<KeyValuePair<string, string>> FindRelevantEntries(string userMessage, Dictionary<string, string> entries)
        {
            if (string.IsNullOrWhiteSpace(userMessage) || entries is not { Count: > 0 })
                return [];

            const int MAX_ENTRIES = 10;

            var message       = userMessage.ToLowerInvariant();
            var messageTokens = Tokenize(message);

            var scored = new List<(int Score, KeyValuePair<string, string> Entry)>(entries.Count);

            if (ModuleConfig is { EnableGameContext: true }             &&
                entries.TryGetValue("GameContext", out var gameContext) &&
                !string.IsNullOrWhiteSpace(gameContext))
                scored.Add((int.MaxValue, new KeyValuePair<string, string>("GameContext", gameContext)));

            foreach (var entry in entries)
            {
                if (entry.Key == "GameContext") continue;
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                if (string.IsNullOrWhiteSpace(entry.Value)) continue;

                var key      = entry.Key.Trim();
                var keyLower = key.ToLowerInvariant();

                var score = 0;

                if (message.Contains(keyLower, StringComparison.Ordinal))
                {
                    score += 1000;
                    score += Math.Min(200, keyLower.Length);
                }
                else
                {
                    foreach (var token in Tokenize(keyLower))
                    {
                        if (token.Length < 2) continue;
                        if (messageTokens.Contains(token))
                            score += 30;
                        else if (message.Contains(token, StringComparison.Ordinal))
                            score += 10;
                    }
                }

                if (score > 0)
                    scored.Add((score, entry));
            }

            return scored
                   .OrderByDescending(x => x.Score)
                   .Take(MAX_ENTRIES)
                   .Select(x => x.Entry)
                   .ToList();
        }

        public static string BuildWorldBookContext(List<KeyValuePair<string, string>> matches, int maxLength)
        {
            var context = new StringBuilder();
            context.AppendLine("[WorldBookInfo]");

            var currentLength = 0;

            foreach (var match in matches)
            {
                var content = match.Value.Trim();
                if (string.IsNullOrWhiteSpace(content)) continue;

                var entryText     = $"[{match.Key}]: {content}";
                var contentLength = entryText.Length;
                if (currentLength + contentLength > maxLength) break;

                context.AppendLine(entryText);
                currentLength += contentLength;
            }

            return context.ToString();
        }

        private static HashSet<string> Tokenize(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) return tokens;

            var buffer = new StringBuilder(32);

            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer.Append(ch);
                    continue;
                }

                if (buffer.Length == 0) continue;
                tokens.Add(buffer.ToString());
                buffer.Clear();
            }

            if (buffer.Length > 0)
                tokens.Add(buffer.ToString());

            return tokens;
        }
    }
}
