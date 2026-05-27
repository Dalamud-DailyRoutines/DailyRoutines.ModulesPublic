using System.Collections.Frozen;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private sealed class ToolExecutionContext
    {
        public required Config            ModuleConfig      { get; init; }
        public required ReplyContext      ReplyContext      { get; init; }
        public required ConversationStore ConversationStore { get; init; }
        public          bool              SendMessageCalled { get; set; }
        public required CancellationToken CancellationToken { get; init; }
    }

    private sealed class ReplyContext
    {
        public string                           Target          { get; init; } = string.Empty;
        public XivChatType                      OriginalType    { get; init; }
        public string?                          SentMessage     { get; set; }
        public FrozenDictionary<string, string> ChannelCommands { get; init; } = null!;
        public string                           DefaultChannel  { get; init; } = "tell";
    }

    private static List<object> GetToolAPIDefinitions()
    {
        var list = new List<object>(ToolRegistry.Count);
        foreach (var tool in ToolRegistry.Values)
            list.Add(tool.ToAPIFormat());
        return list;
    }

    private static async Task<string> ExecuteToolAsync(string name, string argumentsJSON, ToolExecutionContext context)
    {
        DLog.Debug($"[ExecuteToolAsync] 准备执行工具: {name}, 参数: {argumentsJSON}");
        if (!ToolRegistry.TryGetValue(name, out var tool))
        {
            DLog.Warning($"[ExecuteToolAsync] 未找到工具定义: {name}");
            return $"Error: unknown tool '{name}'";
        }

        try
        {
            var args = string.IsNullOrWhiteSpace(argumentsJSON)
                           ? new JObject()
                           : JObject.Parse(argumentsJSON);
            
            DLog.Debug($"[ExecuteToolAsync] 调度到主线程执行: {name}");
            var result = await DService.Instance().Framework.RunOnTick(async () =>
            {
                try
                {
                    return await tool.ExecuteAsync(args, context).ConfigureAwait(false);
                }
                catch (Exception innerEx)
                {
                    DLog.Error($"[ExecuteToolAsync] 工具 {name} 在主线程执行内部发生异常: {innerEx}");
                    throw;
                }
            }).ConfigureAwait(false);

            DLog.Debug($"[ExecuteToolAsync] 工具 {name} 执行完成，返回结果长度: {result?.Length ?? 0}");
            return result;
        }
        catch (Exception ex)
        {
            DLog.Error($"[ExecuteToolAsync] 工具 '{name}' 执行失败: {ex}");
            return $"Error executing '{name}': {ex.Message}";
        }
    }

    private static ToolCall? ParseToolCallsFromResponse(JObject jObj, IChatBackend backend)
    {
        var toolCalls = backend.ParseToolCalls(jObj);
        if (toolCalls is not { Count: > 0 }) return null;

        return toolCalls[0];
    }

    private sealed class ToolCall
    {
        public string ID        { get; init; } = string.Empty;
        public string Name      { get; init; } = string.Empty;
        public string Arguments { get; init; } = string.Empty;
    }

    private sealed class StreamToolCallAccumulator
    {
        private readonly Dictionary<int, Builder> builders = [];
        private readonly StringBuilder reasoningBuilder = new();

        public IReadOnlyList<ToolCall>? ToolCalls    { get; private set; }
        public bool                     HasToolCalls => ToolCalls is { Count: > 0 };
        public string?                  FinishReason { get; private set; }
        public string                   Reasoning    => reasoningBuilder.ToString();

        public string? ProcessChunk(string chunk, IChatBackend backend)
        {
            var result = backend.ParseStreamChunkFull(chunk);

            if (result.FinishReason != null)
                FinishReason = result.FinishReason;

            if (result.Reasoning != null)
                reasoningBuilder.Append(result.Reasoning);

            if (result.Fragments is { Count: > 0 })
            {
                foreach (var frag in result.Fragments)
                {
                    if (!builders.TryGetValue(frag.Index, out var builder))
                        builders[frag.Index] = builder = new Builder();

                    if (frag.ID != null)
                    {
                        builder.ID    = frag.ID;
                        builder.Index = frag.Index;
                    }

                    if (frag.Name != null)
                        builder.Name = frag.Name;

                    if (frag.ArgumentsDelta != null)
                        builder.ArgumentsBuilder.Append(frag.ArgumentsDelta);
                }
            }

            return result.Content;
        }

        public void BuildToolCalls()
        {
            if (builders.Count == 0)
            {
                ToolCalls = null;
                return;
            }

            ToolCalls = builders.Values
                                .OrderBy(b => b.Index)
                                .Select
                                (b => new ToolCall
                                    {
                                        ID        = b.ID   ?? string.Empty,
                                        Name      = b.Name ?? string.Empty,
                                        Arguments = b.ArgumentsBuilder.ToString()
                                    }
                                )
                                .ToList();
        }

        private sealed class Builder
        {
            public int           Index            { get; set; }
            public string?       ID               { get; set; }
            public string?       Name             { get; set; }
            public StringBuilder ArgumentsBuilder { get; } = new();
        }
    }

    #region Tool Definitions

    private abstract class ChatTool
    {
        public abstract string   Name        { get; }
        public abstract string   Description { get; }
        public abstract JObject? Parameters  { get; }

        public abstract Task<string> ExecuteAsync(JObject args, ToolExecutionContext context);

        public object ToAPIFormat() => new
        {
            type = "function",
            function = new
            {
                name        = Name,
                description = Description,
                parameters  = (object?)Parameters
            }
        };
    }

    private sealed class GetPlayerInfoTool : ChatTool
    {
        public const string ToolName = "get_player_info";

        public override string Name        => ToolName;
        public override string Description => "获取当前角色的基本信息, 包括姓名、职业、等级、大区";

        public override JObject? Parameters => null;

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"- 姓名: {LocalPlayerState.Name}");
            sb.AppendLine($"- 职业: {LocalPlayerState.ClassJobData.Name}");
            sb.AppendLine($"- 等级: {LocalPlayerState.CurrentLevel}");
            sb.AppendLine($"- 大区: {GameState.HomeWorldData.Name}");
            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }

    private sealed class GetCurrentLocationTool : ChatTool
    {
        public const string ToolName = "get_current_location";

        public override string Name        => ToolName;
        public override string Description => "获取当前所在地图、天气等信息";

        public override JObject? Parameters => null;

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"- 当前服务器: {GameState.CurrentWorldData.Name}");
            sb.AppendLine($"- 当前地图: {GameState.TerritoryTypeData.ExtractPlaceName()} (Type: {GameState.TerritoryIntendedUse})");
            sb.AppendLine($"- 当前天气: {GameState.WeatherData.Name}");
            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }

    private sealed class GetGameTimeTool : ChatTool
    {
        public const string ToolName = "get_game_time";

        public override string Name        => ToolName;
        public override string Description => "获取当前服务器时间和艾欧泽亚时间 (ET)";

        public override JObject? Parameters => null;

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"- 服务器时间: {StandardTimeManager.Instance().Now:yyyy/MM/dd HH:mm}");
            sb.AppendLine($"- 艾欧泽亚时间: {EorzeaDate.GetTime()}");
            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }

    private sealed class GetPlayerStatusTool : ChatTool
    {
        public const string ToolName = "get_player_status";

        public override string   Name        => ToolName;
        public override string   Description => "获取当前角色的活跃状态效果 (如战斗中、坐骑上等)";
        public override JObject? Parameters  => null;

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var conditions = Enum.GetValues<ConditionFlag>()
                                 .Where(x => DService.Instance().Condition[x])
                                 .ToList();

            if (conditions.Count == 0)
                return Task.FromResult("当前无特殊状态");

            return Task.FromResult($"当前状态: {string.Join(", ", conditions)}");
        }
    }

    private sealed class GetItemQuantityTool : ChatTool
    {
        public const string ToolName = "get_item_quantity";

        public override string Name        => ToolName;
        public override string Description => "根据物品名称查询背包中的持有数量";

        public override JObject Parameters => new()
        {
            ["type"]                 = "object",
            ["properties"]           = new JObject { ["item_name"] = new JObject { ["type"] = "string", ["description"] = "要查询的物品名称" } },
            ["required"]             = new JArray("item_name"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var itemName = args["item_name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(itemName))
                return Task.FromResult("错误: 缺少 item_name 参数");

            var sheets = LuminaGetter.Get<Item>();
            if (sheets == null)
                return Task.FromResult("错误: 无法访问物品数据");

            var item = FindItem(sheets, itemName);

            if (item == null)
            {
                var fuzzy = FindFuzzyItems(sheets, itemName);
                if (fuzzy.Count == 0)
                    return Task.FromResult($"未找到物品 '{itemName}'");

                var names = string.Join(", ", fuzzy.Select(i => i.Name.ToString()));
                return Task.FromResult($"未找到精确匹配 '{itemName}', 你是否想找: {names}");
            }

            var count  = LocalPlayerState.GetItemCount(item.Value.RowId);
            var result = $"{item.Value.Name} x{count}";

            return Task.FromResult(result);
        }

        private static Item? FindItem(ExcelSheet<Item> sheets, string name)
        {
            foreach (var item in sheets)
            {
                if (item.RowId == 0) continue;
                var itemName = item.Name.ToString();
                if (string.IsNullOrEmpty(itemName)) continue;
                if (itemName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        private static List<Item> FindFuzzyItems(ExcelSheet<Item> sheets, string name)
        {
            const int maxResults = 5;
            var       results    = new List<Item>();

            foreach (var item in sheets)
            {
                if (item.RowId == 0) continue;
                var itemName = item.Name.ToString();
                if (string.IsNullOrEmpty(itemName)) continue;

                if (itemName.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(item);
                    if (results.Count >= maxResults) break;
                }
            }

            return results;
        }
    }

    private sealed class GetClassLevelTool : ChatTool
    {
        public const string ToolName = "get_class_level";

        public override string Name        => ToolName;
        public override string Description => "查询指定职业的等级";

        public override JObject Parameters => new()
        {
            ["type"]                 = "object",
            ["properties"]           = new JObject { ["class_name"] = new JObject { ["type"] = "string", ["description"] = "职业名称, 如 '骑士'、'Paladin'、'PLD'" } },
            ["required"]             = new JArray("class_name"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var className = args["class_name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(className))
                return Task.FromResult("错误: 缺少 class_name 参数");

            var classJob = FindClassJob(className);
            if (classJob == null)
                return Task.FromResult($"未找到职业 '{className}'");

            var level = LocalPlayerState.GetClassJobLevel(classJob.Value.RowId);
            return Task.FromResult($"{classJob.Value.Name} 等级: {level}");
        }

        private static ClassJob? FindClassJob(string name)
        {
            foreach (var job in LuminaGetter.Get<ClassJob>())
            {
                if (job.RowId == 0) continue;

                var jobName = job.Name.ToString();
                var jobAbbr = job.Abbreviation.ToString();

                if (string.IsNullOrEmpty(jobName)) continue;

                if (jobName.Equals(name, StringComparison.OrdinalIgnoreCase)   ||
                    jobAbbr.Equals(name, StringComparison.OrdinalIgnoreCase)   ||
                    jobName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    jobAbbr.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return job;
            }

            return null;
        }
    }

    private sealed class GetPartyInfoTool : ChatTool
    {
        public const string ToolName = "get_party_info";

        public override string Name        => ToolName;
        public override string Description => "获取当前小队成员列表 (姓名、职业)";

        public override JObject? Parameters => null;

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var list = DService.Instance().PartyList;

            if (list.Length <= 1)
                return Task.FromResult("当前未组队或仅有自己一人在小队中");

            var sb = new StringBuilder();

            for (var i = 0; i < list.Length; i++)
            {
                var member = list[i];
                if (member == null) continue;

                var name   = member.Name.TextValue;
                var world  = LuminaWrapper.GetWorldName(member.World.RowId);
                var job    = LuminaWrapper.GetJobName(member.ClassJob.RowId);
                var prefix = member.EntityId == LocalPlayerState.EntityID ? " (你)" : "";

                sb.AppendLine($"- [{i + 1}] {name}@{world} {job}{prefix}");
            }

            if (sb.Length == 0)
                return Task.FromResult("无法获取小队信息");

            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }

    private sealed class ReadPastMessagesTool : ChatTool
    {
        public const string TOOL_NAME = "read_past_messages";

        public override string Name        => TOOL_NAME;
        public override string Description => "读取上一次模块生命周期中与某个用户的聊天记录";

        public override JObject Parameters => new()
        {
            ["type"]                 = "object",
            ["properties"]           = new JObject { ["user_key"] = new JObject { ["type"] = "string", ["description"] = "用户名, 如 'Name@World'" } },
            ["required"]             = new JArray("user_key"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var userKey = args["user_key"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(userKey))
                return Task.FromResult("错误: 缺少 user_key 参数");

            var conv = context.ConversationStore.GetOrLoad(userKey);
            if (conv.RecentTurns is not { Count: > 0 })
                return Task.FromResult($"未找到与 {userKey} 的历史记录");

            var sb    = new StringBuilder();
            var count = Math.Min(conv.RecentTurns.Count, 20);

            for (var i = conv.RecentTurns.Count - count; i < conv.RecentTurns.Count; i++)
            {
                var msg = conv.RecentTurns[i];
                msg.LocalTime ??= msg.Timestamp.ToUTCDateTimeFromUnixSeconds().ToLocalTime();
                sb.AppendLine($"[{msg.LocalTime:MM/dd HH:mm}] {msg.Name}: {msg.Text}");
            }

            return Task.FromResult(sb.ToString().TrimEnd());
        }
    }

    private sealed class SendMessageTool : ChatTool
    {
        public const string TOOL_NAME = "send_message";

        public override string Name        => TOOL_NAME;
        public override string Description => "向对话对象发送一条消息, 可通过 channel 参数指定频道";

        public override JObject Parameters => new()
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["content"] = new JObject { ["type"] = "string", ["description"] = "要发送的消息内容" },
                ["channel"] = new JObject { ["type"] = "string", ["description"] = "发送频道: tell / say / yell / shout / party / fc" }
            },
            ["required"]             = new JArray("content"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var content = args["content"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult("错误: 消息内容为空");

            context.SendMessageCalled = true;

            var ctx = context.ReplyContext;
            if (string.IsNullOrWhiteSpace(ctx.Target))
                return Task.FromResult(content);

            var channel = args["channel"]?.Value<string>()?.ToLowerInvariant() ?? ctx.DefaultChannel;

            if (channel != "tell" && ctx.ChannelCommands.TryGetValue(channel, out var command))
            {
                ChatManager.Instance().SendMessage($"{command} {content}");
                ctx.SentMessage = content;
                return Task.FromResult($"已通过 /{channel} 发送");
            }

            SendReply(ctx.OriginalType, ctx.Target, content);
            ctx.SentMessage = content;
            return Task.FromResult($"已回复 {ctx.Target}");
        }
    }

    #endregion
}
