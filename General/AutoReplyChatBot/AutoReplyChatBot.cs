using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot : DailyModuleBase
{
    private static Config ModuleConfig = null!;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoReplyChatBotTitle"),
        Description = GetLoc("AutoReplyChatBotDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Wotou"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        if (ModuleConfig.SystemPrompts is not { Count: > 0 })
        {
            ModuleConfig.SystemPrompts       = [new()];
            ModuleConfig.SelectedPromptIndex = 0;
        }

        foreach (var contextType in Enum.GetValues<GameContextType>())
            ModuleConfig.GameContextSettings.TryAdd(contextType, true);

        ModuleConfig.SystemPrompts = ModuleConfig.SystemPrompts.DistinctBy(x => x.Name).ToList();
        SaveConfig(ModuleConfig);

        DService.Instance().Chat.ChatMessage += OnChat;
    }

    protected override void Uninit()
    {
        DService.Instance().Chat.ChatMessage -= OnChat;
        FlushSaveConfig();
        DisposeSaveConfigScheduler();
        DisposeAllSessions();
    }

    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!ModuleConfig.ValidChatTypes.Contains(type)) return;

        var (playerName, worldID, worldName) = ExtractNameWorld(sender);
        if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(worldName)) return;
        if (playerName == LocalPlayerState.Name    && worldID == GameState.HomeWorld) return;
        if (type       == XivChatType.TellIncoming && ModuleConfig.OnlyReplyNonFriendTell && IsFriend(playerName, worldID)) return;

        var userText = message.TextValue;
        if (string.IsNullOrWhiteSpace(userText)) return;

        var historyKey = $"{playerName}@{worldName}";
        AppendHistory(historyKey, "user", userText);

        var helper = GetSession(historyKey).TaskHelper;
        helper.Abort();
        helper.DelayNext(1000, "等待 1 秒收集更多消息");
        helper.Enqueue(() => IsCooldownReady(historyKey));
        helper.EnqueueAsync(ct => GenerateAndReplyAsync(playerName, worldName, type, ct));
    }
}
