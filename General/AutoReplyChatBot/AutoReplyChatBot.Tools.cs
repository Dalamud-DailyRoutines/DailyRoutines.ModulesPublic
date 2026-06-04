using System.Collections;
using System.Collections.Frozen;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
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
            var result = await DService.Instance().Framework.RunOnTick
                         (async () =>
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
                             }
                         ).ConfigureAwait(false);

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
        private readonly Dictionary<int, Builder> builders         = [];
        private readonly StringBuilder            reasoningBuilder = new();

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

    #region ExdSchemaTool

    private sealed class ExdSchemaTool : ChatTool
    {
        public const string TOOL_NAME = "exd_schema";

        private static readonly FrozenDictionary<string, Type> SheetTypes;

        static ExdSchemaTool()
        {
            var dict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var assembly = typeof(Emote).Assembly;

                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.GetInterfaces().Any
                        (i =>
                             i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IExcelRow<>)
                        ))
                        dict[type.Name] = type;
                }
            }
            catch
            {
                // 降级: 静默失败
            }

            SheetTypes = dict.ToFrozenDictionary();
        }

        public override string Name        => TOOL_NAME;
        public override string Description => "搜索游戏数据表或查看表结构. action=search 模糊搜表名; action=schema 查看指定表的字段定义";

        public override JObject Parameters => new()
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["enum"]        = new JArray("search", "schema"), ["description"] = "操作类型" },
                ["sheet"]  = new JObject { ["type"] = "string", ["description"] = "action=schema 时必填, 表名" },
                ["query"]  = new JObject { ["type"] = "string", ["description"] = "action=search 时可选, 模糊搜索表名" }
            },
            ["required"]             = new JArray("action"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var action = args["action"]?.Value<string>()?.ToLowerInvariant();

            return action switch
            {
                "search" => Task.FromResult(DoSearch(args["query"]?.Value<string>())),
                "schema" => Task.FromResult(DoSchema(args["sheet"]?.Value<string>())),
                _        => Task.FromResult($"未知 action: {action}, 可用: search, schema")
            };
        }

        private static string DoSearch(string? query)
        {
            if (SheetTypes.Count == 0)
                return "无法访问游戏数据表列表";

            var matches = string.IsNullOrWhiteSpace(query)
                              ? SheetTypes.Keys.Take(50).ToList()
                              : SheetTypes.Keys.Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
                return $"未找到包含 '{query}' 的表";

            var sb = new StringBuilder();
            sb.AppendLine($"匹配到 {matches.Count} 张表:");

            foreach (var name in matches.Take(30))
            {
                var type      = SheetTypes[name];
                var propCount = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
                sb.AppendLine($"- {name} ({propCount} 字段)");
            }

            if (matches.Count > 30)
                sb.AppendLine($"... 及另外 {matches.Count - 30} 张");

            return sb.ToString().TrimEnd();
        }

        private static string DoSchema(string? sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
                return "错误: 缺少 sheet 参数";

            if (!SheetTypes.TryGetValue(sheetName, out var type))
            {
                var similar = SheetTypes.Keys
                                        .Where(n => n.Contains(sheetName, StringComparison.OrdinalIgnoreCase))
                                        .Take(5)
                                        .ToList();
                return similar.Count > 0
                           ? $"未找到表 '{sheetName}', 你是否想找: {string.Join(", ", similar)}"
                           : $"未找到表 '{sheetName}'";
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var sb    = new StringBuilder();
            sb.AppendLine($"{sheetName} | 共 {props.Length} 字段");

            foreach (var prop in props)
            {
                var propType = prop.PropertyType;
                var typeName = GetFriendlyTypeName(propType);
                sb.AppendLine($"- {prop.Name}: {typeName}");
            }

            return sb.ToString().TrimEnd();
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(RowRef<>))
                    return $"Link→{type.GetGenericArguments()[0].Name}";
                if (def == typeof(ExcelSheet<>))
                    return $"Sheet<{type.GetGenericArguments()[0].Name}>";
                return $"{def.Name.Replace("`1", "")}<{type.GetGenericArguments()[0].Name}>";
            }

            if (type == typeof(ReadOnlySeString)) return "string";
            if (type == typeof(uint) || type == typeof(int) || type == typeof(ushort) || type == typeof(byte) || type == typeof(long) || type == typeof(ulong))
                return "integer";
            if (type == typeof(float) || type == typeof(double)) return "float";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(string)) return "string";
            if (type == typeof(Vector2)) return "Vector2";
            if (type == typeof(Vector3)) return "Vector3";

            if (type.IsArray && type.GetArrayRank() == 1)
            {
                var elemType = type.GetElementType()!;
                if (elemType == typeof(byte) || elemType == typeof(short) || elemType == typeof(ushort) || elemType == typeof(int) || elemType == typeof(uint))
                    return $"{GetFriendlyTypeName(elemType)}[]";
            }

            if (type.IsEnum) return "enum";
            if (type is { IsValueType: true, IsPrimitive: true }) return "scalar";

            return type.Name;
        }
    }

    #endregion

    #region ExdQueryTool

    private sealed class ExdQueryTool : ChatTool
    {
        public const string TOOL_NAME = "exd_query";

        private static readonly FrozenDictionary<string, Type> SheetTypes;

        static ExdQueryTool()
        {
            // 复用 ExdSchemaTool 的静态构造逻辑, 用同一个字典
            var dict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var assembly = typeof(Emote).Assembly;

                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.GetInterfaces().Any
                        (i =>
                             i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IExcelRow<>)
                        ))
                        dict[type.Name] = type;
                }
            }
            catch
            {
                // ignored
            }

            SheetTypes = dict.ToFrozenDictionary();
        }

        public override string Name        => TOOL_NAME;
        public override string Description => "查询游戏数据表. action=query 按 display_field 模糊搜索; action=get_row 按 row_id 获取单行; action=follow 沿 Link 字段跳转到关联行";

        public override JObject Parameters => new()
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["action"] = new JObject { ["type"] = "string", ["enum"]         = new JArray("query", "get_row", "follow"), ["description"] = "操作类型" },
                ["sheet"]  = new JObject { ["type"] = "string", ["description"]  = "表名" },
                ["query"]  = new JObject { ["type"] = "string", ["description"]  = "action=query 时, 按表的主显示字段模糊搜索" },
                ["row_id"] = new JObject { ["type"] = "integer", ["description"] = "action=get_row/follow 时目标行的 RowId" },
                ["field"]  = new JObject { ["type"] = "string", ["description"]  = "action=follow 时, 要跟随的字段名" },
                ["filter"] = new JObject { ["type"] = "string", ["description"]  = "可选简单筛选, 格式 `FieldName op Value`, 如 `Order >= 5000`" },
                ["fields"] = new JObject { ["type"] = "string", ["description"]  = "可选, 逗号分隔要返回的字段, 留空返回所有" },
                ["limit"]  = new JObject { ["type"] = "integer", ["default"]     = 10, ["description"] = "最大返回行数" }
            },
            ["required"]             = new JArray("action", "sheet"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var action = args["action"]?.Value<string>()?.ToLowerInvariant();
            var sheet  = args["sheet"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(sheet))
                return Task.FromResult("错误: 缺少 sheet 参数");

            if (!SheetTypes.TryGetValue(sheet, out var sheetType))
            {
                var similar = SheetTypes.Keys
                                        .Where(n => n.Contains(sheet, StringComparison.OrdinalIgnoreCase))
                                        .Take(5)
                                        .ToList();
                return Task.FromResult
                (
                    similar.Count > 0
                        ? $"未找到表 '{sheet}', 你是否想找: {string.Join(", ", similar)}"
                        : $"未找到表 '{sheet}'"
                );
            }

            try
            {
                return action switch
                {
                    "query"   => Task.FromResult(DoQuery(sheetType, args)),
                    "get_row" => Task.FromResult(DoGetRow(sheetType, args)),
                    "follow"  => Task.FromResult(DoFollow(sheetType, args)),
                    _         => Task.FromResult($"未知 action: {action}, 可用: query, get_row, follow")
                };
            }
            catch (Exception ex)
            {
                return Task.FromResult($"查询失败: {ex.Message}");
            }
        }

        private static string DoQuery(Type sheetType, JObject args)
        {
            var query  = args["query"]?.Value<string>() ?? "";
            var limit  = args["limit"]?.Value<int>()    ?? 10;
            var fields = args["fields"]?.Value<string>();
            var filter = args["filter"]?.Value<string>();

            var sheet = GetSheet(sheetType);
            if (sheet == null) return $"无法加载表 {sheetType.Name}";

            var props       = sheetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var displayProp = FindDisplayProperty(props);

            var results = ((IEnumerable)sheet).Cast<object>();

            if (!string.IsNullOrWhiteSpace(query) && displayProp != null)
            {
                results = results.Where
                (r =>
                    {
                        var val = displayProp.GetValue(r)?.ToString() ?? "";
                        return val.Contains(query, StringComparison.OrdinalIgnoreCase);
                    }
                );
            }

            // 简单 filter: "FieldName op Value" 如 "Order >= 5000"
            if (!string.IsNullOrWhiteSpace(filter))
                results = ApplySimpleFilter(results, props, filter);

            var rows = results.Take(limit).ToList();

            if (rows.Count == 0)
                return query != "" ? $"未找到匹配 '{query}' 的行" : "表为空";

            var sb = new StringBuilder();
            sb.AppendLine($"{sheetType.Name}: 返回 {rows.Count} 行 (共 {((IEnumerable)sheet).Cast<object>().Count()} 行)");

            var fieldSet    = ParseFieldSet(fields, props);
            var isTruncated = fields != null && fieldSet.Count < props.Length;

            foreach (var row in rows)
            {
                var rowID = GetRowID(row);
                sb.AppendLine
                (
                    isTruncated
                        ? $"\n[#{rowID}]"
                        : $"\n[#{rowID}] ---"
                );

                foreach (var prop in fieldSet)
                {
                    var val = FormatValue(prop.GetValue(row));
                    sb.AppendLine($"  {prop.Name}: {val}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string DoGetRow(Type sheetType, JObject args)
        {
            var rowID = args["row_id"]?.Value<uint>();
            if (rowID == null)
                return "错误: get_row 需要 row_id 参数";

            var fields = args["fields"]?.Value<string>();

            var method = typeof(LuminaGetter)
                         .GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .First
                         (m => m is { Name: "GetRow", IsGenericMethod: true } &&
                               m.GetParameters().Length           == 1        &&
                               m.GetParameters()[0].ParameterType == typeof(uint)
                         );
            var typed        = method.MakeGenericMethod(sheetType);
            var nullableType = typeof(Nullable<>).MakeGenericType(sheetType);

            var result = typed.Invoke(null, [rowID.Value]);
            if (result == null || !((bool?)nullableType.GetProperty("HasValue")?.GetValue(result) ?? false))
                return $"未找到 {sheetType.Name} #{rowID}";

            var row = nullableType.GetProperty("Value")?.GetValue(result);
            if (row == null) return $"未找到 {sheetType.Name} #{rowID}";

            var props    = sheetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var fieldSet = ParseFieldSet(fields, props);

            var sb = new StringBuilder();
            sb.AppendLine($"{sheetType.Name} #{rowID}:");

            foreach (var prop in fieldSet)
            {
                var val = FormatValue(prop.GetValue(row));
                sb.AppendLine($"  {prop.Name}: {val}");
            }

            return sb.ToString().TrimEnd();
        }

        private static string DoFollow(Type sheetType, JObject args)
        {
            var rowID = args["row_id"]?.Value<uint>();
            var field = args["field"]?.Value<string>();
            if (rowID == null) return "错误: follow 需要 row_id 参数";
            if (field == null) return "错误: follow 需要 field 参数";

            // 获取源行
            var getRowMethod = typeof(LuminaGetter)
                               .GetMethods(BindingFlags.Public | BindingFlags.Static)
                               .First
                               (m =>
                                   {
                                       if (m.Name != "GetRow" ||
                                           !m.IsGenericMethod)
                                           return false;
                                       return m.GetParameters().Length           == 1 &&
                                              m.GetParameters()[0].ParameterType == typeof(uint);
                                   }
                               );
            var typedGetRow  = getRowMethod.MakeGenericMethod(sheetType);
            var nullableType = typeof(Nullable<>).MakeGenericType(sheetType);
            var result       = typedGetRow.Invoke(null, [rowID.Value]);
            if (result == null || !((bool?)nullableType.GetProperty("HasValue")?.GetValue(result) ?? false))
                return $"未找到 {sheetType.Name} #{rowID} 作为源行";

            var row = nullableType.GetProperty("Value")?.GetValue(result);
            if (row == null) return $"未找到 {sheetType.Name} #{rowID}";

            // 读取目标字段的值
            var prop = sheetType.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);

            if (prop == null)
            {
                var similar = sheetType.GetProperties().Select(p => p.Name)
                                       .Where(n => n.Contains(field, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
                return similar.Count > 0
                           ? $"字段 '{field}' 不存在, 你是否想找: {string.Join(", ", similar)}"
                           : $"字段 '{field}' 不存在于 {sheetType.Name}";
            }

            var rawValue = prop.GetValue(row);

            // 处理 RowRef<T> 类型
            var propType = prop.PropertyType;

            if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(RowRef<>))
            {
                var targetType  = propType.GetGenericArguments()[0];
                var targetRowID = (uint?)propType.GetProperty("RowId")?.GetValue(rawValue) ?? 0;

                if (targetRowID == 0)
                    return $"{field} → empty link (RowId=0)";

                var followGetRow   = getRowMethod.MakeGenericMethod(targetType);
                var followNullable = typeof(Nullable<>).MakeGenericType(targetType);
                var followResult   = followGetRow.Invoke(null, [targetRowID]);
                if (followResult == null || !((bool?)followNullable.GetProperty("HasValue")?.GetValue(followResult) ?? false))
                    return $"{field} → {targetType.Name} #{targetRowID} (未找到)";

                var followRow = followNullable.GetProperty("Value")?.GetValue(followResult);
                var sb        = new StringBuilder();
                sb.AppendLine($"{sheetType.Name}#{rowID}.{field} → {targetType.Name} #{targetRowID}:");

                var targetProps = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(10);

                foreach (var tp in targetProps)
                {
                    var val = FormatValue(tp.GetValue(followRow));
                    sb.AppendLine($"  {tp.Name}: {val}");
                }

                var totalProps = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
                if (totalProps > 10)
                    sb.AppendLine($"  ... (另有 {totalProps - 10} 字段, 用 get_row 查看完整)");

                return sb.ToString().TrimEnd();
            }

            // 简单类型 (uint, int, bool, string)
            return $"{sheetType.Name}#{rowID}.{field} = {FormatValue(rawValue)}";
        }

        #region Reflection Helpers

        private static object? GetSheet(Type sheetType)
        {
            var method = typeof(LuminaGetter)
                         .GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .First(m => m is { Name: "Get", IsGenericMethod: true } && m.GetParameters().Length == 0);
            var typed = method.MakeGenericMethod(sheetType);
            return typed.Invoke(null, null);
        }

        private static PropertyInfo? FindDisplayProperty(PropertyInfo[] props)
        {
            // 优先 Name, 其次 Command/Singular/DisplayField, 否则第一个 string 属性
            var preferred = new[] { "Name", "Command", "Singular", "DisplayField", "Title", "PlaceName", "Abbreviation" };

            foreach (var name in preferred)
            {
                var p = props.FirstOrDefault(x => x.Name == name && x.PropertyType == typeof(ReadOnlySeString));
                if (p != null) return p;
            }

            return props.FirstOrDefault
            (p =>
                 p.PropertyType == typeof(ReadOnlySeString) || p.PropertyType == typeof(string)
            );
        }

        private static List<PropertyInfo> ParseFieldSet(string? fields, PropertyInfo[] props)
        {
            if (string.IsNullOrWhiteSpace(fields))
                return [.. props];

            var names  = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<PropertyInfo>();

            foreach (var name in names)
            {
                var p = props.FirstOrDefault
                (x =>
                     x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                );
                if (p != null) result.Add(p);
            }

            return result.Count > 0 ? result : [.. props.Take(10)];
        }

        private static uint GetRowID(object row)
        {
            var prop = row.GetType().GetProperty("RowId");
            return (uint)(prop?.GetValue(row) ?? 0);
        }

        private static string FormatValue(object? value)
        {
            if (value == null) return "(null)";

            var type = value.GetType();

            if (value is ReadOnlySeString ros) return ros.ToString();
            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";

            // RowRef<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RowRef<>))
            {
                var rowID = (uint?)type.GetProperty("RowId")?.GetValue(value) ?? 0;
                var name  = type.GetGenericArguments()[0].Name;
                return rowID == 0 ? "(empty)" : $"{name}#{rowID}";
            }

            // 数组
            if (type.IsArray)
            {
                var arr   = (Array)value;
                var elems = new List<string>();
                for (var i = 0; i < Math.Min(arr.Length, 8); i++)
                    elems.Add(arr.GetValue(i)?.ToString() ?? "0");
                if (arr.Length > 8) elems.Add("...");
                return $"[{string.Join(", ", elems)}]";
            }

            if (type.IsEnum) return $"{value} ({(int)value})";
            if (value is float f) return $"{f:F2}";
            if (value is double d) return $"{d:F2}";

            return value.ToString() ?? "";
        }

        private static IEnumerable<object> ApplySimpleFilter(IEnumerable<object> rows, PropertyInfo[] props, string filter)
        {
            // 解析 "FieldName op Value"
            var parts = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return rows;

            var fieldName = parts[0];
            var op        = parts[1];
            var valueStr  = string.Join(" ", parts.Skip(2));

            var prop = props.FirstOrDefault
            (p =>
                 p.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)
            );
            if (prop == null) return rows;

            return rows.Where
            (r =>
                {
                    var raw = prop.GetValue(r);
                    if (raw == null) return false;

                    return op switch
                    {
                        ">="        => CompareValues(raw, valueStr) >= 0,
                        "<="        => CompareValues(raw, valueStr) <= 0,
                        ">"         => CompareValues(raw, valueStr) > 0,
                        "<"         => CompareValues(raw, valueStr) < 0,
                        "==" or "=" => CompareValues(raw, valueStr) == 0,
                        "!="        => CompareValues(raw, valueStr) != 0,
                        _           => true
                    };
                }
            );
        }

        private static int CompareValues(object raw, string valueStr)
        {
            if (raw is uint u) return u.CompareTo(uint.TryParse(valueStr, out var uv) ? uv : 0);
            if (raw is int i) return i.CompareTo(int.TryParse(valueStr, out var iv) ? iv : 0);
            if (raw is ushort us) return us.CompareTo(ushort.TryParse(valueStr, out var usv) ? usv : 0);
            if (raw is byte b) return b.CompareTo(byte.TryParse(valueStr, out var bv) ? bv : 0);
            if (raw is float f) return f.CompareTo(float.TryParse(valueStr, out var fv) ? fv : 0);
            if (raw is bool bl) return bl.CompareTo(bool.TryParse(valueStr, out var blv) && blv);
            if (raw is ReadOnlySeString ros) return string.Compare(ros.ToString(), valueStr, StringComparison.OrdinalIgnoreCase);
            if (raw is string s) return string.Compare(s,                          valueStr, StringComparison.OrdinalIgnoreCase);

            // RowRef<T>: compare by RowId
            var rowIDProp = raw.GetType().GetProperty("RowId");

            if (rowIDProp != null)
            {
                var rowID = (uint?)rowIDProp.GetValue(raw) ?? 0;
                return rowID.CompareTo(uint.TryParse(valueStr, out var rv) ? rv : 0);
            }

            return 0;
        }

        #endregion
    }

    #endregion

    #region GetGameStateTool

    private sealed class GetGameStateTool : ChatTool
    {
        public const string TOOL_NAME = "get_game_state";

        public override string Name        => TOOL_NAME;
        public override string Description => "查询游戏运行时状态. include 数组指定要查的类别: self, location, conditions, party, nearby, inventory";

        public override JObject Parameters => new()
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["include"] = new JObject
                {
                    ["type"]        = "array",
                    ["items"]       = new JObject { ["type"] = "string" },
                    ["description"] = "要查询的类别: self, location, conditions, party, nearby, inventory"
                },
                ["nearby_radius"] = new JObject { ["type"] = "integer", ["default"] = 50, ["description"] = "附近玩家搜索半径 (yalms)" },
                ["max_nearby"]    = new JObject { ["type"] = "integer", ["default"] = 20, ["description"] = "最大返回附近玩家数" },
                ["item_queries"] = new JObject
                {
                    ["type"]        = "array",
                    ["items"]       = new JObject { ["type"] = "string" },
                    ["description"] = "可选, 要查数量的物品名称列表"
                }
            },
            ["required"]             = new JArray("include"),
            ["additionalProperties"] = false
        };

        public override Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var include = args["include"]?.ToObject<string[]>();
            if (include == null || include.Length == 0)
                return Task.FromResult("错误: include 不能为空, 至少指定一个类别");

            var sb = new StringBuilder();

            foreach (var category in include)
            {
                switch (category.ToLowerInvariant())
                {
                    case "self":
                        sb.AppendLine("[Self]");
                        sb.AppendLine(BuildSelf());
                        sb.AppendLine();
                        break;
                    case "location":
                        sb.AppendLine("[Location]");
                        sb.AppendLine(BuildLocation());
                        sb.AppendLine();
                        break;
                    case "conditions":
                        sb.AppendLine("[Conditions]");
                        sb.AppendLine(BuildConditions());
                        sb.AppendLine();
                        break;
                    case "party":
                        sb.AppendLine("[Party]");
                        sb.AppendLine(BuildParty());
                        sb.AppendLine();
                        break;
                    case "nearby":
                        var radius = args["nearby_radius"]?.Value<int>() ?? 50;
                        var max    = args["max_nearby"]?.Value<int>()    ?? 20;
                        sb.AppendLine("[Nearby]");
                        sb.AppendLine(BuildNearby(radius, max));
                        sb.AppendLine();
                        break;
                    case "inventory":
                        var itemQueries = args["item_queries"]?.ToObject<string[]>();
                        sb.AppendLine("[Inventory]");
                        sb.AppendLine(BuildInventory(itemQueries));
                        sb.AppendLine();
                        break;
                    default:
                        sb.AppendLine($"未知类别: {category}");
                        break;
                }
            }

            return Task.FromResult(sb.ToString().TrimEnd());
        }

        private static string BuildSelf()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {LocalPlayerState.Name}");
            sb.AppendLine($"ClassJob: {LocalPlayerState.ClassJobData.Name} (ID={LocalPlayerState.ClassJob})");
            sb.AppendLine($"Level: {LocalPlayerState.CurrentLevel} / Max: {LocalPlayerState.MaxLevel}");
            sb.AppendLine($"World: {GameState.HomeWorldData.Name} (当前: {GameState.CurrentWorldData.Name})");
            sb.AppendLine($"DataCenter: {GameState.HomeDataCenterData.Name}");

            var obj = LocalPlayerState.Object;

            if (obj != null)
            {
                sb.AppendLine($"HP: {obj.CurrentHp}/{obj.MaxHp}");
                sb.AppendLine($"MP: {obj.CurrentMp}/{obj.MaxMp}");
                sb.AppendLine($"GP: {obj.CurrentGp}/{obj.MaxGp}");
                sb.AppendLine($"CP: {obj.CurrentCp}/{obj.MaxCp}");
                sb.AppendLine($"Position: ({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})");
                sb.AppendLine($"Target: {obj.TargetObject?.Name                 ?? "(none)"}");
                sb.AppendLine($"CurrentMount: {obj.CurrentMount?.Value.Singular ?? "(none)"}");
                sb.AppendLine($"OnlineStatus: {obj.OnlineStatus.Value.Name}");
                sb.AppendLine($"Sex: {(obj.Sex == 0 ? "Male" : "Female")}");
                sb.AppendLine($"IsDead: {obj.IsDead}");
                sb.AppendLine($"IsTargetable: {obj.IsTargetable}");
            }

            sb.AppendLine($"Commendations: {LocalPlayerState.Commendations}");
            sb.AppendLine($"IsInAnyParty: {LocalPlayerState.IsInAnyParty}");
            sb.AppendLine($"IsMoving: {LocalPlayerState.Instance().IsMoving}");
            sb.AppendLine($"IsWalking: {LocalPlayerState.IsWalking}");

            return sb.ToString().TrimEnd();
        }

        private static string BuildLocation()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TerritoryType: {GameState.TerritoryTypeData.ExtractPlaceName()} ({GameState.TerritoryType})");
            sb.AppendLine($"Map: {GameState.MapData.PlaceName.Value.Name}");
            sb.AppendLine($"Weather: {GameState.WeatherData.Name}");
            sb.AppendLine($"World: {GameState.CurrentWorldData.Name}");
            sb.AppendLine($"IsInInstance: {GameState.IsInInstanceArea}");
            sb.AppendLine($"IsInPVP: {GameState.IsInPVPArea}");
            sb.AppendLine($"IsInPVEArea: {GameState.IsInPVEActonZone}");
            sb.AppendLine($"IsLoggedIn: {GameState.IsLoggedIn}");

            if (GameState.IsFlagMarkerSet)
            {
                var flag = GameState.FlagMarker;
                sb.AppendLine($"FlagMarker: ({flag.XFloat:F1}, {flag.YFloat:F1})");
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildConditions()
        {
            var conditions = Enum.GetValues<ConditionFlag>()
                                 .Where(x => DService.Instance().Condition[x])
                                 .ToList();

            if (conditions.Count == 0)
                return "当前无特殊状态";

            return string.Join(", ", conditions);
        }

        private static string BuildParty()
        {
            var list = DService.Instance().PartyList;

            if (list.Length <= 1)
                return "当前未组队或仅有自己一人在小队中";

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

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "无法获取小队信息";
        }

        private static string BuildNearby(int radius, int max)
        {
            var selfObj = LocalPlayerState.Object;
            if (selfObj == null)
                return "无法获取自身位置";

            var sb    = new StringBuilder();
            var count = 0;

            // ObjectTable.SearchObjects 需要 Predicate<IGameObject>
            var nearby = DService.Instance().ObjectTable.SearchObjects
            (obj =>
                {
                    if (obj is not IPlayerCharacter pc) return false;
                    if (pc.EntityID == LocalPlayerState.EntityID) return false;
                    if (count       >= max) return false;

                    var dist = Vector3.Distance(selfObj.Position, pc.Position);
                    return dist <= radius;
                }
            );

            foreach (var obj in nearby)
            {
                if (obj is not IPlayerCharacter pc) continue;
                count++;
                var dist = Vector3.Distance(selfObj.Position, pc.Position);
                sb.AppendLine($"- {pc.Name} | Lv{pc.Level} {pc.ClassJob.Value.Abbreviation} | {pc.HomeWorld.Value.Name} | {dist:F1}y");
            }

            if (count == 0)
                return $"半径 {radius} yalm 内未发现其他玩家";

            return sb.ToString().TrimEnd();
        }

        private static unsafe string BuildInventory(string[]? itemQueries)
        {
            var sb = new StringBuilder();

            // 背包空格
            var isFull = Inventories.Player.IsFull(10);
            sb.AppendLine($"BagSlots: {(isFull ? "即将满" : "充足")}");

            // 装备耐久
            var minDura   = byte.MaxValue;
            var totalDura = 0;
            var slotCount = 0;

            var equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);

            if (equipped != null && equipped->IsLoaded)
            {
                for (var i = 0; i < equipped->Size; i++)
                {
                    var slot = equipped->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    var cond                    = (byte)slot->Condition;
                    if (cond < minDura) minDura = cond;
                    totalDura += cond;
                    slotCount++;
                }
            }

            if (slotCount > 0)
            {
                var avgDura = totalDura / slotCount;
                sb.AppendLine($"EquipmentDurability: min={minDura}% avg={avgDura}%");
            }

            // Gil
            var gil = InventoryManager.Instance()->GetGil();
            sb.AppendLine($"Gil: {gil:N0}");

            // 指定物品数量
            if (itemQueries is { Length: > 0 })
            {
                foreach (var q in itemQueries)
                {
                    if (string.IsNullOrWhiteSpace(q)) continue;
                    var item = FindItemByName(q);

                    if (item == null)
                    {
                        sb.AppendLine($"{q}: 未找到");
                        continue;
                    }

                    var count = LocalPlayerState.GetItemCount(item.Value.RowId);
                    sb.AppendLine($"{item.Value.Name}: x{count}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static Item? FindItemByName(string name)
        {
            foreach (var item in LuminaGetter.Get<Item>())
            {
                if (item.RowId == 0) continue;
                var itemName = item.Name.ToString();
                if (string.IsNullOrEmpty(itemName)) continue;
                if (itemName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }
    }

    #endregion

    #region ExecuteCommandTool

    private sealed class ExecuteCommandTool : ChatTool
    {
        public const string TOOL_NAME = "execute_command";

        public override string Name        => TOOL_NAME;
        public override string Description => "执行游戏文本命令. 执行后自动监测系统日志, 指令出错时返回格式化错误信息. 发消息用 /tell 玩家名 内容 格式";

        public override JObject Parameters => new()
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["command"] = new JObject
                {
                    ["type"]        = "string",
                    ["description"] = "游戏命令, 如: /bow, /target 玩家名, /gearset change 1, /follow, /teleport 81, /tell 玩家名 你好"
                },
                ["silent"] = new JObject
                {
                    ["type"]        = "boolean",
                    ["default"]     = true,
                    ["description"] = "true=静默执行(不显示在聊天栏), false=走聊天栏发送"
                }
            },
            ["required"]             = new JArray("command"),
            ["additionalProperties"] = false
        };

        public override async Task<string> ExecuteAsync(JObject args, ToolExecutionContext context)
        {
            var command = args["command"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(command))
                return "错误: command 不能为空";
            
            var trimmed = command.Trim();
            
            if (command.ContainsAny( '\"', '”', '“'))
                return "错误: command 不能包含引号";

            // /tell 路径: 走回复追踪
            if (TryParseTell(trimmed, out var target, out var content))
            {
                if (string.IsNullOrWhiteSpace(target))
                    return "错误: /tell 格式应为 /tell 玩家名@服务器 内容 或 /tell 玩家名 内容";

                context.SendMessageCalled = true;
                SendReply(context.ReplyContext.OriginalType, target, content);
                context.ReplyContext.SentMessage = content;
                return $"已回复 {target}";
            }

            if (!trimmed.StartsWith('/'))
                return "错误: 命令应以 / 开头";

            var errorIDs = new HashSet<uint> { 725, 726, 728, 729, 3802, 3803 };
            var receivedErrors = new List<(uint id, string formatted)>();

            LogMessageManager.PostLogMessageDelegate handler = (id, item) =>
            {
                if (errorIDs.Contains(id))
                    receivedErrors.Add((id, item.ToReadOnlySeString().ToString()));
            };

            LogMessageManager.Instance().RegPost(handler);

            try
            {
                ChatManager.Instance().SendMessage(trimmed);
                
                await Task.Delay(1000).ConfigureAwait(false);

                if (receivedErrors.Count > 0)
                {
                    var combined = string.Join("; ", receivedErrors.Select(e => $"[#{e.id}] {e.formatted}"));
                    return $"指令执行出现问题: {combined}. 建议使用 exd_query 或 exd_schema 查询相关信息后再试";
                }
            }
            finally
            {
                LogMessageManager.Instance().Unreg(handler);
            }

            return $"已执行: {trimmed}";
        }

        private static bool TryParseTell(string command, out string? target, out string? content)
        {
            target  = null;
            content = null;

            if (!command.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase) &&
                !command.StartsWith("/t ",    StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = command[(command[1] == 't' && command.Length > 2 && command[3] == ' ' ? 3 : 6)..].Trim();
            if (string.IsNullOrWhiteSpace(rest)) return false;

            // 格式: /tell 玩家名@服务器 内容 或 /tell 玩家名 内容
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx <= 0) return false;

            target  = rest[..spaceIdx].Trim();
            content = rest[spaceIdx..].Trim();

            return !string.IsNullOrWhiteSpace(content);
        }
    }

    #endregion

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

    #endregion
}
