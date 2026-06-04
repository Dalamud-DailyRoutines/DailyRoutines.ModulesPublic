using System.Numerics;
using Dalamud.Game.Text;
using Newtonsoft.Json.Linq;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private void DrawGeneralTab(float fieldW)
    {
        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo
               (
                   $"{Lang.Get("AutoReplyChatBot-ValidChatTypes")}",
                   string.Join(',', config.ValidChatTypes.Select(x => ValidChatTypes.GetValueOrDefault(x, string.Empty))),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var (chatType, loc) in ValidChatTypes)
                {
                    if (ImGui.Selectable($"{loc}##{chatType}", config.ValidChatTypes.Contains(chatType), ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!config.ValidChatTypes.Remove(chatType))
                            config.ValidChatTypes.Add(chatType);
                        RequestSaveConfig();
                    }
                }
            }
        }

        if (config.ValidChatTypes.Contains(XivChatType.TellIncoming) &&
            ImGui.Checkbox(Lang.Get("AutoReplyChatBot-OnlyReplyNonFriendTell"), ref config.OnlyReplyNonFriendTell))
            RequestSaveConfig();

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-CooldownSeconds"), ref config.CooldownSeconds, 0, 120))
            RequestSaveConfig();
        
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-MaxContextTokens"), ref config.MaxContextTokens, 4096, 1_000_000))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-MaxContextTokens-Help"));

        ImGui.SameLine();

        if (ImGui.SmallButton("250K"))
        {
            config.MaxContextTokens = 250_000;
            RequestSaveConfig();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("500K"))
        {
            config.MaxContextTokens = 500_000;
            RequestSaveConfig();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("1M"))
        {
            config.MaxContextTokens = 1_000_000;
            RequestSaveConfig();
        }
        
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-MaxTokens"), ref config.MaxTokens, 256, 8192))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-MaxTokens-Help"));
        
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderFloat(Lang.Get("AutoReplyChatBot-Temperature"), ref config.Temperature, 0.0f, 2.0f))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-Temperature-Help"));
    }

    private void DrawAPITab(float fieldW)
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Type"));

        using (ImRaii.PushIndent())
        {
            var currentProvider = config.Provider;
            if (ImGui.RadioButton("OpenAI", currentProvider == APIProvider.OpenAI))
                config.Provider = APIProvider.OpenAI;

            ImGui.SameLine();
            if (ImGui.RadioButton("Ollama", currentProvider == APIProvider.Ollama))
                config.Provider = APIProvider.Ollama;
            RequestSaveConfig();
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText("API Key", ref config.APIKey, 256))
            RequestSaveConfig();
        ImGuiOm.TooltipHover(config.APIKey);

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText("Base URL", ref config.BaseURL, 256))
            RequestSaveConfig();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText(Lang.Get("AutoReplyChatBot-Model"), ref config.Model, 128))
            RequestSaveConfig();
    }

    private void DrawFilterTab(float fieldW, float promptW, float promptH)
    {
        if (ImGui.Checkbox(Lang.Get("AutoReplyChatBot-EnableFilterModel"), ref config.EnableFilter))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-EnableFilterModel-Help"));

        using (ImRaii.Disabled(!config.EnableFilter))
        {
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.InputText($"{Lang.Get("AutoReplyChatBot-Model")}##FilterModelInput", ref config.FilterModel, 128))
                RequestSaveConfig();
            ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-FiterModelChoice-Help"));

            ImGui.NewLine();

            ImGui.TextUnformatted(Lang.Get("AutoReplyChatBot-FilterSystemPrompt"));

            ImGui.SameLine();

            if (ImGui.SmallButton($"{Lang.Get("Reset")}##ResetFilterPrompt"))
            {
                config.FilterPrompt = FILTER_SYSTEM_PROMPT;
                RequestSaveConfig();
            }

            ImGui.InputTextMultiline("##FilterSystemPrompt", ref config.FilterPrompt, 4096, new(promptW, promptH));
            if (ImGui.IsItemDeactivatedAfterEdit())
                RequestSaveConfig();
        }

        ImGui.NewLine();
        ImGui.Separator();
        ImGui.NewLine();

        // ── Hard Guard ──
        if (ImGui.Checkbox(Lang.Get("AutoReplyChatBot-HardGuardEnabled"), ref config.HardGuardEnabled))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-HardGuardEnabled-Help"));

        using (ImRaii.Disabled(!config.HardGuardEnabled))
        {
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.SliderInt($"{Lang.Get("AutoReplyChatBot-MaxMessageLength")}##HardGuard", ref config.MaxMessageLength, 50, 2000))
                RequestSaveConfig();
            ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-MaxMessageLength-Help"));

            ImGui.TextUnformatted(Lang.Get("AutoReplyChatBot-AttackBehavior"));

            using (ImRaii.PushIndent())
            {
                var isDefend = config.AttackBehavior == AttackAction.Defend;

                if (ImGui.RadioButton($"{Lang.Get("AutoReplyChatBot-AttackDefend")}##AtkBehave", isDefend))
                {
                    config.AttackBehavior = AttackAction.Defend;
                    RequestSaveConfig();
                }

                ImGui.SameLine();

                var isSilent = config.AttackBehavior == AttackAction.Silent;

                if (ImGui.RadioButton($"{Lang.Get("AutoReplyChatBot-AttackSilent")}##AtkBehave", isSilent))
                {
                    config.AttackBehavior = AttackAction.Silent;
                    RequestSaveConfig();
                }
            }

            ImGui.NewLine();

            ImGui.TextUnformatted(Lang.Get("AutoReplyChatBot-HardGuardKeywords"));

            ImGui.SameLine();

            if (ImGui.SmallButton($"{Lang.Get("Reset")}##ResetKeywords"))
            {
                config.HardGuardKeywords = [..HardGuardDefaultKeywords];
                RequestSaveConfig();
            }

            using var child = ImRaii.Child("##KeywordList", new(promptW, 120f * GlobalUIScale), true);

            if (child)
            {
                var kwToRemove = -1;

                for (var i = 0; i < config.HardGuardKeywords.Count; i++)
                {
                    var kw = config.HardGuardKeywords.ElementAt(i);

                    ImGui.PushID($"kw_{i}");
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(kw);

                    ImGui.SameLine(promptW - (20f * GlobalUIScale));
                    if (ImGui.SmallButton("X"))
                        kwToRemove = i;

                    ImGui.PopID();
                }

                if (kwToRemove >= 0)
                {
                    config.HardGuardKeywords.Remove(config.HardGuardKeywords.ElementAt(kwToRemove));
                    RequestSaveConfig();
                }
            }

            ImGui.NewLine();

            var newKw = string.Empty;

            if (ImGui.InputTextWithHint("##NewKeyword", Lang.Get("AutoReplyChatBot-AddKeyword"), ref newKw, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (!string.IsNullOrWhiteSpace(newKw))
                {
                    config.HardGuardKeywords.Add(newKw.Trim());
                    newKw = string.Empty;
                    RequestSaveConfig();
                }
            }
        }
    }

    private void DrawSystemPromptTab(float fieldW, float promptW, float promptH)
    {
        if (config.SelectedPromptIndex < 0 ||
            config.SelectedPromptIndex >= config.SystemPrompts.Count)
        {
            config.SelectedPromptIndex = 0;
            RequestSaveConfig();
        }

        var selectedPrompt = config.SystemPrompts[config.SelectedPromptIndex];

        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo("##PromptSelector", selectedPrompt.Name))
        {
            if (combo)
            {
                for (var i = 0; i < config.SystemPrompts.Count; i++)
                    if (ImGui.Selectable(config.SystemPrompts[i].Name, i == config.SelectedPromptIndex))
                    {
                        config.SelectedPromptIndex = i;
                        RequestSaveConfig();
                    }
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(Lang.Get("Add")))
        {
            var newPromptName = $"Prompt {config.SystemPrompts.Count + 1}";
            config.SystemPrompts.Add
            (
                new()
                {
                    Name    = newPromptName,
                    Content = string.Empty
                }
            );
            config.SelectedPromptIndex = config.SystemPrompts.Count - 1;
            RequestSaveConfig();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(config.SelectedPromptIndex == 0))
        {
            if (ImGui.Button(Lang.Get("Delete")))
            {
                config.SystemPrompts.RemoveAt(config.SelectedPromptIndex);
                if (config.SelectedPromptIndex >= config.SystemPrompts.Count)
                    config.SelectedPromptIndex = config.SystemPrompts.Count - 1;

                RequestSaveConfig();
            }
        }

        if (config.SelectedPromptIndex == 0)
        {
            ImGui.SameLine();

            if (ImGui.Button(Lang.Get("Reset")))
            {
                config.SystemPrompts[0].Content = DEFAULT_SYSTEM_PROMPT;
                RequestSaveConfig();
            }
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);

        using (ImRaii.Disabled(config.SelectedPromptIndex == 0))
        {
            if (ImGui.InputText(Lang.Get("Name"), ref selectedPrompt.Name, 128))
                RequestSaveConfig();
        }

        if (config.SelectedPromptIndex == 0)
        {
            ImGui.SameLine(0, 8f * GlobalUIScale);
            ImGui.TextDisabled($"({Lang.Get("Default")})");
        }

        ImGui.InputTextMultiline("##SystemPrompt", ref selectedPrompt.Content, 4096, new(promptW, promptH));
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();
    }

    private void DrawWorldBookTab(float fieldW, float promptW)
    {
        if (ImGui.Checkbox(Lang.Get("AutoReplyChatBot-EnableWorldBook"), ref config.EnableWorldBook))
            RequestSaveConfig();

        if (!config.EnableWorldBook)
            return;

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt(Lang.Get("AutoReplyChatBot-MaxWorldBookContext"), ref config.MaxWorldBookContext, 256, 2048))
            config.MaxWorldBookContext = Math.Max(256, config.MaxWorldBookContext);
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();

        ImGui.NewLine();

        if (ImGui.Button($"{Lang.Get("Add")}##AddWorldBook"))
        {
            var newKey = $"Entry {config.WorldBookEntry.Count + 1}";
            config.WorldBookEntry[newKey] = Lang.Get("AutoReplyChatBot-WorldBookEntryContent");
            RequestSaveConfig();
        }

        if (config.WorldBookEntry.Count > 0)
        {
            ImGui.SameLine();

            if (ImGui.Button($"{Lang.Get("Clear")}##ClearWorldBook"))
            {
                config.WorldBookEntry.Clear();
                RequestSaveConfig();
            }
        }

        var counter         = -1;
        var entriesToRemove = new List<string>();

        foreach (var entry in config.WorldBookEntry)
        {
            counter++;

            using var id = ImRaii.PushId($"WorldBookEntry_{counter}");

            var key   = entry.Key;
            var value = entry.Value;

            if (ImGui.CollapsingHeader($"{key}###Header_{counter}"))
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.TextUnformatted(Lang.Get("AutoReplyChatBot-WorldBookEntryName"));

                    ImGui.SetNextItemWidth(fieldW);
                    ImGui.InputText($"##Key_{key}", ref key, 128);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        if (!string.IsNullOrWhiteSpace(key) && key != entry.Key)
                        {
                            config.WorldBookEntry.Remove(entry.Key);
                            config.WorldBookEntry[key] = value;
                            RequestSaveConfig();

                            continue;
                        }
                    }

                    ImGui.TextUnformatted(Lang.Get("AutoReplyChatBot-WorldBookEntryContent"));

                    ImGui.SetNextItemWidth(promptW);
                    ImGui.InputTextMultiline($"##Value_{key}", ref value, 2048, new(promptW, 100 * GlobalUIScale));

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.WorldBookEntry[entry.Key] = value;
                        RequestSaveConfig();

                        continue;
                    }

                    if (ImGui.Button(Lang.Get("Delete")))
                        entriesToRemove.Add(entry.Key);
                }
            }
        }

        foreach (var key in entriesToRemove)
        {
            config.WorldBookEntry.Remove(key);
            RequestSaveConfig();
        }
    }

    private void DrawHistoryTab(float fieldW, float promptW, float promptH)
    {
        var keys = conversationStore!.GetKeys().ToArray();

        var noneLabel   = Lang.Get("None");
        var displayKeys = new List<string>(keys.Length + 1) { string.Empty };
        displayKeys.AddRange(keys);

        if (config.HistoryKeyIndex < 0 || config.HistoryKeyIndex >= displayKeys.Count)
            config.HistoryKeyIndex = 0;

        var currentLabel = config.HistoryKeyIndex == 0 ? noneLabel : displayKeys[config.HistoryKeyIndex];

        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo("###UserKey", currentLabel))
        {
            if (combo)
            {
                for (var i = 0; i < displayKeys.Count; i++)
                {
                    var label    = i == 0 ? noneLabel : displayKeys[i];
                    var selected = i == config.HistoryKeyIndex;

                    if (ImGui.Selectable(label, selected))
                    {
                        config.HistoryKeyIndex = i;
                        RequestSaveConfig();
                    }
                }
            }
        }

        ImGui.SameLine();

        if (ImGui.Button($"{Lang.Get("Clear")}##ClearHistory"))
        {
            if (config.HistoryKeyIndex > 0)
            {
                var currentKey = displayKeys[config.HistoryKeyIndex];
                conversationStore!.DeleteConversation(currentKey);
            }
        }

        if (config.HistoryKeyIndex <= 0)
            return;

        var currentKey2 = displayKeys[config.HistoryKeyIndex];
        var entries     = conversationStore!.GetTurns(currentKey2);

        using (ImRaii.Child("##HistoryViewer", new(promptW, promptH), true))
        {
            var isAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

            for (var i = 0; i < entries.Count; i++)
            {
                var message = entries[i];
                var isUser  = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
                message.LocalTime ??= message.Timestamp.ToUTCDateTimeFromUnixSeconds().ToLocalTime();
                var timestamp = message.LocalTime?.ToString("HH:mm:ss") ?? string.Empty;

                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.90f, 0.85f, 1f, 1f), !isUser))
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.85f, 0.90f, 1f, 1f), isUser))
                {
                    if (ImGui.Selectable($"[{timestamp}] [{message.Name}] {message.Text}"))
                    {
                        ImGui.SetClipboardText(message.Text);
                        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {message.Text}");
                    }

                    using (var context = ImRaii.ContextPopupItem($"{i}"))
                    {
                        if (context)
                        {
                            if (ImGui.MenuItem($"{Lang.Get("Delete")}"))
                            {
                                try
                                {
                                    var delConv = conversationStore!.GetOrLoad(currentKey2);
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
                }

                ImGui.Separator();
            }

            if (isAtBottom)
                ImGui.SetScrollHereY(1f);
        }
    }

    private static void DrawToolsTab(float promptW)
    {
        if (ToolRegistry.Count == 0)
        {
            ImGui.TextUnformatted(Lang.Get("None"));
            return;
        }

        using var child = ImRaii.Child("##ToolsList", new(promptW, 0), false, ImGuiWindowFlags.NoScrollbar);
        if (!child) return;

        var counter = 0;

        foreach (var tool in ToolRegistry.Values)
        {
            using var id = ImRaii.PushId($"Tool_{counter++}");

            using (ImRaii.PushColor(ImGuiCol.Header, KnownColor.DarkSlateBlue.ToVector4()))
            {
                if (ImGui.CollapsingHeader(tool.Name))
                {
                    using (ImRaii.PushIndent())
                    {
                        ImGui.TextWrapped(tool.Description);

                        if (tool.Parameters is { HasValues: true })
                        {
                            ImGui.Spacing();
                            ImGui.TextDisabled("Parameters:");

                            if (tool.Parameters["properties"] is JObject props)
                            {
                                foreach (var prop in props)
                                    ImGui.BulletText($"{prop.Key}: {prop.Value?["description"]?.Value<string>() ?? prop.Value?.Value<string>() ?? "-"}");
                            }
                        }

                        ImGui.Spacing();
                    }
                }
            }
        }
    }
}
