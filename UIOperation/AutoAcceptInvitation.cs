using System.Text;
using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Components;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAcceptInvitation : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoAcceptInvitationTitle"),
        Description = Lang.Get("AutoAcceptInvitationDescription"),
        Category    = ModuleCategory.UIOperation,
        Author      = ["Fragile"]
    };
    
    private Config config = null!;

    private string playerNameInput = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnSelectYesno);
    
    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("Mode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("ModeSwitch", ref config.Mode))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get(config.Mode ? "Whitelist" : "Blacklist"));

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetAddonText(9818)}:");

        using var indent = ImRaii.PushIndent();

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputText("##NewPlayerInput", ref playerNameInput, 128);
        ImGuiOm.TooltipHover(Lang.Get("AutoAcceptInvitationTitle-PlayerNameInputHelp"));

        ImGui.SameLine();

        using (ImRaii.Disabled
               (
                   string.IsNullOrWhiteSpace(playerNameInput) ||
                   (config.Mode ? config.Whitelist : config.Blacklist).Contains(playerNameInput)
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (!string.IsNullOrWhiteSpace(playerNameInput) &&
                    (config.Mode ? config.Whitelist : config.Blacklist).Add(playerNameInput))
                {
                    config.Save(this);
                    playerNameInput = string.Empty;
                }
            }
        }

        var playersToRemove = new List<string>();

        foreach (var player in config.Mode ? config.Whitelist : config.Blacklist)
        {
            using var id = ImRaii.PushId($"{player}");

            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                playersToRemove.Add(player);

            ImGui.SameLine();
            ImGui.Bullet();

            ImGui.SameLine(0, 8f * GlobalUIScale);
            ImGui.TextUnformatted($"{player}");
        }

        if (playersToRemove.Count > 0)
        {
            playersToRemove.ForEach(x => (config.Mode ? config.Whitelist : config.Blacklist).Remove(x));
            config.Save(this);
        }
    }

    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        var addon = (AddonSelectYesno*)SelectYesno;
        if (addon == null || DService.Instance().PartyList.Length > 1) return;

        var text = addon->PromptText->NodeText.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        var playerName = ExtractPlayerName(text);
        if (string.IsNullOrWhiteSpace(playerName)) return;
        if (config.Mode  && !config.Whitelist.Contains(playerName) ||
            !config.Mode && config.Blacklist.Contains(playerName))
            return;

        AddonSelectYesnoEvent.ClickYes();
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

    private class Config : ModuleConfig
    {
        public HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase);

        // true - 白名单, false - 黑名单
        public bool Mode = true;

        public HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase);
    }
    
    #region 常量

    private static string Pattern { get; } = 
        BuildPattern(LuminaGetter.GetRow<Addon>(120).GetValueOrDefault().Text.ToDalamudString().Payloads);

    #endregion
}
