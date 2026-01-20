using System;
using System.Linq;
using System.Numerics;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private void DrawTestChatTab()
    {
        if (ModuleConfig.TestChatWindows.Count == 0)
        {
            var testGUID = Guid.NewGuid().ToString();
            ModuleConfig.TestChatWindows[testGUID] = new ChatWindow
            {
                ID          = testGUID,
                Name        = "Chat Test",
                Role        = "Tester",
                HistoryGUID = testGUID
            };
            ModuleConfig.CurrentActiveChat = testGUID;
            RequestSaveConfig();
        }

        using (var tabBar = ImRaii.TabBar("ChatTabs"))
        {
            if (tabBar)
            {
                if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing))
                {
                    var newGUID = Guid.NewGuid().ToString();
                    ModuleConfig.TestChatWindows[newGUID] = new ChatWindow
                    {
                        ID          = newGUID,
                        Name        = "New Chat",
                        Role        = "NewUser",
                        HistoryGUID = newGUID
                    };
                    ModuleConfig.CurrentActiveChat = newGUID;
                    RequestSaveConfig();
                }

                var chatTabs = ModuleConfig.TestChatWindows.ToList();

                foreach (var (id, window) in chatTabs)
                {
                    var isOpen = true;

                    var flags = ImGuiTabItemFlags.None;
                    if (id == ModuleConfig.CurrentActiveChat)
                        flags |= ImGuiTabItemFlags.SetSelected;

                    using (var tabItem = ImRaii.TabItem($"{window.Name}###{id}", ref isOpen, flags))
                    {
                        if (tabItem)
                        {
                            // ignored
                        }
                    }

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        ModuleConfig.CurrentActiveChat = id;

                    if (!isOpen && ModuleConfig.TestChatWindows.Count > 1)
                    {
                        ModuleConfig.TestChatWindows.Remove(id);
                        if (ModuleConfig.CurrentActiveChat == id && ModuleConfig.TestChatWindows.Count > 0)
                            ModuleConfig.CurrentActiveChat = ModuleConfig.TestChatWindows.Keys.First();
                        RequestSaveConfig();
                    }
                }
            }
        }

        ImGui.Spacing();

        if (!ModuleConfig.TestChatWindows.TryGetValue(ModuleConfig.CurrentActiveChat, out var currentWindow))
            return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{GetLoc("AutoReplyChatBot-TestChat-Role")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputText("##CurrentRole", ref currentWindow.Role, 96);
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();

        ImGui.SameLine();
        ImGui.TextUnformatted($"{GetLoc("Name")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputText("##WindowName", ref currentWindow.Name, 96);

        ImGui.SameLine(0, 10f * GlobalFontScale);

        if (ImGui.Button($"{GetLoc("Clear")}"))
        {
            var historyKey = currentWindow.HistoryKey;
            if (ModuleConfig.Histories.TryGetValue(historyKey, out var historyList))
                historyList.Clear();
        }

        ImGui.Spacing();

        var chatHeight = 300f * GlobalFontScale;
        var chatWidth  = ImGui.GetContentRegionAvail().X - 4 * ImGui.GetStyle().ItemSpacing.X;

        using (var child = ImRaii.Child("##ChatMessages", new(chatWidth, chatHeight - 60f * GlobalFontScale), true))
        {
            var isAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

            if (child)
            {
                var historyKey = currentWindow.HistoryKey;
                var messages   = ModuleConfig.Histories.TryGetValue(historyKey, out var list) ? list.ToList() : [];

                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    var isUser  = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

                    var textSize     = ImGui.CalcTextSize(message.Text) + new Vector2(2 * ImGui.GetStyle().ItemSpacing.X, 4 * ImGui.GetStyle().ItemSpacing.Y);
                    var messageWidth = Math.Min(textSize.X + 20f                        * GlobalFontScale, chatWidth * 0.75f);

                    if (isUser)
                        ImGui.SetCursorPosX(chatWidth - messageWidth - 16f * GlobalFontScale);
                    else
                        ImGui.SetCursorPosX(8f * GlobalFontScale);

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
                                    if (ImGui.MenuItem($"{GetLoc("Copy")}"))
                                    {
                                        ImGui.SetClipboardText(message.Text);
                                        NotificationSuccess($"{GetLoc("CopiedToClipboard")}");
                                    }

                                    if (ImGui.MenuItem($"{GetLoc("Delete")}"))
                                    {
                                        try
                                        {
                                            if (ModuleConfig.Histories.TryGetValue(historyKey, out var historyList) && i < historyList.Count)
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

                    ScaledDummy(0, 6f);
                }
            }

            if (isAtBottom)
                ImGui.SetScrollHereY(1f);
        }

        ImGui.SetNextItemWidth(chatWidth - ImGui.CalcTextSize(GetLoc("AutoReplyChatBot-Send")).X - 4 * ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputText("##MessageInput", ref currentWindow.InputText, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();

        if ((ImGui.Button(GetLoc("AutoReplyChatBot-Send")) || ImGui.IsKeyPressed(ImGuiKey.Enter)) &&
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
                        reply = await GenerateReplyAsync(ModuleConfig, historyKey, ct) ?? string.Empty;
                    }
                    catch (OperationCanceledException)
                    {
                        currentWindow.IsProcessing = false;
                        return;
                    }
                    catch (Exception ex)
                    {
                        NotificationError(GetLoc("AutoReplyChatBot-ErrorTitle"));
                        Error($"{GetLoc("AutoReplyChatBot-ErrorTitle")}:", ex);
                    }

                    if (!string.IsNullOrWhiteSpace(reply))
                        AppendHistory(historyKey, "assistant", reply);

                    currentWindow.IsProcessing = false;
                }
            );
        }
    }
}
