using System.Numerics;
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
        var chatWidth  = ImGui.GetContentRegionAvail().X - 4 * ImGui.GetStyle().ItemSpacing.X;

        using (var child = ImRaii.Child("##ChatMessages", new(chatWidth, chatHeight - 60f * GlobalUIScale), true))
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
                    var messageWidth = Math.Min(textSize.X + 20f                        * GlobalUIScale, chatWidth * 0.75f);

                    if (isUser)
                        ImGui.SetCursorPosX(chatWidth - messageWidth - 16f * GlobalUIScale);
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

            if (isAtBottom)
                ImGui.SetScrollHereY(1f);
        }

        ImGui.SetNextItemWidth(chatWidth - ImGui.CalcTextSize(Lang.Get("Send")).X - 4 * ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##MessageInput", ref currentWindow.InputText, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();

        if ((ImGui.Button(Lang.Get("Send")) || ImGui.IsKeyPressed(ImGuiKey.Enter)) &&
            !string.IsNullOrWhiteSpace(currentWindow.InputText))
        {
            var text       = currentWindow.InputText;
            var historyKey = currentWindow.HistoryKey;

            currentWindow.InputText    = string.Empty;
            currentWindow.IsProcessing = true;

            var helper = GetSession(historyKey).TaskHelper;
            helper.Abort();
            helper.DelayNext(1000, "等待 1 秒收集更多消息");
            helper.Enqueue(() => IsCooldownReady(historyKey));
            helper.EnqueueAsync
            (async ct =>
                {
                    SetCooldown(historyKey);

                    AppendHistory(historyKey, "user", text, currentWindow.Role);
                    var reply = string.Empty;

                    try
                    {
                        reply = await GenerateReplyAsync(config, historyKey, ct) ?? string.Empty;
                    }
                    catch (OperationCanceledException)
                    {
                        currentWindow.IsProcessing = false;
                        return;
                    }
                    catch (Exception ex)
                    {
                        NotifyHelper.Instance().NotificationError(Lang.Get("AutoReplyChatBot-ErrorTitle"));
                        DLog.Error($"{Lang.Get("AutoReplyChatBot-ErrorTitle")}:", ex);
                    }

                    if (!string.IsNullOrWhiteSpace(reply))
                        AppendHistory(historyKey, "assistant", reply);

                    currentWindow.IsProcessing = false;
                }
            );
        }
    }
}
