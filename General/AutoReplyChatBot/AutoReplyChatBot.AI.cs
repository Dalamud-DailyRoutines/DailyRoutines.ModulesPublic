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
    private static async Task SendReply
    (
        XivChatType originalType,
        string      target,
        string      reply
    )
    {
        if (originalType == XivChatType.TellIncoming || !ChatTypeToCommand.TryGetValue(originalType, out var command))
        {
            await IFramework.Instance().RunOnFrameworkThread
                                (() => ChatManager.Instance().SendMessage($"/tell {target} {reply}"))
                            .ConfigureAwait(false);
            return;
        }

        await IFramework.Instance().RunOnFrameworkThread
                            (() => ChatManager.Instance().SendMessage($"{command} {reply}"))
                        .ConfigureAwait(false);
    }

    private async Task<List<object>?> PrepareMessagesAsync
    (
        Config            cfg,
        string            historyKey,
        CancellationToken ct
    )
    {
        if ((cfg.Provider == APIProvider.OpenAI && cfg.APIKey.IsNullOrWhitespace()) ||
            cfg.BaseURL.IsNullOrWhitespace()                                        ||
            cfg.Model.IsNullOrWhitespace())
            return null;

        var conv        = conversationStore.GetOrLoad(historyKey);
        var hist        = conv.RecentTurns.ToList();
        var userMessage = hist.LastOrDefault(x => x.Role == "user")?.Text;
        if (string.IsNullOrWhiteSpace(userMessage) || HardGuardCheck(userMessage, cfg) != null)
            return null;

        if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
        {
            using var filterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            filterCts.CancelAfter(TimeSpan.FromSeconds(30));
            var guardResult = await FilterMessageAsync(cfg, userMessage, filterCts.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (guardResult == null)
                return null;

            switch (guardResult.Value.Level)
            {
                case GuardLevel.Block:
                case GuardLevel.Attack when cfg.AttackBehavior == AttackAction.Silent:
                    return null;
                case GuardLevel.Attack:
                    userMessage = $"[Guard: 用户试图进行 {guardResult.Value.Intent ?? "未知"} 攻击 — {guardResult.Value.Reason}] {userMessage}";
                    break;
                case GuardLevel.Flag:
                    userMessage = $"[Guard: 消息被标记为可疑 — {guardResult.Value.Reason}] {userMessage}";
                    break;
            }

            if (guardResult.Value.Level != GuardLevel.Safe)
            {
                ReplaceLastUserMessage(conv, hist, userMessage);
                conversationStore.RequestSave(historyKey);
            }
        }

        await CompressConversationIfNeededAsync(cfg, historyKey, hist, ct).ConfigureAwait(false);
        return BuildMessageList(cfg, historyKey, hist);
    }

    private static HttpRequestMessage CreateChatRequest
    (
        Config       cfg,
        List<object> messages,
        bool         filter = false,
        List<object> tools  = null,
        bool         stream = false
    )
    {
        var backend = Backends[cfg.Provider];
        var body    = backend.BuildRequestBody(cfg, messages, filter, tools);
        body["stream"] = stream;
        var request = new HttpRequestMessage(HttpMethod.Post, backend.BuildURL(cfg.BaseURL));
        if (!cfg.APIKey.IsNullOrWhitespace())
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.APIKey);
        request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task EnsureChatSuccessAsync
    (
        HttpResponseMessage response,
        CancellationToken   ct
    )
    {
        if (response.IsSuccessStatusCode)
            return;

        var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"AI 请求失败 ({(int)response.StatusCode}): {error}", null, response.StatusCode);
    }

    private static async Task<JObject> SendChatRequestAsync
    (
        HttpRequestMessage request,
        CancellationToken  ct
    )
    {
        using var response = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME).SendAsync(request, ct).ConfigureAwait(false);
        await EnsureChatSuccessAsync(response, ct).ConfigureAwait(false);
        var json   = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JObject.Parse(json);
        if (result["error"] is { } error)
            throw new InvalidOperationException($"AI 请求失败: {error}");
        return result;
    }

    private async Task<string?> GenerateReplyAsync
    (
        Config               cfg,
        string               historyKey,
        ToolExecutionContext toolContext,
        CancellationToken    ct
    )
    {
        var messages = await PrepareMessagesAsync(cfg, historyKey, ct).ConfigureAwait(false);
        if (messages == null)
            return null;

        var backend = Backends[cfg.Provider];

        for (var round = 0; round <= MAX_TOOL_ROUNDS; round++)
        {
            var tools = round < MAX_TOOL_ROUNDS ?
                            GetToolAPIDefinitions() :
                            null;
            using var request   = CreateChatRequest(cfg, messages, tools: tools);
            var       response  = await SendChatRequestAsync(request, ct).ConfigureAwait(false);
            var       toolCalls = backend.ParseToolCalls(response);

            if (toolCalls is { Count: > 0 })
            {
                if (round == MAX_TOOL_ROUNDS)
                    throw new InvalidOperationException("工具调用超过轮数上限。");
                var assistantMessage = cfg.Provider == APIProvider.OpenAI ?
                                           response["choices"]?[0]?["message"] :
                                           response["message"];
                messages.Add(assistantMessage.DeepClone());
                await AppendToolResultsAsync(cfg, messages, toolCalls, toolContext).ConfigureAwait(false);
                continue;
            }

            var final = backend.ParseContent(response);

            if (string.IsNullOrWhiteSpace(final))
            {
                return response["choices"]?[0]?["finish_reason"]?.Value<string>() == "length" ?
                           throw new InvalidOperationException("模型已用完 Token 上限，未返回回复，请提高 Token 上限。") :
                           null;
            }

            return final.StartsWith("[ATTACK", StringComparison.Ordinal) ?
                       string.Empty :
                       final;
        }

        return null;
    }

    private static async Task AppendToolResultsAsync
    (
        Config                  cfg,
        List<object>            messages,
        IReadOnlyList<ToolCall> toolCalls,
        ToolExecutionContext    context,
        ChatMessage             placeholder = null
    )
    {
        foreach (var toolCall in toolCalls)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var record = new ToolCallRecord
            {
                Name      = toolCall.Name,
                Arguments = toolCall.Arguments,
                Result    = "Executing..."
            };

            if (placeholder != null)
            {
                var records = placeholder.ToolCalls?.ToList() ?? [];
                records.Add(record);
                placeholder.ToolCalls = records;
            }

            record.Result = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, context).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (cfg.Provider == APIProvider.OpenAI)
                messages.Add(new { role = "tool", tool_call_id = toolCall.ID, content = record.Result });
            else
                messages.Add(new { role = "tool", tool_name = toolCall.Name, content = record.Result });
        }
    }

    private async Task CompressConversationIfNeededAsync
    (
        Config            cfg,
        string            historyKey,
        List<ChatMessage> hist,
        CancellationToken ct
    )
    {
        var conv     = conversationStore.GetOrLoad(historyKey);
        var estimate = hist.Sum(m => m.Text.Length) + (conv.CompressedSummary?.Length ?? 0);
        var trigger  = cfg.MaxContextTokens * 8 / 10;
        if (estimate <= trigger)
            return;

        var keepTokens    = cfg.MaxContextTokens * 45 / 100;
        var lastUserIndex = hist.FindLastIndex(m => m.Role == "user");
        var keepFrom      = hist.Count;
        var used          = 0;

        for (var i = hist.Count - 1; i >= 0; i--)
        {
            var length = hist[i].Text.Length;
            if (used + length > keepTokens && i < lastUserIndex)
                break;
            used     += length;
            keepFrom =  i;
        }

        if (keepFrom == 0)
            return;

        var summary = await SummarizeMessagesAsync(cfg, conv.CompressedSummary, hist.GetRange(0, keepFrom), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("对话摘要为空，请检查模型参数和 Token 上限。");

        conv.RecentTurns.RemoveRange(0, keepFrom);
        hist.RemoveRange(0, keepFrom);
        conversationStore.UpdateSummary(historyKey, summary, conv.SummaryVersion + 1);
    }

    private static async Task<string?> SummarizeMessagesAsync
    (
        Config            cfg,
        string?           previousSummary,
        List<ChatMessage> messages,
        CancellationToken ct
    )
    {
        var input = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            input.AppendLine("[先前的摘要]");
            input.AppendLine(previousSummary);
            input.AppendLine();
        }

        input.AppendLine("[需要压缩的对话]");
        foreach (var message in messages)
            input.AppendLine($"[{message.Role}] {message.Text}");

        var msgList = new List<object>
        {
            new { role = "system", content = COMPRESSOR_PROMPT },
            new { role = "user", content   = input.ToString() }
        };
        using var request  = CreateChatRequest(cfg, msgList);
        var       response = await SendChatRequestAsync(request, ct).ConfigureAwait(false);
        return Backends[cfg.Provider].ParseContent(response);
    }

    private List<object> BuildMessageList
    (
        Config            cfg,
        string            historyKey,
        List<ChatMessage> hist
    )
    {
        if (cfg.SelectedPromptIndex < 0 || cfg.SelectedPromptIndex >= cfg.SystemPrompts.Count)
            cfg.SelectedPromptIndex = 0;
        var currentPrompt = cfg.SystemPrompts[cfg.SelectedPromptIndex];
        var sys = string.IsNullOrWhiteSpace(currentPrompt.Content) ?
                      DEFAULT_SYSTEM_PROMPT :
                      currentPrompt.Content;
        var worldBookContext = string.Empty;

        if (cfg is { EnableWorldBook: true, WorldBookEntry.Count: > 0 })
        {
            var lastUserMessage = hist.LastOrDefault(x => x.Role == "user")?.Text;

            if (!string.IsNullOrWhiteSpace(lastUserMessage))
            {
                var relevantEntries = WorldBookManager.FindRelevantEntries(lastUserMessage, cfg.WorldBookEntry);
                worldBookContext = WorldBookManager.BuildWorldBookContext(relevantEntries, cfg.MaxWorldBookContext);
            }
        }

        var messages = new List<object> { new { role = "system", content = sys } };
        var conv     = conversationStore.GetOrLoad(historyKey);
        if (!string.IsNullOrWhiteSpace(conv.CompressedSummary))
            messages.Add(new { role = "system", content = $"[对话摘要 v{conv.SummaryVersion}]\n{conv.CompressedSummary}" });
        if (!string.IsNullOrWhiteSpace(worldBookContext))
            messages.Add(new { role = "system", content = worldBookContext });

        var tokenBudget    = cfg.MaxContextTokens - cfg.MaxTokens - sys.Length - worldBookContext.Length - (conv.CompressedSummary?.Length ?? 0);
        var messagesToSend = new List<ChatMessage>();
        var used           = 0;

        for (var i = hist.Count - 1; i >= 0; i--)
        {
            var message = hist[i];
            if (string.IsNullOrWhiteSpace(message.Text))
                continue;
            if (used + message.Text.Length > tokenBudget)
                break;
            used += message.Text.Length;
            messagesToSend.Add(message);
        }

        if (!messagesToSend.Exists(m => m.Role == "user"))
            throw new InvalidOperationException("上下文空间不足，请提高上下文上限或降低回复 Token 上限。");

        messagesToSend.Reverse();
        foreach (var message in messagesToSend)
            messages.Add(new { role = message.Role, content = message.Text });
        return messages;
    }

    private async IAsyncEnumerable<string> GenerateReplyStreamAsync
    (
        Config                                     cfg,
        string                                     historyKey,
        ToolExecutionContext                       toolContext,
        ChatMessage                                placeholder,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var messages = await PrepareMessagesAsync(cfg, historyKey, ct).ConfigureAwait(false);
        if (messages == null)
            yield break;

        var backend = Backends[cfg.Provider];

        for (var round = 0; round <= MAX_TOOL_ROUNDS; round++)
        {
            var tools = round < MAX_TOOL_ROUNDS ?
                            GetToolAPIDefinitions() :
                            null;
            using var request = CreateChatRequest(cfg, messages, tools: tools, stream: true);
            using var response = await HTTPClientHelper.Instance().Get(HTTP_CLIENT_NAME)
                                                       .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            await EnsureChatSuccessAsync(response, ct).ConfigureAwait(false);
            await using var stream      = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var       reader      = new StreamReader(stream, Encoding.UTF8);
            var             accumulator = new StreamToolCallAccumulator();
            var             content     = new StringBuilder();

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (cfg.Provider == APIProvider.OpenAI && line.StartsWith("data:", StringComparison.Ordinal) && line[5..].Trim() == "[DONE]")
                    break;
                var text = accumulator.ProcessChunk(line, backend);

                if (!string.IsNullOrEmpty(text))
                {
                    content.Append(text);
                    yield return text;
                }
            }

            accumulator.BuildToolCalls();

            if (!accumulator.HasToolCalls)
            {
                if (accumulator.FinishReason == "length")
                    throw new InvalidOperationException("模型已用完 Token 上限，请提高 Token 上限。");
                yield break;
            }

            if (round == MAX_TOOL_ROUNDS)
                throw new InvalidOperationException("工具调用超过轮数上限。");

            var assistantMessage = new JObject
            {
                ["role"]    = "assistant",
                ["content"] = content.ToString(),
                ["tool_calls"] = new JArray
                (
                    accumulator.ToolCalls.Select
                    (call =>
                        {
                            var function = new JObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = cfg.Provider == APIProvider.OpenAI ?
                                                    JValue.CreateString(call.Arguments) :
                                                    JObject.Parse(call.Arguments)
                            };
                            var toolCall = new JObject { ["function"] = function };

                            if (cfg.Provider == APIProvider.OpenAI)
                            {
                                toolCall["id"]   = call.ID;
                                toolCall["type"] = "function";
                            }

                            return toolCall;
                        }
                    )
                )
            };
            if (!string.IsNullOrEmpty(accumulator.Reasoning))
                assistantMessage["reasoning_content"] = accumulator.Reasoning;
            messages.Add(assistantMessage);
            await AppendToolResultsAsync(cfg, messages, accumulator.ToolCalls, toolContext, placeholder).ConfigureAwait(false);
        }
    }

    private static async Task<GuardResult?> FilterMessageAsync
    (
        Config            cfg,
        string            userMessage,
        CancellationToken ct
    )
    {
        var systemPrompt = string.IsNullOrWhiteSpace(cfg.FilterPrompt) ?
                               FILTER_SYSTEM_PROMPT :
                               cfg.FilterPrompt;
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content   = userMessage }
        };

        try
        {
            using var request  = CreateChatRequest(cfg, messages, filter: true);
            var       response = await SendChatRequestAsync(request, ct).ConfigureAwait(false);
            var       raw      = Backends[cfg.Provider].ParseContent(response);
            return string.IsNullOrWhiteSpace(raw) ?
                       null :
                       ParseGuardResponse(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DLog.Error($"Guard 分类失败: {ex.Message}");
            return null;
        }
    }

    private static GuardResult? ParseGuardResponse
    (
        string raw
    )
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

    private static void ReplaceLastUserMessage
    (
        ConversationStore.Conversation conv,
        List<ChatMessage>              hist,
        string                         newText
    )
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

    private static GuardResult? HardGuardCheck
    (
        string message,
        Config cfg
    )
    {
        if (!cfg.HardGuardEnabled) return null;

        if (message.Length > cfg.MaxMessageLength)
            return GuardResult.Blocked("消息过长");

        var lowerMsg = message.ToLowerInvariant();
        var keywords = cfg.HardGuardKeywords;

        if (keywords is not { Count: > 0 })
            keywords = [.. HardGuardDefaultKeywords];

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
        string BuildURL
        (
            string baseURL
        );

        Dictionary<string, object> BuildRequestBody
        (
            Config       cfg,
            List<object> messages,
            bool         filter = false,
            List<object> tools  = null
        );

        string? ParseContent
        (
            JObject jsonObject
        );

        List<ToolCall>? ParseToolCalls
        (
            JObject jsonObject
        );

        StreamChunkResult ParseStreamChunkFull
        (
            string chunk
        );
    }

    private class OpenAIBackend : IChatBackend
    {
        public string BuildURL
        (
            string baseURL
        ) => baseURL.TrimEnd('/') + "/chat/completions";

        public Dictionary<string, object> BuildRequestBody
        (
            Config       cfg,
            List<object> messages,
            bool         filter = false,
            List<object> tools  = null
        )
        {
            var useMaxCompletionTokens = filter ?
                                             cfg.FilterUseMaxCompletionTokens :
                                             cfg.UseMaxCompletionTokens;
            var body = new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["model"] = filter ?
                                cfg.FilterModel :
                                cfg.Model,
                [useMaxCompletionTokens ?
                     "max_completion_tokens" :
                     "max_tokens"] = filter ?
                                         cfg.FilterMaxTokens :
                                         cfg.MaxTokens
            };
            if (filter ?
                    cfg.FilterSendTemperature :
                    cfg.SendTemperature)
                body["temperature"] = filter ?
                                          0.0f :
                                          cfg.Temperature;
            if (tools is { Count: > 0 })
                body["tools"] = tools;
            return body;
        }

        public string? ParseContent
        (
            JObject jsonObject
        )
        {
            var msg = jsonObject["choices"] is JArray { Count: > 0 } choices ?
                          choices[0]["message"] :
                          null;
            return msg?["content"]?.Value<string>();
        }

        public List<ToolCall>? ParseToolCalls
        (
            JObject jsonObject
        )
        {
            if (jsonObject["choices"]?[0]?["message"]?["tool_calls"] is not JArray { Count: > 0 } toolCalls)
                return null;

            return
            [
                .. toolCalls.Select
                (tc => new ToolCall
                    {
                        ID        = tc["id"]?.Value<string>()                     ?? string.Empty,
                        Name      = tc["function"]?["name"]?.Value<string>()      ?? string.Empty,
                        Arguments = tc["function"]?["arguments"]?.Value<string>() ?? "{}"
                    }
                )
            ];
        }

        public StreamChunkResult ParseStreamChunkFull
        (
            string chunk
        )
        {
            var result = new StreamChunkResult();
            if (!chunk.StartsWith("data:", StringComparison.Ordinal)) return result;
            var json = chunk[5..].TrimStart();
            if (json == "[DONE]") return result;

            try
            {
                var jObj = JObject.Parse(json);
                if (jObj["error"] is { } error)
                    throw new InvalidOperationException($"AI 流式请求失败: {error}");
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
                                Index = tc["index"]?.Value<int>() ?? 0,
                                ID    = tc["id"]?.Value<string>(),
                                Name  = func?["name"]?.Value<string>(),
                                ArgumentsDelta = func?["arguments"]?.Type == JTokenType.Object ?
                                                     func["arguments"]?.ToString(Formatting.None) :
                                                     func?["arguments"]?.Value<string>()
                            }
                        );
                    }

                    result = result with { Fragments = fragments };
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("模型返回了无效的流式 JSON 数据。", ex);
            }

            return result;
        }
    }

    private class OllamaBackend : IChatBackend
    {
        public string BuildURL
        (
            string baseURL
        ) => baseURL.TrimEnd('/') + "/chat";

        public Dictionary<string, object> BuildRequestBody
        (
            Config       cfg,
            List<object> messages,
            bool         filter = false,
            List<object> tools  = null
        )
        {
            var options = new Dictionary<string, object>
            {
                ["num_predict"] = filter ?
                                      cfg.FilterMaxTokens :
                                      cfg.MaxTokens
            };
            if (filter ?
                    cfg.FilterSendTemperature :
                    cfg.SendTemperature)
                options["temperature"] = filter ?
                                             0.0f :
                                             cfg.Temperature;
            var body = new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["model"] = filter ?
                                cfg.FilterModel :
                                cfg.Model,
                ["stream"]  = false,
                ["think"]   = false,
                ["options"] = options
            };
            if (tools is { Count: > 0 })
                body["tools"] = tools;
            return body;
        }

        public string? ParseContent
        (
            JObject jsonObject
        )
        {
            var messageToken = jsonObject["message"];
            return messageToken?["content"]?.Value<string>();
        }

        public List<ToolCall>? ParseToolCalls
        (
            JObject jsonObject
        )
        {
            if (jsonObject["message"]?["tool_calls"] is not JArray { Count: > 0 } toolCalls) return null;

            return
            [
                .. toolCalls.Select
                (tc => new ToolCall
                    {
                        ID        = string.Empty,
                        Name      = tc["function"]?["name"]?.Value<string>()                ?? string.Empty,
                        Arguments = tc["function"]?["arguments"]?.ToString(Formatting.None) ?? "{}"
                    }
                )
            ];
        }

        public StreamChunkResult ParseStreamChunkFull
        (
            string chunk
        )
        {
            var result = new StreamChunkResult();

            try
            {
                var jObj = JObject.Parse(chunk);
                if (jObj["error"] is { } error)
                    throw new InvalidOperationException($"AI 流式请求失败: {error}");
                var message = jObj["message"];
                if (message == null) return result;

                result = result with
                {
                    Content = message["content"]?.Value<string>(),
                    FinishReason = jObj["done"]?.Value<bool>() == true ?
                                       "stop" :
                                       null
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
                                ArgumentsDelta = func?["arguments"]?.ToString(Formatting.None)
                            }
                        );
                    }

                    result = result with { Fragments = fragments };
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("模型返回了无效的流式 JSON 数据。", ex);
            }

            return result;
        }
    }
}
