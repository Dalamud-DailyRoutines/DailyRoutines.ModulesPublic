using System.Numerics;
using System.Text;
using DailyRoutines.Extensions;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private readonly HashSet<string> expandedBlocks = [];

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
            var histConv   = conversationStore!.GetOrLoad(historyKey);
            histConv.RecentTurns.Clear();
        }

        ImGui.Spacing();

        var chatHeight = 500f * GlobalUIScale;
        var chatWidth  = ImGui.GetContentRegionAvail().X - (4 * ImGui.GetStyle().ItemSpacing.X);

        using (var child = ImRaii.Child("##ChatMessages", new(chatWidth, chatHeight - (60f * GlobalUIScale)), true))
        {
            var isAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

            if (child)
            {
                var historyKey = currentWindow.HistoryKey;
                var messages   = conversationStore!.GetTurns(historyKey);

                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    var isUser  = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);

                    if (isUser)
                    {
                        var maxBubbleWidth  = chatWidth * 0.75f;
                        var isLongOrSpecial = message.Text.Length > 20;
                        var textSize        = ImGui.CalcTextSize(message.Text);
                        var messageWidth    = isLongOrSpecial ? maxBubbleWidth : Math.Min(textSize.X + (32f * GlobalUIScale), maxBubbleWidth);
                        var startPosX       = chatWidth - messageWidth - (16f * GlobalUIScale);

                        using (ImRaii.Group())
                        {
                            ImGui.SetCursorPosX(startPosX);

                            using (ImRaii.Group())
                            {
                                ImGui.Dummy(new Vector2(0, 6f * GlobalUIScale));

                                using (ImRaii.PushIndent(12f * GlobalUIScale))
                                {
                                    if (isLongOrSpecial)
                                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + messageWidth - (24f * GlobalUIScale));

                                    using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.White.ToVector4()))
                                        ImGui.TextWrapped(message.ParsedContent);

                                    if (isLongOrSpecial)
                                        ImGui.PopTextWrapPos();
                                }

                                ImGui.Dummy(new Vector2(0, 6f * GlobalUIScale));
                            }

                            var innerGroupMin = ImGui.GetItemRectMin();
                            var innerGroupMax = ImGui.GetItemRectMax();

                            var bubbleMin = new Vector2(innerGroupMin.X - (8f * GlobalUIScale), innerGroupMin.Y);
                            var bubbleMax = new Vector2(innerGroupMax.X + (8f * GlobalUIScale), innerGroupMax.Y);

                            var bgCol     = KnownColor.Teal.ToVector4() with { W = 0.45f };
                            var borderCol = KnownColor.CadetBlue.ToVector4() with { W = 0.55f };

                            var drawList = ImGui.GetWindowDrawList();
                            drawList.AddRectFilled(bubbleMin, bubbleMax, ImGui.GetColorU32(bgCol), 8f * GlobalUIScale);
                            drawList.AddRect(bubbleMin, bubbleMax, ImGui.GetColorU32(borderCol), 8f   * GlobalUIScale, ImDrawFlags.None, 1f * GlobalUIScale);

                            using (var context = ImRaii.ContextPopupItem($"Context_{i}"))
                            {
                                if (context)
                                {
                                    if (ImGui.MenuItem($"{Lang.Get("Copy")}"))
                                        ImGuiOm.ClickToCopyAndNotify(message.Text);

                                    if (ImGui.MenuItem($"{Lang.Get("Delete")}"))
                                    {
                                        try
                                        {
                                            var delConv = conversationStore!.GetOrLoad(historyKey);
                                            if (i < delConv.RecentTurns.Count)
                                                delConv.RecentTurns.RemoveAt(i);
                                            break;
                                        }
                                        catch
                                        {
                                            // ignored
                                        }
                                    }
                                }
                            }

                            message.LocalTime ??= message.Timestamp.ToUTCDateTimeFromUnixSeconds().ToLocalTime();
                            var timeStr  = message.LocalTime?.ToString("HH:mm:ss") ?? string.Empty;
                            var metaText = $"[{timeStr}] {message.Name}";

                            using (FontManager.Instance().UIFont80.Push())
                            {
                                var metaWidth = ImGui.CalcTextSize(metaText).X;
                                ImGui.SetCursorPosX(chatWidth - metaWidth - (8f * GlobalUIScale));
                                ImGui.TextDisabled(metaText);
                            }
                        }
                    }
                    else
                    {
                        using (ImRaii.Group())
                        {
                            ImGui.SetCursorPosX(8f * GlobalUIScale);

                            using (ImRaii.Group())
                            {
                                var hasAnyBlock = false;

                                if (!string.IsNullOrEmpty(message.ParsedReasoning))
                                {
                                    DrawReasoningBlock(message.ParsedReasoning, i.ToString());
                                    hasAnyBlock = true;
                                }

                                if (message.ToolCalls is { Count: > 0 })
                                {
                                    if (hasAnyBlock) ImGui.Spacing();
                                    DrawToolCallsBlock(message.ToolCalls, i.ToString());
                                    hasAnyBlock = true;
                                }

                                if (!string.IsNullOrEmpty(message.ParsedContent))
                                {
                                    if (hasAnyBlock) ImGui.Spacing();
                                    using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.LightGray.ToVector4()))
                                        ImGui.TextWrapped(message.ParsedContent);
                                }
                                else if (string.IsNullOrEmpty(message.ParsedReasoning) && (message.ToolCalls == null || message.ToolCalls.Count == 0))
                                {
                                    if (hasAnyBlock) ImGui.Spacing();
                                    ImGui.TextDisabled("...");
                                }
                            }

                            using (var context = ImRaii.ContextPopupItem($"Context_{i}"))
                            {
                                if (context)
                                {
                                    if (ImGui.MenuItem($"{Lang.Get("Copy")}"))
                                        ImGuiOm.ClickToCopyAndNotify(message.Text);

                                    if (ImGui.MenuItem($"{Lang.Get("Delete")}"))
                                    {
                                        try
                                        {
                                            var delConv = conversationStore!.GetOrLoad(historyKey);
                                            if (i < delConv.RecentTurns.Count)
                                                delConv.RecentTurns.RemoveAt(i);
                                            break;
                                        }
                                        catch
                                        {
                                            // ignored
                                        }
                                    }
                                }
                            }

                            message.LocalTime ??= message.Timestamp.ToUTCDateTimeFromUnixSeconds().ToLocalTime();
                            var timeStr  = message.LocalTime?.ToString("HH:mm:ss") ?? string.Empty;
                            var metaText = $"[{timeStr}] {message.Name}";

                            ImGui.SetCursorPosX(8f * GlobalUIScale);
                            using (FontManager.Instance().UIFont80.Push())
                                ImGui.TextDisabled(metaText);
                        }
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
            conversationStore!.GetOrLoad(historyKey).RecentTurns.Add(placeholder);

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

            var toolContext = new ToolExecutionContext
            {
                ModuleConfig      = config,
                ReplyContext      = new ReplyContext(),
                ConversationStore = conversationStore!,
                CancellationToken = CancellationToken.None
            };

            // Layer 1: Hard Guard (与生产管线一致)
            var hardBlock = HardGuardCheck(userText, cfg);

            if (hardBlock != null)
            {
                placeholder.Text = string.Empty;
                return;
            }

            var tstHist = conversationStore!.GetOrLoad(historyKey).RecentTurns;
            if (tstHist is { Count: > 0 })
                await CompressConversationIfNeededAsync(cfg, historyKey, [.. tstHist], CancellationToken.None).ConfigureAwait(false);

            if (cfg.EnableFilter && !string.IsNullOrWhiteSpace(cfg.FilterModel))
            {
                using var filterCts   = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var       guardResult = await FilterMessageAsync(cfg, userText, filterCts.Token).ConfigureAwait(false);

                if (guardResult == null || guardResult.Value.Level is GuardLevel.Block)
                {
                    placeholder.Text = string.Empty;
                    return;
                }

                if (guardResult.Value.Level != GuardLevel.Safe)
                {
                    userText = guardResult.Value.Level switch
                    {
                        GuardLevel.Attack => $"[Guard: 攻击 — {guardResult.Value.Reason}] {userText}",
                        GuardLevel.Flag   => $"[Guard: 可疑 — {guardResult.Value.Reason}] {userText}",
                        _                 => userText
                    };

                    var fltConv = conversationStore!.GetOrLoad(historyKey);

                    for (var i = fltConv.RecentTurns.Count - 1; i >= 0; i--)
                        if (fltConv.RecentTurns[i].Role == "user")
                        {
                            fltConv.RecentTurns[i] = new ChatMessage
                                (fltConv.RecentTurns[i].Role, userText, fltConv.RecentTurns[i].Timestamp, fltConv.RecentTurns[i].Name);
                            break;
                        }
                }
            }

            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var       fullText = new StringBuilder();

            await foreach (var chunk in GenerateReplyStreamAsync(cfg, historyKey, toolContext, placeholder, cts.Token).ConfigureAwait(false))
            {
                fullText.Append(chunk);
                placeholder.Text      = fullText.ToString();
                placeholder.Timestamp = GameState.ServerTimeUnix;
            }

            var finalText = fullText.ToString();

            if (string.IsNullOrWhiteSpace(finalText) || finalText.StartsWith("[ATTACK", StringComparison.Ordinal))
                placeholder.Text = string.Empty;
            else if (cfg.HardGuardEnabled)
            {
                var postBlock = HardGuardCheck(finalText, cfg);

                if (postBlock != null)
                    placeholder.Text = string.Empty;
                else
                {
                    conversationStore!.RequestSave(historyKey);
                    RequestSaveConfig();
                }
            }
            else
            {
                conversationStore!.RequestSave(historyKey);
                RequestSaveConfig();
            }
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

    private void DrawReasoningBlock(string reasoning, string id)
    {
        var blockID    = $"reasoning_{id}";
        var isExpanded = expandedBlocks.Contains(blockID);

        var width     = ImGui.GetContentRegionAvail().X;
        var barHeight = 28f * GlobalUIScale;
        var screenPos = ImGui.GetCursorScreenPos();

        ImGui.Dummy(new Vector2(width, barHeight));

        if (ImGui.IsItemClicked())
        {
            if (isExpanded)
                expandedBlocks.Remove(blockID);
            else
                expandedBlocks.Add(blockID);
        }

        var isHovered = ImGui.IsItemHovered();
        var drawList  = ImGui.GetWindowDrawList();

        var bgCol = isHovered
                        ? KnownColor.DarkSlateGray.ToVector4() with { W = 0.8f }
                        : KnownColor.DarkSlateGray.ToVector4() with { W = 0.5f };
        var borderCol = KnownColor.SlateGray.ToVector4() with { W = 0.4f };

        drawList.AddRectFilled(screenPos, screenPos + new Vector2(width, barHeight), ImGui.GetColorU32(bgCol), 6f * GlobalUIScale);
        drawList.AddRect
            (screenPos, screenPos + new Vector2(width, barHeight), ImGui.GetColorU32(borderCol), 6f * GlobalUIScale, ImDrawFlags.None, 1f * GlobalUIScale);

        // 绘制左侧圆形图标
        var circleRadius  = 9f * GlobalUIScale;
        var circleCenterX = screenPos.X + (16f       * GlobalUIScale);
        var circleCenterY = screenPos.Y + (barHeight / 2f);
        var circleBgCol   = KnownColor.MediumPurple.ToVector4() with { W = 0.4f };

        drawList.AddCircleFilled(new(circleCenterX, circleCenterY), circleRadius, ImGui.GetColorU32(circleBgCol));

        var iconStr  = FontAwesomeIcon.Brain.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        drawList.AddText
        (
            new Vector2(circleCenterX - (iconSize.X / 2f), circleCenterY - (iconSize.Y / 2f)),
            ImGui.GetColorU32(KnownColor.MediumPurple.ToVector4()),
            iconStr
        );

        // 绘制中间文字
        var textY    = screenPos.Y + ((barHeight - ImGui.GetTextLineHeight()) / 2f);
        var textPosX = screenPos.X + (32f                                     * GlobalUIScale);

        drawList.AddText(new(textPosX, textY), ImGui.GetColorU32(KnownColor.DarkGray.ToVector4()), "reasoning");
        textPosX += ImGui.CalcTextSize("reasoning").X + (6f * GlobalUIScale);

        var label = Lang.Get("AutoReplyChatBot-ThinkingProcess") ?? "思考";
        drawList.AddText(new(textPosX, textY), ImGui.GetColorU32(KnownColor.White.ToVector4()), label);

        // 右侧状态和折叠箭头
        var arrowStr  = isExpanded ? FontAwesomeIcon.AngleDown.ToIconString() : FontAwesomeIcon.AngleRight.ToIconString();
        var arrowSize = ImGui.CalcTextSize(arrowStr);
        drawList.AddText
        (
            new Vector2(screenPos.X + width - (16f * GlobalUIScale) - arrowSize.X, screenPos.Y + ((barHeight - arrowSize.Y) / 2f)),
            ImGui.GetColorU32(KnownColor.DarkGray.ToVector4() with { W = 0.8f }),
            arrowStr
        );

        var checkStr  = FontAwesomeIcon.Check.ToIconString();
        var checkSize = ImGui.CalcTextSize(checkStr);
        drawList.AddText
        (
            new Vector2(screenPos.X + width - (36f * GlobalUIScale) - checkSize.X, screenPos.Y + ((barHeight - checkSize.Y) / 2f)),
            ImGui.GetColorU32(KnownColor.GreenYellow.ToVector4()),
            checkStr
        );

        // 展开后的内容
        if (isExpanded)
        {
            ImGui.Spacing();

            using (ImRaii.PushIndent(12f * GlobalUIScale))
            {
                var pMin = ImGui.GetCursorScreenPos();

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.LightGray.ToVector4() with { W = 0.9f }))
                    ImGui.TextWrapped(reasoning);

                var pMax = ImGui.GetItemRectMax();

                // 绘制左侧的 CadetBlue 竖线
                drawList.AddLine
                (
                    new Vector2(pMin.X - (6f * GlobalUIScale), pMin.Y),
                    new Vector2(pMin.X - (6f * GlobalUIScale), pMax.Y),
                    ImGui.GetColorU32(KnownColor.CadetBlue.ToVector4()),
                    2f * GlobalUIScale
                );
            }

            ImGui.Spacing();
        }
    }

    private void DrawToolCallsBlock(List<ToolCallRecord> toolCalls, string id)
    {
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var tc         = toolCalls[index];
            var tcID       = $"tool_{id}_{index}";
            var isExpanded = expandedBlocks.Contains(tcID);

            var isCompleted = tc.Result != "Executing...";
            var isError     = tc.Result.Contains("error", StringComparison.OrdinalIgnoreCase) || tc.Result.Contains("fail", StringComparison.OrdinalIgnoreCase);
            var isShell     = tc.Name.Contains("shell", StringComparison.OrdinalIgnoreCase)   || tc.Name.Contains("command", StringComparison.OrdinalIgnoreCase);

            var width     = ImGui.GetContentRegionAvail().X;
            var barHeight = 28f * GlobalUIScale;
            var screenPos = ImGui.GetCursorScreenPos();

            ImGui.Dummy(new Vector2(width, barHeight));

            if (ImGui.IsItemClicked())
            {
                if (isExpanded)
                    expandedBlocks.Remove(tcID);
                else
                    expandedBlocks.Add(tcID);
            }

            var isHovered = ImGui.IsItemHovered();
            var drawList  = ImGui.GetWindowDrawList();

            var bgCol = isHovered
                            ? KnownColor.DarkSlateGray.ToVector4() with { W = 0.8f }
                            : KnownColor.DarkSlateGray.ToVector4() with { W = 0.5f };
            var borderCol = KnownColor.SlateGray.ToVector4() with { W = 0.4f };

            drawList.AddRectFilled(screenPos, screenPos + new Vector2(width, barHeight), ImGui.GetColorU32(bgCol), 6f * GlobalUIScale);
            drawList.AddRect
                (screenPos, screenPos + new Vector2(width, barHeight), ImGui.GetColorU32(borderCol), 6f * GlobalUIScale, ImDrawFlags.None, 1f * GlobalUIScale);

            // 绘制左侧圆形图标
            var circleRadius  = 9f * GlobalUIScale;
            var circleCenterX = screenPos.X + (16f       * GlobalUIScale);
            var circleCenterY = screenPos.Y + (barHeight / 2f);

            Vector4         circleBgCol;
            Vector4         iconCol;
            FontAwesomeIcon icon;

            if (!isCompleted)
            {
                circleBgCol = KnownColor.Orange.ToVector4() with { W = 0.4f };
                iconCol     = KnownColor.Orange.ToVector4();
                icon        = FontAwesomeIcon.Spinner;
            }
            else if (isError)
            {
                circleBgCol = KnownColor.Crimson.ToVector4() with { W = 0.4f };
                iconCol     = KnownColor.Crimson.ToVector4();
                icon        = isShell ? FontAwesomeIcon.Terminal : FontAwesomeIcon.Times;
            }
            else
            {
                circleBgCol = isShell ? KnownColor.Crimson.ToVector4() with { W = 0.4f } : KnownColor.ForestGreen.ToVector4() with { W = 0.4f };
                iconCol     = isShell ? KnownColor.Crimson.ToVector4() : KnownColor.GreenYellow.ToVector4();
                icon        = isShell ? FontAwesomeIcon.Terminal : FontAwesomeIcon.Wrench;
            }

            drawList.AddCircleFilled(new(circleCenterX, circleCenterY), circleRadius, ImGui.GetColorU32(circleBgCol));

            var iconStr  = icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconStr);
            drawList.AddText
            (
                new Vector2(circleCenterX - (iconSize.X / 2f), circleCenterY - (iconSize.Y / 2f)),
                ImGui.GetColorU32(iconCol),
                iconStr
            );

            // 绘制中间文字
            var textY    = screenPos.Y + ((barHeight - ImGui.GetTextLineHeight()) / 2f);
            var textPosX = screenPos.X + (32f                                     * GlobalUIScale);

            var typeStr = isShell ? "shell" : "tool";
            drawList.AddText(new(textPosX, textY), ImGui.GetColorU32(KnownColor.DarkGray.ToVector4()), typeStr);
            textPosX += ImGui.CalcTextSize(typeStr).X + (6f * GlobalUIScale);
            drawList.AddText(new(textPosX, textY), ImGui.GetColorU32(KnownColor.White.ToVector4()), tc.Name);

            // 绘制右侧状态和折叠箭头
            var arrowStr  = isExpanded ? FontAwesomeIcon.AngleDown.ToIconString() : FontAwesomeIcon.AngleRight.ToIconString();
            var arrowSize = ImGui.CalcTextSize(arrowStr);
            drawList.AddText
            (
                new Vector2(screenPos.X + width - (16f * GlobalUIScale) - arrowSize.X, screenPos.Y + ((barHeight - arrowSize.Y) / 2f)),
                ImGui.GetColorU32(KnownColor.DarkGray.ToVector4() with { W = 0.8f }),
                arrowStr
            );

            // 状态图标
            var statusStr = string.Empty;
            var statusCol = KnownColor.White.ToVector4();

            if (!isCompleted)
            {
                statusStr = FontAwesomeIcon.Spinner.ToIconString();
                statusCol = KnownColor.Orange.ToVector4();
            }
            else if (isError)
            {
                statusStr = FontAwesomeIcon.Times.ToIconString();
                statusCol = KnownColor.Crimson.ToVector4();
            }
            else
            {
                statusStr = FontAwesomeIcon.Check.ToIconString();
                statusCol = KnownColor.GreenYellow.ToVector4();
            }

            var statusSize   = ImGui.CalcTextSize(statusStr);
            var rightOffsetX = 36f * GlobalUIScale;

            if (isCompleted && !isError)
            {
                var msStr  = "0 ms ";
                var msSize = ImGui.CalcTextSize(msStr);
                drawList.AddText
                (
                    new Vector2(screenPos.X + width - rightOffsetX - msSize.X - statusSize.X, screenPos.Y + ((barHeight - msSize.Y) / 2f)),
                    ImGui.GetColorU32(KnownColor.DarkGray.ToVector4() with { W = 0.6f }),
                    msStr
                );
                rightOffsetX += msSize.X;
            }

            drawList.AddText
            (
                new Vector2(screenPos.X + width - rightOffsetX - statusSize.X, screenPos.Y + ((barHeight - statusSize.Y) / 2f)),
                ImGui.GetColorU32(statusCol),
                statusStr
            );

            // 展开后的内容
            if (isExpanded)
            {
                ImGui.Spacing();

                using (ImRaii.PushIndent(12f * GlobalUIScale))
                {
                    var pMin = ImGui.GetCursorScreenPos();

                    using (ImRaii.Group())
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Orange.ToVector4()))
                            ImGui.TextUnformatted("Arguments:");
                        ImGui.TextWrapped(tc.Arguments);

                        ImGui.Spacing();

                        using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.GreenYellow.ToVector4()))
                            ImGui.TextUnformatted("Result:");
                        ImGui.TextWrapped(tc.Result);
                    }

                    var pMax = ImGui.GetItemRectMax();

                    // 绘制左侧的 CadetBlue 竖线
                    drawList.AddLine
                    (
                        new Vector2(pMin.X - (6f * GlobalUIScale), pMin.Y),
                        new Vector2(pMin.X - (6f * GlobalUIScale), pMax.Y),
                        ImGui.GetColorU32(KnownColor.CadetBlue.ToVector4()),
                        2f * GlobalUIScale
                    );
                }

                ImGui.Spacing();
            }

            if (index < toolCalls.Count - 1)
                ImGui.Spacing();
        }
    }
}
