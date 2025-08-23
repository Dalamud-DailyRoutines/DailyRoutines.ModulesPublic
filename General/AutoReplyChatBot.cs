using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.ModulesPublic;

public class AutoReplyChatBot : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoReplyChatBotTitle"),
        Description = GetLoc("AutoReplyChatBotDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Wotou"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;
    private static string TestChatInput = "";
    
    private static DateTime LastTs = DateTime.MinValue;
    private static int HistKeyIndex = 0;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.Chat.ChatMessage += OnChat;
    }

    protected override void Uninit() => 
        DService.Chat.ChatMessage -= OnChat;

    protected override void ConfigUI()
    {
        var fieldW     = 230f * GlobalFontScale;
        var promptH    = 200f * GlobalFontScale;
        var promptW    = ImGui.GetContentRegionAvail().X * 0.9f;
        
        if (ImGui.Checkbox(GetLoc("AutoReplyChatBot-OnlyReplyNonFriendTell"), ref ModuleConfig.OnlyReplyNonFriendTell))
            SaveConfig(ModuleConfig);

        // 冷却秒
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(GetLoc("AutoReplyChatBot-CooldownSeconds"), ref ModuleConfig.CooldownSeconds, 0, 120))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoReplyChatBot-APIConfig"));

        using (ImRaii.PushIndent())
        {
            // API Key
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.InputText("API Key", ref ModuleConfig.APIKey, 256))
                SaveConfig(ModuleConfig);
            ImGuiOm.TooltipHover(ModuleConfig.APIKey);

            // Base Url
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.InputText("Base URL", ref ModuleConfig.BaseUrl, 256))
                SaveConfig(ModuleConfig);

            // Model
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.InputText(GetLoc("AutoReplyChatBot-Model"), ref ModuleConfig.Model, 128))
                SaveConfig(ModuleConfig);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoReplyChatBot-SystemPrompt"));
        
        ImGui.SameLine();
        if (ImGui.SmallButton(GetLoc("AutoReplyChatBot-RestoreDefaultPrompt")))
        {
            ModuleConfig.SystemPrompt = DefaultSystemPrompt;
            SaveConfig(ModuleConfig);
        }
        
        using (ImRaii.PushIndent())
        {
            if (ImGui.InputTextMultiline("##sysPrompt", ref ModuleConfig.SystemPrompt, 4096, new(promptW, promptH)))
                SaveConfig(ModuleConfig);
        }
        
        ImGui.NewLine();
        
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoReplyChatBot-TestChat"));
        
        ImGui.SameLine();
        if (ImGui.SmallButton(GetLoc("AutoReplyChatBot-Send")))
        {
            if (string.IsNullOrWhiteSpace(TestChatInput)) return;

            const string histKey = "Tester@DailyRoutines";
            var text = TestChatInput;
            Task.Run(async () =>
            {
                var reply = string.Empty;
                AppendHistory(histKey, "user", text);
                try
                {
                    reply = await GenerateReplyAsync(ModuleConfig, histKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    NotificationError(GetLoc("AutoReplyChatBot-ErrorTitle"));
                    Error($"{GetLoc("AutoReplyChatBot-ErrorTitle")}:", ex);

                    reply = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(reply)) return;

                AppendHistory(histKey, "assistant", reply);
                var builder = new SeStringBuilder();

                builder.AddUiForeground(25)
                       .AddText($"[{Info.Title}]")
                       .AddUiForegroundOff()
                       .Add(NewLinePayload.Payload)
                       .AddUiForeground(537)
                       .AddText($">> {ModuleConfig.Model}: ")
                       .AddText(text)
                       .AddUiForegroundOff()
                       .Add(NewLinePayload.Payload)
                       .AddUiForeground(537)
                       .AddText($"{ModuleConfig.Model} >> ")
                       .AddText(reply)
                       .AddUiForegroundOff();
                
                Chat(builder.Build());
            });
        }

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(promptW);
            ImGui.InputText("##TestInput", ref TestChatInput, 1024);
        }
        
        ImGui.NewLine();
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoReplyChatBot-ChatHistory"));

        using (ImRaii.PushIndent())
        {
            var keys = Histories.Keys.ToArray();

            var noneLabel = GetLoc("None");
            var displayKeys = new List<string>(keys.Length + 1) { string.Empty };
            displayKeys.AddRange(keys);
            
            if (HistKeyIndex < 0 || HistKeyIndex >= displayKeys.Count)
                HistKeyIndex = 0;
            
            var currentLabel = HistKeyIndex == 0 ? noneLabel : displayKeys[HistKeyIndex];

            ImGui.SetNextItemWidth(fieldW);
            using (var combo = ImRaii.Combo(GetLoc("AutoReplyChatBot-UserKey"), currentLabel))
            {
                if (combo)
                {
                    for (var i = 0; i < displayKeys.Count; i++)
                    {
                        var label    = i == 0 ? noneLabel : displayKeys[i];
                        var selected = (i == HistKeyIndex);
                        if (ImGui.Selectable(label, selected))
                            HistKeyIndex = i;
                    }
                }
            }

            if (HistKeyIndex > 0)
            {
                var currentKey = displayKeys[HistKeyIndex];
                var entries = Histories.TryGetValue(currentKey, out var list) ? list.ToList() : [];

                using (ImRaii.Child("##histViewer", new System.Numerics.Vector2(promptW, promptH), true))
                {
                    var isAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

                    foreach (var (role, text) in entries)
                    {
                        var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
                        var color  = isUser
                                         ? new System.Numerics.Vector4(0.85f, 0.90f, 1f, 1f)
                                         : new System.Numerics.Vector4(0.90f, 0.85f, 1f, 1f);
                        using (ImRaii.PushColor(ImGuiCol.Text, color))
                            ImGui.TextWrapped($"[{role}] {text}");
                        ImGui.Separator();
                    }
                    if (isAtBottom)
                        ImGui.SetScrollHereY(1f);
                }
            }
        }
    }
    
    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != XivChatType.TellIncoming) return;
        
        var (name, worldId, worldName) = ExtractNameWorld(ref sender);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(worldName)) return;
        if (ModuleConfig.OnlyReplyNonFriendTell && IsFriend(name, worldId)) return;
        
        var userText = message.TextValue;
        if (string.IsNullOrWhiteSpace(userText)) return;
        
        AppendHistory($"{name}@{worldName}", "user", userText);
        
        if (!IsCooldownReady()) return;
        
        _ = GenerateAndReplyAsync(name, worldName);
    }

    private static (string name, ushort worldId, string? worldName) ExtractNameWorld(ref SeString sender)
    {
        var p = sender.Payloads?.OfType<PlayerPayload>().FirstOrDefault();
        if (p != null)
        {
            var name     = p.PlayerName;
            var worldId  = (ushort)p.World.RowId;
            var worldStr = p.World.Value.Name.ExtractText();
            if (!string.IsNullOrEmpty(name))
                return (name, worldId, worldStr);
        }
        
        var text = sender.TextValue?.Trim() ?? string.Empty;
        var idx  = text.IndexOf('@');
        var nm   = idx < 0 ? text : text[..idx].Trim();
        var wn   = idx < 0 ? null : text[(idx + 1)..].Trim();
        return (nm, 0, wn);
    }
    
    private static async Task GenerateAndReplyAsync(string name, string world)
    {
        var target = $"{name}@{world}";
        var reply = string.Empty;
        
        try
        {
            reply = await GenerateReplyAsync(ModuleConfig, target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            NotificationError(GetLoc("AutoReplyChatBot-ErrorTitle"));
            Error($"{GetLoc("AutoReplyChatBot-ErrorTitle")}:", ex);

            reply = string.Empty;
        }
        
        if (string.IsNullOrWhiteSpace(reply)) 
            return;

        SetCooldown();
        
        ChatHelper.SendMessage($"/tell {target} {reply}");
        NotificationInfo(reply, $"{GetLoc("AutoReplyChatBot-AutoRepliedTo")}{target}");
        AppendHistory(target, "assistant", reply);
    }

    private static async Task<string?> GenerateReplyAsync(Config cfg, string historyKey, CancellationToken ct)
    {
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseUrl.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
            return null;

        var       url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);

        var sys  = string.IsNullOrWhiteSpace(cfg.SystemPrompt) ? DefaultSystemPrompt : cfg.SystemPrompt;
        var context = BuildContextSummary(); 
        var hist = Histories.TryGetValue(historyKey, out var list) ? list.ToList() : [];
        
        var messages = new List<object>
        {
            new { role = "system", content = sys },
            new { role = "system", content = context }
        };
        foreach (var (role, text) in hist)
            messages.Add(new { role, content = text });
        
        var body = new
        {
            model       = cfg.Model,
            messages    = messages,
            max_tokens  = cfg.MaxTokens,
            temperature = cfg.Temperature
        };

        var json = JsonSerializer.Serialize(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await HttpClientHelper.Get().SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        var msg = choices[0].GetProperty("message");
        return msg.TryGetProperty("content", out var content) ? content.GetString() : null;
    }

    private static unsafe bool IsFriend(string name, ushort worldId)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null) return false;

        for (var i = 0u; i < proxy->EntryCount; i++)
        {
            var entry = proxy->GetEntry(i);
            if (entry == null) continue;
            var fName = SeString.Parse(entry->Name).TextValue;
            var fWorld = entry->HomeWorld;
            if (fWorld == worldId && fName == name)
                return true;
        }
        return false;
    }

    private static void AppendHistory(string key, string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var list = Histories.GetOrAdd(key, _ => new());
        {
            list.Add((role, text));
            if (list.Count > ModuleConfig.MaxHistory)
                list.RemoveAt(0);
        }
    }

    private static string BuildContextSummary()
    {
        var jobText  = LocalPlayerState.ClassJobData.Name;
        var level  = LocalPlayerState.CurrentLevel;
        var myName   = LocalPlayerState.Name;
        var myWorld = GameState.CurrentWorldData.Name;
        var terrName = LuminaWrapper.GetZonePlaceName(GameState.TerritoryType);
        
        return $"[GAME CONTEXT] [ABOUT YOU] \n" +
               $"YourName:{myName}, YourJob:{jobText}, Level:{level}, HomeWorld:{myWorld}\n" +
               $"CurrentTerritory:{terrName}";
    }
    
    private static readonly ConcurrentDictionary<string, List<(string role, string text)>> Histories =
        new(StringComparer.OrdinalIgnoreCase);
    
    private static string DefaultSystemPrompt { get; } =
        GetLoc("AutoReplyChatBot-DefaultPrompt")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\n");
    

    private static bool IsCooldownReady()
    {
        var cd = TimeSpan.FromSeconds(Math.Max(5, ModuleConfig.CooldownSeconds));
        return DateTime.UtcNow - LastTs >= cd;
    }

    private static void SetCooldown() => LastTs = DateTime.UtcNow;
    
    private class Config : ModuleConfiguration
    {
        public bool   OnlyReplyNonFriendTell = true;
        public int    CooldownSeconds        = 5;
        public string APIKey                 = string.Empty;
        public string BaseUrl                = "https://api.deepseek.com/v1";
        public string Model                  = "deepseek-chat";
        public string SystemPrompt           = DefaultSystemPrompt;
        public int    MaxHistory             = 16;
        public int    MaxTokens              = 2048;
        public float  Temperature            = 1.4f;
    }
}
