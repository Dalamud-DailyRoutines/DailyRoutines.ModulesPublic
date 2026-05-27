using System.Numerics;
using System.Text;
using DailyRoutines.Extensions;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private void DrawTestChatTab()
    {
        if (config.TestChatWindows.Count == 0)
        {
            var testGUID = Guid.NewGuid().ToString();
            config.TestChatWindows[testGUID] = new ChatWindow
            {
                ID          = testGUID,
                Name        = "Chat Test",
                Role        = "Tester",
                HistoryGUID = testGUID
            };
            config.CurrentActiveChat = testGUID;
            RequestSaveConfig();
        }

        using (var tabBar = ImRaii.TabBar("ChatTabs"))
        {
            if (tabBar)
            {
                if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing))
                {
                    var newGUID = Guid.NewGuid().ToString();
                    config.TestChatWindows[newGUID] = new ChatWindow
                    {
                        ID          = newGUID,
                        Name        = "New Chat",
                        Role        = "NewUser",
                        HistoryGUID = newGUID
                    };
                    config.CurrentActiveChat = newGUID;
                    RequestSaveConfig();
                }

                var chatTabs = config.TestChatWindows.ToList();

                foreach (var (id, window) in chatTabs)
                {
                    var isOpen = true;

                    var flags = ImGuiTabItemFlags.None;
                    if (id == config.CurrentActiveChat)
                        flags |= ImGuiTabItemFlags.SetSelected;

                    using (var tabItem = ImRaii.TabItem($"{window.Name}###{id}", ref isOpen, flags))
                    {
                        if (tabItem)
                        {
                            // ignored
                        }
                    }

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        config.CurrentActiveChat = id;

                    if (!isOpen && config.TestChatWindows.Count > 1)
                    {
                        config.TestChatWindows.Remove(id);
                        if (config.CurrentActiveChat == id && config.TestChatWindows.Count > 0)
                            config.CurrentActiveChat = config.TestChatWindows.Keys.First();
                        RequestSaveConfig();
                    }
                }
            }
        }

        ImGui.Spacing();

        if (!config.TestChatWindows.TryGetValue(config.CurrentActiveChat, out var currentWindow))
            return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("AutoReplyChatBot-TestChat-Role")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalUIScale);
        ImGui.InputText("##CurrentRole", ref currentWindow.Role, 96);
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Lang.Get("Name")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalUIScale);
        ImGui.InputText("##WindowName", ref currentWindow.Name, 96);

        ImGui.SameLine(0, 10f * GlobalUIScale);

        if (ImGui.Button($"{Lang.Get("Clear")}"))
        {
            var historyKey = currentWindow.HistoryKey;
            if (config.Histories.TryGetValue(historyKey, out var historyList))
                historyList.Clear();
        }

        ImGui.Spacing();

        var chatHeight = 300f * GlobalUIScale;
        var chatWidth  = ImGui.GetContentRegionAvail().X - (4 * ImGui.GetStyle().ItemSpacing.X);

        using (var child = ImRaii.Child("##ChatMessages", new(chatWidth, chatHeight - (60f * GlobalUIScale)), true))
        {
            var isAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

            if (child)
            {
                var historyKey = currentWindow.HistoryKey;
                var messages   = config.Histories.TryGetValue(historyKey, out var list) ? list.ToList() : [];

                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    var isUser  = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

                    var textSize     = ImGui.CalcTextSize(message.Text) + new Vector2(2 * ImGui.GetStyle().ItemSpacing.X, 4 * ImGui.GetStyle().ItemSpacing.Y);
                    var messageWidth = Math.Min(textSize.X + (20f * GlobalUIScale), chatWidth * 0.75f);

                    if (isUser)
                        ImGui.SetCursorPosX(chatWidth - messageWidth - (16f * GlobalUIScale));
                    else
                        ImGui.SetCursorPosX(8f * GlobalUIScale);

                    var bgColor   = isUser ? KnownColor.CadetBlue.ToVector4() : KnownColor.SlateGray.ToVector4();
                    var textColor = isUser ? KnownColor.White.ToVector4() : new(0.9f, 0.9f, 0.9f, 1.0f);

                    using (ImRaii.Group())
                    using (ImRaii.PushColor(ImGuiCol.ChildBg, bgColor))
                    using (ImRaii.PushColor(ImGuiCol.Text, textColor))
                    {
                        using (var msgChild = ImRaii.Child
                               (
                                   $"##Msg_{i}",
                                   textSize with { X = messageWidth },
                                   true,
                                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                               ))
                        {
                            if (msgChild)
                            {
                                ImGui.TextWrapped(message.Text);

                                using var context = ImRaii.ContextPopupItem($"Context_{i}");

                                if (context)
                                {
                                    ImGui.MenuItem($"{Lang.Get("Copy")}");
                                    ImGuiOm.ClickToCopyAndNotify(message.Text);

                                    if (ImGui.MenuItem($"{Lang.Get("Delete")}"))
                                    {
                                        try
                                        {
                                            if (config.Histories.TryGetValue(historyKey, out var historyList) && i < historyList.Count)
                                            {
                                                historyList.RemoveAt(i);
                                                RequestSaveConfig();
                                            }

                                            break;
                                        }
                                        catch
                                        {
                                            // ignored
                                        }
                                    }
                                }
                            }
                        }

                        message.LocalTime ??= message.Timestamp.ToUTCDateTimeFromUnixSeconds().ToLocalTime();
                        var timeStr = message.LocalTime?.ToString("HH:mm:ss") ?? string.Empty;

                        using (FontManager.Instance().UIFont80.Push())
                            ImGui.TextDisabled($"[{timeStr}] {message.Name}");
                    }

                    ImGuiOm.ScaledDummy(0, 6f);
                }
            }

            if (isAtBottom || currentWindow.IsProcessing)
                ImGui.SetScrollHereY(1f);
        }

        ImGui.SetNextItemWidth(chatWidth - ImGui.CalcTextSize(Lang.Get("Send")).X - (4 * ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##MessageInput", ref currentWindow.InputText, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();

        if ((ImGui.Button(Lang.Get("Send")) || ImGui.IsKeyPressed(ImGuiKey.Enter)) &&
            !string.IsNullOrWhiteSpace(currentWindow.InputText))
        {
            var text       = currentWindow.InputText;
            var historyKey = currentWindow.HistoryKey;

            currentWindow.InputText    = string.Empty;
            currentWindow.IsProcessing = true;

            AppendHistory(historyKey, "user", text, currentWindow.Role);

            var placeholder = new ChatMessage("assistant", string.Empty, config.Model);
            config.Histories.GetOrAdd(historyKey, _ => []).Add(placeholder);
            RequestSaveConfig();

            _ = StreamReplyAsync(currentWindow, historyKey, text, placeholder);
        }
    }

    private async Task StreamReplyAsync(ChatWindow window, string historyKey, string userText, ChatMessage placeholder)
    {
        try
        {
            const int MAX_WAIT_MS = 15_000;
            var       waited      = 0;

            while (!IsCooldownReady(historyKey) && waited < MAX_WAIT_MS)
            {
                await Task.Delay(500).ConfigureAwait(false);
                waited += 500;
            }

            if (!IsCooldownReady(historyKey)) return;

            SetCooldown(historyKey);

            var cfg = config;
            placeholder.Name = cfg.Model;

            if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
            {
                using var filterCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var       result    = await FilterMessageAsync(cfg, userText, filterCts.Token).ConfigureAwait(false);

                if (result == null)
                {
                    placeholder.Text = string.Empty;
                    return;
                }

                if (result != userText && cfg.Histories.TryGetValue(historyKey, out var originalList))
                {
                    for (var i = originalList.Count - 1; i >= 0; i--)
                        if (originalList[i].Role == "user")
                        {
                            originalList[i] = new ChatMessage(originalList[i].Role, result, originalList[i].Timestamp, originalList[i].Name);
                            break;
                        }
                }
            }

            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var       fullText = new StringBuilder();

            await foreach (var chunk in GenerateReplyStreamAsync(cfg, historyKey, cts.Token).ConfigureAwait(false))
            {
                fullText.Append(chunk);
                placeholder.Text      = fullText.ToString();
                placeholder.Timestamp = GameState.ServerTimeUnix;
            }

            var finalText = fullText.ToString();

            if (string.IsNullOrWhiteSpace(finalText) || finalText.StartsWith("[ATTACK", StringComparison.Ordinal))
                placeholder.Text = string.Empty;
            else
                RequestSaveConfig();
        }
        catch (OperationCanceledException)
        {
            placeholder.Text = string.Empty;
        }
        catch (Exception ex)
        {
            placeholder.Text = Lang.Get("AutoReplyChatBot-ErrorTitle");
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoReplyChatBot-ErrorTitle"));
            DLog.Error($"{Lang.Get("AutoReplyChatBot-ErrorTitle")}:", ex);
        }
        finally
        {
            try
            {
                window.IsProcessing = false;
            }
            catch
            {
                // ignored
            }
        }
    }
}
