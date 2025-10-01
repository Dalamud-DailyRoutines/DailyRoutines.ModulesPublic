using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAcceptInvitation : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAcceptInvitationTitle"),
        Description = GetLoc("AutoAcceptInvitationDescription"),
        Category    = ModuleCategories.UIOperation,
        Author      = ["Fragile"],
    };

    private static Config ModuleConfig = null!;

    private static string PlayerNameInput = string.Empty;

    private static string Pattern { get; } = BuildPattern(LuminaGetter.GetRow<Addon>(120).GetValueOrDefault().Text.ToDalamudString().Payloads);

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("Mode")}:");
        
        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("ModeSwitch", ref ModuleConfig.Mode))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.Text(GetLoc(ModuleConfig.Mode ? "Whitelist" : "Blacklist"));
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(9818)}:");

        using var indent = ImRaii.PushIndent();

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputText("##NewPlayerInput", ref PlayerNameInput, 128);
        ImGuiOm.TooltipHover(GetLoc("AutoAcceptInvitationTitle-PlayerNameInputHelp"));

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(PlayerNameInput) || 
                               (ModuleConfig.Mode ? ModuleConfig.Whitelist : ModuleConfig.Blacklist).Contains(PlayerNameInput)))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
            {
                if (!string.IsNullOrWhiteSpace(PlayerNameInput) &&
                    (ModuleConfig.Mode ? ModuleConfig.Whitelist : ModuleConfig.Blacklist).Add(PlayerNameInput))
                {
                    SaveConfig(ModuleConfig);
                    PlayerNameInput = string.Empty;
                }
            }
        }

        var playersToRemove = new List<string>();
        foreach (var player in ModuleConfig.Mode ? ModuleConfig.Whitelist : ModuleConfig.Blacklist)
        {
            using var id = ImRaii.PushId($"{player}");
            
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, GetLoc("Delete")))
                playersToRemove.Add(player);
            
            ImGui.SameLine();
            ImGui.Bullet();
            
            ImGui.SameLine(0, 8f * GlobalFontScale);
            ImGui.Text($"{player}");
        }

        if (playersToRemove.Count > 0)
        {
            playersToRemove.ForEach(x => (ModuleConfig.Mode ? ModuleConfig.Whitelist : ModuleConfig.Blacklist).Remove(x));
            SaveConfig(ModuleConfig);
        }
    }
    
    private static void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        var addon = (AddonSelectYesno*)SelectYesno;
        if (addon == null || DService.PartyList.Length > 1) return;
        
        var text = addon->PromptText->NodeText.ExtractText();
        if (string.IsNullOrWhiteSpace(text)) return;
        
        var playerName = ExtractPlayerName(text);
        if (string.IsNullOrWhiteSpace(playerName)) return;
        if ((ModuleConfig.Mode  && !ModuleConfig.Whitelist.Contains(playerName)) ||
            (!ModuleConfig.Mode && ModuleConfig.Blacklist.Contains(playerName)))
            return;
        
        ClickSelectYesnoYes();
    }
    
    private static string ExtractPlayerName(string inputText) => 
        Regex.Match(inputText, Pattern) is { Success: true, Groups.Count: > 1 } match ? match.Groups[1].Value : string.Empty;

    private static string BuildPattern(List<Payload> payloads)
    {
        var pattern = new StringBuilder();
        foreach (var payload in payloads)
        {
            if (payload is TextPayload textPayload)
                pattern.Append(Regex.Escape(textPayload.Text));
            else
                pattern.Append("(.*?)");
        }
        
        return pattern.ToString();
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnSelectYesno);
    
    private class Config : ModuleConfiguration
    {
        // true - 白名单, false - 黑名单
        public bool Mode = true;
        
        public HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase);
    }
}
