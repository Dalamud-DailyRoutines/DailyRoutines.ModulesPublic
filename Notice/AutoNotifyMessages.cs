using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyMessages : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyMessagesTitle"),
        Description = Lang.Get("AutoNotifyMessagesDescription"),
        Category    = ModuleCategory.Notice
    };
    
    private Config config = null!;

    private string searchChatTypesContent = string.Empty;
    private string keywordInput           = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        DService.Instance().Chat.ChatMessage += OnChatMessage;
    }
    
    protected override void Uninit() =>
        DService.Instance().Chat.ChatMessage -= OnChatMessage;

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyNotifyWhenBackground"), ref config.OnlyNotifyWhenBackground))
            config.Save(this);

        ImGui.SetNextItemWidth(300f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   "###SelectChatTypesCombo",
                   Lang.Get("AutoNotifyMessages-SelectedTypesAmount", config.ValidChatTypes.Count),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint
                (
                    "###ChatTypeSelectInput",
                    $"{Lang.Get("PleaseSearch")}...",
                    ref searchChatTypesContent,
                    50
                );

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var chatType in KnownChatTypes)
                {
                    if (!string.IsNullOrEmpty(searchChatTypesContent) &&
                        !chatType.ToString().Contains(searchChatTypesContent, StringComparison.OrdinalIgnoreCase)) continue;

                    var existed = config.ValidChatTypes.Contains(chatType);

                    if (ImGui.Checkbox(chatType.ToString(), ref existed))
                    {
                        if (!config.ValidChatTypes.Remove(chatType))
                            config.ValidChatTypes.Add(chatType);

                        config.Save(this);
                    }
                }
            }
        }

        ImGui.SetNextItemWidth(300f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   "###ExistedKeywordsCombo",
                   Lang.Get
                   (
                       "AutoNotifyMessages-ExistedKeywords",
                       config.ValidKeywords.Count
                   ),
                   ImGuiComboFlags.HeightLarge
               ))
        {
            if (combo)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Keyword")}");

                ImGui.SameLine();

                if (ImGui.SmallButton(Lang.Get("Add")))
                {
                    if (!string.IsNullOrWhiteSpace(keywordInput) && !config.ValidKeywords.Contains(keywordInput))
                    {
                        config.ValidKeywords.Add(keywordInput);
                        config.Save(this);

                        keywordInput = string.Empty;
                    }
                }

                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("###KeywordInput", ref keywordInput, 128);

                if (config.ValidKeywords.Count == 0) return;

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var keyword in config.ValidKeywords.ToArray())
                {
                    using var id = ImRaii.PushId(keyword);
                    ImGui.Selectable(keyword);

                    using (var context = ImRaii.ContextPopupItem($"{keyword}"))
                    {
                        if (context)
                        {
                            if (ImGui.MenuItem(Lang.Get("Delete")))
                            {
                                config.ValidKeywords.Remove(keyword);
                                config.Save(this);
                            }
                        }
                    }
                }
            }
        }
    }

    private unsafe void OnChatMessage(IHandleableChatMessage message)
    {
        if (!KnownChatTypes.Contains(message.LogKind)) return;
        if (config.OnlyNotifyWhenBackground  && !Framework.Instance()->WindowInactive) return;
        if (config.ValidChatTypes.Count == 0 && config.ValidKeywords.Count == 0) return;

        var messageContent = message.Message.ToString();
        var conditionType  = config.ValidChatTypes.Count > 0 && config.ValidChatTypes.Contains(message.LogKind);
        var conditionMessage = config.ValidKeywords.Count                                                                               > 0 &&
                               config.ValidKeywords.FirstOrDefault(x => messageContent.Contains(x, StringComparison.OrdinalIgnoreCase)) != null;
        if (!conditionType && !conditionMessage) return;

        var title   = $"[{message.LogKind}]  {message.Sender.TextValue}";
        var content = message.Message.TextValue;

        NotifyHelper.Instance().NotificationInfo(content, title);
        NotifyHelper.Speak($"{message.Sender.TextValue}{Lang.Get("AutoNotifyMessages-SomeoneSay")}: {content}");
    }

    private class Config : ModuleConfig
    {
        public bool                 OnlyNotifyWhenBackground;
        public HashSet<XivChatType> ValidChatTypes = [];
        public List<string>         ValidKeywords  = [];
    }
    
    #region 常量

    private static FrozenSet<XivChatType> KnownChatTypes { get; } = [.. Enum.GetValues<XivChatType>()];

    #endregion
}
