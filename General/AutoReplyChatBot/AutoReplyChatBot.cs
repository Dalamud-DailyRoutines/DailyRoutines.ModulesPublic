using System.Collections.Concurrent;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReplyChatBotTitle"),
        Description = Lang.Get("AutoReplyChatBotDescription"),
        Category    = ModuleCategory.General,
        Author      = ["Wotou"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    
    private RateLimiter?  rateLimiter;
    private ChatPipeline? pipeline;
    
    private readonly ConcurrentDictionary<string, CancellationTokenSource> activePipelines = new(StringComparer.OrdinalIgnoreCase);

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        if (config.SystemPrompts is not { Count: > 0 })
        {
            config.SystemPrompts       = [new()];
            config.SelectedPromptIndex = 0;
        }

        config.SystemPrompts = config.SystemPrompts.DistinctBy(x => x.Name).ToList();

        rateLimiter       = new RateLimiter();
        conversationStore = new ConversationStore(ConfigDirectoryPath);
        pipeline          = new ChatPipeline(this);

        _ = conversationStore.PruneAsync();

        DService.Instance().Chat.ChatMessage += OnChat;
    }

    protected override void Uninit()
    {
        DService.Instance().Chat.ChatMessage -= OnChat;

        foreach (var (_, cts) in activePipelines)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                /* ignored */
            }

            cts.Dispose();
        }

        activePipelines.Clear();
        conversationStore?.Dispose();
        rateLimiter?.Dispose();

        FlushSaveConfig();
        DisposeSaveConfigScheduler();
    }

    private void OnChat(IHandleableChatMessage message)
    {
        if (!config.ValidChatTypes.Contains(message.LogKind)) return;

        var (playerName, worldID, worldName) = ExtractNameWorld(message.Sender);
        if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(worldName)) return;
        if (playerName      == LocalPlayerState.Name    && worldID == GameState.HomeWorld) return;
        if (message.LogKind == XivChatType.TellIncoming && config.OnlyReplyNonFriendTell && IsFriend(playerName, worldID)) return;

        var userText = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(userText)) return;

        var target = $"{playerName}@{worldName}";

        var newCts = new CancellationTokenSource();
        var oldCts = activePipelines.GetOrAdd(target, newCts);

        if (oldCts != newCts)
        {
            try
            {
                oldCts.Cancel();
            }
            catch
            {
                /* ignored */
            }

            oldCts.Dispose();
            activePipelines[target] = newCts;
        }

        _ = pipeline!.ExecuteAsync(playerName, worldID, worldName, message.LogKind, userText, newCts.Token);
    }
}
