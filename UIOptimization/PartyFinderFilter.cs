using DailyRoutines.Abstracts;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DailyRoutines.ModulesPublic;

public class PartyFinderFilter : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("PartyFinderFilterTitle"),
        Description = GetLoc("PartyFinderFilterDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["status102"]
    };

    private static Config ModuleConfig = null!;

    private static int batchIndex;
    private static bool isSecret;
    private static bool isRaid;
    private static readonly HashSet<(ushort, string)> descriptionSet = [];
    private static bool ManualMode;

    public override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        Overlay ??= new Overlay(this);

        DService.PartyFinder.ReceiveListing += OnReceiveListing;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroup))
            OnAddon(AddonEvent.PostSetup, null);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("PartyFinderFilter-FilterDuplicate"), ref ModuleConfig.FilterSameDescription))
            SaveConfig(ModuleConfig);

        ImGui.Spacing();

        DrawHighEndSettings();

        ImGui.Spacing();

        ImGui.TextColored(LightSkyBlue, GetLoc("PartyFinderFilter-DescriptionRegexFilter"));

        ImGui.Spacing();

        DrawRegexFilterSettings();
    }

    private void DrawHighEndSettings()
    {
        using var group = ImRaii.Group();

        ImGui.TextColored(LightSkyBlue, GetLoc("PartyFinderFilter-HighEndFilter"));

        using var indent = ImRaii.PushIndent();

        if (ImGui.Checkbox(GetLoc("PartyFinderFilter-HighEndFilterSameJob"), ref ModuleConfig.HighEndFilterSameJob))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox($"{GetLoc("PartyFinderFilter-HighEndFilterRoleCount")}", ref ModuleConfig.HighEndFilterRoleCount))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("PartyFinderFilter-HighEndFilterRoleCountHelp"), 20f * GlobalFontScale);

        ImGui.SameLine();
        ImGuiComponents.ToggleButton("###IsHighEndRoleCountFilterManualMode", ref ManualMode);

        ImGui.SameLine();
        ImGui.Text(GetLoc(ManualMode ? "ManualMode" : "AutoMode"));

        if (!ModuleConfig.HighEndFilterRoleCount)
            return;

        using var pushIndent = ImRaii.PushIndent();
        ImGui.SetNextItemWidth(60f * GlobalFontScale);
        ImGui.InputInt3($"{LuminaWarpper.GetAddonText(1082)} / {LuminaWarpper.GetAddonText(11300)} / {LuminaWarpper.GetAddonText(11301)}",
                        ref ModuleConfig.HighEndFilterRoleCountData[0]);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        ImGui.SetNextItemWidth(60f * GlobalFontScale);
        ImGui.InputInt3($"{LuminaWrapper.GetAddonText(1084)} / {LuminaWrapper.GetAddonText(1085)} / {LuminaWrapper.GetAddonText(1086)}",
                        ref ModuleConfig.HighEndFilterRoleCountData[3]);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private void DrawRegexFilterSettings()
    {
        using var indent = ImRaii.PushIndent();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, GetLoc("PartyFinderFilter-AddPreset")))
            ModuleConfig.BlackList.Add(new(true, string.Empty));

        ImGui.SameLine();
        DrawWorkModeSettings();

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
            if (DrawRegexFilterItemText(index, item))
                index++;
        }
    }

    private void DrawWorkModeSettings()
    {
        using var group = ImRaii.Group();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("ModeToggle", ref ModuleConfig.IsWhiteList))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        ImGui.Text(ModuleConfig.IsWhiteList ? GetLoc("Whitelist") : GetLoc("Blacklist"));

        ImGui.SameLine();
        ImGuiOm.HelpMarker(GetLoc("PartyFinderFilter-WorkModeHelp"), 20f * GlobalFontScale);
    }

    private bool DrawRegexFilterItemText(int index, KeyValuePair<bool, string> item)
    {
        var value = item.Value;
        ImGui.InputText($"##{index}", ref value, 500);

        if (ImGui.IsItemDeactivatedAfterEdit())
            HandleRegexUpdate(index, item.Key, value);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"##Delete{index}", FontAwesomeIcon.Trash))
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
            NotificationWarning(GetLoc("PartyFinderFilter-RegexError"));
            ModuleConfig = LoadConfig<Config>() ?? new Config();
        }
    }

    private static void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (batchIndex != args.BatchNumber)
        {
            isSecret = listing.SearchArea.HasFlag(SearchAreaFlags.Private);
            isRaid = listing.Category == DutyCategory.HighEndDuty;
            batchIndex = args.BatchNumber;
            descriptionSet.Clear();
        }

        if (isSecret)
            return;

        args.Visible &= FilterBySameDescription(listing);
        args.Visible &= FilterByRegexList(listing);
        args.Visible &= FilterByHighEndSameJob(listing);
        args.Visible &= FilterByHighEndSameRole(listing);
    }

    private static bool FilterBySameDescription(IPartyFinderListing listing)
    {
        if (!ModuleConfig.FilterSameDescription)
            return true;

        var description = listing.Description.ExtractText();
        if (string.IsNullOrWhiteSpace(description))
            return true;

        return descriptionSet.Add((listing.RawDuty, description));
    }

    private static bool FilterByRegexList(IPartyFinderListing listing)
    {
        var description = listing.Description.ToString();
        if (string.IsNullOrEmpty(description))
            return true;

        var isMatch = ModuleConfig.BlackList
                                  .Where(i => i.Key)
                                  .Any(item => Regex.IsMatch(listing.Name.ExtractText(), item.Value) ||
                                               Regex.IsMatch(description, item.Value));

        return ModuleConfig.IsWhiteList ? isMatch : !isMatch;
    }

    private static bool FilterByHighEndSameJob(IPartyFinderListing listing)
    {
        if (!ModuleConfig.HighEndFilterSameJob)
            return true;
        if (!isRaid || DService.ClientState.LocalPlayer is not { } localPlayer)
            return true;

        var job = localPlayer.ClassJob.Value;
        if (job.Unknown11 == 0)
            return true; // 生产职业 / 基础职业

        foreach (var present in listing.JobsPresent)
            if (present.RowId == localPlayer.ClassJob.RowId)
                return false;

        return true;
    }

    private static bool FilterByHighEndSameRole(IPartyFinderListing listing)
    {
        if (!ModuleConfig.HighEndFilterRoleCount)
            return true;
        if (!isRaid || DService.ClientState.LocalPlayer is not { } localPlayer)
            return true;

        var job = localPlayer.ClassJob.Value;

        if (ManualMode)
        {
            var filter0 = RoleCounter(1, ModuleConfig.HighEndFilterRoleCountData[0], job);
            var filter1 = RoleCounter(2, ModuleConfig.HighEndFilterRoleCountData[1], job);
            var filter2 = RoleCounter(6, ModuleConfig.HighEndFilterRoleCountData[2], job);
            var filter3 = RoleCounter(3, ModuleConfig.HighEndFilterRoleCountData[3], job);
            var filter4 = RoleCounter(4, ModuleConfig.HighEndFilterRoleCountData[4], job);
            var filter5 = RoleCounter(5, ModuleConfig.HighEndFilterRoleCountData[5], job);

            return filter0 && filter1 && filter2 && filter3 && filter4 && filter5;
        }
        else
        {
            return job.Unknown11 switch
            {
                0 => true,
                1 => RoleCounter(1, ModuleConfig.HighEndFilterRoleCountData[0], job),
                2 => RoleCounter(2, ModuleConfig.HighEndFilterRoleCountData[1], job),
                6 => RoleCounter(6, ModuleConfig.HighEndFilterRoleCountData[2], job),
                3 or 4 or 5 => RoleCounter(job.Unknown11, ModuleConfig.HighEndFilterRoleCountData[job.Unknown11], job),
                _ => true,
            };
        }

        bool RoleCounter(int roleType, int maxCount, ClassJob currentJob)
        {
            if (maxCount == -1)
                return true;

            var count = 0;
            var hasSlot = false;

            var slots = listing.Slots.ToList();
            var jobsPresent = listing.JobsPresent.ToList();
            foreach (var i in Enumerable.Range(0, 8))
            {
                if (slots.Count <= i || jobsPresent.Count <= i || count >= maxCount)
                    break;

                if (jobsPresent.ElementAt(i).Value.RowId != 0)
                {
                    // 如果该位置已有玩家，检查职业类型
                    if (jobsPresent.ElementAt(i).Value.Unknown11 == roleType)
                        count++;
                }
                else if (!hasSlot) // 有空位后不再检查
                {
                    // 检查空位是否允许当前角色类型
                    if (ManualMode)
                    {
                        // 手动模式：检查所有同类角色是否有空位
                        foreach (var playerJob in LuminaGetter.Get<ClassJob>().Where(j => j.RowId != 0 && j.Unknown11 == roleType))
                        {
                            if (Enum.TryParse<JobFlags>(playerJob.NameEnglish.ExtractText().Replace(" ", string.Empty), out var flag) &&
                                slots.ElementAt(i)[flag])
                            {
                                hasSlot = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // 自动模式：检查当前职业是否有空位
                        if (Enum.TryParse<JobFlags>(currentJob.NameEnglish.ExtractText().Replace(" ", string.Empty), out var flag) && slots.ElementAt(i)[flag])
                            hasSlot = true;
                    }
                }
            }

            return count < maxCount && hasSlot;
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        ToggleOverlayConfig(type == AddonEvent.PostSetup);

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        DService.PartyFinder.ReceiveListing -= OnReceiveListing;
        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public List<KeyValuePair<bool, string>> BlackList = [];

        public bool IsWhiteList;

        public bool FilterSameDescription = true;
        public bool HighEndFilterSameJob = true;

        public bool HighEndFilterRoleCount = true;
        public int[] HighEndFilterRoleCountData = [2, 1, 1, 2, 1, 2]; // T2, 血奶1, 盾奶1, 近2, 远1, 法2
    }
}
