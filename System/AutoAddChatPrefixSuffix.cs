using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Lumina.Text.ReadOnly;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoAddChatPrefixSuffix : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAddChatPrefixSuffixTitle"),
        Description = Lang.Get("AutoAddChatPrefixSuffixDescription"),
        Category    = ModuleCategory.System,
        Author      = ["那年雪落"]
    };
    
    private Config? config;

    protected override void Init()
    {
        config = Config.Load(this) ??
                       new()
                       {
                           Blacklist = !GameState.IsCN
                                           ? []
                                           :
                                           [
                                               ".",
                                               "。",
                                               "？",
                                               "?",
                                               "！",
                                               "!",
                                               "吗",
                                               "吧",
                                               "呢",
                                               "啊",
                                               "呗",
                                               "呀",
                                               "阿",
                                               "哦",
                                               "嘛",
                                               "咯",
                                               "哎",
                                               "啦",
                                               "哇",
                                               "呵",
                                               "哈",
                                               "奥",
                                               "嗷"
                                           ]
                       };

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("Prefix"), ref config.IsAddPrefix))
            config.Save(this);

        if (config.IsAddPrefix)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputText("###Prefix", ref config.PrefixString, 48);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        if (ImGui.Checkbox(Lang.Get("Suffix"), ref config.IsAddSuffix))
            config.Save(this);

        if (config.IsAddSuffix)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.InputText("###Suffix", ref config.SuffixString, 48);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Blacklist"));

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
        {
            config.Blacklist.Add(string.Empty);
            config.Save(this);
        }

        ImGui.Spacing();

        if (config.Blacklist.Count == 0) return;

        var       blackListItems = config.Blacklist.ToList();
        var       tableSize      = (ImGui.GetContentRegionAvail() * 0.85f) with { Y = 0 };
        using var table          = ImRaii.Table(Lang.Get("Blacklist"), 5, ImGuiTableFlags.NoBordersInBody, tableSize);
        if (!table) return;

        for (var i = 0; i < blackListItems.Count; i++)
        {
            if (i % 5 == 0)
                ImGui.TableNextRow();

            ImGui.TableNextColumn();

            var       inputRef = blackListItems[i];
            using var id       = ImRaii.PushId($"{inputRef}_{i}_Command");

            ImGui.InputText($"##Item{i}", ref inputRef, 48);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.Blacklist.Remove(blackListItems[i]);
                config.Blacklist.Add(inputRef);
                config.Save(this);
                blackListItems[i] = inputRef;
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                config.Blacklist.Remove(blackListItems[i]);
                config.Save(this);
                blackListItems.RemoveAt(i);
                i--;
            }
        }
    }

    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var messageText   = message.ToString();
        var isCommand     = messageText.StartsWith('/') || messageText.StartsWith('／');
        var isTellCommand = isCommand && messageText.StartsWith("/tell ");

        if (!string.IsNullOrWhiteSpace(messageText) && !isCommand || isTellCommand)
        {
            if (IsBlackListChat(messageText) || IsGameItemChat(messageText))
                return;

            if (AddPrefixAndSuffixIfNeeded(messageText, out var modifiedMessage, isTellCommand))
                message = new(modifiedMessage);
        }
    }

    private bool IsBlackListChat(string message) =>
        config?.Blacklist.Any(blackListChat => !string.IsNullOrEmpty(blackListChat) && message.EndsWith(blackListChat)) ?? false;

    private static bool IsGameItemChat(string message) =>
        message.Contains("<item>") || message.Contains("<flag>") || message.Contains("<pfinder>");

    private bool AddPrefixAndSuffixIfNeeded(string original, out string handledMessage, bool isTellCommand = false)
    {
        handledMessage = original;

        if (config.IsAddPrefix)
        {
            if (isTellCommand)
            {
                var firstSpaceIndex = original.IndexOf(' ');
                if (firstSpaceIndex == -1) return false;
                var secondSpaceIndex = original.IndexOf(' ', firstSpaceIndex + 1);
                if (secondSpaceIndex == -1) return false;
                handledMessage = $"{original[..secondSpaceIndex]} {config.PrefixString}{original[secondSpaceIndex..].TrimStart()}";
            }
            else
                handledMessage = $"{config.PrefixString}{handledMessage}";
        }

        if (config.IsAddSuffix)
            handledMessage = $"{handledMessage}{config.SuffixString}";
        return true;
    }

    private class Config : ModuleConfig
    {
        public HashSet<string> Blacklist = [];
        public bool            IsAddPrefix;
        public bool            IsAddSuffix;
        public string          PrefixString = string.Empty;
        public string          SuffixString = string.Empty;
    }
}
