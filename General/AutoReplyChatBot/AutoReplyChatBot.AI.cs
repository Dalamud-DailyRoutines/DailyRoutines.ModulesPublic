using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Helpers;
using Dalamud.Game.Text;
using Dalamud.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private const string HTTP_CLIENT_NAME = "AutoReplyChatBot-Default";

    private static readonly Dictionary<APIProvider, IChatBackend> Backends = new()
    {
        [APIProvider.OpenAI] = new OpenAIBackend(),
        [APIProvider.Ollama] = new OllamaBackend()
    };

    private static readonly Dictionary<XivChatType, string> ChatTypeToCommand = new()
    {
        [XivChatType.Party]           = "/p",
        [XivChatType.FreeCompany]     = "/fc",
        [XivChatType.Ls1]             = "/l1",
        [XivChatType.Ls2]             = "/l2",
        [XivChatType.Ls3]             = "/l3",
        [XivChatType.Ls4]             = "/l4",
        [XivChatType.Ls5]             = "/l5",
        [XivChatType.Ls6]             = "/l6",
        [XivChatType.Ls7]             = "/l7",
        [XivChatType.Ls8]             = "/l8",
        [XivChatType.CrossLinkShell1] = "/cwlinkshell1",
        [XivChatType.CrossLinkShell2] = "/cwlinkshell2",
        [XivChatType.CrossLinkShell3] = "/cwlinkshell3",
        [XivChatType.CrossLinkShell4] = "/cwlinkshell4",
        [XivChatType.CrossLinkShell5] = "/cwlinkshell5",
        [XivChatType.CrossLinkShell6] = "/cwlinkshell6",
        [XivChatType.CrossLinkShell7] = "/cwlinkshell7",
        [XivChatType.CrossLinkShell8] = "/cwlinkshell8",
        [XivChatType.Say]             = "/say",
        [XivChatType.Yell]            = "/yell",
        [XivChatType.Shout]           = "/shout"
    };

    private static void SendReply(XivChatType originalType, string target, string reply)
    {
        if (originalType == XivChatType.TellIncoming || !ChatTypeToCommand.TryGetValue(originalType, out var command))
        {
            ChatManager.Instance().SendMessage($"/tell {target} {reply}");
            return;
        }

        ChatManager.Instance().SendMessage($"{command} {reply}");
    }

    private static async Task GenerateAndReplyAsync(string name, string world, XivChatType originalType, CancellationToken ct)
    {
        var target = $"{name}@{world}";
        var reply  = string.Empty;

        SetCooldown(target);

        try
        {
            reply = await GenerateReplyAsync(ModuleConfig, target, ct) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            NotificationError(GetLoc("AutoReplyChatBot-ErrorTitle"));
            Error($"{GetLoc("AutoReplyChatBot-ErrorTitle")}:", ex);

            reply = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(reply))
            return;

        SendReply(originalType, target, reply);

        NotificationInfo(reply, $"{GetLoc("AutoReplyChatBot-AutoRepliedTo")}{target}");
        AppendHistory(target, "assistant", reply);
    }

    private static async Task<string?> GenerateReplyAsync(Config cfg, string historyKey, CancellationToken ct)
    {
        UpdateGameContextInWorldBook();

        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
            return null;

        var hist = ModuleConfig.Histories.TryGetValue(historyKey, out var list) ? list.ToList() : [];
        if (hist.Count == 0)
            return null;

        var userMessage = hist.LastOrDefault(x => x.Role == "user").Text;
        if (string.IsNullOrWhiteSpace(userMessage)) return null;

        if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
        {
            var filteredMessage = await FilterMessageAsync(cfg, userMessage, ct);

            switch (filteredMessage)
            {
                case null:
                    return null;
            }

            if (filteredMessage != userMessage)
            {
                if (ModuleConfig.Histories.TryGetValue(historyKey, out var originalList))
                {
                    for (var i = originalList.Count - 1; i >= 0; i--)
                        if (originalList[i].Role == "user")
                        {
                            originalList[i] = new ChatMessage(originalList[i].Role, filteredMessage, originalList[i].Timestamp, originalList[i].Name);
                            break;
                        }
                }

                for (var i = hist.Count - 1; i >= 0; i--)
                    if (hist[i].Role == "user")
                    {
                        hist[i] = new ChatMessage(hist[i].Role, filteredMessage, hist[i].Timestamp, hist[i].Name);
                        break;
                    }
            }
        }

        var url = Backends[cfg.Provider].BuildURL(cfg.BaseURL);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);

        if (cfg.SelectedPromptIndex < 0 || cfg.SelectedPromptIndex >= cfg.SystemPrompts.Count)
            cfg.SelectedPromptIndex = 0;
        var currentPrompt = cfg.SystemPrompts[cfg.SelectedPromptIndex];
        var sys = string.IsNullOrWhiteSpace(currentPrompt.Content)
                      ? DEFAULT_SYSTEM_PROMPT
                      : currentPrompt.Content;

        var worldBookContext = string.Empty;

        if (cfg is { EnableWorldBook: true, WorldBookEntry.Count: > 0 })
        {
            var lastUserMessage = hist.LastOrDefault(x => x.Role == "user").Text;

            if (!string.IsNullOrWhiteSpace(lastUserMessage))
            {
                var relevantEntries = WorldBookManager.FindRelevantEntries(lastUserMessage, cfg.WorldBookEntry);
                worldBookContext = WorldBookManager.BuildWorldBookContext(relevantEntries, cfg.MaxWorldBookContext);
            }
        }

        var messages = new List<object>
        {
            new { role = "system", content = sys }
        };

        if (!string.IsNullOrWhiteSpace(worldBookContext))
            messages.Add(new { role = "system", content = worldBookContext });

        var messagesToSend = hist;

        if (cfg is { EnableContextLimit: true, MaxContextMessages: > 0 })
        {
            // 每轮对话包含用户和 AI 消息
            var totalMessagesToTake = Math.Min(cfg.MaxContextMessages * 2, hist.Count);
            messagesToSend = hist.Skip(Math.Max(0, hist.Count - totalMessagesToTake)).ToList();
        }

        foreach (var message in messagesToSend)
            messages.Add(new { role = message.Role, content = message.Text });

        var body = Backends[cfg.Provider].BuildRequestBody(messages, cfg.Model, cfg.MaxTokens, cfg.Temperature);

        var json = JsonConvert.SerializeObject(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await HTTPClientHelper.Get(HTTP_CLIENT_NAME).SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var jsonResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var jObj = JObject.Parse(jsonResponse);

        var final = Backends[cfg.Provider].ParseContent(jObj);
        if (string.IsNullOrWhiteSpace(final))
            return null;

        return final.StartsWith("[ATTACK", StringComparison.Ordinal) ? string.Empty : final;
    }

    private static async Task<string?> FilterMessageAsync(Config cfg, string userMessage, CancellationToken ct)
    {
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.FilterModel.IsNullOrWhitespace())
            return userMessage;

        var url = Backends[cfg.Provider].BuildURL(cfg.BaseURL);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);

        // 无记忆
        var messages = new List<object>
        {
            new { role = "system", content = string.IsNullOrWhiteSpace(cfg.FilterPrompt) ? FILTER_SYSTEM_PROMPT : cfg.FilterPrompt },
            new { role = "user", content   = userMessage }
        };

        var body = Backends[cfg.Provider].BuildRequestBody(messages, cfg.FilterModel, 512, 0.0f); // 过滤器不需要太多token & 极低温度，确保严格按照规则执行

        var json = JsonConvert.SerializeObject(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await HTTPClientHelper.Get(HTTP_CLIENT_NAME).SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var jsonResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var jObj         = JObject.Parse(jsonResponse);

            var content = Backends[cfg.Provider].ParseContent(jObj);

            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (Exception ex)
        {
            Error($"过滤失败: {ex.Message}");
            return null;
        }
    }

    private interface IChatBackend
    {
        string BuildURL(string baseURL);

        /// <summary>
        ///     组装完整的请求体，包括 messages / model 以及 provider 特定参数。
        /// </summary>
        /// <param name="messages">聊天消息数组</param>
        /// <param name="model">model name</param>
        /// <param name="maxTokens">最大 token</param>
        /// <param name="temperature">采样温度</param>
        /// <returns>序列化前的请求体字典</returns>
        Dictionary<string, object> BuildRequestBody(List<object> messages, string model, int maxTokens, float temperature);

        /// <summary>
        ///     从后端返回的 JSON 字符串中解析出最终的回复文本。
        /// </summary>
        /// <param name="jsonObject">
        ///     已经解析好的 <see cref="JObject" />，对应完整的接口响应。
        /// </param>
        /// <returns>
        ///     - 如果成功，返回 <c>string</c> 类型的对话回复内容。<br />
        ///     - 如果失败（没有 content 字段或结构不符），返回 <c>null</c>。
        /// </returns>
        string? ParseContent(JObject jsonObject);
    }

    private class OpenAIBackend : IChatBackend
    {
        public string BuildURL(string baseURL) => baseURL.TrimEnd('/') + "/chat/completions";

        public Dictionary<string, object> BuildRequestBody(List<object> messages, string model, int maxTokens, float temperature)
        {
            var body = new Dictionary<string, object>
            {
                ["messages"]    = messages,
                ["model"]       = model,
                ["max_tokens"]  = maxTokens,
                ["temperature"] = temperature
            };
            return body;
        }

        public string? ParseContent(JObject jsonObject)
        {
            var msg = jsonObject["choices"] is JArray { Count: > 0 } choices ? choices[0]["message"] : null;
            return msg?["content"]?.Value<string>();
        }
    }

    private class OllamaBackend : IChatBackend
    {
        public string BuildURL(string baseURL) => baseURL.TrimEnd('/') + "/chat";

        public Dictionary<string, object> BuildRequestBody(List<object> messages, string model, int maxTokens, float temperature)
        {
            var body = new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["model"]    = model,
                ["stream"]   = false,
                ["think"]    = false,
                ["options"] = new Dictionary<string, object>
                {
                    ["num_predict"] = maxTokens,
                    ["temperature"] = temperature
                }
            };
            return body;
        }

        public string? ParseContent(JObject jsonObject)
        {
            var messageToken = jsonObject["message"];
            return messageToken?["content"]?.Value<string>();
        }
    }
}
