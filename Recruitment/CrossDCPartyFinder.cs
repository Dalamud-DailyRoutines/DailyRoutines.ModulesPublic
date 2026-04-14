using System.Collections.Concurrent;
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
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

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

        if (DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
        {
            dataCenters = LuminaGetter.Get<WorldDCGroupType>()
                                      .Where(x => x.Region == localPlayer.HomeWorld.Value.DataCenter.Value.Region)
                                      .Select(x => x.Name.ToString())
                                      .ToList();
            selectedDataCenter = GameState.CurrentDataCenterData.Name.ToString();
        }

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
            if (formatted is { EventKind: 1, ValueCount: 3 } && atkValues[1].Type == ValueType.UInt)
                SendRequestDynamic();

            // 招募刷新
            if (formatted is { EventKind: 1, ValueCount: 1 } && atkValues[0].Type == ValueType.Int && atkValues[0].Int == 17)
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
        (async _ =>
            {
                isNeedToDisable = false;

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
        public uint       Page       { get; set; } = 1;
        public uint       PageSize   { get; set; } = 100;
        public string     Category   { get; set; } = string.Empty;
        public string     World      { get; set; } = string.Empty;
        public string     DataCenter { get; set; } = string.Empty;
        public List<uint> Jobs       { get; set; } = [];

        public bool Equals(PartyFinderRequest? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category == other.Category && World == other.World && DataCenter == other.DataCenter;
        }

        public async Task<PartyFinderList> Request() =>
            JsonConvert.DeserializeObject<PartyFinderList>(await HTTPClientHelper.Instance().Get().GetStringAsync(Format())) ?? new();

        public string Format()
        {
            var builder = new StringBuilder();

            if (Page != 1)
                builder.Append($"&page={Page}");
            if (PageSize != 20)
                builder.Append($"&per_page={PageSize}");

            if (Category != string.Empty)
            {
                if (Category.Contains(' '))
                    builder.Append($"&category=\"{Category}\"");
                else
                    builder.Append($"&category={Category}");
            }

            if (World != string.Empty)
                builder.Append($"&world={World}");
            if (DataCenter != string.Empty)
                builder.Append($"&datacenter={DataCenter}");
            if (Jobs.Count != 0)
                builder.Append($"&jobs=\"{string.Join(",", Jobs)}\"");

            return $"{BASE_URL}{builder}";
        }

        public static unsafe string ParseCategory(AgentLookingForGroup* agent) =>
            agent->CategoryTab switch
            {
                1  => "DutyRoulette",
                2  => "Dungeons",
                3  => "Guildhests",
                4  => "Trials",
                5  => "Raids",
                6  => "HighEndDuty",
                7  => "Pvp",
                8  => "GoldSaucer",
                9  => "Fates",
                10 => "TreasureHunt",
                11 => "TheHunt",
                12 => "GatheringForays",
                13 => "DeepDungeons",
                14 => "FieldOperations",
                15 => "V&C Dungeon Finder",
                16 => "None",
                _  => string.Empty
            };

        public static uint ParseOnlineCategoryToID(string onlineCategory) =>
            onlineCategory.Trim() switch
            {
                "DutyRoulette"       => 1,
                "Dungeons"           => 2,
                "Guildhests"         => 3,
                "Trials"             => 4,
                "Raids"              => 5,
                "HighEndDuty"        => 6,
                "Pvp"                => 7,
                "GoldSaucer"         => 8,
                "Fates"              => 9,
                "TreasureHunt"       => 10,
                "TheHunt"            => 11,
                "GatheringForays"    => 12,
                "DeepDungeons"       => 13,
                "DeepDungeon"        => 13,
                "FieldOperations"    => 14,
                "V&C Dungeon Finder" => 15,
                "None"               => 16,
                _                    => 0
            };

        public static string ParseCategoryIDToLoc(uint categoryID) =>
            categoryID switch
            {
                1  => LuminaWrapper.GetAddonText(8605),
                2  => LuminaWrapper.GetAddonText(8607),
                3  => LuminaWrapper.GetAddonText(8606),
                4  => LuminaWrapper.GetAddonText(8608),
                5  => LuminaWrapper.GetAddonText(8609),
                6  => LuminaWrapper.GetAddonText(10822),
                7  => LuminaWrapper.GetAddonText(8610),
                8  => LuminaWrapper.GetAddonText(8612),
                9  => LuminaWrapper.GetAddonText(8601),
                10 => LuminaWrapper.GetAddonText(8107),
                11 => LuminaWrapper.GetAddonText(8613),
                12 => LuminaWrapper.GetAddonText(2306),
                13 => LuminaWrapper.GetAddonText(2304),
                14 => LuminaWrapper.GetAddonText(2307),
                15 => LuminaGetter.GetRowOrDefault<ContentType>(30).Name.ToString(),
                16 => LuminaWrapper.GetAddonText(7),
                _  => string.Empty
            };

        public static uint ParseCategoryIDToIconID(uint categoryID) =>
            categoryID switch
            {
                1  => 61807,
                2  => 61801,
                3  => 61803,
                4  => 61804,
                5  => 61802,
                6  => 61832,
                7  => 61806,
                8  => 61820,
                9  => 61809,
                10 => 61808,
                11 => 61819,
                12 => 61815,
                13 => 61824,
                14 => 61837,
                15 => 61846,
                _  => 0
            };

        public PartyFinderRequest Clone() =>
            new()
            {
                Page       = Page,
                PageSize   = PageSize,
                Category   = Category,
                World      = World,
                DataCenter = DataCenter,
                Jobs       = [..Jobs]
            };

        public override bool Equals(object? obj) =>
            obj != null && Equals(obj as PartyFinderRequest);

        public override int GetHashCode() =>
            HashCode.Combine(Category, World, DataCenter, Jobs);

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
            public int ID { get; set; }

            [JsonProperty("name")]
            public string PlayerName { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("created_world")]
            public string CreatedAtWorldName { get; set; }

            [JsonProperty("created_world_id")]
            public string CreatedAtWorld { get; set; }

            [JsonProperty("home_world")]
            public string HomeWorldName { get; set; }

            [JsonProperty("home_world_id")]
            public string HomeWorld { get; set; }

            [JsonProperty("datacenter")]
            public string DataCenter { get; set; }

            [JsonProperty("category")]
            public string CategoryName { get; set; }

            [JsonProperty("category_id")]
            public DutyCategory Category { get; set; }

            [JsonProperty("duty")]
            public string Duty { get; set; }

            [JsonProperty("min_item_level")]
            public uint MinItemLevel { get; set; }

            [JsonProperty("time_left")]
            public float TimeLeft { get; set; }

            [JsonProperty("updated_at")]
            public DateTime UpdatedAt { get; set; }

            [JsonProperty("is_cross_world")]
            public bool IsCrossWorld { get; set; }

            [JsonProperty("slots_filled")]
            public int SlotFilled { get; set; }

            [JsonProperty("slots_available")]
            public int SlotAvailable { get; set; }

            public PartyFinderListingDetail? Detail { get; set; }

            public uint CategoryIcon
            {
                get
                {
                    if (categoryIcon != 0) return categoryIcon;
                    return categoryIcon = PartyFinderRequest.ParseCategoryIDToIconID(PartyFinderRequest.ParseOnlineCategoryToID(CategoryName));
                }
            }

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

                detailReuqestTask = HTTPClientHelper.Instance().Get().GetStringAsync($"{BASE_DETAIL_URL}{ID}");
                Detail            = JsonConvert.DeserializeObject<PartyFinderListingDetail>(await detailReuqestTask.ConfigureAwait(false)) ?? new();
            }

            public string GetSearchString() =>
                $"{PlayerName}_{Description}_{PartyFinderRequest.ParseCategoryIDToLoc(PartyFinderRequest.ParseOnlineCategoryToID(CategoryName))}_{Duty}";
        }

        public class PartyFinderOverview
        {
            [JsonProperty("total")]
            public uint Total { get; set; }

            [JsonProperty("page")]
            public uint Page { get; set; }

            [JsonProperty("per_page")]
            public uint PerPage { get; set; }

            [JsonProperty("total_pages")]
            public uint TotalPages { get; set; }
        }
    }

    private class PartyFinderListingDetail
    {
        [JsonProperty("id")]
        public long ID { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("created_world")]
        public string CreatedAtWorld { get; set; }

        [JsonProperty("home_world")]
        public string HomeWorld { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("duty")]
        public string Duty { get; set; }

        [JsonProperty("min_item_level")]
        public int MinItemLevel { get; set; }

        [JsonProperty("slots_filled")]
        public int SlotsFilled { get; set; }

        [JsonProperty("slots_available")]
        public int SlotsAvailable { get; set; }

        [JsonProperty("time_left")]
        public double TimeLeft { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("is_cross_world")]
        public bool IsCrossWorld { get; set; }

        [JsonProperty("beginners_welcome")]
        public bool BeginnersWelcome { get; set; }

        [JsonProperty("duty_type")]
        public string DutyType { get; set; }

        [JsonProperty("objective")]
        public string Objective { get; set; }

        [JsonProperty("conditions")]
        public string Conditions { get; set; }

        [JsonProperty("loot_rules")]
        public string LootRules { get; set; }

        [JsonProperty("slots")]
        public List<Slot> Slots { get; set; }

        [JsonProperty("datacenter")]
        public string DataCenter { get; set; }

        public class Slot
        {
            public static readonly HashSet<string> BattleJobs;
            public static readonly HashSet<string> TankJobs;
            public static readonly HashSet<string> DPSJobs;
            public static readonly HashSet<string> HealerJobs;

            static Slot()
            {
                BattleJobs = LuminaGetter.Get<ClassJob>()
                                         .Where(x => x.RowId != 0 && x.DohDolJobIndex == -1)
                                         .Select(x => x.Abbreviation.ToString())
                                         .ToHashSet();

                TankJobs = LuminaGetter.Get<ClassJob>()
                                       .Where(x => x.RowId != 0 && x.Role is 1)
                                       .Select(x => x.Abbreviation.ToString())
                                       .ToHashSet();

                DPSJobs = LuminaGetter.Get<ClassJob>()
                                      .Where(x => x.RowId != 0 && x.Role is 2 or 3)
                                      .Select(x => x.Abbreviation.ToString())
                                      .ToHashSet();

                HealerJobs = LuminaGetter.Get<ClassJob>()
                                         .Where(x => x.RowId != 0 && x.Role is 4)
                                         .Select(x => x.Abbreviation.ToString())
                                         .ToHashSet();
            }

            [JsonProperty("filled")]
            public bool Filled { get; set; }

            [JsonProperty("role")]
            public string? RoleName { get; set; }

            [JsonProperty("role_id")]
            public string? Role { get; set; }

            [JsonProperty("job")]
            public string JobName { get; set; }

            public List<uint> JobIcons
            {
                get
                {
                    if (string.IsNullOrEmpty(JobName)) return [];

                    var splited = JobName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (field.Count == 0)
                    {
                        if (splited.Length == 1)
                            field = [ParseClassJobIdByName(JobName)];
                        // 全战职
                        else if (splited.Length == BattleJobs.Count && splited.All(BattleJobs.Contains))
                            field = [62145];
                        // 坦克
                        else if (splited.All(TankJobs.Contains))
                            field = [62571];
                        // DPS
                        else if (splited.All(DPSJobs.Contains))
                            field = [62573];
                        // 奶妈
                        else if (splited.All(HealerJobs.Contains))
                            field = [62572];
                        else
                        {
                            List<uint> icons = [];
                            splited.ForEach(x => icons.Add(ParseClassJobIdByName(x)));
                            field = icons.Where(x => x != 0).ToList();
                        }
                    }

                    return field;

                    uint ParseClassJobIdByName(string job)
                    {
                        var rowID = LuminaGetter.Get<ClassJob>().FirstOrDefault(x => x.Abbreviation.ToString() == job).RowId;
                        return rowID == 0 ? 62145 : 62100 + rowID;
                    }
                }
            } = [];
        }
    }
    
    #region 常量

    private const string BASE_URL        = "https://xivpf.littlenightmare.top/api/listings?";
    private const string BASE_DETAIL_URL = "https://xivpf.littlenightmare.top/api/listing/";

    #endregion
}
