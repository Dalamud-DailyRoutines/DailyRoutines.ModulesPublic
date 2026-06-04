using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game.Text;
using Dalamud.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private static void SendReply(XivChatType originalType, string target, string reply)
    {
        if (originalType == XivChatType.TellIncoming || !ChatTypeToCommand.TryGetValue(originalType, out var command))
        {
            ChatManager.Instance().SendMessage($"/tell {target} {reply}");
            return;
        }

        ChatManager.Instance().SendMessage($"{command} {reply}");
    }

    private async Task<string?> GenerateReplyAsync
        (Config cfg, string historyKey, ToolExecutionContext toolContext, CancellationToken ct)
    {
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
            return null;

        var conv = conversationStore!.GetOrLoad(historyKey);
        var hist = conv.RecentTurns.ToList();
        if (hist.Count == 0)
            return null;

        var userMessage = hist.LastOrDefault(x => x.Role == "user").Text;
        if (string.IsNullOrWhiteSpace(userMessage)) return null;

        await CompressConversationIfNeededAsync(cfg, historyKey, hist, ct).ConfigureAwait(false);

        if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
        {
            var guardResult = await FilterMessageAsync(cfg, userMessage, ct);

            if (guardResult == null)
                return null;

            switch (guardResult.Value.Level)
            {
                case GuardLevel.Block:
                    return null;
                case GuardLevel.Attack:
                    if (cfg.AttackBehavior == AttackAction.Silent)
                        return null;
                    userMessage = $"[Guard: 用户试图进行 {guardResult.Value.Intent ?? "未知"} 攻击 — {guardResult.Value.Reason}] {userMessage}";
                    break;
                case GuardLevel.Flag:
                    userMessage = $"[Guard: 消息被标记为可疑 — {guardResult.Value.Reason}] {userMessage}";
                    break;
            }

            if (guardResult.Value.Level != GuardLevel.Safe)
                ReplaceLastUserMessage(conv, hist, userMessage);
        }

        var messages = BuildMessageList(cfg, historyKey, hist);

        var round = 0;
        while (true)
        {
            var tools = GetToolAPIDefinitions();
            var body  = Backends[cfg.Provider].BuildRequestBodyWithTools(messages, cfg.Model, cfg.MaxTokens, cfg.Temperature, tools);

            var url  = Backends[cfg.Provider].BuildURL(cfg.BaseURL);
            var json = JsonConvert.SerializeObject(body);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);
            req.Content               = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME).SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var jsonResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var jObj         = JObject.Parse(jsonResponse);

            var toolCall = ParseToolCallsFromResponse(jObj, Backends[cfg.Provider]);
            if (toolCall != null && round < MAX_TOOL_ROUNDS)
            {
                var toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, toolContext).ConfigureAwait(false);

                var reasoning = jObj["choices"]?[0]?["message"]?["reasoning_content"]?.Value<string>();
                var assistantMessage = new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new[]
                    {
                        new
                        {
                            id       = toolCall.ID,
                            type     = "function",
                            function = new { name = toolCall.Name, arguments = toolCall.Arguments }
                        }
                    }
                };

                if (!string.IsNullOrEmpty(reasoning))
                {
                    assistantMessage["reasoning_content"] = reasoning;
                }

                messages.Add(assistantMessage);

                messages.Add(new { role = "tool", tool_call_id = toolCall.ID, content = toolResult });

                round++;
                continue;
            }

            var final = Backends[cfg.Provider].ParseContent(jObj);
            if (string.IsNullOrWhiteSpace(final))
                return null;

            return final.StartsWith("[ATTACK", StringComparison.Ordinal) ? string.Empty : final;
        }
    }

    private async Task CompressConversationIfNeededAsync(
        Config cfg, string historyKey, List<ChatMessage> hist, CancellationToken ct)
    {
        var cmprConv = conversationStore!.GetOrLoad(historyKey);
        var estimate = hist.Sum(m => m.Text.Length * 2 / 7);
        var trigger  = cfg.MaxContextTokens * 8 / 10;

        if (estimate <= trigger) return;

        var keepTokens = (int)(cfg.MaxContextTokens * 0.45f);
        var keep       = new List<ChatMessage>();
        var used       = 0;

        for (var i = hist.Count - 1; i >= 0; i--)
        {
            var t = hist[i].Text.Length * 2 / 7;
            if (used + t > keepTokens) break;
            used += t;
            keep.Add(hist[i]);
        }

        keep.Reverse();
        var toCompress = hist.Take(hist.Count - keep.Count).ToList();
        if (toCompress.Count == 0) return;

        var newSummary = await SummarizeMessagesAsync(cfg, cmprConv.CompressedSummary, toCompress, ct).ConfigureAwait(false);

        conversationStore!.UpdateSummary(historyKey, newSummary, cmprConv.SummaryVersion + 1);

        cmprConv.RecentTurns.Clear();
        cmprConv.RecentTurns.AddRange(keep);

        hist.Clear();
        hist.AddRange(keep);
    }

    private static async Task<string?> SummarizeMessagesAsync(
        Config cfg, string? previousSummary, List<ChatMessage> messages, CancellationToken ct)
    {
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
            return previousSummary ?? string.Empty;
        
        var input = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            input.AppendLine("[先前的摘要]");
            input.AppendLine(previousSummary);
            input.AppendLine();
        }

        input.AppendLine("[需要压缩的对话]");
        foreach (var msg in messages)
            input.AppendLine($"[{msg.Role}] {msg.Text}");

        var msgList = new List<object>
        {
            new { role = "system", content = COMPRESSOR_PROMPT },
            new { role = "user", content   = input.ToString() }
        };

        var body = Backends[cfg.Provider].BuildRequestBody(msgList, cfg.Model, 1024, 0.3f);
        var json = JsonConvert.SerializeObject(body);

        var url = Backends[cfg.Provider].BuildURL(cfg.BaseURL);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);
        req.Content               = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME).SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var respJSON = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var jObj     = JObject.Parse(respJSON);

        return Backends[cfg.Provider].ParseContent(jObj);
    }

    private List<object> BuildMessageList(Config cfg, string historyKey, List<ChatMessage> hist)
    {
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

        var bldConv = conversationStore!.GetOrLoad(historyKey);
        if (!string.IsNullOrWhiteSpace(bldConv.CompressedSummary))
            messages.Add(new { role = "system", content = $"[对话摘要 v{bldConv.SummaryVersion}]\n{bldConv.CompressedSummary}" });

        if (!string.IsNullOrWhiteSpace(worldBookContext))
            messages.Add(new { role = "system", content = worldBookContext });

        var tokenBudget = cfg.MaxContextTokens;
        // 扣除 system prompt + world book 的 token
        tokenBudget -= sys.Length + worldBookContext.Length;

        var messagesToSend = new List<ChatMessage>();
        var used           = 0;

        for (var i = hist.Count - 1; i >= 0; i--)
        {
            var tokens = hist[i].Text.Length;
            if (used + tokens > tokenBudget) break;
            used += tokens;
            messagesToSend.Add(hist[i]);
        }

        messagesToSend.Reverse();

        foreach (var message in messagesToSend)
            messages.Add(new { role = message.Role, content = message.Text });

        return messages;
    }

    private async IAsyncEnumerable<string> GenerateReplyStreamAsync
        (Config cfg, string historyKey, ToolExecutionContext toolContext, ChatMessage placeholder, [EnumeratorCancellation] CancellationToken ct)
    {
        DLog.Debug($"[GenerateReplyStreamAsync] 开始流式生成回复, historyKey: {historyKey}");
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.Model.IsNullOrWhitespace())
        {
            DLog.Warning("[GenerateReplyStreamAsync] API 配置不完整，退出");
            yield break;
        }

        var strmConv = conversationStore!.GetOrLoad(historyKey);
        var hist     = strmConv.RecentTurns.ToList();
        if (hist.Count == 0)
        {
            DLog.Warning("[GenerateReplyStreamAsync] 历史记录为空，退出");
            yield break;
        }

        var userMessage = hist.LastOrDefault(x => x.Role == "user").Text;
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            DLog.Warning("[GenerateReplyStreamAsync] 用户的最新消息为空，退出");
            yield break;
        }

        var messages = BuildMessageList(cfg, historyKey, hist);
        var backend  = Backends[cfg.Provider];
        
        for (var round = 0; round <= MAX_TOOL_ROUNDS; round++)
        {
            DLog.Debug($"[GenerateReplyStreamAsync] 进入第 {round} 轮 Tool-Call 迭代");
            var tools = GetToolAPIDefinitions();
            DLog.Debug($"[GenerateReplyStreamAsync] 当前已定义工具数量: {tools.Count}");
            
            var body  = backend.BuildRequestBodyWithTools(messages, cfg.Model, cfg.MaxTokens, cfg.Temperature, tools);
            body["stream"] = true;

            var url  = backend.BuildURL(cfg.BaseURL);
            var json = JsonConvert.SerializeObject(body);
            DLog.Debug($"[GenerateReplyStreamAsync] 发送流式请求至: {url}, 请求体长度: {json.Length}");

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);
            req.Content               = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME)
                                                   .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            DLog.Debug("[GenerateReplyStreamAsync] 收到响应状态码: " + resp.StatusCode);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var       reader = new StreamReader(stream, Encoding.UTF8);

            var accumulator = new StreamToolCallAccumulator();
            var hasContent  = false;
            var lineCount   = 0;

            DLog.Debug("[GenerateReplyStreamAsync] 开始读取 SSE 流...");
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lineCount++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                DLog.Verbose($"[SSE Line #{lineCount}]: {line}");

                var text = accumulator.ProcessChunk(line, backend);

                if (!string.IsNullOrEmpty(text))
                {
                    hasContent = true;
                    yield return text;
                }
            }
            DLog.Debug($"[GenerateReplyStreamAsync] SSE 流读取完毕, 总行数: {lineCount}, hasContent: {hasContent}");

            accumulator.BuildToolCalls();
            DLog.Debug($"[GenerateReplyStreamAsync] 组装 ToolCalls 完毕. 发现工具调用数量: {(accumulator.ToolCalls?.Count ?? 0)}");

            if (!accumulator.HasToolCalls)
            {
                DLog.Debug($"[GenerateReplyStreamAsync] 没有检测到工具调用，流式正常结束。退出流。");
                yield break;
            }

            var toolCalls = accumulator.ToolCalls;
            if (toolCalls == null) 
            {
                DLog.Warning("[GenerateReplyStreamAsync] toolCalls 为 null，异常退出");
                yield break;
            }

            foreach (var tc in toolCalls)
            {
                DLog.Debug($"[GenerateReplyStreamAsync] 触发工具: {tc.Name}, ID: {tc.ID}, 参数: {tc.Arguments}");
                var currentToolCalls = placeholder.ToolCalls != null ? new List<ToolCallRecord>(placeholder.ToolCalls) : [];
                var record = new ToolCallRecord
                {
                    Name      = tc.Name,
                    Arguments = tc.Arguments,
                    Result    = "Executing..."
                };

                currentToolCalls.Add(record);
                placeholder.ToolCalls = currentToolCalls;

                var toolResult = await ExecuteToolAsync(tc.Name, tc.Arguments, toolContext).ConfigureAwait(false);

                DLog.Debug($"[GenerateReplyStreamAsync] 工具 {tc.Name} 执行结果完毕: {toolResult}");
                record.Result = toolResult;

                var assistantMessage = new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new[]
                    {
                        new
                        {
                            id       = tc.ID,
                            type     = "function",
                            function = new { name = tc.Name, arguments = tc.Arguments }
                        }
                    }
                };

                if (!string.IsNullOrEmpty(accumulator.Reasoning))
                {
                    assistantMessage["reasoning_content"] = accumulator.Reasoning;
                }

                messages.Add(assistantMessage);

                messages.Add(new { role = "tool", tool_call_id = tc.ID, content = toolResult });
            }
            
            DLog.Debug("[GenerateReplyStreamAsync] 准备进入下一轮 Tool-Call 请求循环");
        }
    }

    private static async Task<GuardResult?> FilterMessageAsync(Config cfg, string userMessage, CancellationToken ct)
    {
        if (cfg.APIKey.IsNullOrWhitespace() || cfg.BaseURL.IsNullOrWhitespace() || cfg.FilterModel.IsNullOrWhitespace())
            return GuardResult.Safe();

        var url = Backends[cfg.Provider].BuildURL(cfg.BaseURL);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);

        var systemPrompt = string.IsNullOrWhiteSpace(cfg.FilterPrompt) ? FILTER_SYSTEM_PROMPT : cfg.FilterPrompt;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content   = userMessage }
        };

        var body = Backends[cfg.Provider].BuildRequestBody(messages, cfg.FilterModel, 256, 0.0f);

        var json = JsonConvert.SerializeObject(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME).SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var jsonResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var jObj         = JObject.Parse(jsonResponse);
            var raw          = Backends[cfg.Provider].ParseContent(jObj);

            return string.IsNullOrWhiteSpace(raw) ? null : ParseGuardResponse(raw);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            DLog.Error($"Guard 分类失败: {ex.Message}");
            return null;
        }
    }

    private static GuardResult? ParseGuardResponse(string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                var jObj     = JObject.Parse(trimmed);
                var levelStr = jObj["level"]?.Value<string>()?.ToLowerInvariant();

                var level = levelStr switch
                {
                    "safe"   => GuardLevel.Safe,
                    "flag"   => GuardLevel.Flag,
                    "attack" => GuardLevel.Attack,
                    "block"  => GuardLevel.Block,
                    _        => GuardLevel.Flag
                };

                return new GuardResult
                {
                    Level  = level,
                    Reason = jObj["reason"]?.Value<string>(),
                    Intent = jObj["intent"]?.Value<string>()
                };
            }
            catch
            {
                // JSON 解析失败，降级为 Flag
            }
        }

        return new GuardResult { Level = GuardLevel.Flag, Reason = "分类器返回非 JSON 格式" };
    }

    private static void ReplaceLastUserMessage(ConversationStore.Conversation conv, List<ChatMessage> hist, string newText)
    {
        for (var i = conv.RecentTurns.Count - 1; i >= 0; i--)
            if (conv.RecentTurns[i].Role == "user")
            {
                conv.RecentTurns[i] = new ChatMessage(conv.RecentTurns[i].Role, newText, conv.RecentTurns[i].Timestamp, conv.RecentTurns[i].Name);
                break;
            }

        for (var i = hist.Count - 1; i >= 0; i--)
            if (hist[i].Role == "user")
            {
                hist[i] = new ChatMessage(hist[i].Role, newText, hist[i].Timestamp, hist[i].Name);
                break;
            }
    }

    private static GuardResult? HardGuardCheck(string message, Config cfg)
    {
        if (!cfg.HardGuardEnabled) return null;

        if (message.Length > cfg.MaxMessageLength)
            return GuardResult.Blocked("消息过长");

        var lowerMsg = message.ToLowerInvariant();
        var keywords = cfg.HardGuardKeywords;

        if (keywords is not { Count: > 0 })
            keywords = [..HardGuardDefaultKeywords];

        foreach (var kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            if (lowerMsg.Contains(kw, StringComparison.Ordinal))
                return GuardResult.Blocked($"匹配硬护栏关键词: {kw}");
        }

        return null;
    }

    private sealed class StreamChunkFragment
    {
        public int     Index          { get; init; }
        public string? ID             { get; init; }
        public string? Name           { get; init; }
        public string? ArgumentsDelta { get; init; }
    }

    private readonly struct StreamChunkResult
    {
        public string?                             Content      { get; init; }
        public string?                             Reasoning    { get; init; }
        public IReadOnlyList<StreamChunkFragment>? Fragments    { get; init; }
        public string?                             FinishReason { get; init; }
    }

    private interface IChatBackend
    {
        string BuildURL(string baseURL);

        Dictionary<string, object> BuildRequestBody(List<object> messages, string model, int maxTokens, float temperature);

        string? ParseContent(JObject jsonObject);

        Dictionary<string, object> BuildRequestBodyWithTools(List<object> messages, string model, int maxTokens, float temperature, List<object> tools);

        List<ToolCall>? ParseToolCalls(JObject jsonObject);

        StreamChunkResult ParseStreamChunkFull(string chunk);
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

        public Dictionary<string, object> BuildRequestBodyWithTools(List<object> messages, string model, int maxTokens, float temperature, List<object> tools)
        {
            var body = BuildRequestBody(messages, model, maxTokens, temperature);
            body["tools"] = tools;
            return body;
        }

        public List<ToolCall>? ParseToolCalls(JObject jsonObject)
        {
            if (jsonObject["choices"]?[0]?["message"]?["tool_calls"] is not JArray { Count: > 0 } toolCalls) 
                return null;

            return toolCalls.Select
            (tc => new ToolCall
                {
                    ID        = tc["id"]?.Value<string>()                     ?? string.Empty,
                    Name      = tc["function"]?["name"]?.Value<string>()      ?? string.Empty,
                    Arguments = tc["function"]?["arguments"]?.Value<string>() ?? "{}"
                }
            ).ToList();
        }

        public StreamChunkResult ParseStreamChunkFull(string chunk)
        {
            var result = new StreamChunkResult();
            if (!chunk.StartsWith("data:", StringComparison.Ordinal)) return result;
            var json = chunk[5..].TrimStart();
            if (json == "[DONE]") return result;

            try
            {
                var jObj   = JObject.Parse(json);
                var choice = jObj["choices"]?[0];
                if (choice == null) return result;

                result = result with
                {
                    Content = choice["delta"]?["content"]?.Value<string>(),
                    Reasoning = choice["delta"]?["reasoning_content"]?.Value<string>(),
                    FinishReason = choice["finish_reason"]?.Value<string>()
                };

                if (choice["delta"]?["tool_calls"] is JArray { Count: > 0 } tcArray)
                {
                    var fragments = new List<StreamChunkFragment>(tcArray.Count);

                    foreach (var tc in tcArray)
                    {
                        var func = tc["function"];
                        fragments.Add
                        (
                            new StreamChunkFragment
                            {
                                Index          = tc["index"]?.Value<int>() ?? 0,
                                ID             = tc["id"]?.Value<string>(),
                                Name           = func?["name"]?.Value<string>(),
                                ArgumentsDelta = func?["arguments"]?.Type == JTokenType.Object 
                                                     ? func["arguments"]?.ToString(Formatting.None) 
                                                     : func?["arguments"]?.Value<string>()
                            }
                        );
                    }

                    result = result with { Fragments = fragments };
                }
            }
            catch
            {
                // ignored
            }

            return result;
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

        public Dictionary<string, object> BuildRequestBodyWithTools(List<object> messages, string model, int maxTokens, float temperature, List<object> tools)
        {
            var body = BuildRequestBody(messages, model, maxTokens, temperature);
            body["tools"] = tools;
            return body;
        }

        public List<ToolCall>? ParseToolCalls(JObject jsonObject)
        {
            if (jsonObject["message"]?["tool_calls"] is not JArray { Count: > 0 } toolCalls) return null;

            return toolCalls.Select
            (tc => new ToolCall
                {
                    ID        = string.Empty,
                    Name      = tc["function"]?["name"]?.Value<string>()      ?? string.Empty,
                    Arguments = tc["function"]?["arguments"]?.Value<string>() ?? "{}"
                }
            ).ToList();
        }

        public StreamChunkResult ParseStreamChunkFull(string chunk)
        {
            var result = new StreamChunkResult();

            try
            {
                var jObj    = JObject.Parse(chunk);
                var message = jObj["message"];
                if (message == null) return result;

                result = result with
                {
                    Content = message["content"]?.Value<string>(),
                    FinishReason = jObj["done"]?.Value<bool>() == true ? "stop" : null
                };

                if (message["tool_calls"] is JArray { Count: > 0 } tcArray)
                {
                    var fragments = new List<StreamChunkFragment>(tcArray.Count);

                    for (var i = 0; i < tcArray.Count; i++)
                    {
                        var func = tcArray[i]["function"];
                        fragments.Add
                        (
                            new StreamChunkFragment
                            {
                                Index          = i,
                                Name           = func?["name"]?.Value<string>(),
                                ArgumentsDelta = func?["arguments"]?.Value<string>()
                            }
                        );
                    }

                    result = result with { Fragments = fragments };
                }
            }
            catch
            {
                // ignored
            }

            return result;
        }
    }
}
