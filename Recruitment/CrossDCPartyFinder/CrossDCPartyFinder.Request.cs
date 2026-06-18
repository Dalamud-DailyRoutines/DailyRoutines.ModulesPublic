using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.CrossDCPartyFinder;

public partial class CrossDCPartyFinder
{
    private void SendRequest(PartyFinderRequest req)
    {
        cancelSource?.Cancel();
        cancelSource?.Dispose();
        PartyFinderList.PartyFinderListing.ReleaseSlim();

        unsafe
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null || !agent->IsAgentActive()) return;
        }

        cancelSource = new();

        var testReq = req.Clone();
        testReq.PageSize = 1;

        // 收集用
        var bag = new ConcurrentBag<PartyFinderList.PartyFinderListing>();

        _ = Task.Run
        (
            async () =>
            {
                if (StandardTimeManager.Instance().Now - lastUpdate < TimeSpan.FromSeconds(30) && lastRequest.Equals(req))
                {
                    listingsDisplay = FilterAndSort(listings);
                    return;
                }

                isNeedToDisable = true;
                lastUpdate      = StandardTimeManager.Instance().Now;
                lastRequest     = req;

                var testResult = await testReq.Request().ConfigureAwait(false);

                // 没有数据就不继续请求了
                var totalPage = testResult.Overview.Total == 0 ? 0 : (testResult.Overview.Total + 99) / 100;
                if (totalPage == 0) return;

                var tasks = new List<Task>();
                Enumerable.Range(1, (int)totalPage).ForEach(x => tasks.Add(Gather((uint)x)));
                await Task.WhenAll(tasks).ConfigureAwait(false);

                listings = bag.OrderByDescending(x => x.TimeLeft)
                              .DistinctBy(x => x.ID)
                              .DistinctBy(x => $"{x.PlayerName}@{x.HomeWorldName}")
                              .ToList();
                listingsDisplay = FilterAndSort(listings);
            },
            cancelSource.Token
        ).ContinueWith
        (async t =>
            {
                isNeedToDisable = false;

                if (t is { IsFaulted: true, Exception.InnerException: PartyFinderApiException })
                {
                    listings    = listingsDisplay = [];
                    lastUpdate  = DateTime.MinValue;
                    lastRequest = new();
                    await DService.Instance().Framework.RunOnFrameworkThread
                    (() =>
                        {
                            unsafe
                            {
                                if (!LookingForGroup->IsAddonAndNodesReady()) return;
                                LookingForGroup->GetTextNodeById(49)->SetText($"{selectedDataCenter}: 0");
                            }
                        }
                    ).ConfigureAwait(false);
                    return;
                }

                NotifyHelper.Instance().NotificationInfo($"获取了 {listingsDisplay.Count} 条招募信息");

                await DService.Instance().Framework.RunOnFrameworkThread
                (() =>
                    {
                        unsafe
                        {
                            if (!LookingForGroup->IsAddonAndNodesReady()) return;
                            LookingForGroup->GetTextNodeById(49)->SetText($"{selectedDataCenter}: {listingsDisplay.Count}");
                        }
                    }
                ).ConfigureAwait(false);
            }
        );

        async Task Gather(uint page)
        {
            var clonedRequest = req.Clone();
            clonedRequest.Page = page;

            var result = await clonedRequest.Request().ConfigureAwait(false);
            bag.AddRange(result.Listings);
        }

        unsafe List<PartyFinderList.PartyFinderListing> FilterAndSort(IEnumerable<PartyFinderList.PartyFinderListing> source) =>
            source.Where
                  (x => string.IsNullOrWhiteSpace(currentSeach) ||
                        x.GetSearchString().Contains(currentSeach, StringComparison.OrdinalIgnoreCase)
                  )
                  .OrderByDescending(x => FlagStatusModule.Instance()->UIFlags[4] == 3 ? x.TimeLeft : 1 / x.TimeLeft)
                  .ToList();
    }

    private unsafe void SendRequestDynamic()
    {
        var req = lastRequest.Clone();

        req.DataCenter = selectedDataCenter;
        req.Category   = PartyFinderRequest.ParseCategory(AgentLookingForGroup.Instance());

        SendRequest(req);
        currentPage = 0;
    }

    private void ClearResources()
    {
        cancelSource?.Cancel();
        cancelSource?.Dispose();
        cancelSource = null;

        isNeedToDisable = false;

        listings = listingsDisplay = [];

        PartyFinderList.PartyFinderListing.ReleaseSlim();

        lastUpdate  = DateTime.MinValue;
        lastRequest = new();
    }

    private class PartyFinderRequest : IEquatable<PartyFinderRequest>
    {
        public uint          Page        { get; set; } = 1;
        public uint          PageSize    { get; set; } = 100;
        public DutyCategory? Category    { get; set; }
        public string        World       { get; set; } = string.Empty;
        public string        DataCenter  { get; set; } = string.Empty;
        public List<uint>    Jobs        { get; set; } = [];
        public uint          HomeWorldID { get; set; }
        public string        Region      { get; set; } = string.Empty;
        public uint          DutyID      { get; set; }
        public List<uint>    JobIds      { get; set; } = [];

        public bool Equals(PartyFinderRequest? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category    == other.Category    &&
                   World       == other.World       &&
                   DataCenter  == other.DataCenter  &&
                   HomeWorldID == other.HomeWorldID &&
                   Region      == other.Region      &&
                   DutyID      == other.DutyID      &&
                   Jobs.SequenceEqual(other.Jobs)   &&
                   JobIds.SequenceEqual(other.JobIds);
        }

        public async Task<PartyFinderList> Request()
        {
            var client   = HTTPClientHelper.Instance().Get();
            var response = await client.GetAsync(Format()).ConfigureAwait(false);
            var content  = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = JsonConvert.DeserializeObject<V2ErrorEnvelope>(content);
                throw new PartyFinderApiException(response.StatusCode, error?.Error?.Code, error?.Error?.Message);
            }

            return JsonConvert.DeserializeObject<PartyFinderList>(content) ?? new();
        }

        public string Format()
        {
            var builder = new StringBuilder();

            if (Page != 1)
                builder.Append($"&page={Page}");
            if (PageSize != 20)
                builder.Append($"&per_page={PageSize}");

            if (Category != null)
                builder.Append($"&category_id={(uint)Category.Value}");

            if (World != string.Empty)
                builder.Append($"&created_world_id={World}");
            if (HomeWorldID != 0)
                builder.Append($"&home_world_id={HomeWorldID}");
            if (DataCenter != string.Empty)
                builder.Append($"&datacenter={DataCenter}");
            if (Region != string.Empty)
                builder.Append($"&region={Region}");
            if (DutyID != 0)
                builder.Append($"&duty_id={DutyID}");
            if (JobIds.Count != 0)
                builder.Append($"&job_ids={string.Join(",", JobIds)}");
            else if (Jobs.Count != 0)
                builder.Append($"&job_ids={string.Join(",", Jobs)}");

            return $"{BASE_URL}{builder}";
        }

        public static unsafe DutyCategory? ParseCategory(AgentLookingForGroup* agent) =>
            agent->CategoryTab switch
            {
                1  => DutyCategory.Roulette,
                2  => DutyCategory.Dungeons,
                3  => DutyCategory.GuildQuests,
                4  => DutyCategory.Trials,
                5  => DutyCategory.Raids,
                6  => DutyCategory.HighEndDuty,
                7  => DutyCategory.PvP,
                8  => DutyCategory.GoldSaucer,
                9  => DutyCategory.FATEs,
                10 => DutyCategory.TreasureHunts,
                11 => DutyCategory.TheHunt,
                12 => DutyCategory.GatheringForays,
                13 => DutyCategory.DeepDungeons,
                14 => DutyCategory.FieldOperations,
                15 => DutyCategory.VCDungeonFinder,
                16 => DutyCategory.None,
                _  => null
            };

        public static string ParseCategoryIDToLoc(DutyCategory categoryID) =>
            categoryID switch
            {
                DutyCategory.Roulette        => LuminaWrapper.GetAddonText(8605),
                DutyCategory.Dungeons        => LuminaWrapper.GetAddonText(8607),
                DutyCategory.GuildQuests     => LuminaWrapper.GetAddonText(8606),
                DutyCategory.Trials          => LuminaWrapper.GetAddonText(8608),
                DutyCategory.Raids           => LuminaWrapper.GetAddonText(8609),
                DutyCategory.HighEndDuty     => LuminaWrapper.GetAddonText(10822),
                DutyCategory.PvP             => LuminaWrapper.GetAddonText(8610),
                DutyCategory.GoldSaucer      => LuminaWrapper.GetAddonText(8612),
                DutyCategory.FATEs           => LuminaWrapper.GetAddonText(8601),
                DutyCategory.TreasureHunts   => LuminaWrapper.GetAddonText(8107),
                DutyCategory.TheHunt         => LuminaWrapper.GetAddonText(8613),
                DutyCategory.GatheringForays => LuminaWrapper.GetAddonText(2306),
                DutyCategory.DeepDungeons    => LuminaWrapper.GetAddonText(2304),
                DutyCategory.FieldOperations => LuminaWrapper.GetAddonText(2307),
                DutyCategory.VCDungeonFinder => LuminaGetter.GetRowOrDefault<ContentType>(30).Name.ToString(),
                DutyCategory.None            => LuminaWrapper.GetAddonText(7),
                _                            => string.Empty
            };

        public static uint ParseCategoryIDToIconID(DutyCategory categoryID) =>
            categoryID switch
            {
                DutyCategory.Roulette        => 61807,
                DutyCategory.Dungeons        => 61801,
                DutyCategory.GuildQuests     => 61803,
                DutyCategory.Trials          => 61804,
                DutyCategory.Raids           => 61802,
                DutyCategory.HighEndDuty     => 61832,
                DutyCategory.PvP             => 61806,
                DutyCategory.GoldSaucer      => 61820,
                DutyCategory.FATEs           => 61809,
                DutyCategory.TreasureHunts   => 61808,
                DutyCategory.TheHunt         => 61819,
                DutyCategory.GatheringForays => 61815,
                DutyCategory.DeepDungeons    => 61824,
                DutyCategory.FieldOperations => 61837,
                DutyCategory.VCDungeonFinder => 61846,
                _                            => 0
            };

        public PartyFinderRequest Clone() =>
            new()
            {
                Page        = Page,
                PageSize    = PageSize,
                Category    = Category,
                World       = World,
                DataCenter  = DataCenter,
                Jobs        = [..Jobs],
                HomeWorldID = HomeWorldID,
                Region      = Region,
                DutyID      = DutyID,
                JobIds      = [..JobIds]
            };

        public override bool Equals(object? obj) =>
            obj != null && Equals(obj as PartyFinderRequest);

        public override int GetHashCode() =>
            HashCode.Combine
            (
                Category,
                World,
                DataCenter,
                HomeWorldID,
                Region,
                DutyID,
                Jobs.Aggregate(0, HashCode.Combine),
                JobIds.Aggregate(0, HashCode.Combine)
            );

        public static bool operator ==(PartyFinderRequest? left, PartyFinderRequest? right) =>
            Equals(left, right);

        public static bool operator !=(PartyFinderRequest? left, PartyFinderRequest? right) =>
            !Equals(left, right);
    }

    private class PartyFinderList
    {
        [JsonProperty("data")]
        public List<PartyFinderListing> Listings { get; set; } = [];

        [JsonProperty("pagination")]
        public PartyFinderOverview Overview { get; set; } = new();

        public class PartyFinderListing : IEquatable<PartyFinderListing>
        {
            private static readonly SemaphoreSlim DetailSemaphoreSlim = new(Environment.ProcessorCount);

            private uint categoryIcon;

            private Task<string>? detailReuqestTask;

            [JsonProperty("id")]
            public string ID { get; set; }

            [JsonProperty("player_name")]
            public string PlayerName { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("created_world_id")]
            public uint CreatedAtWorldID { get; set; }

            [JsonProperty("home_world_id")]
            public uint HomeWorldID { get; set; }

            [JsonProperty("category_id")]
            public DutyCategory CategoryID { get; set; }

            [JsonProperty("duty_id")]
            public uint DutyID { get; set; }

            [JsonProperty("min_item_level")]
            public uint MinItemLevel { get; set; }

            [JsonProperty("slots_filled")]
            public int SlotFilled { get; set; }

            [JsonProperty("slots_available")]
            public int SlotAvailable { get; set; }

            [JsonProperty("time_left_seconds")]
            public float TimeLeft { get; set; }

            public PartyFinderListingDetail? Detail { get; set; }

            public uint CategoryIcon
            {
                get
                {
                    if (categoryIcon != 0) return categoryIcon;
                    return categoryIcon = PartyFinderRequest.ParseCategoryIDToIconID(CategoryID);
                }
            }

            public string CreatedAtWorldName =>
                LuminaGetter.GetRowOrDefault<World>(CreatedAtWorldID).Name.ToString();

            public string HomeWorldName =>
                LuminaGetter.GetRowOrDefault<World>(HomeWorldID).Name.ToString();

            public string Duty =>
                CategoryID switch
                {
                    DutyCategory.Roulette     => LuminaWrapper.GetContentRouletteName(DutyID),
                    DutyCategory.DeepDungeons => LuminaWrapper.GetDeepDungeonName(DutyID),
                    _                         => LuminaWrapper.GetContentName(DutyID)
                };

            public string CategoryName =>
                PartyFinderRequest.ParseCategoryIDToLoc(CategoryID);

            public bool Equals(PartyFinderListing? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return ID == other.ID;
            }

            public static void ReleaseSlim() => DetailSemaphoreSlim.Release();

            public async Task RequestAsync()
            {
                if (Detail != null || detailReuqestTask != null) return;

                detailReuqestTask = RequestContentAsync();
                Detail            = JsonConvert.DeserializeObject<PartyFinderDetailResponse>(await detailReuqestTask.ConfigureAwait(false))?.Data ?? new();

                async Task<string> RequestContentAsync()
                {
                    var client   = HTTPClientHelper.Instance().Get();
                    var response = await client.GetAsync($"{BASE_DETAIL_URL}{ID}").ConfigureAwait(false);
                    var content  = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = JsonConvert.DeserializeObject<V2ErrorEnvelope>(content);
                        throw new PartyFinderApiException(response.StatusCode, error?.Error?.Code, error?.Error?.Message);
                    }

                    return content;
                }
            }

            public string GetSearchString() =>
                $"{PlayerName}_{Description}_{CategoryName}_{Duty}";
        }

        public class PartyFinderOverview
        {
            [JsonProperty("total")]
            public uint Total { get; set; }
        }
    }

    private class PartyFinderDetailResponse
    {
        [JsonProperty("data")]
        public PartyFinderListingDetail Data { get; set; } = new();
    }

    private class PartyFinderListingDetail
    {
        [JsonProperty("slots")]
        public List<Slot> Slots { get; set; }

        public class Slot
        {
            [JsonProperty("filled")]
            public bool Filled { get; set; }

            [JsonProperty("role_id")]
            public uint RoleID { get; set; }

            [JsonProperty("filled_job_id")]
            public uint? FilledJobID { get; set; }

            [JsonProperty("accepted_job_ids")]
            public List<uint> AcceptedJobIDs { get; set; }

            public List<uint> JobIcons
            {
                get
                {
                    if (field != null) return field;

                    var icons = new List<uint>();

                    if (FilledJobID != null)
                        icons.Add(62100 + FilledJobID.Value);
                    else if (AcceptedJobIDs.Count != 0)
                    {
                        var role = RoleID;

                        switch (role)
                        {
                            case 1:
                                icons.Add(62571);
                                break;
                            case 2:
                            case 3:
                                icons.Add(62573);
                                break;
                            case 4:
                                icons.Add(62572);
                                break;
                            default:
                            {
                                foreach (var jobID in AcceptedJobIDs)
                                    icons.Add(62100 + jobID);
                                break;
                            }
                        }
                    }
                    else icons.Add(62145);

                    field = icons;
                    return field;
                }
            }
        }
    }

    #region 常量

    private const string BASE_URL        = "https://xivpf.littlenightmare.top/api/v2/listings?";
    private const string BASE_DETAIL_URL = "https://xivpf.littlenightmare.top/api/v2/listings/";

    #endregion

    private class V2ErrorEnvelope
    {
        [JsonProperty("error")]
        public V2Error? Error { get; set; }
    }

    private class V2Error
    {
        [JsonProperty("code")]
        public string? Code { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("details")]
        public string? Details { get; set; }
    }

    private class PartyFinderApiException
    (
        HttpStatusCode statusCode,
        string?        errorCode,
        string?        message
    ) : Exception(message ?? $"API error: {statusCode}")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string?        ErrorCode  { get; } = errorCode;
    }
}
