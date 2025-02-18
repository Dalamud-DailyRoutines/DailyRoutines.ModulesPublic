using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DailyRoutines.Modules;

public class PartyFinderFilter : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("PartyFinderFilterTitle"),
        Description = GetLoc("PartyFinderFilterDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["status102"]
    };

    private int batchIndex;
    private bool isSecret;
    private readonly HashSet<(ushort, string)> descriptionSet = [];
    private static Config ModuleConfig = null!;

    public override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        DService.PartyFinder.ReceiveListing += OnReceiveListing;
        Overlay ??= new Overlay(this);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnAddonPF);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddonPF);
        if (LookingForGroup != null) OnAddonPF(AddonEvent.PostSetup, null);
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{Lang.Get("WorkTheory")}:");
        ImGuiOm.HelpMarker(Lang.Get("PartyFinderFilter-WorkTheoryHelp"));

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{Lang.Get("PartyFinderFilter-CurrentMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("ModeToggle", ref ModuleConfig.IsWhiteList))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        ImGui.Text(ModuleConfig.IsWhiteList ? Lang.Get("Whitelist") : Lang.Get("Blacklist"));

        if (ImGui.Checkbox(Lang.Get("PartyFinderFilter-FilterDuplicate"), ref ModuleConfig.NeedFilterDuplicate))
            SaveConfig(ModuleConfig);

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, Lang.Get("PartyFinderFilter-AddPreset")))
            ModuleConfig.BlackList.Add(new(true, string.Empty));

        DrawBlacklistEditor();
    }

    public override void OverlayUI() => ConfigUI();

    private void OnAddonPF(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen
        };
    }

    private void DrawBlacklistEditor()
    {
        var index = 0;
        foreach (var item in ModuleConfig.BlackList.ToList())
        {
            var enableState = item.Key;
            if (ImGui.Checkbox($"##available{index}", ref enableState))
            {
                ModuleConfig.BlackList[index] = new(enableState, item.Value);
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            if (DrawBlacklistItemText(index, item))
                index++;
        }
    }

    private bool DrawBlacklistItemText(int index, KeyValuePair<bool, string> item)
    {
        var value = item.Value;
        ImGui.InputText($"##{index}", ref value, 500);

        if (ImGui.IsItemDeactivatedAfterEdit())
            HandleRegexUpdate(index, item.Key, value);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"##delete{index}", FontAwesomeIcon.Trash))
            ModuleConfig.BlackList.RemoveAt(index);
        return true;
    }

    private void HandleRegexUpdate(int index, bool key, string value)
    {
        try
        {
            _ = new Regex(value);
            ModuleConfig.BlackList[index] = new(key, value);
            SaveConfig(ModuleConfig);
        }
        catch (ArgumentException)
        {
            NotificationWarning(Lang.Get("PartyFinderFilter-RegexError"));
            ModuleConfig = LoadConfig<Config>() ?? new Config();
        }
    }

    private unsafe void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (batchIndex != args.BatchNumber)
        {
            isSecret = listing.SearchArea.HasFlag(SearchAreaFlags.Private);
            batchIndex = args.BatchNumber;
            descriptionSet.Clear();
        }

        args.Visible = args.Visible && (isSecret || Verify(listing));
    }

    private bool Verify(IPartyFinderListing listing)
    {
        var description = listing.Description.ToString();

        if (!string.IsNullOrEmpty(description) && ModuleConfig.NeedFilterDuplicate && !descriptionSet.Add((listing.RawDuty, description)))
            return false;

        var isMatch = ModuleConfig.BlackList
                                  .Where(i => i.Key)
                                  .Any(item => Regex.IsMatch(listing.Name.ToString(), item.Value) ||
                                               Regex.IsMatch(description, item.Value));

        return ModuleConfig.IsWhiteList ? isMatch : !isMatch;
    }


    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonPF);
        DService.PartyFinder.ReceiveListing -= OnReceiveListing;
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public List<KeyValuePair<bool, string>> BlackList = [];
        public bool IsWhiteList;
        public bool NeedFilterDuplicate = true;
    }
}
