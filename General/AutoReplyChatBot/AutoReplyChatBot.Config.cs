using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Newtonsoft.Json;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private class Config : ModuleConfiguration
    {
        public string APIKey            = string.Empty;
        public string BaseURL           = "https://api.deepseek.com/v1";
        public int    CooldownSeconds   = 5;
        public string CurrentActiveChat = "TestChat";

        // 聊天上下文限制配置
        public bool EnableContextLimit;
        public bool EnableFilter = true;

        // 游戏上下文配置
        public bool EnableGameContext = true;

        // 世界书相关配置
        public bool                              EnableWorldBook     = true;
        public string                            FilterModel         = "deepseek-chat";
        public string                            FilterPrompt        = FILTER_SYSTEM_PROMPT;
        public Dictionary<GameContextType, bool> GameContextSettings = [];

        // 聊天记录存储
        public Dictionary<string, List<ChatMessage>> Histories = new(StringComparer.OrdinalIgnoreCase);
        public int                                   HistoryKeyIndex;
        public int                                   MaxContextMessages     = 10;
        public int                                   MaxHistory             = 16;
        public int                                   MaxTokens              = 2048;
        public int                                   MaxWorldBookContext    = 1024;
        public string                                Model                  = "deepseek-chat";
        public bool                                  OnlyReplyNonFriendTell = true;
        public APIProvider                           Provider               = APIProvider.OpenAI;
        public int                                   SelectedPromptIndex;
        public List<Prompt>                          SystemPrompts = [new()];
        public float                                 Temperature   = 1.4f;

        // 聊天窗口配置
        public Dictionary<string, ChatWindow> TestChatWindows = [];
        public HashSet<XivChatType>           ValidChatTypes  = [XivChatType.TellIncoming];
        public Dictionary<string, string>     WorldBookEntry  = [];
    }

    private class Prompt
    {
        public string Content = DEFAULT_SYSTEM_PROMPT;
        public string Name    = GetLoc("Default");
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
        public string Text      { get; set; } = string.Empty;
        public long   Timestamp { get; set; }
        public string Name      { get; set; } = string.Empty;

        [JsonIgnore] public DateTime? LocalTime { get; set; }

        public override string ToString() => $"[{Name}] {Text}";
    }

    private enum APIProvider
    {
        OpenAI = 0,
        Ollama = 1
    }

    private enum GameContextType
    {
        PlayerName,
        ClassJob,
        Level,
        HomeWorld,
        CurrentWorld,
        CurrentZone,
        Weather,
        LocalTime,
        EorzeaTime,
        Condition
    }
}
