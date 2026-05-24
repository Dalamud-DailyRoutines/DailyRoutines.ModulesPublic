using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.BetterPartyFilter;

public partial class BetterPartyFinderFilter : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterPartyFinderFilterTitle"),
        Description = Lang.Get("BetterPartyFinderFilterDescription"),
        Category    = ModuleCategory.Recruitment,
        Author      = ["status102"],
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/BetterPartyFinderFilter/preview-1.png",
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/BetterPartyFinderFilter/preview-2.png",
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/BetterPartyFinderFilter/preview-3.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static uint NotifyNewRecruitment
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("PartyFinderNewArrivalDisp");
        set => DService.Instance().GameConfig.UiConfig.Set("PartyFinderNewArrivalDisp", value);
    }

    private Config config = null!;

    private int  batchIndex;
    private bool isSecret;
    private bool isRaid;
    private bool manualMode;

    private readonly HashSet<(ushort, string)> descriptionSet = [];

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        DService.Instance().PartyFinder.ReceiveListing += OnReceiveListing;

        addon ??= new(this)
        {
            InternalName          = "DRBetterPartyFinderFilter",
            Title                 = Info.Title,
            Size                  = new(400f, 220f),
            RememberClosePosition = false
        };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "LookingForGroup", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().PartyFinder.ReceiveListing -= OnReceiveListing;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        addon?.Dispose();
        addon = null;

        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static unsafe void RefreshDisplaySettings
    (
        bool? displayBlacklisted = null,
        bool? displayLocked      = null,
        bool? notifyRecruitment  = null,
        uint? notifyInterval     = null,
        bool? noNotifyWhenZero   = null
    )
    {
        displayBlacklisted ??= FlagStatusModule.Instance()->UIFlags[12] == 1;
        displayLocked      ??= FlagStatusModule.Instance()->UIFlags[7]  == 0;
        notifyInterval     ??= FlagStatusModule.Instance()->UIFlags[5];
        noNotifyWhenZero   ??= FlagStatusModule.Instance()->UIFlags[6] == 1;
        notifyRecruitment  ??= NotifyNewRecruitment                    == 1;

        var flag0 = displayBlacklisted.Value ? 0 : 0x20000;
        var flag1 = displayLocked.Value ? 0 : 0x10000;
        var flag2 = noNotifyWhenZero.Value ? 0x10000 : 0;
        var flag3 = notifyRecruitment.Value ? 0x1 : 0;

        var targetValue0 = displayBlacklisted.Value ? 1 : 0;
        var targetValue1 = displayLocked.Value ? 0 : 1;
        var targetValue2 = noNotifyWhenZero.Value ? 1 : 0;
        var targetValue3 = notifyRecruitment.Value ? 1 : 0;

        if (FlagStatusModule.Instance()->UIFlags[12] == targetValue0         &&
            FlagStatusModule.Instance()->UIFlags[7]  == targetValue1         &&
            FlagStatusModule.Instance()->UIFlags[5]  == notifyInterval.Value &&
            FlagStatusModule.Instance()->UIFlags[6]  == targetValue2         &&
            NotifyNewRecruitment                     == targetValue3)
            return;

        AgentId.LookingForGroup.SendEvent(13, 0, (uint)(flag2 + notifyInterval.Value), (uint)(flag0 + flag1 + flag3));
        NotifyNewRecruitment = (uint)targetValue3;
    }

    private void HandleRegexUpdate(int index, bool key, string value)
    {
        try
        {
            _                       = new Regex(value);
            config.BlackList[index] = new(key, value);
            config.Save(this);
        }
        catch (ArgumentException)
        {
            NotifyHelper.Instance().NotificationWarning(Lang.Get("BetterPartyFinderFilter-RegexError"));
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

    private class Config : ModuleConfig
    {
        public override string PreviousModuleName => "PartyFinderFilter";

        public List<KeyValuePair<bool, string>> BlackList = [];

        // T2, 血奶1, 盾奶1, 近2, 远1, 法2
        public (int Tank, int PureHealer, int ShieldHealer, int Melee, int PhysicalRanged, int MagicalRanged) FilterJobTypeCountData = (2, 1, 1, 2, 1, 2);

        public bool FilterSameDescription = true;

        public bool HighEndFilterRoleCount = true;
        public bool HighEndFilterSameJob   = true;

        public bool IsWhiteList;
    }
}
