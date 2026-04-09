using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Components;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class PartyFinderFilter : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("PartyFinderFilterTitle"),
        Description = Lang.Get("PartyFinderFilterDescription"),
        Category    = ModuleCategory.Recruitment,
        Author      = ["status102"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    private int  batchIndex;
    private bool isSecret;
    private bool isRaid;
    private bool manualMode;

    private readonly HashSet<(ushort, string)> descriptionSet = [];

    protected override unsafe void Init()
    {
        config =   Config.Load(this) ?? new();
        Overlay      ??= new(this);

        DService.Instance().PartyFinder.ReceiveListing += OnReceiveListing;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
        if (LookingForGroup->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("PartyFinderFilter-FilterDuplicate"), ref config.FilterSameDescription))
            config.Save(this);

        ImGui.Spacing();

        DrawHighEndSettings();

        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("PartyFinderFilter-DescriptionRegexFilter"));

        ImGui.Spacing();

        DrawRegexFilterSettings();
    }

    private void DrawHighEndSettings()
    {
        using var group = ImRaii.Group();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("PartyFinderFilter-HighEndFilter"));

        using var indent = ImRaii.PushIndent();

        if (ImGui.Checkbox(Lang.Get("PartyFinderFilter-HighEndFilterSameJob"), ref config.HighEndFilterSameJob))
            config.Save(this);

        if (ImGui.Checkbox($"{Lang.Get("PartyFinderFilter-HighEndFilterRoleCount")}", ref config.HighEndFilterRoleCount))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("PartyFinderFilter-HighEndFilterRoleCountHelp"), 20f * GlobalUIScale);

        ImGui.SameLine();
        ImGuiComponents.ToggleButton("###IsHighEndRoleCountFilterManualMode", ref manualMode);

        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.Get(manualMode ? "ManualMode" : "AutoMode"));

        if (!config.HighEndFilterRoleCount)
            return;

        var changed = false;

        using var pushIndent = ImRaii.PushIndent();
        using var itemWidth  = ImRaii.ItemWidth(50f * GlobalUIScale);

        using (ImRaii.Group())
        {
            ImGui.InputInt($"{LuminaWrapper.GetAddonText(1082)}", ref config.FilterJobTypeCountData.Tank);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;

            ImGui.InputInt($"{LuminaWrapper.GetAddonText(11300)}", ref config.FilterJobTypeCountData.PureHealer);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;

            ImGui.InputInt($"{LuminaWrapper.GetAddonText(11301)}", ref config.FilterJobTypeCountData.ShieldHealer);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;
        }

        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.InputInt($"{LuminaWrapper.GetAddonText(1084)}", ref config.FilterJobTypeCountData.Melee);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;

            ImGui.InputInt($"{LuminaWrapper.GetAddonText(1085)}", ref config.FilterJobTypeCountData.PhysicalRanged);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;

            ImGui.InputInt($"{LuminaWrapper.GetAddonText(1086)}", ref config.FilterJobTypeCountData.MagicalRanged);
            if (ImGui.IsItemDeactivatedAfterEdit())
                changed = true;
        }

        if (changed)
            config.Save(this);
    }

    private void DrawRegexFilterSettings()
    {
        using var indent = ImRaii.PushIndent();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, Lang.Get("PartyFinderFilter-AddPreset")))
            config.BlackList.Add(new(true, string.Empty));

        ImGui.SameLine();
        DrawWorkModeSettings();

        var index = 0;

        foreach (var item in config.BlackList.ToList())
        {
            var enableState = item.Key;

            if (ImGui.Checkbox($"##available{index}", ref enableState))
            {
                config.BlackList[index] = new(enableState, item.Value);
                config.Save(this);
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
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("WorkMode")}:");

        ImGui.SameLine();
        if (ImGuiComponents.ToggleButton("ModeToggle", ref config.IsWhiteList))
            config.Save(this);

        ImGui.SameLine();
        ImGui.TextUnformatted(config.IsWhiteList ? Lang.Get("Whitelist") : Lang.Get("Blacklist"));

        ImGui.SameLine();
        ImGuiOm.HelpMarker(Lang.Get("PartyFinderFilter-WorkModeHelp"), 20f * GlobalUIScale);
    }

    private bool DrawRegexFilterItemText(int index, KeyValuePair<bool, string> item)
    {
        var value = item.Value;
        ImGui.InputText($"##{index}", ref value, 500);

        if (ImGui.IsItemDeactivatedAfterEdit())
            HandleRegexUpdate(index, item.Key, value);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"##Delete{index}", FontAwesomeIcon.Trash))
            config.BlackList.RemoveAt(index);
        return true;
    }

    private void HandleRegexUpdate(int index, bool key, string value)
    {
        try
        {
            _                             = new Regex(value);
            config.BlackList[index] = new(key, value);
            config.Save(this);
        }
        catch (ArgumentException)
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get("PartyFinderFilter-RegexError"));
            config = Config.Load(this) ?? new();
        }
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (batchIndex != args.BatchNumber)
        {
            isSecret   = listing.SearchArea.HasFlag(SearchAreaFlags.Private);
            isRaid     = listing.Category == DutyCategory.HighEndDuty;
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

    private bool FilterBySameDescription(IPartyFinderListing listing)
    {
        if (!config.FilterSameDescription)
            return true;

        var description = listing.Description.ToString();
        if (string.IsNullOrWhiteSpace(description))
            return true;

        return descriptionSet.Add((listing.RawDuty, description));
    }

    private bool FilterByRegexList(IPartyFinderListing listing)
    {
        var description = listing.Description.ToString();
        if (string.IsNullOrEmpty(description))
            return true;

        var isMatch = config.BlackList
                                  .Where(i => i.Key)
                                  .Any
                                  (item => Regex.IsMatch(listing.Name.ToString(), item.Value) ||
                                           Regex.IsMatch(description,             item.Value)
                                  );

        return config.IsWhiteList ? isMatch : !isMatch;
    }

    private bool FilterByHighEndSameJob(IPartyFinderListing listing)
    {
        if (!config.HighEndFilterSameJob) return true;
        if (!isRaid) return true;

        var job = LocalPlayerState.ClassJobData;
        // 生产职业 / 基础职业
        if (job.DohDolJobIndex != -1 || job.ClassJobParent.RowId == job.RowId)
            return true;

        foreach (var present in listing.JobsPresent)
        {
            if (present.RowId == LocalPlayerState.ClassJob)
                return false;
        }

        return true;
    }

    private bool FilterByHighEndSameRole(IPartyFinderListing listing)
    {
        if (!config.HighEndFilterRoleCount) return true;
        if (!isRaid) return true;

        var job = LocalPlayerState.ClassJobData;

        if (manualMode)
        {
            var filter0 = JobTypeCounter(1, config.FilterJobTypeCountData.Tank,           job);
            var filter1 = JobTypeCounter(2, config.FilterJobTypeCountData.PureHealer,     job);
            var filter2 = JobTypeCounter(6, config.FilterJobTypeCountData.ShieldHealer,   job);
            var filter3 = JobTypeCounter(3, config.FilterJobTypeCountData.Melee,          job);
            var filter4 = JobTypeCounter(4, config.FilterJobTypeCountData.PhysicalRanged, job);
            var filter5 = JobTypeCounter(5, config.FilterJobTypeCountData.MagicalRanged,  job);

            return filter0 && filter1 && filter2 && filter3 && filter4 && filter5;
        }

        return job.JobType switch
        {
            0 => true,
            1 => JobTypeCounter(1, config.FilterJobTypeCountData.Tank,           job),
            2 => JobTypeCounter(2, config.FilterJobTypeCountData.PureHealer,     job),
            3 => JobTypeCounter(3, config.FilterJobTypeCountData.Melee,          job),
            4 => JobTypeCounter(4, config.FilterJobTypeCountData.PhysicalRanged, job),
            5 => JobTypeCounter(5, config.FilterJobTypeCountData.MagicalRanged,  job),
            6 => JobTypeCounter(6, config.FilterJobTypeCountData.ShieldHealer,   job),
            _ => true
        };

        bool JobTypeCounter(int jobType, int maxCount, ClassJob currentJob)
        {
            if (maxCount == -1)
                return true;

            var count   = 0;
            var hasSlot = false;

            var slots       = listing.Slots.ToList();
            var jobsPresent = listing.JobsPresent.ToList();

            foreach (var i in Enumerable.Range(0, 8))
            {
                if (slots.Count <= i || jobsPresent.Count <= i || count >= maxCount)
                    break;

                if (jobsPresent.ElementAt(i).Value.RowId != 0)
                {
                    // 如果该位置已有玩家，检查职业类型
                    if (jobsPresent.ElementAt(i).Value.JobType == jobType)
                        count++;
                }
                else if (!hasSlot) // 有空位后不再检查
                {
                    // 检查空位是否允许当前角色类型
                    if (manualMode)
                    {
                        // 手动模式：检查所有同类角色是否有空位
                        foreach (var playerJob in LuminaGetter.Get<ClassJob>().Where(j => j.RowId != 0 && j.JobType == jobType))
                        {
                            if (Enum.TryParse<JobFlags>(playerJob.NameEnglish.ToString().Replace(" ", string.Empty), out var flag) &&
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
                        if (Enum.TryParse<JobFlags>(currentJob.NameEnglish.ToString().Replace(" ", string.Empty), out var flag) && slots.ElementAt(i)[flag])
                            hasSlot = true;
                    }
                }
            }

            return count < maxCount && hasSlot;
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        ToggleOverlayConfig(type == AddonEvent.PostSetup);

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        DService.Instance().PartyFinder.ReceiveListing -= OnReceiveListing;
    }

    private class Config : ModuleConfig
    {
        public List<KeyValuePair<bool, string>> BlackList = [];

        // T2, 血奶1, 盾奶1, 近2, 远1, 法2
        public (int Tank, int PureHealer, int ShieldHealer, int Melee, int PhysicalRanged, int MagicalRanged) FilterJobTypeCountData = (2, 1, 1, 2, 1, 2);

        public bool FilterSameDescription = true;

        public bool HighEndFilterRoleCount = true;
        public bool HighEndFilterSameJob   = true;

        public bool IsWhiteList;
    }
}
