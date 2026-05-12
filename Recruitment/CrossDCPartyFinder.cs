using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Text;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;
using NotifyHelper = OmenTools.OmenService.NotifyHelper;

namespace DailyRoutines.ModulesPublic;

public class CrossDCPartyFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "跨大区队员招募",
        Description = "为队员招募界面新增大区切换按钮, 以选择并查看由众包网站提供的其他大区的招募信息",
        Category    = ModuleCategory.Recruitment
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true, CNDefaultEnabled = true };
    
    private static string LocatedDataCenter =>
        GameState.CurrentDataCenterData.Name.ToString();

    private Config config = null!;

    private List<string> dataCenters = [];

    private CancellationTokenSource? cancelSource;

    private List<PartyFinderList.PartyFinderListing> listings        = [];
    private List<PartyFinderList.PartyFinderListing> listingsDisplay = [];
    private DateTime                                 lastUpdate      = DateTime.MinValue;

    private bool isNeedToDisable;

    private PartyFinderRequest lastRequest  = new();
    private string             currentSeach = string.Empty;

    private int currentPage;

    private string selectedDataCenter = string.Empty;

    private Dictionary<string, CheckboxNode> checkboxNodes = [];
    private HorizontalListNode?              layoutNode;

    protected override unsafe void Init()
    {
        config  =   Config.Load(this) ?? new();
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
        if (LookingForGroup->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);

        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PostReceiveEvent, Dalamud.Game.Agent.AgentId.LookingForGroup, OnAgent);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);

        ClearResources();

        ClearNodes();
    }

    protected override unsafe void OverlayUI()
    {
        var addon = LookingForGroup;

        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (selectedDataCenter == LocatedDataCenter) return;

        var nodeInfo0  = addon->GetNodeById(38)->GetNodeState();
        var nodeInfo1  = addon->GetNodeById(31)->GetNodeState();
        var nodeInfo2  = addon->GetNodeById(41)->GetNodeState();
        var size       = nodeInfo0.Size + new Vector2(0, nodeInfo1.Height + nodeInfo2.Height);
        var sizeOffset = new Vector2(4, 4);
        ImGui.SetNextWindowPos(new(addon->GetNodeById(31)->ScreenX - 4f, addon->GetNodeById(31)->ScreenY));
        ImGui.SetNextWindowSize(size + 2 * sizeOffset);

        if (ImGui.Begin
            (
                "###CrossDCPartyFinder_PartyListWindow",
                ImGuiWindowFlags.NoTitleBar            |
                ImGuiWindowFlags.NoResize              |
                ImGuiWindowFlags.NoDocking             |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoCollapse            |
                ImGuiWindowFlags.NoScrollbar           |
                ImGuiWindowFlags.NoScrollWithMouse
            ))
        {
            var isNeedToResetY = false;

            using (ImRaii.Disabled(isNeedToDisable))
            {
                if (ImGui.Checkbox("倒序", ref config.OrderByDescending))
                {
                    isNeedToResetY = true;

                    config.Save(this);
                    SendRequestDynamic();
                }

                var totalPages = (int)Math.Ceiling(listingsDisplay.Count / (float)config.PageSize);

                using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0)))
                {
                    ImGui.SameLine(0, 4f * GlobalUIScale);

                    if (ImGui.Button("<<"))
                    {
                        isNeedToResetY = true;
                        currentPage    = 0;
                    }

                    ImGui.SameLine();

                    if (ImGui.Button("<"))
                    {
                        isNeedToResetY = true;
                        currentPage    = Math.Max(0, currentPage - 1);
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted($" {currentPage + 1} / {Math.Max(1, totalPages)} ");
                    ImGuiOm.TooltipHover($"{listingsDisplay.Count}");

                    ImGui.SameLine();

                    if (ImGui.Button(">"))
                    {
                        isNeedToResetY = true;
                        currentPage    = Math.Min(totalPages - 1, currentPage + 1);
                    }

                    ImGui.SameLine();

                    if (ImGui.Button(">>"))
                    {
                        isNeedToResetY = true;
                        currentPage    = Math.Max(0, totalPages - 1);
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("关闭"))
                    selectedDataCenter = LocatedDataCenter;

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("###SearchString", Lang.Get("PleaseSearch"), ref currentSeach, 128);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    isNeedToResetY = true;
                    SendRequestDynamic();
                }
            }

            var sizeAfter = size - new Vector2(0, ImGui.GetTextLineHeightWithSpacing());

            using (var child = ImRaii.Child("Child", sizeAfter, false, ImGuiWindowFlags.NoBackground))
            {
                if (child)
                {
                    if (isNeedToResetY)
                        ImGui.SetScrollHereY();
                    if (!isNeedToDisable)
                        DrawPartyFinderList(sizeAfter);

                    ImGuiOm.ScaledDummy(8f);
                }
            }

            ImGui.End();
        }
    }

    private unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        ClearResources();

        dataCenters = LuminaGetter.Get<WorldDCGroupType>()
                                  .Where(x => x.Region.RowId == GameState.HomeDataCenterData.Region.RowId)
                                  .Select(x => x.Name.ToString())
                                  .ToList();
        selectedDataCenter = GameState.CurrentDataCenterData.Name.ToString();

        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen = true;

                layoutNode = new()
                {
                    IsVisible = true,
                    Position  = new(85, 8)
                };

                foreach (var dataCenter in dataCenters)
                {
                    var node = new CheckboxNode
                    {
                        Size      = new(100f, 28f),
                        IsVisible = true,
                        IsChecked = dataCenter == selectedDataCenter,
                        IsEnabled = true,
                        String    = dataCenter,
                        OnClick = _ =>
                        {
                            selectedDataCenter = dataCenter;

                            foreach (var x in checkboxNodes)
                                x.Value.IsChecked = x.Key == dataCenter;

                            if (LocatedDataCenter == dataCenter)
                            {
                                AgentId.LookingForGroup.SendEvent(1, 17);
                                return;
                            }

                            SendRequestDynamic();
                            isNeedToDisable = true;
                        }
                    };

                    checkboxNodes[dataCenter] = node;

                    layoutNode.AddNode(node);
                }

                layoutNode.AttachNode(LookingForGroup->GetComponentNodeById(51));
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                ClearNodes();
                break;
        }
    }

    private unsafe void OnAgent(AgentEvent type, AgentArgs args)
    {
        var agent = args.Agent.ToStruct<AgentLookingForGroup>();
        if (agent == null) return;

        var formatted = args as AgentReceiveEventArgs;
        var atkValues = (AtkValue*)formatted.AtkValues;

        if (selectedDataCenter != LocatedDataCenter)
        {
            // 招募类别刷新
            if (formatted is { EventKind: 1, ValueCount: 3 } && atkValues[1].Type == AtkValueType.UInt)
                SendRequestDynamic();

            // 招募刷新
            if (formatted is { EventKind: 1, ValueCount: 1 } && atkValues[0].Type == AtkValueType.Int && atkValues[0].Int == 17)
                SendRequestDynamic();
        }
    }

    private void DrawPartyFinderList(Vector2 size)
    {
        using var table = ImRaii.Table("###ListingsTable", 3, ImGuiTableFlags.BordersInnerH, size);
        if (!table) return;

        ImGui.TableSetupColumn("招募图标", ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing() * 3 + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TableSetupColumn("招募详情", ImGuiTableColumnFlags.WidthStretch, 50);
        ImGui.TableSetupColumn("招募信息", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("八个汉字八个汉字").X);

        var startIndex = currentPage * config.PageSize;
        var pageItems  = listingsDisplay.Skip(startIndex).Take(config.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), cancelSource.Token).ConfigureAwait(false));

        var iconSize = new Vector2(ImGui.GetTextLineHeightWithSpacing() * 3) +
                       new Vector2(ImGui.GetStyle().ItemSpacing.X, 2    * ImGui.GetStyle().ItemSpacing.Y);
        var jobIconSize = new Vector2(ImGui.GetTextLineHeight());

        foreach (var listing in pageItems)
        {
            using var id = ImRaii.PushId(listing.ID);

            var lineEndPosY = 0f;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (DService.Instance().Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture))
            {
                ImGui.Spacing();

                ImGui.Image(categoryTexture.GetWrapOrEmpty().Handle, iconSize);

                ImGui.Spacing();

                lineEndPosY = ImGui.GetCursorPosY();
            }

            // 招募详情
            ImGui.TableNextColumn();

            using (ImRaii.Group())
            {
                using (FontManager.Instance().UIFont120.Push())
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f * GlobalUIScale);
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{listing.Duty}");
                }

                ImGui.SameLine(0, 8f * GlobalUIScale);
                var startCursorPos = ImGui.GetCursorPos();

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.DarkGray.ToVector4()))
                using (FontManager.Instance().UIFont90.Push())
                using (ImRaii.Group())
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f * GlobalUIScale);
                    ImGuiOm.RenderPlayerInfo(listing.PlayerName, listing.HomeWorldName);
                }

                ImGui.SameLine();
                ImGui.SetCursorPos(startCursorPos);
                ImGui.InvisibleButton($"PlayerName##{listing.ID}", ImGui.CalcTextSize($"{listing.PlayerName}@{listing.HomeWorldName}"));

                ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName}");
                ImGuiOm.ClickToCopyAndNotify($"{listing.PlayerName}@{listing.HomeWorldName}");

                var isDescEmpty = string.IsNullOrEmpty(listing.Description);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 2f * GlobalUIScale);
                using (FontManager.Instance().UIFont80.Push())
                    ImGui.TextWrapped(isDescEmpty ? $"({LuminaWrapper.GetAddonText(11100)})" : $"{listing.Description}");
                ImGui.Spacing();

                if (!isDescEmpty)
                    ImGuiOm.TooltipHover(listing.Description);
                if (!isDescEmpty)
                    ImGuiOm.ClickToCopyAndNotify(listing.Description);

                lineEndPosY = MathF.Max(ImGui.GetCursorPosY(), lineEndPosY);
            }

            if (listing.Detail != null)
            {
                using (ImRaii.Group())
                {
                    foreach (var slot in listing.Detail.Slots)
                    {
                        if (slot.JobIcons.Count == 0) continue;

                        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.5f, !slot.Filled))
                        {
                            var displayIcon = slot.JobIcons.Count > 1 ? 62146 : slot.JobIcons[0];

                            if (DService.Instance().Texture.TryGetFromGameIcon(new(displayIcon), out var jobTexture))
                            {
                                ImGui.Image(jobTexture.GetWrapOrEmpty().Handle, jobIconSize);

                                ImGui.SameLine();
                            }
                        }
                    }

                    ImGui.Spacing();

                    if (listing.MinItemLevel > 0)
                    {
                        ImGui.SameLine(0, 6f * GlobalUIScale);
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, listing.MinItemLevel != 0))
                            ImGui.TextUnformatted($"[{listing.MinItemLevel}]");
                    }
                }

                lineEndPosY = MathF.Max(ImGui.GetCursorPosY(), lineEndPosY);
            }

            // 招募信息
            ImGui.TableNextColumn();

            ImGui.SetCursorPosY(lineEndPosY - 3 * ImGui.GetTextLineHeightWithSpacing() - 4 * ImGui.GetStyle().ItemSpacing.Y);

            using (ImRaii.Group())
            using (FontManager.Instance().UIFont80.Push())
            {
                ImGui.NewLine();

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "当前位于:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{listing.CreatedAtWorldName}");

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "剩余人数:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{listing.SlotAvailable - listing.SlotFilled}");

                ImGui.TextColored(KnownColor.Orange.ToVector4(), "剩余时间:");

                ImGui.SameLine();
                ImGui.TextUnformatted($"{TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0} 分钟");
            }

            ImGui.TableNextRow();
        }
    }

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
                    listingsDisplay = FilterAndSort(this.listings);
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

                this.listings = bag.OrderByDescending(x => x.TimeLeft)
                                        .DistinctBy(x => x.ID)
                                        .DistinctBy(x => $"{x.PlayerName}@{x.HomeWorldName}")
                                        .ToList();
                listingsDisplay = FilterAndSort(this.listings);
            },
            cancelSource.Token
        ).ContinueWith
        (async t =>
            {
                isNeedToDisable = false;

                if (t.IsFaulted && t.Exception?.InnerException is PartyFinderApiException)
                {
                    listings = listingsDisplay = [];
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

        List<PartyFinderList.PartyFinderListing> FilterAndSort(IEnumerable<PartyFinderList.PartyFinderListing> source)
        {
            return source.Where
                         (x => string.IsNullOrWhiteSpace(currentSeach) ||
                               x.GetSearchString().Contains(currentSeach, StringComparison.OrdinalIgnoreCase)
                         )
                         .OrderByDescending(x => config.OrderByDescending ? x.TimeLeft : 1 / x.TimeLeft)
                         .ToList();
        }
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

    private void ClearNodes()
    {
        layoutNode?.Dispose();
        layoutNode = null;

        checkboxNodes.Values.ForEach(x => x?.Dispose());
        checkboxNodes.Clear();
    }

    private class Config : ModuleConfig
    {
        public bool OrderByDescending = true;
        public int  PageSize          = 50;
    }

    private class PartyFinderRequest : IEquatable<PartyFinderRequest>
    {
        public uint       Page          { get; set; } = 1;
        public uint       PageSize      { get; set; } = 100;
        public DutyCategory? Category   { get; set; }
        public string     World         { get; set; } = string.Empty;
        public string     DataCenter    { get; set; } = string.Empty;
        public List<uint> Jobs          { get; set; } = [];
        public uint       HomeWorldId   { get; set; }
        public string     Region        { get; set; } = string.Empty;
        public uint       DutyId        { get; set; }
        public List<uint> JobIds        { get; set; } = [];

        public bool Equals(PartyFinderRequest? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category == other.Category && World == other.World && DataCenter == other.DataCenter &&
                   HomeWorldId == other.HomeWorldId && Region == other.Region && DutyId == other.DutyId &&
                   Jobs.SequenceEqual(other.Jobs) && JobIds.SequenceEqual(other.JobIds);
        }

        public async Task<PartyFinderList> Request()
        {
            var client = HTTPClientHelper.Instance().Get();
            var response = await client.GetAsync(Format()).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

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
            if (HomeWorldId != 0)
                builder.Append($"&home_world_id={HomeWorldId}");
            if (DataCenter != string.Empty)
                builder.Append($"&datacenter={DataCenter}");
            if (Region != string.Empty)
                builder.Append($"&region={Region}");
            if (DutyId != 0)
                builder.Append($"&duty_id={DutyId}");
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
                Page       = Page,
                PageSize   = PageSize,
                Category   = Category,
                World      = World,
                DataCenter = DataCenter,
                Jobs       = [..Jobs],
                HomeWorldId = HomeWorldId,
                Region     = Region,
                DutyId     = DutyId,
                JobIds     = [..JobIds]
            };

        public override bool Equals(object? obj) =>
            obj != null && Equals(obj as PartyFinderRequest);

        public override int GetHashCode() =>
            HashCode.Combine
            (
                Category, World, DataCenter, HomeWorldId, Region, DutyId,
                Jobs.Aggregate(0, (hash, job) => HashCode.Combine(hash, job)),
                JobIds.Aggregate(0, (hash, job) => HashCode.Combine(hash, job))
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
            public uint CreatedAtWorldId { get; set; }

            [JsonProperty("home_world_id")]
            public uint HomeWorldId { get; set; }

            [JsonProperty("category_id")]
            public DutyCategory CategoryId { get; set; }

            [JsonProperty("duty_id")]
            public uint DutyId { get; set; }

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
                    return categoryIcon = PartyFinderRequest.ParseCategoryIDToIconID(CategoryId);
                }
            }

            public string CreatedAtWorldName =>
                LuminaGetter.GetRowOrDefault<World>(CreatedAtWorldId).Name.ToString();

            public string HomeWorldName =>
                LuminaGetter.GetRowOrDefault<World>(HomeWorldId).Name.ToString();

            public string Duty =>
                LuminaWrapper.GetContentName(DutyId);

            public string CategoryName =>
                PartyFinderRequest.ParseCategoryIDToLoc(CategoryId);

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
                    var client = HTTPClientHelper.Instance().Get();
                    var response = await client.GetAsync($"{BASE_DETAIL_URL}{ID}").ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

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
            public uint RoleId { get; set; }

            [JsonProperty("filled_job_id")]
            public uint? FilledJobId { get; set; }

            [JsonProperty("accepted_job_ids")]
            public List<uint> AcceptedJobIds { get; set; }

            private List<uint>? field;

            public List<uint> JobIcons
            {
                get
                {
                    if (field != null) return field;

                    var icons = new List<uint>();

                    if (FilledJobId != null)
                    {
                        icons.Add(62100 + FilledJobId.Value);
                    }
                    else if (AcceptedJobIds.Count != 0)
                    {
                        var role = RoleId;
                        if (role == 1)
                            icons.Add(62571);
                        else if (role == 2 || role == 3)
                            icons.Add(62573);
                        else if (role == 4)
                            icons.Add(62572);
                        else
                        {
                            foreach (var jobId in AcceptedJobIds)
                                icons.Add(62100 + jobId);
                        }
                    }
                    else
                    {
                        icons.Add(62145);
                    }

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

    #region v2 Error Handling

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

    private class PartyFinderApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string? ErrorCode { get; }

        public PartyFinderApiException(HttpStatusCode statusCode, string? errorCode, string? message)
            : base(message ?? $"API error: {statusCode}")
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }

    #endregion
}
