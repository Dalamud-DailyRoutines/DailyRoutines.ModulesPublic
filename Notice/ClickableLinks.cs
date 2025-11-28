using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility.Raii;
using OmenTools;
using OmenTools.Helpers;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using ImGuiCol = Dalamud.Bindings.ImGui.ImGuiCol;
using ImGuiMouseButton = Dalamud.Bindings.ImGui.ImGuiMouseButton;

namespace ClickableLinks;

public class ClickableLinks : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "Link Records",
        Description = "Record all links and sender information that appear in the chat",
        Category    = ModuleCategories.Notice,
        Author      = ["AZZ"]
    };

    private static readonly Regex UrlRegex = new(
        @"(http|ftp|https)://([\w_-]+(?:(?:\.[\w_-]+)+))([\w.,@?^=%&:/~+#-]*[\w@?^=%&/~+#-])?",
        RegexOptions.Compiled);

    private const int MaxRecords = 50;

    protected override void Init()
    {
        DService.Chat.CheckMessageHandled += OnChatMessage;
        LinkRecordManager.Initialize();
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30000 };
    }

    protected override void Uninit()
    {
        DService.Chat.CheckMessageHandled -= OnChatMessage;
        LinkRecordManager.ClearCache();
    }

    protected override void ConfigUI()
    {
        ImGui.Text("=== 聊天链接记录 ===");
        ImGui.TextWrapped("此模块会记录聊天中出现的所有链接和发送者信息。点击链接可复制到剪贴板。");
        ImGui.Spacing();

        var cacheData = LinkRecordManager.GetCachedData();
        var recordCount = cacheData?.Records.Count ?? 0;

        if (recordCount > 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"已记录 {recordCount} 个链接");

            ImGui.SameLine();
            if (ImGui.Button("清空记录"))
            {
                LinkRecordManager.ClearRecords();
                DService.Chat.Print("✓ 已清空所有链接记录");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // 从最新到最旧显示
            for (var i = recordCount - 1; i >= 0; i--)
            {
                var record = cacheData!.Records[i];
                DrawLinkRecord(record, i);

                if (i > 0)
                    ImGui.Separator();
            }
        }
        else
        {
            ImGui.Spacing();
            ImGui.Spacing();

            var windowSize = ImGui.GetContentRegionAvail();
            var text = "暂无记录的链接";
            var textSize = ImGui.CalcTextSize(text);
            ImGui.SetCursorPosX((windowSize.X - textSize.X) / 2);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 50);
            ImGui.TextDisabled(text);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "提示：在聊天窗口发送包含链接的消息，链接会自动记录在这里");
        }
    }

    private static void DrawLinkRecord(LinkRecord record, int index)
    {
        var timeStr = record.Time.ToString("HH:mm:ss");

        using (ImRaii.PushId(index))
        {
            // 第一行：时间和发送者
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"[{timeStr}]");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), record.SenderName);

            // 第二行：链接（可点击按钮）
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.3f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.5f, 0.7f, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.4f, 0.6f, 0.8f, 0.7f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.7f, 1f, 1f));

            var linkText = record.Url.Length > 80 ? record.Url.Substring(0, 77) + "..." : record.Url;

            // 左键复制，右键打开
            if (ImGui.Button($"{linkText}##link{index}", new Vector2(-1, 0)))
            {
                ImGui.SetClipboardText(record.Url);
                DService.Chat.Print("✓ 链接已复制到剪贴板");
            }

            // 右键菜单
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = record.Url,
                        UseShellExecute = true
                    });
                    DService.Chat.Print("✓ 正在浏览器中打开链接");
                }
                catch (Exception ex)
                {
                    DService.Chat.Print($"✗ 打开链接失败: {ex.Message}");
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"左键：复制链接\n右键：在浏览器中打开\n\n{record.Url}");

            ImGui.PopStyleColor(4);
            ImGui.Spacing();
        }
    }

    private static bool IsBattleType(XivChatType type)
    {
        var channel = (int)type & 0x7F;
        return channel switch
        {
            41 => true, // Damage
            42 => true, // Miss
            43 => true, // Action
            44 => true, // Item
            45 => true, // Healing
            46 => true, // GainBeneficialStatus
            47 => true, // GainDetrimentalStatus
            48 => true, // LoseBeneficialStatus
            49 => true, // LoseDetrimentalStatus
            58 => true, // BattleSystem
            _ => false,
        };
    }

    private void OnChatMessage(
        XivChatType type,
        int senderid,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        // 忽略战斗消息
        if (IsBattleType(type)) return;

        var messageText = message.TextValue;
        if (string.IsNullOrWhiteSpace(messageText)) return;

        var senderName = sender.TextValue;
        if (string.IsNullOrWhiteSpace(senderName))
            senderName = "未知";

        // 检测URL
        var matches = UrlRegex.Matches(messageText);
        if (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                var url = match.Value;
                LinkRecordManager.AddRecord(senderName, url, MaxRecords);
            }

            // 提示用户
            var linkCount = matches.Count;
            DService.Chat.Print($"💡 检测到 {linkCount} 个链接来自 {senderName}，已保存到链接记录");
        }
    }
    public class LinkRecord
    {
        public string   SenderName { get; init; } = string.Empty;
        public string   Url        { get; init; } = string.Empty;
        public DateTime Time       { get; init; }
    }

    public class LinkRecordCacheData
    {
        public List<LinkRecord> Records        { get; set; } = [];
        public DateTime         LastUpdateTime { get; set; } = DateTime.MinValue;
    }

    private static class LinkRecordManager
    {
        private static LinkRecordCacheData? cachedData;
        private static readonly object      lockObject = new();

        public static void Initialize()
        {
            lock (lockObject)
            {
                cachedData ??= new LinkRecordCacheData
                {
                    Records        = [],
                    LastUpdateTime = DateTime.Now
                };
            }
        }

        public static LinkRecordCacheData? GetCachedData()
        {
            lock (lockObject)
                return cachedData;
        }

        public static void AddRecord(string senderName, string url, int maxRecords)
        {
            lock (lockObject)
            {
                if (cachedData == null)
                    Initialize();

                var newRecord = new LinkRecord
                {
                    SenderName = senderName,
                    Url        = url,
                    Time       = DateTime.Now
                };

                cachedData!.Records.Add(newRecord);
                cachedData.LastUpdateTime = DateTime.Now;

                while (cachedData.Records.Count > maxRecords)
                    cachedData.Records.RemoveAt(0);
            }
        }

        public static void ClearRecords()
        {
            lock (lockObject)
            {
                if (cachedData != null)
                {
                    cachedData.Records.Clear();
                    cachedData.LastUpdateTime = DateTime.Now;
                }
            }
        }

        public static void ClearCache()
        {
            lock (lockObject)
                cachedData = null;
        }
    }
}