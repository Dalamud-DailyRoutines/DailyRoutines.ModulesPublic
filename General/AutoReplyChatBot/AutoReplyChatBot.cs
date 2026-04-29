using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

// TODO: 支持现代化 AI, 比如流式传输等等
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

    private Config config = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        if (config.SystemPrompts is not { Count: > 0 })
        {
            config.SystemPrompts       = [new()];
            config.SelectedPromptIndex = 0;
        }

        foreach (var contextType in Enum.GetValues<GameContextType>())
            config.GameContextSettings.TryAdd(contextType, true);

        config.SystemPrompts = config.SystemPrompts.DistinctBy(x => x.Name).ToList();
        config.Save(this);

        DService.Instance().Chat.ChatMessage += OnChat;
    }

    protected override void Uninit()
    {
        DService.Instance().Chat.ChatMessage -= OnChat;
        FlushSaveConfig();
        DisposeSaveConfigScheduler();
        DisposeAllSessions();
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

        var historyKey = $"{playerName}@{worldName}";
        AppendHistory(historyKey, "user", userText);

        var helper = GetSession(historyKey).TaskHelper;
        helper.Abort();
        helper.DelayNext(1000, "等待 1 秒收集更多消息");
        helper.Enqueue(() => IsCooldownReady(historyKey));
        helper.EnqueueAsync(ct => GenerateAndReplyAsync(playerName, worldName, message.LogKind, ct));
    }
}
