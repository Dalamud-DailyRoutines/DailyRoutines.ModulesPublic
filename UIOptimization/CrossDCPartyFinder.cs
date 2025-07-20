using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Hooking;
using Dalamud.Interface.Colors;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.Modules;

public class CrossDCPartyFinder : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "跨大区队员招募",
        Description = "允许在游戏原生的 队员招募 界面内选择并查看由众包网站提供的其他大区的招募信息",
        Category    = ModuleCategories.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private const string BASE_URL        = "https://xivpf.littlenightmare.top/api/listings?";
    private const string BASE_DETAIL_URL = "https://xivpf.littlenightmare.top/api/listing/";

    private static string HomeDataCenter =>
        DService.ObjectTable.LocalPlayer.HomeWorld.Value.DataCenter.Value.Name.ExtractText();

    private static readonly CompSig AgentLookingForGroupReceiveEventSig =
        new("48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 45 8B D1");

    private static Hook<AgentReceiveEventDelegate>? AgentLookingForGroupReceiveEventHook;

    private static Config ModuleConfig = null!;

    private static List<string> DataCenters = [];

    private static CancellationTokenSource? CancelSource;

    private static List<PartyFinderList.PartyFinderListing> Listings        = [];
    private static List<PartyFinderList.PartyFinderListing> ListingsDisplay = [];
    private static DateTime                                 LastUpdate      = DateTime.MinValue;

    private static bool IsNeedToDisable;

    private static PartyFinderRequest LastRequest       = new();
    private static string             CurrentDataCenter = string.Empty;
    private static string             CurrentSeach      = string.Empty;

    private static int CurrentPage;

    public override unsafe void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        Overlay      ??= new(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LookingForGroup", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnAddon);
        if (IsAddonAndNodesReady(LookingForGroup))
            OnAddon(AddonEvent.PostSetup, null);

        AgentLookingForGroupReceiveEventHook ??=
            AgentLookingForGroupReceiveEventSig.GetHook<AgentReceiveEventDelegate>(AgentLookingForGroupReceiveEventDetour);
        AgentLookingForGroupReceiveEventHook.Enable();
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        ClearResources();

        base.Uninit();
    }

    public override unsafe void OverlayUI()
    {
        var addon = LookingForGroup;
        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        ImGui.SetWindowPos(new(addon->GetNodeById(31)->ScreenX - 4f, addon->GetY() - ImGui.GetWindowSize().Y));

        ImGui.AlignTextToFramePadding();
        ImGui.Text("招募信息大区:");

        using (ImRaii.Disabled(IsNeedToDisable))
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            using (var combo = ImRaii.Combo("###DataCenterSelectCombo", CurrentDataCenter, ImGuiComboFlags.HeightLargest))
            {
                if (combo)
                {
                    foreach (var dataCenter in DataCenters)
                    {
                        if (ImGui.Selectable(dataCenter, dataCenter == CurrentDataCenter))
                        {
                            CurrentDataCenter = dataCenter;
                            if (HomeDataCenter == dataCenter)
                            {
                                SendEvent(AgentId.LookingForGroup, 1, 17);
                                return;
                            }

                            SendRequestDynamic();
                            IsNeedToDisable = true;
                        }
                    }
                }
            }
        }

        if (CurrentDataCenter == HomeDataCenter) return;

        var nodeInfo0  = NodeState.Get(addon->GetNodeById(38));
        var nodeInfo1  = NodeState.Get(addon->GetNodeById(31));
        var nodeInfo2  = NodeState.Get(addon->GetNodeById(41));
        var size       = nodeInfo0.Size + nodeInfo1.Size.WithX(0) + nodeInfo2.Size.WithX(0);
        var sizeOffset = new Vector2(4, 4);
        ImGui.SetNextWindowPos(new(addon->GetNodeById(31)->ScreenX - 4f, addon->GetNodeById(31)->ScreenY));
        ImGui.SetNextWindowSize(size + (2 * sizeOffset));
        if (ImGui.Begin("###CrossDCPartyFinder_PartyListWindow",
                        ImGuiWindowFlags.NoTitleBar            | ImGuiWindowFlags.NoResize   | ImGuiWindowFlags.NoDocking   |
                        ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
        {
            var isNeedToResetY = false;

            using (ImRaii.Disabled(IsNeedToDisable))
            {
                if (ImGui.Checkbox("倒序", ref ModuleConfig.OrderByDescending))
                {
                    isNeedToResetY = true;

                    SaveConfig(ModuleConfig);
                    SendRequestDynamic();
                }

                var totalPages = (int)Math.Ceiling(ListingsDisplay.Count / (float)ModuleConfig.PageSize);

                using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0)))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("<<"))
                    {
                        isNeedToResetY = true;
                        CurrentPage    = 0;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("<"))
                    {
                        isNeedToResetY = true;
                        CurrentPage    = Math.Max(0, CurrentPage - 1);
                    }

                    ImGui.SameLine();
                    ImGui.Text($" {CurrentPage + 1} / {Math.Max(1, totalPages)} ");
                    ImGuiOm.TooltipHover($"{ListingsDisplay.Count}");

                    ImGui.SameLine();
                    if (ImGui.Button(">"))
                    {
                        isNeedToResetY = true;
                        CurrentPage    = Math.Min(totalPages - 1, CurrentPage + 1);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button(">>"))
                    {
                        isNeedToResetY = true;
                        CurrentPage    = Math.Max(0, totalPages - 1);
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("关闭"))
                    CurrentDataCenter = HomeDataCenter;

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("###SearchString", GetLoc("PleaseSearch"), ref CurrentSeach, 128);
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
                    if (!IsNeedToDisable)
                        DrawPartyFinderList(sizeAfter);

                    ScaledDummy(8f);
                }
            }

            ImGui.End();
        }
    }

    private static void DrawPartyFinderList(Vector2 size)
    {
        using var table = ImRaii.Table("###ListingsTable", 3, ImGuiTableFlags.BordersInnerH, size);
        if (!table) return;

        ImGui.TableSetupColumn("招募图标", ImGuiTableColumnFlags.WidthFixed,
                               (ImGui.GetTextLineHeightWithSpacing() * 3) + ImGui.GetStyle().ItemSpacing.X);
        ImGui.TableSetupColumn("招募详情", ImGuiTableColumnFlags.WidthStretch, 50);
        ImGui.TableSetupColumn("招募信息", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("八个汉字八个汉字").X);

        var startIndex = CurrentPage * ModuleConfig.PageSize;
        var pageItems  = ListingsDisplay.Skip(startIndex).Take(ModuleConfig.PageSize).ToList();

        pageItems.ForEach(x => Task.Run(async () => await x.RequestAsync(), CancelSource.Token).ConfigureAwait(false));

        foreach (var listing in pageItems)
        {
            using var id = ImRaii.PushId(listing.ID);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (DService.Texture.TryGetFromGameIcon(new(listing.CategoryIcon), out var categoryTexture))
                ImGui.Image(categoryTexture.GetWrapOrEmpty().ImGuiHandle,
                            new Vector2(ImGui.GetTextLineHeightWithSpacing() * 3) +
                            new Vector2(ImGui.GetStyle().ItemSpacing.X, 2    * ImGui.GetStyle().ItemSpacing.Y));

            // 招募详情
            ImGui.TableNextColumn();
            using (ImRaii.Group())
            {
                using (FontManager.UIFont120.Push())
                {
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (4f * GlobalFontScale));
                    ImGui.TextColored(LightSkyBlue, $"{listing.Duty}");
                }

                ImGui.SameLine();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2f * GlobalFontScale));
                ImGui.TextColored(LightSkyBlue, $"({listing.PlayerName}@{listing.HomeWorldName})");
                ImGuiOm.TooltipHover($"{listing.PlayerName}@{listing.HomeWorldName}");
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText($"{listing.PlayerName}@{listing.HomeWorldName}");
                    NotificationSuccess(GetLoc("CopiedToClipboard"));
                }

                var isDescEmpty = string.IsNullOrWhiteSpace(listing.Description);
                ImGui.Text(isDescEmpty ? $"({LuminaGetter.GetRow<Addon>(11100)!.Value.Text.ExtractText()})" : $"{listing.Description}");
                if (!isDescEmpty) 
                    ImGuiOm.TooltipHover(listing.Description);
                if (ImGui.IsItemHovered() && !isDescEmpty)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsItemClicked() && !isDescEmpty)
                {
                    ImGui.SetClipboardText(listing.Description);
                    NotificationSuccess(GetLoc("CopiedToClipboard"));
                }
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
                            if (DService.Texture.TryGetFromGameIcon(new(displayIcon), out var jobTexture))
                            {
                                ImGui.Image(jobTexture.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
                                if (slot.JobIcons.Count > 1 && ImGui.IsItemHovered())
                                {
                                    using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f))
                                    {
                                        ImGui.BeginTooltip();
                                        foreach (var jobIcon in slot.JobIcons)
                                        {
                                            if (DService.Texture.TryGetFromGameIcon(new(jobIcon), out var jobTooltipTexture))
                                            {
                                                ImGui.Image(jobTooltipTexture.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
                                                ImGui.SameLine();
                                            }
                                        }

                                        ImGui.EndTooltip();
                                    }
                                }

                                ImGui.SameLine();
                            }
                        }
                    }

                    ImGui.Spacing();

                    ImGui.SameLine(0, 4f * GlobalFontScale);
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, listing.MinItemLevel != 0))
                        ImGui.Text($"[{(listing.MinItemLevel == 0 ? "无" : $"{listing.MinItemLevel}")}]");
                }
            }

            // 招募信息
            ImGui.TableNextColumn();
            using (FontManager.UIFont80.Push())
            {
                ImGui.NewLine();

                ImGui.TextColored(ImGuiColors.DalamudYellow, "当前位于:");

                ImGui.SameLine();
                ImGui.Text($"{listing.CreatedAtWorldName}");

                ImGui.TextColored(ImGuiColors.DalamudYellow, "剩余人数:");

                ImGui.SameLine();
                ImGui.Text($"{listing.SlotAvailable - listing.SlotFilled}");

                ImGui.TextColored(ImGuiColors.DalamudYellow, "剩余时间:");

                ImGui.SameLine();
                ImGui.Text($"{TimeSpan.FromSeconds(listing.TimeLeft).TotalMinutes:F0} 分钟");
            }
        }
    }

    private static void SendRequest(PartyFinderRequest req)
    {
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        PartyFinderList.PartyFinderListing.ReleaseSlim();

        unsafe
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null || !agent->IsAgentActive()) return;
        }

        CancelSource = new();

        var testReq = req.Clone();
        testReq.PageSize = 1;

        // 收集用
        var listings = new ConcurrentBag<PartyFinderList.PartyFinderListing>();

        _ = Task.Run(async () =>
        {
            if (DateTime.Now - LastUpdate < TimeSpan.FromSeconds(30) && LastRequest.Equals(req))
            {
                ListingsDisplay = FilterAndSort(Listings);
                return;
            }

            IsNeedToDisable = true;
            LastUpdate      = DateTime.Now;
            LastRequest     = req;

            var testResult = await testReq.Request().ConfigureAwait(false);

            // 没有数据就不继续请求了
            var totalPage = testResult.Overview.Total == 0 ? 0 : (testResult.Overview.Total + 99) / 100;
            if (totalPage == 0) return;

            var tasks = new List<Task>();
            Enumerable.Range(1, (int)totalPage).ForEach(x => tasks.Add(Gather((uint)x)));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            Listings = listings.OrderByDescending(x => x.TimeLeft)
                               .DistinctBy(x => x.ID)
                               .DistinctBy(x => $"{x.PlayerName}@{x.HomeWorldName}")
                               .Where(x => DateTime.Now - x.UpdatedAt > TimeSpan.FromMinutes(5)) // 5 分钟没更新
                               .ToList();
            ListingsDisplay = FilterAndSort(Listings);
        }, CancelSource.Token).ContinueWith(async _ =>
        {
            IsNeedToDisable = false;

            NotificationInfo($"获取了 {ListingsDisplay.Count} 条招募信息");

            await DService.Framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    if (!IsAddonAndNodesReady(LookingForGroup)) return;
                    LookingForGroup->GetTextNodeById(49)->SetText($"{CurrentDataCenter}: {ListingsDisplay.Count}");
                }
            }).ConfigureAwait(false);
        });

        async Task Gather(uint page)
        {
            var clonedRequest = req.Clone();
            clonedRequest.Page = page;

            var result = await clonedRequest.Request().ConfigureAwait(false);
            listings.AddRange(result.Listings);
        }

        List<PartyFinderList.PartyFinderListing> FilterAndSort(IEnumerable<PartyFinderList.PartyFinderListing> source) =>
            source.Where(x => string.IsNullOrWhiteSpace(CurrentSeach) ||
                              x.GetSearchString().Contains(CurrentSeach, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(x => ModuleConfig.OrderByDescending ? x.TimeLeft : 1 / x.TimeLeft)
                  .ToList();
    }

    private static unsafe void SendRequestDynamic()
    {
        var req = LastRequest.Clone();

        req.DataCenter = CurrentDataCenter;
        req.Category   = PartyFinderRequest.ParseCategory(AgentLookingForGroup.Instance());

        SendRequest(req);
        CurrentPage = 0;
    }

    private static void ClearResources()
    {
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;

        IsNeedToDisable = false;

        Listings = ListingsDisplay = [];

        PartyFinderList.PartyFinderListing.ReleaseSlim();

        LastUpdate  = DateTime.MinValue;
        LastRequest = new();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        ClearResources();

        switch (type)
        {
            case AddonEvent.PostSetup:
                Overlay.IsOpen    = true;
                CurrentDataCenter = DService.ObjectTable.LocalPlayer?.HomeWorld.Value.DataCenter.Value.Name.ExtractText() ?? string.Empty;
                break;
            case AddonEvent.PreFinalize:
                Overlay.IsOpen    = false;
                CurrentDataCenter = string.Empty;
                break;
            default:
                Overlay.IsOpen = Overlay.IsOpen;
                break;
        }

        if (DService.ObjectTable.LocalPlayer is { } localPlayer)
        {
            DataCenters = LuminaGetter.Get<WorldDCGroupType>()
                                      .Where(x => x.Region == localPlayer.HomeWorld.Value.DataCenter.Value.Region)
                                      .Select(x => x.Name.ExtractText())
                                      .ToList();
        }
    }

    private unsafe AtkValue* AgentLookingForGroupReceiveEventDetour(
        AgentInterface* agent, AtkValue* returnvalues, AtkValue* values, uint valueCount, ulong eventKind)
    {
        var ret = InvokeOriginal();

        if (CurrentDataCenter != HomeDataCenter)
        {
            // 招募类别刷新
            if (eventKind == 1 && valueCount == 3 && values[1].Type == ValueType.UInt)
                SendRequestDynamic();

            // 招募刷新
            if (eventKind == 1 && valueCount == 1 && values[0].Type == ValueType.Int && values[0].Int == 17)
                SendRequestDynamic();
        }

        return ret;

        AtkValue* InvokeOriginal() => AgentLookingForGroupReceiveEventHook.Original(agent, returnvalues, values, valueCount, eventKind);
    }

    private class Config : ModuleConfiguration
    {
        public          bool OrderByDescending = true;
        public readonly int  PageSize          = 50;
    }

    private class PartyFinderRequest : IEquatable<PartyFinderRequest>
    {
        public uint       Page       { get; set; } = 1;
        public uint       PageSize   { get; set; } = 100;
        public string     Category   { get; set; } = string.Empty;
        public string     World      { get; set; } = string.Empty;
        public string     DataCenter { get; set; } = string.Empty;
        public List<uint> Jobs       { get; set; } = [];

        public async Task<PartyFinderList> Request()
            => JsonConvert.DeserializeObject<PartyFinderList>(await HttpClientHelper.Get().GetStringAsync(Format())) ?? new();

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
                "FieldOperations"    => 14,
                "V&C Dungeon Finder" => 15,
                "None"               => 16,
                _                    => 0
            };

        public static string ParseCategoryIDToLoc(uint categoryID) =>
            categoryID switch
            {
                1  => LuminaGetter.GetRow<Addon>(8605)!.Value.Text.ExtractText(),
                2  => LuminaGetter.GetRow<Addon>(8607)!.Value.Text.ExtractText(),
                3  => LuminaGetter.GetRow<Addon>(8606)!.Value.Text.ExtractText(),
                4  => LuminaGetter.GetRow<Addon>(8608)!.Value.Text.ExtractText(),
                5  => LuminaGetter.GetRow<Addon>(8609)!.Value.Text.ExtractText(),
                6  => LuminaGetter.GetRow<Addon>(10822)!.Value.Text.ExtractText(),
                7  => LuminaGetter.GetRow<Addon>(8610)!.Value.Text.ExtractText(),
                8  => LuminaGetter.GetRow<Addon>(8612)!.Value.Text.ExtractText(),
                9  => LuminaGetter.GetRow<Addon>(8601)!.Value.Text.ExtractText(),
                10 => LuminaGetter.GetRow<Addon>(8107)!.Value.Text.ExtractText(),
                11 => LuminaGetter.GetRow<Addon>(8613)!.Value.Text.ExtractText(),
                12 => LuminaGetter.GetRow<Addon>(2306)!.Value.Text.ExtractText(),
                13 => LuminaGetter.GetRow<Addon>(2304)!.Value.Text.ExtractText(),
                14 => LuminaGetter.GetRow<Addon>(2307)!.Value.Text.ExtractText(),
                15 => LuminaGetter.GetRow<ContentType>(30)!.Value.Name.ExtractText(),
                16 => LuminaGetter.GetRow<Addon>(7)!.Value.Text.ExtractText(),
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
                11 => 61809,
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
                Jobs       = new List<uint>(Jobs)
            };

        public bool Equals(PartyFinderRequest? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Category == other.Category && World == other.World && DataCenter == other.DataCenter;
        }
    }

    private class PartyFinderList
    {
        [JsonProperty("data")]
        public List<PartyFinderListing> Listings { get; set; } = [];

        [JsonProperty("pagination")]
        public PartyFinderOverview Overview { get; set; } = new();

        public class PartyFinderListing : IEquatable<PartyFinderListing>
        {
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

            public                  PartyFinderListingDetail? Detail { get; set; }
            private                 Task<string>?             DetailReuqestTask;
            private static readonly SemaphoreSlim             detailSemaphoreSlim = new(Environment.ProcessorCount);

            public static void ReleaseSlim() => detailSemaphoreSlim.Release();

            public uint CategoryIcon
            {
                get
                {
                    if (categoryIcon != 0) return categoryIcon;
                    return categoryIcon = PartyFinderRequest.ParseCategoryIDToIconID(PartyFinderRequest.ParseOnlineCategoryToID(CategoryName));
                }
            }

            private uint categoryIcon;

            public async Task RequestAsync()
            {
                if (Detail != null || DetailReuqestTask != null) return;

                DetailReuqestTask = HttpClientHelper.Get().GetStringAsync($"{BASE_DETAIL_URL}{ID}");
                Detail            = JsonConvert.DeserializeObject<PartyFinderListingDetail>(await DetailReuqestTask.ConfigureAwait(false)) ?? new();
            }

            public bool Equals(PartyFinderListing? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return ID == other.ID;
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
            [JsonProperty("filled")]
            public bool Filled { get; set; }

            [JsonProperty("role")]
            public string? RoleName { get; set; }

            [JsonProperty("role_id")]
            public string? Role { get; set; }

            [JsonProperty("job")]
            public string JobName { get; set; }

            public static readonly HashSet<string> BattleJobs;
            public static readonly HashSet<string> TankJobs;
            public static readonly HashSet<string> DPSJobs;
            public static readonly HashSet<string> HealerJobs;

            static Slot()
            {
                BattleJobs = LuminaGetter.Get<ClassJob>()
                                         .Where(x => x.RowId != 0 && x.DohDolJobIndex == -1)
                                         .Select(x => x.Abbreviation.ExtractText())
                                         .ToHashSet();

                TankJobs = LuminaGetter.Get<ClassJob>()
                                       .Where(x => x.RowId != 0 && x.Role is 1)
                                       .Select(x => x.Abbreviation.ExtractText())
                                       .ToHashSet();

                DPSJobs = LuminaGetter.Get<ClassJob>()
                                      .Where(x => x.RowId != 0 && (x.Role == 2 || x.Role == 3))
                                      .Select(x => x.Abbreviation.ExtractText())
                                      .ToHashSet();

                HealerJobs = LuminaGetter.Get<ClassJob>()
                                         .Where(x => x.RowId != 0 && x.Role is 4)
                                         .Select(x => x.Abbreviation.ExtractText())
                                         .ToHashSet();
            }

            public List<uint> JobIcons
            {
                get
                {
                    if (string.IsNullOrEmpty(JobName)) return [];

                    var splited = JobName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (jobIcons.Count == 0)
                    {
                        if (splited.Length == 1)
                            jobIcons = [ParseClassJobIdByName(JobName)];
                        // 全战职
                        else if (splited.Length == BattleJobs.Count && splited.All(BattleJobs.Contains))
                            jobIcons = [62145];
                        // 坦克
                        else if (splited.All(TankJobs.Contains))
                            jobIcons = [62571];
                        // DPS
                        else if (splited.All(DPSJobs.Contains))
                            jobIcons = [62573];
                        // 奶妈
                        else if (splited.All(HealerJobs.Contains))
                            jobIcons = [62572];
                        else
                        {
                            List<uint> icons = [];
                            splited.ForEach(x => icons.Add(ParseClassJobIdByName(x)));
                            jobIcons = icons.Where(x => x != 0).ToList();
                        }
                    }

                    return jobIcons;

                    uint ParseClassJobIdByName(string job)
                    {
                        var rowID = LuminaGetter.Get<ClassJob>().FirstOrDefault(x => x.Abbreviation.ExtractText() == job).RowId;
                        return rowID == 0 ? 62145 : 62100 + rowID;
                    }
                }
            }

            private List<uint> jobIcons = [];
        }
    }
}
