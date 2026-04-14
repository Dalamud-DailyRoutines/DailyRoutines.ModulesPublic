using System.Numerics;
using Dalamud.Game.Text;
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

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoReplyChatBot-EnableContextLimit"), ref config.EnableContextLimit))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-EnableContextLimit-Help"));

        using (ImRaii.Disabled(!config.EnableContextLimit))
        {
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-MaxContextMessages"), ref config.MaxContextMessages, 1, 50))
                RequestSaveConfig();
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-MaxHistory"), ref config.MaxHistory, 1, 50))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-MaxHistory-Help"));

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(Lang.Get("AutoReplyChatBot-MaxTokens"), ref config.MaxTokens, 256, 8192))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-MaxTokens-Help"));

        ImGui.NewLine();

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
            if (entry.Key == "GameContext") continue;

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
        var keys = config.Histories.Keys.ToArray();

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
                config.Histories.Remove(currentKey);
                RequestSaveConfig();
            }
        }

        if (config.HistoryKeyIndex <= 0)
            return;

        var currentKey2 = displayKeys[config.HistoryKeyIndex];
        var entries     = config.Histories.TryGetValue(currentKey2, out var list) ? list.ToList() : [];

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
                                    config.Histories[currentKey2].RemoveAt(i);
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

    private void DrawGameContextTab()
    {
        if (ImGui.Checkbox(Lang.Get("AutoReplyChatBot-EnableGameContext"), ref config.EnableGameContext))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(Lang.Get("AutoReplyChatBot-EnableGameContext-Help"));

        using (ImRaii.Disabled(!config.EnableGameContext))
        using (ImRaii.PushIndent())
        {
            foreach (var contextType in Enum.GetValues<GameContextType>())
            {
                using var id = ImRaii.PushId($"AutoReplyChatBot-GameContext-{contextType}");

                var enabled = config.GameContextSettings.GetValueOrDefault(contextType, true);
                var label   = GameContextLocMap.GetValueOrDefault(contextType, contextType.ToString());

                if (ImGui.Checkbox($"{label}##{contextType}", ref enabled))
                {
                    config.GameContextSettings[contextType] = enabled;
                    RequestSaveConfig();
                    UpdateGameContextInWorldBook();
                }
            }
        }
    }
}
