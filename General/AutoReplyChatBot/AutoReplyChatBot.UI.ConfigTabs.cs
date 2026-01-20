using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Text;

namespace DailyRoutines.ModulesPublic;

public partial class AutoReplyChatBot
{
    private void DrawGeneralTab(float fieldW)
    {
        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo
               (
                   $"{GetLoc("AutoReplyChatBot-ValidChatTypes")}",
                   string.Join(',', ModuleConfig.ValidChatTypes.Select(x => ValidChatTypes.GetValueOrDefault(x, string.Empty))),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                foreach (var (chatType, loc) in ValidChatTypes)
                {
                    if (ImGui.Selectable($"{loc}##{chatType}", ModuleConfig.ValidChatTypes.Contains(chatType), ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!ModuleConfig.ValidChatTypes.Remove(chatType))
                            ModuleConfig.ValidChatTypes.Add(chatType);
                        RequestSaveConfig();
                    }
                }
            }
        }

        if (ModuleConfig.ValidChatTypes.Contains(XivChatType.TellIncoming) &&
            ImGui.Checkbox(GetLoc("AutoReplyChatBot-OnlyReplyNonFriendTell"), ref ModuleConfig.OnlyReplyNonFriendTell))
            RequestSaveConfig();

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(GetLoc("AutoReplyChatBot-CooldownSeconds"), ref ModuleConfig.CooldownSeconds, 0, 120))
            RequestSaveConfig();

        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("AutoReplyChatBot-EnableContextLimit"), ref ModuleConfig.EnableContextLimit))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-EnableContextLimit-Help"));

        using (ImRaii.Disabled(!ModuleConfig.EnableContextLimit))
        {
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.SliderInt(GetLoc("AutoReplyChatBot-MaxContextMessages"), ref ModuleConfig.MaxContextMessages, 1, 50))
                RequestSaveConfig();
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(GetLoc("AutoReplyChatBot-MaxHistory"), ref ModuleConfig.MaxHistory, 1, 50))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-MaxHistory-Help"));

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderInt(GetLoc("AutoReplyChatBot-MaxTokens"), ref ModuleConfig.MaxTokens, 256, 8192))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-MaxTokens-Help"));

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.SliderFloat(GetLoc("AutoReplyChatBot-Temperature"), ref ModuleConfig.Temperature, 0.0f, 2.0f))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-Temperature-Help"));
    }

    private void DrawAPITab(float fieldW)
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Type"));

        using (ImRaii.PushIndent())
        {
            var currentProvider = ModuleConfig.Provider;
            if (ImGui.RadioButton("OpenAI", currentProvider == APIProvider.OpenAI))
                ModuleConfig.Provider = APIProvider.OpenAI;

            ImGui.SameLine();
            if (ImGui.RadioButton("Ollama", currentProvider == APIProvider.Ollama))
                ModuleConfig.Provider = APIProvider.Ollama;
            RequestSaveConfig();
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText("API Key", ref ModuleConfig.APIKey, 256))
            RequestSaveConfig();
        ImGuiOm.TooltipHover(ModuleConfig.APIKey);

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText("Base URL", ref ModuleConfig.BaseURL, 256))
            RequestSaveConfig();

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputText(GetLoc("AutoReplyChatBot-Model"), ref ModuleConfig.Model, 128))
            RequestSaveConfig();
    }

    private void DrawFilterTab(float fieldW, float promptW, float promptH)
    {
        if (ImGui.Checkbox(GetLoc("AutoReplyChatBot-EnableFilterModel"), ref ModuleConfig.EnableFilter))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-EnableFilterModel-Help"));

        using (ImRaii.Disabled(!ModuleConfig.EnableFilter))
        {
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.InputText($"{GetLoc("AutoReplyChatBot-Model")}##FilterModelInput", ref ModuleConfig.FilterModel, 128))
                RequestSaveConfig();
            ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-FiterModelChoice-Help"));

            ImGui.NewLine();

            ImGui.TextUnformatted(GetLoc("AutoReplyChatBot-FilterSystemPrompt"));

            ImGui.SameLine();

            if (ImGui.SmallButton($"{GetLoc("Reset")}##ResetFilterPrompt"))
            {
                ModuleConfig.FilterPrompt = FILTER_SYSTEM_PROMPT;
                RequestSaveConfig();
            }

            ImGui.InputTextMultiline("##FilterSystemPrompt", ref ModuleConfig.FilterPrompt, 4096, new(promptW, promptH));
            if (ImGui.IsItemDeactivatedAfterEdit())
                RequestSaveConfig();
        }
    }

    private void DrawSystemPromptTab(float fieldW, float promptW, float promptH)
    {
        if (ModuleConfig.SelectedPromptIndex < 0 ||
            ModuleConfig.SelectedPromptIndex >= ModuleConfig.SystemPrompts.Count)
        {
            ModuleConfig.SelectedPromptIndex = 0;
            RequestSaveConfig();
        }

        var selectedPrompt = ModuleConfig.SystemPrompts[ModuleConfig.SelectedPromptIndex];

        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo("##PromptSelector", selectedPrompt.Name))
        {
            if (combo)
            {
                for (var i = 0; i < ModuleConfig.SystemPrompts.Count; i++)
                    if (ImGui.Selectable(ModuleConfig.SystemPrompts[i].Name, i == ModuleConfig.SelectedPromptIndex))
                    {
                        ModuleConfig.SelectedPromptIndex = i;
                        RequestSaveConfig();
                    }
            }
        }

        ImGui.SameLine();

        if (ImGui.Button(GetLoc("Add")))
        {
            var newPromptName = $"Prompt {ModuleConfig.SystemPrompts.Count + 1}";
            ModuleConfig.SystemPrompts.Add
            (
                new()
                {
                    Name    = newPromptName,
                    Content = string.Empty
                }
            );
            ModuleConfig.SelectedPromptIndex = ModuleConfig.SystemPrompts.Count - 1;
            RequestSaveConfig();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(ModuleConfig.SelectedPromptIndex == 0))
        {
            if (ImGui.Button(GetLoc("Delete")))
            {
                ModuleConfig.SystemPrompts.RemoveAt(ModuleConfig.SelectedPromptIndex);
                if (ModuleConfig.SelectedPromptIndex >= ModuleConfig.SystemPrompts.Count)
                    ModuleConfig.SelectedPromptIndex = ModuleConfig.SystemPrompts.Count - 1;

                RequestSaveConfig();
            }
        }

        if (ModuleConfig.SelectedPromptIndex == 0)
        {
            ImGui.SameLine();

            if (ImGui.Button(GetLoc("Reset")))
            {
                ModuleConfig.SystemPrompts[0].Content = DEFAULT_SYSTEM_PROMPT;
                RequestSaveConfig();
            }
        }

        ImGui.NewLine();

        ImGui.SetNextItemWidth(fieldW);

        using (ImRaii.Disabled(ModuleConfig.SelectedPromptIndex == 0))
        {
            if (ImGui.InputText(GetLoc("Name"), ref selectedPrompt.Name, 128))
                RequestSaveConfig();
        }

        if (ModuleConfig.SelectedPromptIndex == 0)
        {
            ImGui.SameLine(0, 8f * GlobalFontScale);
            ImGui.TextDisabled($"({GetLoc("Default")})");
        }

        ImGui.InputTextMultiline("##SystemPrompt", ref selectedPrompt.Content, 4096, new(promptW, promptH));
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();
    }

    private void DrawWorldBookTab(float fieldW, float promptW)
    {
        if (ImGui.Checkbox(GetLoc("AutoReplyChatBot-EnableWorldBook"), ref ModuleConfig.EnableWorldBook))
            RequestSaveConfig();

        if (!ModuleConfig.EnableWorldBook)
            return;

        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.InputInt(GetLoc("AutoReplyChatBot-MaxWorldBookContext"), ref ModuleConfig.MaxWorldBookContext, 256, 2048))
            ModuleConfig.MaxWorldBookContext = Math.Max(256, ModuleConfig.MaxWorldBookContext);
        if (ImGui.IsItemDeactivatedAfterEdit())
            RequestSaveConfig();

        ImGui.NewLine();

        if (ImGui.Button($"{GetLoc("Add")}##AddWorldBook"))
        {
            var newKey = $"Entry {ModuleConfig.WorldBookEntry.Count + 1}";
            ModuleConfig.WorldBookEntry[newKey] = GetLoc("AutoReplyChatBot-WorldBookEntryContent");
            RequestSaveConfig();
        }

        if (ModuleConfig.WorldBookEntry.Count > 0)
        {
            ImGui.SameLine();

            if (ImGui.Button($"{GetLoc("Clear")}##ClearWorldBook"))
            {
                ModuleConfig.WorldBookEntry.Clear();
                RequestSaveConfig();
            }
        }

        var counter         = -1;
        var entriesToRemove = new List<string>();

        foreach (var entry in ModuleConfig.WorldBookEntry)
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
                    ImGui.TextUnformatted(GetLoc("AutoReplyChatBot-WorldBookEntryName"));

                    ImGui.SetNextItemWidth(fieldW);
                    ImGui.InputText($"##Key_{key}", ref key, 128);

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        if (!string.IsNullOrWhiteSpace(key) && key != entry.Key)
                        {
                            ModuleConfig.WorldBookEntry.Remove(entry.Key);
                            ModuleConfig.WorldBookEntry[key] = value;
                            RequestSaveConfig();

                            continue;
                        }
                    }

                    ImGui.TextUnformatted(GetLoc("AutoReplyChatBot-WorldBookEntryContent"));

                    ImGui.SetNextItemWidth(promptW);
                    ImGui.InputTextMultiline($"##Value_{key}", ref value, 2048, new(promptW, 100 * GlobalFontScale));

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        ModuleConfig.WorldBookEntry[entry.Key] = value;
                        RequestSaveConfig();

                        continue;
                    }

                    if (ImGui.Button(GetLoc("Delete")))
                        entriesToRemove.Add(entry.Key);
                }
            }
        }

        foreach (var key in entriesToRemove)
        {
            ModuleConfig.WorldBookEntry.Remove(key);
            RequestSaveConfig();
        }
    }

    private void DrawHistoryTab(float fieldW, float promptW, float promptH)
    {
        var keys = ModuleConfig.Histories.Keys.ToArray();

        var noneLabel   = GetLoc("None");
        var displayKeys = new List<string>(keys.Length + 1) { string.Empty };
        displayKeys.AddRange(keys);

        if (ModuleConfig.HistoryKeyIndex < 0 || ModuleConfig.HistoryKeyIndex >= displayKeys.Count)
            ModuleConfig.HistoryKeyIndex = 0;

        var currentLabel = ModuleConfig.HistoryKeyIndex == 0 ? noneLabel : displayKeys[ModuleConfig.HistoryKeyIndex];

        ImGui.SetNextItemWidth(fieldW);

        using (var combo = ImRaii.Combo("###UserKey", currentLabel))
        {
            if (combo)
            {
                for (var i = 0; i < displayKeys.Count; i++)
                {
                    var label    = i == 0 ? noneLabel : displayKeys[i];
                    var selected = i == ModuleConfig.HistoryKeyIndex;

                    if (ImGui.Selectable(label, selected))
                    {
                        ModuleConfig.HistoryKeyIndex = i;
                        RequestSaveConfig();
                    }
                }
            }
        }

        ImGui.SameLine();

        if (ImGui.Button($"{GetLoc("Clear")}##ClearHistory"))
        {
            if (ModuleConfig.HistoryKeyIndex > 0)
            {
                var currentKey = displayKeys[ModuleConfig.HistoryKeyIndex];
                ModuleConfig.Histories.Remove(currentKey);
                RequestSaveConfig();
            }
        }

        if (ModuleConfig.HistoryKeyIndex <= 0)
            return;

        var currentKey2 = displayKeys[ModuleConfig.HistoryKeyIndex];
        var entries     = ModuleConfig.Histories.TryGetValue(currentKey2, out var list) ? list.ToList() : [];

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
                        NotificationSuccess($"{GetLoc("CopiedToClipboard")}: {message.Text}");
                    }

                    using (var context = ImRaii.ContextPopupItem($"{i}"))
                    {
                        if (context)
                        {
                            if (ImGui.MenuItem($"{GetLoc("Delete")}"))
                            {
                                try
                                {
                                    ModuleConfig.Histories[currentKey2].RemoveAt(i);
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
        if (ImGui.Checkbox(GetLoc("AutoReplyChatBot-EnableGameContext"), ref ModuleConfig.EnableGameContext))
            RequestSaveConfig();
        ImGuiOm.HelpMarker(GetLoc("AutoReplyChatBot-EnableGameContext-Help"));

        using (ImRaii.Disabled(!ModuleConfig.EnableGameContext))
        using (ImRaii.PushIndent())
        {
            foreach (var contextType in Enum.GetValues<GameContextType>())
            {
                using var id = ImRaii.PushId($"AutoReplyChatBot-GameContext-{contextType}");

                var enabled = ModuleConfig.GameContextSettings.GetValueOrDefault(contextType, true);
                var label   = GameContextLocMap.GetValueOrDefault(contextType, contextType.ToString());

                if (ImGui.Checkbox($"{label}##{contextType}", ref enabled))
                {
                    ModuleConfig.GameContextSettings[contextType] = enabled;
                    RequestSaveConfig();
                    UpdateGameContextInWorldBook();
                }
            }
        }
    }
}
