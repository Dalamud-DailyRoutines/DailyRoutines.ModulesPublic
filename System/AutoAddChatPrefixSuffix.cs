using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAddChatPrefixSuffix : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAddChatPrefixSuffixTitle"),
        Description = GetLoc("AutoAddChatPrefixSuffixDescription"),
        Author      = ["那年雪落"],
        Category    = ModuleCategories.System,
    };

    private static readonly CompSig                          ProcessSendedChatSig = new("E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87 ?? ?? ?? ?? ?? ?? ?? ??");
    private delegate        void                             ProcessSendedChatDelegate(ShellCommandModule* module, Utf8String* message, UIModule* uiModule);
    private static          Hook<ProcessSendedChatDelegate>? ProcessSendedChatHook;

    private static Config? ModuleConfig;

    protected override void Init()
    {
        var config = LoadConfig<Config>();
        if (config == null)
        {
            config = new Config();
            if (LanguageManager.CurrentLanguage == "ChineseSimplified")
                config.Blacklist.Add(".", "。", "？", "?", "！", "!", "吗", "吧", "呢", "啊", "呗", "呀", "阿", "哦", "嘛", "咯",
                                     "哎", "啦", "哇", "呵", "哈", "奥", "嗷");
            
            SaveConfig(config);
        }
        
        ModuleConfig = config;

        ProcessSendedChatHook ??= ProcessSendedChatSig.GetHook<ProcessSendedChatDelegate>(ProcessSendedChatDetour);
        ProcessSendedChatHook.Enable();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("Prefix"), ref ModuleConfig.IsAddPrefix)) 
            SaveConfig(ModuleConfig);


        if (ModuleConfig.IsAddPrefix)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputText("###Prefix", ref ModuleConfig.PrefixString, 48);
            if (ImGui.IsItemDeactivatedAfterEdit()) 
                SaveConfig(ModuleConfig);
        }
        
        if (ImGui.Checkbox(GetLoc("Suffix"), ref ModuleConfig.IsAddSuffix)) 
            SaveConfig(ModuleConfig);

        if (ModuleConfig.IsAddSuffix)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputText("###Suffix", ref ModuleConfig.SuffixString, 48);
            if (ImGui.IsItemDeactivatedAfterEdit()) 
                SaveConfig(ModuleConfig);
        }
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("Blacklist"));
        
        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
        {
            ModuleConfig.Blacklist.Add(string.Empty);
            SaveConfig(ModuleConfig);
        }

        ImGui.Spacing();
        
        if (ModuleConfig.Blacklist.Count == 0) return;

        var blackListItems = ModuleConfig.Blacklist.ToList();
        var tableSize = (ImGui.GetContentRegionAvail() * 0.85f) with { Y = 0 };
        using var table = ImRaii.Table(GetLoc("Blacklist"), 5, ImGuiTableFlags.NoBordersInBody, tableSize);
        if (!table) return;
        
        for (var i = 0; i < blackListItems.Count; i++)
        {
            if (i % 5 == 0) 
                ImGui.TableNextRow();
            
            ImGui.TableNextColumn();

            var inputRef = blackListItems[i];
            using var id = ImRaii.PushId($"{inputRef}_{i}_Command");
            
            ImGui.InputText($"##Item{i}", ref inputRef, 48);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ModuleConfig.Blacklist.Remove(blackListItems[i]);
                ModuleConfig.Blacklist.Add(inputRef);
                SaveConfig(ModuleConfig);
                blackListItems[i] = inputRef;
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
            {
                ModuleConfig.Blacklist.Remove(blackListItems[i]);
                SaveConfig(ModuleConfig);
                blackListItems.RemoveAt(i);
                i--;
            }
        }
    }

    private static void ProcessSendedChatDetour(ShellCommandModule* module, Utf8String* message, UIModule* uiModule)
    {
        var messageText = message->ExtractText();
        var isCommand = messageText.StartsWith('/') || messageText.StartsWith('／');
        var isTellCommand = isCommand && messageText.StartsWith("/tell ");

        if ((!string.IsNullOrWhiteSpace(messageText) && !isCommand) || isTellCommand)
        {
            if (IsBlackListChat(messageText) || IsGameItemChat(messageText))
            {
                ProcessSendedChatHook.Original(module, message, uiModule);
                return;
            }

            if (AddPrefixAndSuffixIfNeeded(messageText, out var modifiedMessage, isTellCommand))
            {
                var finalMessage = Utf8String.FromString(modifiedMessage);
                ProcessSendedChatHook.Original(module, finalMessage, uiModule);
                finalMessage->Dtor(true);
                return;
            }
        }
        
        ProcessSendedChatHook.Original(module, message, uiModule);
    }

    private static bool IsWhitelistChat(string message) 
        => ModuleConfig?.Blacklist.Any(whiteListChat => !string.IsNullOrEmpty(whiteListChat) && message.EndsWith(whiteListChat)) ?? false;

    private static bool IsBlackListChat(string message)
       => ModuleConfig?.Blacklist.Any(blackListChat => !string.IsNullOrEmpty(blackListChat) && message.EndsWith(blackListChat)) ?? false;

    private static bool IsGameItemChat(string message)
        => message.Contains("<item>") || message.Contains("<flag>") || message.Contains("<pfinder>");

    private static bool AddPrefixAndSuffixIfNeeded(string original, out string handledMessage, bool isTellCommand = false)
    {
        handledMessage = original;
        if (ModuleConfig.IsAddPrefix)
        {
            if (isTellCommand)
            {
                var firstSpaceIndex = original.IndexOf(' ');
                if (firstSpaceIndex == -1) return false;
                var secondSpaceIndex = original.IndexOf(' ', firstSpaceIndex + 1);
                if (secondSpaceIndex == -1) return false;
                handledMessage = $"{original[..secondSpaceIndex]} {ModuleConfig.PrefixString}{original[secondSpaceIndex..].TrimStart()}";
            }
            else 
                handledMessage = $"{ModuleConfig.PrefixString}{handledMessage}";
        }
        
        if (ModuleConfig.IsAddSuffix) 
            handledMessage = $"{handledMessage}{ModuleConfig.SuffixString}";
        return true;
    }
    
    public class Config : ModuleConfiguration
    {
        public bool            IsAddPrefix;
        public bool            IsAddSuffix;
        public string          PrefixString = string.Empty;
        public string          SuffixString = string.Empty;
        public HashSet<string> Blacklist    = [];
    }
}
