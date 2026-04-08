using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoNotifyMessages : ModuleBase
{
    private static Config ModuleConfig = null!;

    private static HashSet<XivChatType> KnownChatTypes         = [];
    private static string               SearchChatTypesContent = string.Empty;
    private static string               KeywordInput           = string.Empty;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyMessagesTitle"),
        Description = Lang.Get("AutoNotifyMessagesDescription"),
        Category    = ModuleCategory.Notice
    };

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        KnownChatTypes = [.. Enum.GetValues<XivChatType>()];

        DService.Instance().Chat.ChatMessage += OnChatMessage;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyNotifyWhenBackground"), ref ModuleConfig.OnlyNotifyWhenBackground))
            ModuleConfig.Save(this);

        ImGui.SetNextItemWidth(300f * GlobalUIScale);

        using (var combo = ImRaii.Combo
               (
                   "###SelectChatTypesCombo",
                   Lang.Get("AutoNotifyMessages-SelectedTypesAmount", ModuleConfig.ValidChatTypes.Count),
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
                    ref SearchChatTypesContent,
                    50
                );

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

                        ModuleConfig.Save(this);
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
                       ModuleConfig.ValidKeywords.Count
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
                    if (!string.IsNullOrWhiteSpace(KeywordInput) && !ModuleConfig.ValidKeywords.Contains(KeywordInput))
                    {
                        ModuleConfig.ValidKeywords.Add(KeywordInput);
                        ModuleConfig.Save(this);

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

                    using (var context = ImRaii.ContextPopupItem($"{keyword}"))
                    {
                        if (context)
                        {
                            if (ImGui.MenuItem(Lang.Get("Delete")))
                            {
                                ModuleConfig.ValidKeywords.Remove(keyword);
                                ModuleConfig.Save(this);
                            }
                        }
                    }
                }
            }
        }
    }

    private static unsafe void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (!KnownChatTypes.Contains(type)) return;
        if (ModuleConfig.OnlyNotifyWhenBackground  && !Framework.Instance()->WindowInactive) return;
        if (ModuleConfig.ValidChatTypes.Count == 0 && ModuleConfig.ValidKeywords.Count == 0) return;

        var messageContent = message.ToString();
        var conditionType  = ModuleConfig.ValidChatTypes.Count > 0 && ModuleConfig.ValidChatTypes.Contains(type);
        var conditionMessage = ModuleConfig.ValidKeywords.Count                                                                               > 0 &&
                               ModuleConfig.ValidKeywords.FirstOrDefault(x => messageContent.Contains(x, StringComparison.OrdinalIgnoreCase)) != null;
        if (!conditionType && !conditionMessage) return;

        var title   = $"[{type}]  {sender.TextValue}";
        var content = message.TextValue;

        NotifyHelper.Instance().NotificationInfo(content, title);
        NotifyHelper.Speak($"{sender.TextValue}{Lang.Get("AutoNotifyMessages-SomeoneSay")}: {content}");
    }

    protected override void Uninit() =>
        DService.Instance().Chat.ChatMessage -= OnChatMessage;

    private class Config : ModuleConfig
    {
        public bool                 OnlyNotifyWhenBackground;
        public HashSet<XivChatType> ValidChatTypes = [];
        public List<string>         ValidKeywords  = [];
    }
}
