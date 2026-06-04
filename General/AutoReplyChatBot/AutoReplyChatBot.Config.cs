using System.Collections.Concurrent;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private Config             config = null!;
    private ConversationStore? conversationStore;
    
    private static int                      PendingSaveConfig;
    private static CancellationTokenSource? SaveConfigTokenSource;
    
    private class Config : ModuleConfig
    {
        public string APIKey            = string.Empty;
        public string BaseURL           = "https://api.deepseek.com/v1";
        public int    CooldownSeconds   = 5;
        public string CurrentActiveChat = "TestChat";

        // Guard 安全配置
        public bool            EnableFilter      = true;
        public bool            HardGuardEnabled  = true;
        public int             MaxMessageLength  = 500;
        public AttackAction    AttackBehavior    = AttackAction.Defend;
        public HashSet<string> HardGuardKeywords = [..HardGuardDefaultKeywords];

        // 上下文 Token 限制
        public int MaxContextTokens = 250_000;

        // 世界书相关配置
        public bool   EnableWorldBook = true;
        public string FilterModel     = "deepseek-v4-flash";
        public string FilterPrompt    = FILTER_SYSTEM_PROMPT;

        public int          HistoryKeyIndex;
        public int          MaxTokens              = 2048;
        public int          MaxWorldBookContext    = 1024;
        public string       Model                  = "deepseek-chat";
        public bool         OnlyReplyNonFriendTell = true;
        public APIProvider  Provider               = APIProvider.OpenAI;
        public int          SelectedPromptIndex;
        public List<Prompt> SystemPrompts = [new()];
        public float        Temperature   = 1.4f;

        // 聊天窗口配置
        public Dictionary<string, ChatWindow> TestChatWindows = [];
        public HashSet<XivChatType>           ValidChatTypes  = [XivChatType.TellIncoming];
        public Dictionary<string, string>     WorldBookEntry  = [];
    }

    private class Prompt
    {
        public string Content = DEFAULT_SYSTEM_PROMPT;
        public string Name    = Lang.Get("Default");
    }

    private class ChatWindow
    {
        public string HistoryGUID = string.Empty;
        public string ID          = string.Empty;
        public string InputText   = string.Empty;
        public bool   IsProcessing;
        public string Name    = string.Empty;
        public string Role    = "TestUser";
        public float  ScrollY = 0f;

        public string HistoryKey => string.IsNullOrEmpty(HistoryGUID) ? $"{Role}@{Name}" : HistoryGUID;
    }

    private class ToolCallRecord
    {
        public string Name      { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string Result    { get; set; } = string.Empty;
    }

    private class ChatMessage
    {
        public ChatMessage() => Timestamp = GameState.ServerTimeUnix;

        public ChatMessage(string role, string text, string name = null)
        {
            Role      = role;
            Text      = text;
            Timestamp = GameState.ServerTimeUnix;
            Name      = name ?? role;
        }

        public ChatMessage(string role, string text, long timestamp, string name = null)
        {
            Role      = role;
            Text      = text;
            Timestamp = timestamp;
            Name      = name ?? role;
        }

        public string Role      { get; set; } = string.Empty;
        
        private string textVal = string.Empty;
        public string Text
        {
            get => textVal;
            set
            {
                textVal = value;
                ParseText();
            }
        }
        
        public long   Timestamp { get; set; }
        public string Name      { get; set; } = string.Empty;
        
        public List<ToolCallRecord>? ToolCalls { get; set; }

        [JsonIgnore]
        public DateTime? LocalTime { get; set; }

        [JsonIgnore]
        public string? ParsedReasoning { get; private set; }

        [JsonIgnore]
        public string? ParsedContent { get; private set; }

        private void ParseText()
        {
            if (string.IsNullOrEmpty(textVal))
            {
                ParsedReasoning = null;
                ParsedContent   = string.Empty;
                return;
            }

            var thinkStart = textVal.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (thinkStart >= 0)
            {
                var thinkEnd = textVal.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (thinkEnd >= 0)
                {
                    ParsedReasoning = textVal.Substring(thinkStart + 7, thinkEnd - (thinkStart + 7)).Trim();
                    ParsedContent   = (textVal.Substring(0, thinkStart) + textVal.Substring(thinkEnd + 8)).Trim();
                }
                else
                {
                    ParsedReasoning = textVal.Substring(thinkStart + 7).Trim();
                    ParsedContent   = textVal.Substring(0, thinkStart).Trim();
                }
            }
            else
            {
                ParsedReasoning = null;
                ParsedContent   = textVal;
            }
        }

        public override string ToString() => $"[{Name}] {Text}";
    }
    
        private sealed class ConversationStore : IDisposable
    {
        private const int HOT_TURN_LIMIT   = 200;
        private const int PRUNE_DAYS       = 30;
        private const int SAVE_DEBOUNCE_MS = 800;

        private readonly string                                     storageDir;
        private readonly ConcurrentDictionary<string, Conversation> cache        = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte>         pendingSaves = new(StringComparer.OrdinalIgnoreCase);
        private          CancellationTokenSource?                   saveCts;

        public ConversationStore(string moduleConfigDir)
        {
            storageDir = Path.Join(moduleConfigDir, "Conversations");
            Directory.CreateDirectory(storageDir);
        }

        public void Dispose()
        {
            FlushAll();
            cache.Clear();
        }

        public Conversation GetOrLoad(string id)
        {
            if (cache.TryGetValue(id, out var cached))
                return cached;

            var conv = LoadFromDisk(id);
            cache[id] = conv;
            return conv;
        }

        public IReadOnlyCollection<string> GetKeys() => [.. LoadAllIds()];

        public List<ChatMessage> GetTurns(string id)
        {
            var conv = GetOrLoad(id);
            return conv.RecentTurns.ToList();
        }

        public void AppendTurn(string id, ChatMessage turn)
        {
            var conv = GetOrLoad(id);
            conv.RecentTurns.Add(turn);

            if (conv.RecentTurns.Count > HOT_TURN_LIMIT * 2)
                ArchiveOldestTurns(conv);

            conv.LastMessageAt = DateTime.UtcNow;
            RequestSave(id);
        }

        public void UpdateSummary(string id, string? summary, int version)
        {
            var conv = GetOrLoad(id);
            conv.CompressedSummary = summary;
            conv.SummaryVersion    = version;
            conv.LastCompressedAt  = DateTime.UtcNow;
            RequestSave(id);
        }

        public void DeleteConversation(string id)
        {
            cache.TryRemove(id, out _);
            var hotPath  = HotPath(id);
            var coldPath = ColdPath(id);

            try
            {
                if (File.Exists(hotPath))
                    File.Delete(hotPath);
            }
            catch
            {
                /* ignored */
            }

            try
            {
                if (File.Exists(coldPath))
                    File.Delete(coldPath);
            }
            catch
            {
                /* ignored */
            }
        }

        public Task PruneAsync()
        {
            var cutoff   = DateTime.UtcNow.AddDays(-PRUNE_DAYS);
            var allFiles = LoadAllIds();

            foreach (var id in allFiles)
            {
                var conv = GetOrLoad(id);

                if (conv.LastMessageAt < cutoff && conv.RecentTurns.Count <= 5)
                {
                    DeleteConversation(id);
                    continue;
                }

                if (conv.LastMessageAt < cutoff)
                {
                    ArchiveOldestTurns(conv, 5);
                    RequestSave(id);
                }
            }

            return Task.CompletedTask;
        }

        public void FlushAll()
        {
            foreach (var id in pendingSaves.Keys.ToList())
                FlushSave(id);
        }

        public void RequestSave(string id)
        {
            pendingSaves.TryAdd(id, 0);
            var cts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref saveCts, cts);

            try
            {
                old?.Cancel();
            }
            catch
            {
                /* ignored */
            }

            old?.Dispose();
            _ = SaveAfterDelayAsync(cts.Token);
        }

        public async Task MigrateAsync
        (
            Dictionary<string, List<ChatMessage>> histories,
            Dictionary<string, List<ChatMessage>> pastHistories
        )
        {
            var migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            MigrateSet(histories,     migrated, false);
            MigrateSet(pastHistories, migrated, true);

            await Task.CompletedTask;
        }

        private void MigrateSet(Dictionary<string, List<ChatMessage>> source, HashSet<string> migrated, bool isPast)
        {
            foreach (var (id, messages) in source)
            {
                if (messages is not { Count: > 0 }) continue;
                if (!migrated.Add(id)) continue;

                var conv = GetOrLoad(id);

                if (isPast)
                {
                    var coldPath = ColdPath(id);

                    try
                    {
                        using var writer = new StreamWriter(coldPath, true);

                        foreach (var msg in messages)
                        {
                            var json = JsonConvert.SerializeObject(msg);
                            writer.WriteLine(json);
                        }
                    }
                    catch
                    {
                        /* ignored */
                    }
                }
                else
                {
                    foreach (var msg in messages)
                        conv.RecentTurns.Add(msg);

                    if (conv.RecentTurns.Count > HOT_TURN_LIMIT * 2)
                        ArchiveOldestTurns(conv);

                    RequestSave(id);
                }
            }
        }

        private async Task SaveAfterDelayAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(SAVE_DEBOUNCE_MS, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            var ids = pendingSaves.Keys.ToList();
            foreach (var id in ids)
                FlushSave(id);
        }

        private void FlushSave(string id)
        {
            pendingSaves.TryRemove(id, out _);
            if (!cache.TryGetValue(id, out var conv)) return;

            try
            {
                var hotPath = HotPath(id);
                var json    = JsonConvert.SerializeObject(conv, Formatting.Indented);
                SecureSaveHelper.Instance().WriteAllText(hotPath, json);
            }
            catch
            {
                /* ignored */
            }
        }

        private void ArchiveOldestTurns(Conversation conv, int keepLast = HOT_TURN_LIMIT)
        {
            if (conv.RecentTurns.Count <= keepLast) return;

            var toArchive = conv.RecentTurns.Take(conv.RecentTurns.Count - keepLast).ToList();
            conv.RecentTurns.RemoveRange(0, conv.RecentTurns.Count - keepLast);

            var coldPath = ColdPath(conv.ID);

            try
            {
                using var writer = new StreamWriter(coldPath, true);

                foreach (var msg in toArchive)
                {
                    var json = JsonConvert.SerializeObject(msg);
                    writer.WriteLine(json);
                }
            }
            catch
            {
                /* ignored */
            }
        }

        private Conversation LoadFromDisk(string id)
        {
            var hotPath = HotPath(id);

            if (File.Exists(hotPath))
            {
                try
                {
                    var json = File.ReadAllText(hotPath);
                    var conv = JsonConvert.DeserializeObject<Conversation>(json);

                    if (conv != null)
                    {
                        conv.ID = id;
                        return conv;
                    }
                }
                catch
                {
                    /* ignored */
                }
            }

            return new Conversation { ID = id };
        }

        private List<string> LoadAllIds()
        {
            var ids = new List<string>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(storageDir, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(name))
                        ids.Add(name);
                }
            }
            catch
            {
                /* ignored */
            }

            return ids;
        }

        private string HotPath(string id) => Path.Join(storageDir, $"{id}.json");

        private string ColdPath(string id) => Path.Join(storageDir, $"{id}.jsonl");

        public class Conversation
        {
            public string ID { get; set; } = string.Empty;

            [JsonProperty("turns")]
            public List<ChatMessage> RecentTurns { get; set; } = [];

            [JsonProperty("summary")]
            public string? CompressedSummary { get; set; }

            [JsonProperty("summaryVersion")]
            public int SummaryVersion { get; set; }

            [JsonProperty("lastMessageAt")]
            public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

            [JsonProperty("lastCompressedAt")]
            public DateTime? LastCompressedAt { get; set; }
        }
    }

    private void RequestSaveConfig(int delayMs = 800)
    {
        if (delayMs < 0) delayMs = 0;

        Interlocked.Exchange(ref PendingSaveConfig, 1);

        var tokenSource    = new CancellationTokenSource();
        var oldTokenSource = Interlocked.Exchange(ref SaveConfigTokenSource, tokenSource);

        try
        {
            oldTokenSource?.Cancel();
        }
        catch
        {
            // ignored
        }

        oldTokenSource?.Dispose();

        _ = SaveConfigAfterDelayAsync(delayMs, tokenSource.Token);
    }

    private async Task SaveConfigAfterDelayAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        FlushSaveConfig();
    }

    private void FlushSaveConfig()
    {
        if (Interlocked.Exchange(ref PendingSaveConfig, 0) == 0)
            return;

        try
        {
            config.Save(ModuleManager.Instance().GetModule<AutoReplyChatBot>());
        }
        catch
        {
            // ignored
        }
    }

    private static void DisposeSaveConfigScheduler()
    {
        var tokenSource = Interlocked.Exchange(ref SaveConfigTokenSource, null);
        if (tokenSource == null)
            return;

        try
        {
            tokenSource.Cancel();
        }
        catch
        {
            // ignored
        }

        tokenSource.Dispose();
    }
}
