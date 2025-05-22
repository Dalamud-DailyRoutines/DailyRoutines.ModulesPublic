global using static DailyRoutines.Infos.Widgets;
global using static OmenTools.Helpers.HelpersOm;
global using static DailyRoutines.Infos.Extensions;
global using static OmenTools.Infos.InfosOm;
global using static OmenTools.Helpers.ThrottlerHelper;
global using static DailyRoutines.Managers.Configuration;
global using static DailyRoutines.Managers.LanguageManagerExtensions;
global using static DailyRoutines.Helpers.NotifyHelper;
global using static OmenTools.Helpers.ContentsFinderHelper;
global using Dalamud.Interface.Utility.Raii;
global using OmenTools.Infos;
global using OmenTools.ImGuiOm;
global using OmenTools.Helpers;
global using OmenTools;
global using ImGuiNET;
global using ImPlotNET;
global using Dalamud.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.Modules;

public unsafe class AutoAcceptInvitation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "自动接收组队邀请",
        Category = ModuleCategories.General,
        Author = ["Fragile"],
        
    };
    
    private static readonly Regex ChinesePattern = new Regex(
        @"^确定要加入(.*?)的小队吗？$",
        RegexOptions.Compiled
    );
    
    private static readonly Regex EnglishPattern = new Regex(
        @"^Join (.*?)'s party\?$",
        RegexOptions.Compiled
    );
    
    private static readonly Regex JapanesePattern = new Regex(
        @"^(.*?)のパーティに参加します。よろしいですか？$",
        RegexOptions.Compiled
    );
    
    /*private static readonly Regex DeclineChinesePattern = new Regex(
        @"^确定要拒绝(.*?)发来的组队邀请吗？$", RegexOptions.Compiled);
    private static readonly Regex DeclineEnglishPattern = new Regex(
        @"^Decline (.*?)'s party invite\?$", RegexOptions.Compiled);
    private static readonly Regex DeclineJapanesePattern = new Regex(
        @"^(.*?)のパーティ勧誘を断ります。よろしいですか？$", RegexOptions.Compiled);*/

    private static readonly List<Regex> AllPatterns = new List<Regex>
    {
        ChinesePattern,
        EnglishPattern,
        JapanesePattern
    };
    
    public static string ExtractPlayerId(string inputText)
    {
        foreach (Regex pattern in AllPatterns)
        {
            Match match = pattern.Match(inputText);
            if (match is { Success: true, Groups.Count: > 1 })
                return match.Groups[1].Value;
        }
        return "";
    }
    
    private static Config ModuleConfig = null!;
    private string newWhiteListPlayer = string.Empty;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
    }

    public override void Uninit() => DService.AddonLifecycle.UnregisterListener(OnSelectYesno);

    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        var text = ((AddonSelectYesno*)SelectYesno)->PromptText->NodeText.ExtractText();
        var playerId = ExtractPlayerId(text);
        if (playerId!="")
        {
            if (ModuleConfig.WhiteList.Any(whiteListId => playerId.Contains(whiteListId)))
                ClickSelectYesnoYes();
        }
    }
    
    public override void ConfigUI()
    {
        ImGui.Text("组队邀请白名单");
        ImGui.Separator();

        // 添加新的白名单玩家
        ImGui.Text("添加玩家到白名单:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("##NewWhiteListPlayer", ref newWhiteListPlayer, 100))
        {
            // 输入处理
        }
        
        ImGui.SameLine();
        if (ImGui.Button("添加") && !string.IsNullOrWhiteSpace(newWhiteListPlayer))
        {
            if (!ModuleConfig.WhiteList.Contains(newWhiteListPlayer))
            {
                ModuleConfig.WhiteList.Add(newWhiteListPlayer);
                SaveConfig(ModuleConfig);
                newWhiteListPlayer = string.Empty;
            }
        }
        
        ImGui.Separator();
        ImGui.Text("当前白名单:");
        
        // 显示并允许删除白名单中的玩家
        for (int i = 0; i < ModuleConfig.WhiteList.Count; i++)
        {
            using (ImRaii.PushId(i))
            {
                ImGui.Text(ModuleConfig.WhiteList[i]);
                ImGui.SameLine();
                if (ImGui.Button("删除"))
                {
                    ModuleConfig.WhiteList.RemoveAt(i);
                    SaveConfig(ModuleConfig);
                    i--;
                }
            }
        }
    }

    private class Config : ModuleConfiguration
    {
        public List<string> WhiteList = [];
    }
}
