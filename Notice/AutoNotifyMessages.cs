using DailyRoutines.Abstracts;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public class AutoNotifyMessages : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNotifyMessagesTitle"),
        Description = GetLoc("AutoNotifyMessagesDescription"),
        Category = ModuleCategories.Notice,
    };

    private static Config ModuleConfig = null!;

    private static HashSet<XivChatType> KnownChatTypes = [];
    private static string SearchChatTypesContent = string.Empty;
    private static string KeywordInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        KnownChatTypes = [.. Enum.GetValues<XivChatType>()];

        DService.Chat.ChatMessage += OnChatMessage;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyNotifyWhenBackground"), ref ModuleConfig.OnlyNotifyWhenBackground))
            SaveConfig(ModuleConfig);

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###SelectChatTypesCombo",
                                        GetLoc("AutoNotifyMessages-SelectedTypesAmount", ModuleConfig.ValidChatTypes.Count),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("###ChatTypeSelectInput", $"{GetLoc("PleaseSearch")}...",
                                        ref SearchChatTypesContent, 50);

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var chatType in KnownChatTypes)
                {
                    if (!string.IsNullOrEmpty(SearchChatTypesContent) &&
                        !chatType.ToString().Contains(SearchChatTypesContent, StringComparison.OrdinalIgnoreCase)) continue;

                    var existed = ModuleConfig.ValidChatTypes.Contains(chatType);
                    if (ImGui.Checkbox(chatType.ToString(), ref existed))
                    {
                        if (!ModuleConfig.ValidChatTypes.Remove(chatType))
                            ModuleConfig.ValidChatTypes.Add(chatType);

                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###ExistedKeywordsCombo",
                                        GetLoc("AutoNotifyMessages-ExistedKeywords", 
                                                             ModuleConfig.ValidKeywords.Count),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(LightSkyBlue, $"{GetLoc("Keyword")}");

                ImGui.SameLine();
                if (ImGui.SmallButton(GetLoc("Add")))
                {
                    if (!string.IsNullOrWhiteSpace(KeywordInput) && !ModuleConfig.ValidKeywords.Contains(KeywordInput))
                    {
                        ModuleConfig.ValidKeywords.Add(KeywordInput);
                        SaveConfig(ModuleConfig);

                        KeywordInput = string.Empty;
                    }
                }

                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("###KeywordInput", ref KeywordInput, 128);
                
                if (ModuleConfig.ValidKeywords.Count == 0) return;

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var keyword in ModuleConfig.ValidKeywords.ToArray())
                {
                    using var id = ImRaii.PushId(keyword);
                    ImGui.Selectable(keyword);

                    if (ImGui.BeginPopupContextItem())
                    {
                        if (ImGui.MenuItem(GetLoc("Delete")))
                        {
                            ModuleConfig.ValidKeywords.Remove(keyword);
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.EndPopup();
                    }
                }
            }
        }
    }

    private static unsafe void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (!KnownChatTypes.Contains(type)) return;
        if (ModuleConfig.OnlyNotifyWhenBackground && !Framework.Instance()->WindowInactive) return;
        if (ModuleConfig.ValidChatTypes.Count == 0 && ModuleConfig.ValidKeywords.Count == 0) return;

        var messageContent = message.ExtractText();
        var conditionType = ModuleConfig.ValidChatTypes.Count > 0 && ModuleConfig.ValidChatTypes.Contains(type);
        var conditionMessage = ModuleConfig.ValidKeywords.Count > 0 &&
                               ModuleConfig.ValidKeywords.FirstOrDefault(
                                   x => messageContent.Contains(x, StringComparison.OrdinalIgnoreCase)) != default;
        if (!conditionType && !conditionMessage) return;
        
        var title = $"[{type}]  {sender.TextValue}";
        var content = message.TextValue;

        NotificationInfo(content, title);
        Speak($"{sender.TextValue}{GetLoc("AutoNotifyMessages-SomeoneSay")}: {content}");
    }

    protected override void Uninit()
    {
        DService.Chat.ChatMessage -= OnChatMessage;
    }

    private class Config : ModuleConfiguration
    {
        public bool OnlyNotifyWhenBackground;
        public HashSet<XivChatType> ValidChatTypes = [];
        public List<string> ValidKeywords = [];
    }
}
