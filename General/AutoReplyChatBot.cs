using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
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

    private static Config       ModuleConfig = null!;
    private static readonly HttpClient Http  = HttpClientHelper.Get();

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.Chat.ChatMessage += OnChat;
    }

    protected override void Uninit() => DService.Chat.ChatMessage -= OnChat;

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
        ImGui.TextUnformatted(GetLoc("AutoReplyChatBot-ApiConfig"));

        // ApiKey
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText(GetLoc("AutoReplyChatBot-ApiKey"), ref ModuleConfig.ApiKey, 256, ImGuiInputTextFlags.Password))
            SaveConfig(ModuleConfig);
        ImGui.SameLine();
        if (ImGui.SmallButton(GetLoc("AutoReplyChatBot-HowToGetApiKey")))
            Util.OpenLink(GetLoc("AutoReplyChatBot-HowToGetApiKeyUrl"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GetLoc("AutoReplyChatBot-HowToGetApiKeyDesc"));

        // BaseUrl
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText(GetLoc("AutoReplyChatBot-BaseUrl"), ref  ModuleConfig.BaseUrl, 256))
            SaveConfig(ModuleConfig);
        
        // Model
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText(GetLoc("AutoReplyChatBot-Model"), ref ModuleConfig.Model, 128))
            SaveConfig(ModuleConfig);
        
        ImGui.NewLine();
        ImGui.TextUnformatted(GetLoc("AutoReplyChatBot-SystemPrompt"));
        {
            if (ImGui.InputTextMultiline("##sysPrompt", ref ModuleConfig.SystemPrompt, 4096, new Vector2(promptW, promptH)))
                SaveConfig(ModuleConfig);
            if (ImGui.SmallButton(GetLoc("AutoReplyChatBot-RestoreDefaultPrompt")))
            {
                ModuleConfig.SystemPrompt = DefaultSystemPrompt;
                SaveConfig(ModuleConfig);
            }
        }
        
    }
    
    private static void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != XivChatType.TellIncoming) return;
        if (!IsCooldownReady()) return;
        
        var (name, worldId, worldName) = ExtractNameWorld(ref sender);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(worldName)) return;
        if (ModuleConfig.OnlyReplyNonFriendTell && IsFriend(name, worldId)) return;
        
        var userText = message.TextValue;
        if (string.IsNullOrWhiteSpace(userText)) return;

        _ = GenerateAndReplyAsync(name, worldName, userText);
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
    
    private static async Task GenerateAndReplyAsync(string name, string world, string userText)
    {
        var target = $"{name}@{world}";
        var reply = string.Empty;
        try
        {
            reply = await GenerateReplyAsync(userText, ModuleConfig, CancellationToken.None);
        }
        catch (Exception ex)
        {
            NotificationError(ex.Message, GetLoc("AutoReplyChatBot-ErrorTitle"));
            Error("Send auto reply failed:", ex);
        }
        if (string.IsNullOrWhiteSpace(reply)) return;

        SetCooldown();
        ChatHelper.SendMessage($"/tell {target} {reply}");
        NotificationInfo(reply, $"{GetLoc("AutoReplyChatBot-AutoRepliedTo")}{target}");
    }

    private static async Task<string?> GenerateReplyAsync(string userText, Config cfg, CancellationToken ct)
    {
        if (cfg.ApiKey.IsNullOrWhitespace() || cfg.BaseUrl.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
            return null;

        var url = cfg.BaseUrl!.TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        var sys  = string.IsNullOrWhiteSpace(cfg.SystemPrompt) ? DefaultSystemPrompt : cfg.SystemPrompt!;
        var body = new
        {
            model       = cfg.Model,
            messages    = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user",   content = userText }
            },
            max_tokens  = 800,
            temperature = 1.4
        };

        var json = JsonSerializer.Serialize(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
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
    
    private static string DefaultSystemPrompt { get; } =
        GetLoc("AutoReplyChatBot-DefaultPrompt")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\n");
    
    private static DateTime LastTs = DateTime.MinValue;

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
        public string ApiKey                 = string.Empty;
        public string BaseUrl                = "https://api.deepseek.com/v1";
        public string Model                  = "deepseek-chat";
        public string SystemPrompt           = DefaultSystemPrompt;
    }
}
