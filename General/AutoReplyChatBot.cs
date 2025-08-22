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
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.ModulesPublic
{
    public class AiAutoReply : DailyModuleBase
    {
        public override ModuleInfo Info { get; } = new()
        {
            Title       = GetLoc("AiAutoReplyTitle"),
            Description = GetLoc("AiAutoReplyDescription"),
            Category    = ModuleCategories.General,
            Author      = ["HSS", "DR-AI"]
        };

        private static Config ModuleConfig = null!;
        private static readonly HttpClient Http = new();
        
        protected override void Init()
        {
            ModuleConfig = LoadConfig<Config>() ?? new Config();
            DService.Chat.ChatMessage += OnChatMessage;
        }

        protected override void Uninit()
        {
            DService.Chat.ChatMessage -= OnChatMessage;
        }

        protected override void ConfigUI()
        {
            using (ImRaii.PushId("AiAutoReplyCfg"))
            {
                /* 暂时只开放私聊
                ImGui.TextUnformatted(GetLoc("Channels"));
                if (ImGui.Checkbox(GetLoc("EnableTell"), ref ModuleConfig.EnableTell))
                    SaveConfig(ModuleConfig);
                */

                if (ImGui.Checkbox(GetLoc("OnlyReplyNonFriendTell"), ref ModuleConfig.OnlyReplyNonFriendTell))
                    SaveConfig(ModuleConfig);

                float fieldW = 250;
                
                ImGui.SetNextItemWidth(fieldW);
                if (ImGui.SliderInt(GetLoc("CooldownSeconds"), ref ModuleConfig.CooldownSeconds, 0, 120))
                    SaveConfig(ModuleConfig);


                ImGui.TextUnformatted(GetLoc("ApiConfig"));

                // ApiKey
                {
                    ImGui.SetNextItemWidth(fieldW);
                    var v = ModuleConfig.ApiKey ?? string.Empty;
                    if (ImGui.InputText(GetLoc("ApiKey"), ref v, 256, ImGuiInputTextFlags.Password))
                    {
                        ModuleConfig.ApiKey = v;
                        SaveConfig(ModuleConfig);
                    }
                }
                // BaseUrl
                {
                    ImGui.SetNextItemWidth(fieldW);
                    var v = ModuleConfig.BaseUrl ?? "https://api.deepseek.com/v1";
                    if (ImGui.InputText(GetLoc("BaseUrl"), ref v, 256))
                    {
                        ModuleConfig.BaseUrl = v;
                        SaveConfig(ModuleConfig);
                    }
                }
                // Model
                {
                    ImGui.SetNextItemWidth(fieldW);
                    var v = ModuleConfig.Model ?? "deepseek-chat";
                    if (ImGui.InputText(GetLoc("Model"), ref v, 128))
                    {
                        ModuleConfig.Model = v;
                        SaveConfig(ModuleConfig);
                    }
                }
                
                ImGui.TextUnformatted(GetLoc("SystemPrompt"));
                {
                    var v = string.IsNullOrEmpty(ModuleConfig.SystemPrompt) ? DefaultSystemPrompt : ModuleConfig.SystemPrompt;
                    if (ImGui.InputTextMultiline("##sysPrompt", ref v, 4096, new Vector2(ImGui.GetContentRegionAvail().X * 0.9f, 160)))
                    {
                        ModuleConfig.SystemPrompt = v;
                        SaveConfig(ModuleConfig);
                    }
                    if (ImGui.SmallButton(GetLoc("RestoreDefaultPrompt")))
                    {
                        ModuleConfig.SystemPrompt = DefaultSystemPrompt;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        private static string DefaultSystemPrompt =>
            @"你平时说话简短随意。

            如果路人正常互动（送礼、夸你模型、寒暄客气）：
            你要礼貌得体回应，但保持疏远拒绝，而且要求简短，一句话以内：

            “谢谢，我先走了”

            “你也好呀”

            “你也很好看”

            “多谢”

            “辛苦啦”

            如果有人突然鉴挂、阴阳怪气、或者随便甩个问号，你直接甩贴吧老哥口的口吻，简短敷衍，不解释，比如类似下文这样的：

            “闹麻了”

            “？？”

            “懂？”

            “就这”

            “笑死”

            “典，这就鉴上了？”

            “急”

            “孝”

            “乐”

            “蚌”

            “赢”

            但不要一直重复回答同一句话，要有创新";
        
        private static void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            try
            {
                if (!ModuleConfig.EnableTell) return;
                if (type != XivChatType.TellIncoming) return;
                if (!CooldownReady()) return;
                
                var senderStr = ExtractSender(ref sender);
                if (string.IsNullOrWhiteSpace(senderStr)) return;
                
                if (ModuleConfig.OnlyReplyNonFriendTell && IsFriend(senderStr))
                    return;

                var userText = message.TextValue;
                if (string.IsNullOrWhiteSpace(userText)) return;

                _ = GenerateAndReplyAsync(senderStr, userText);
            }
            catch (Exception ex)
            {
                NotificationError("[AI-Chat] Error", ex.Message);
            }
        }

        private static string ExtractSender(ref SeString sender)
        {
            var p = sender.Payloads?.OfType<PlayerPayload>().FirstOrDefault();
            if (p != null)
            {
                var name  = p.PlayerName;
                var world = p.World.Value.Name.ExtractText();

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(world))
                    return $"{name}@{world}";

                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return sender.TextValue?.Trim() ?? string.Empty;
        }

        // —— Fire-and-forget：生成并回复 /tell —— //
        private static async Task GenerateAndReplyAsync(string target, string userText)
        {
            var reply = await GenerateReplyAsync(userText, ModuleConfig, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(reply)) return;

            ChatHelper.SendMessage($"/tell {target} {reply}");
            SetCooldown();
            NotificationInfo(GetLoc("AiRepliedTo", target), reply);
        }
        
        private static async Task<string?> GenerateReplyAsync(string userText, Config cfg, CancellationToken ct)
        {
            if (cfg.ApiKey.IsNullOrEmpty() || cfg.BaseUrl.IsNullOrEmpty() || cfg.Model.IsNullOrEmpty())
                throw new InvalidOperationException(GetLoc("AiNotConfigured"));

            var url = cfg.BaseUrl!.TrimEnd('/') + "/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

            var sys = string.IsNullOrWhiteSpace(cfg.SystemPrompt) ? DefaultSystemPrompt : cfg.SystemPrompt!;
            var body = new
            {
                model = cfg.Model,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user", content = userText }
                },
                max_tokens = 800,
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
        
        private static unsafe bool IsFriend(string rawSender)
        {
            var (name, world) = SplitNameWorld(rawSender);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world)) return false;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null) return false;
            
            for (var i = 0u; i < proxy->EntryCount; i++)
            {
                var entry = proxy->GetEntry(i);
                var entryHomeWorld = entry->HomeWorld;
                var entryName = SeString.Parse(entry->Name).TextValue;
                var entryHomeWorldName = ResolveWorldName(entryHomeWorld);
                if (entryHomeWorldName == world && entryName == name)
                    return true;
            }
            return false;
        }
        
        private static string? ResolveWorldName(ushort worldId)
        {
            var sheet = DService.Data?.GetExcelSheet<Lumina.Excel.Sheets.World>();
            if (sheet == null || worldId == 0)
                return null;

            var row = sheet.GetRow(worldId);

            var name = row.Name.ToString();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private static (string name, string? world) SplitNameWorld(string raw)
        {
            var s = (raw ?? string.Empty).Trim();
            var idx = s.IndexOf('@');
            return idx < 0 ? (s, null) : (s[..idx].Trim(), s[(idx + 1)..].Trim());
        }

        // 冷却
        private static DateTime _lastTs = DateTime.MinValue;
        private static bool CooldownReady()
        {
            var cd = TimeSpan.FromSeconds(Math.Max(5, ModuleConfig.CooldownSeconds));
            return DateTime.UtcNow - _lastTs >= cd;
        }
        private static void SetCooldown() => _lastTs = DateTime.UtcNow;

        // 配置
        private class Config : ModuleConfiguration
        {
            public bool   EnableTell  = true;           // 暂时只开启私聊
            public bool   OnlyReplyNonFriendTell = true;
            public int    CooldownSeconds = 5;

            public string ApiKey       = string.Empty;
            public string BaseUrl      = "https://api.deepseek.com/v1";
            public string Model        = "deepseek-chat";
            public string SystemPrompt = string.Empty;
        }
    }
}
