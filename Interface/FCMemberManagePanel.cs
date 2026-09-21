using System.Text.RegularExpressions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using AgentFreeCompany = OmenTools.Interop.Game.Models.Native.AgentFreeCompany;
using AgentFreeCompanyProfile = OmenTools.Interop.Game.Models.Native.AgentFreeCompanyProfile;
using InfoProxyFreeCompany = OmenTools.Interop.Game.Models.Native.InfoProxyFreeCompany;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class FCMemberManagePanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FCMemberManagePanelTitle"),
        Description = Lang.Get("FCMemberManagePanelDescription"),
        Category    = ModuleCategory.Interface
    };

    private readonly Dictionary<ulong, MemberRecord> members            = [];
    private readonly HashSet<ulong>                  selectedContentIDs = [];
    private readonly HashSet<byte>                   rankFilter         = [];

    private uint totalMemberCount;
    private int  currentPage;

    private bool            isDescending;
    private string          nameFilter       = string.Empty;
    private LastOnlineRange lastOnlineFilter = LastOnlineRange.All;

    private ulong[]? pendingTargets;
    private bool     requestConfirmPopup;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 3000 };

        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoResize;
        Overlay.WindowName =   Lang.Get("FCMemberManagePanelTitle");

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "FreeCompanyMember", OnFreeCompanyMemberAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "FreeCompanyMember", OnFreeCompanyMemberAddon);

        if (FreeCompanyMember != null && FreeCompanyMember->IsAddonAndNodesReady())
            OnFreeCompanyMemberAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnFreeCompanyMemberAddon);
        ResetMembers();
    }

    protected override void OverlayPreDraw()
    {
        if (!IClientState.Instance().IsLoggedIn) return;
        if (!Throttler.Shared.Throttle("FCMemberManagePanel-SyncMembers", 1_000)) return;

        SyncMembers();
    }

    protected override void OverlayUI()
    {
        var pageCount = GetPageCount();

        using (ImRaii.Disabled(currentPage <= 0))
        {
            if (ImGuiOm.ButtonIcon("MemberPagePrev", FontAwesomeIcon.AngleLeft, string.Empty, true))
                SwitchPage(currentPage - 1);
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{currentPage + 1} / {pageCount}");

        ImGui.SameLine();

        using (ImRaii.Disabled(currentPage >= pageCount - 1))
        {
            if (ImGuiOm.ButtonIcon("MemberPageNext", FontAwesomeIcon.AngleRight, string.Empty, true))
                SwitchPage(currentPage + 1);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("###MemberSearchInput", Lang.Get("PleaseSearch"), ref nameFilter, 128);

        var       list      = FilteredMembers();
        var       tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
        using var table     = ImRaii.Table("FCMembersTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable, tableSize);

        if (!table) return;

        var columWidth = ImGui.GetFrameHeight();

        ImGui.TableSetupColumn("序号",  ImGuiTableColumnFlags.WidthFixed,   columWidth);
        ImGui.TableSetupColumn("名称",  ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("阶级",  ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("职业",  ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("位置",  ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed,   columWidth);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderRow();

        foreach (var member in list)
        {
            if (member.ContentID == LocalPlayerState.ContentID) continue;

            using var id       = ImRaii.PushId(member.ContentID.ToString());
            var       selected = selectedContentIDs.Contains(member.ContentID);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGui.Selectable($"{member.Index}", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (!selectedContentIDs.Remove(member.ContentID))
                    selectedContentIDs.Add(member.ContentID);
            }

            DrawMemberContextMenu(member);

            ImGui.TableNextColumn();
            DrawOnlineStatus(member);
            ImGui.TextUnformatted(member.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(member.RankText);

            ImGui.TableNextColumn();
            DrawJob(member);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(member.LocationText);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled())
                ImGui.Checkbox($"##{member.ContentID}_Checkbox", ref selected);
        }

        DrawConfirmPopup();
    }

    private void DrawHeaderRow()
    {
        ImGui.TableNextColumn();

        if (ImGui.Button
            (
                isDescending ?
                    FontAwesomeIcon.ArrowUp.ToIconString() :
                    FontAwesomeIcon.ArrowDown.ToIconString()
            ))
            isDescending ^= true;

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Name"));

        ImGui.TableNextColumn();

        if (ImGui.Selectable(Lang.Get("FCMemberManagePanel-Rank"), rankFilter.Count > 0))
            ImGui.OpenPopup(RANK_FILTER_POPUP_ID);

        DrawRankFilterPopup();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Job"));

        ImGui.TableNextColumn();

        if (ImGui.Selectable(Lang.Get("FCMemberManagePanel-PositionLastTime"), lastOnlineFilter != LastOnlineRange.All))
            ImGui.OpenPopup(LAST_ONLINE_FILTER_POPUP_ID);

        DrawLastOnlineFilterPopup();

        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIcon("OpenMultiPopup", FontAwesomeIcon.EllipsisH, string.Empty, true))
            ImGui.OpenPopup("Multi_Popup");

        DrawMultiContextMenu();
    }

    private void DrawRankFilterPopup()
    {
        using var popup = ImRaii.ContextPopupItem(RANK_FILTER_POPUP_ID);
        if (!popup) return;

        foreach (var (rankIndex, name) in GetRankOptions())
        {
            var selected = rankFilter.Contains(rankIndex);

            if (ImGui.Checkbox($"{name}##FilterRank{rankIndex}", ref selected))
            {
                if (!rankFilter.Remove(rankIndex))
                    rankFilter.Add(rankIndex);
            }
        }

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("Reset"), new(-1f, 0f)))
            rankFilter.Clear();
    }

    private void DrawLastOnlineFilterPopup()
    {
        using var popup = ImRaii.ContextPopupItem(LAST_ONLINE_FILTER_POPUP_ID);
        if (!popup) return;

        foreach (var range in LastOnlineRanges)
        {
            if (ImGui.RadioButton($"{GetLastOnlineRangeName(range)}##FilterLastOnline", lastOnlineFilter == range))
                lastOnlineFilter = range;
        }

        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("Reset"), new(-1f, 0f)))
            lastOnlineFilter = LastOnlineRange.All;
    }

    private static List<(byte RankIndex, string Name)> GetRankOptions()
    {
        List<(byte, string)> ranks = [];

        var infoProxy = InfoProxyFreeCompany.Instance();
        if (infoProxy == null) return ranks;

        for (byte rankIndex = 0; rankIndex < FC_RANK_COUNT; rankIndex++)
        {
            var name = infoProxy->GetRankNameText(rankIndex);
            if (string.IsNullOrWhiteSpace(name)) continue;

            ranks.Add((rankIndex, name));
        }

        return ranks;
    }

    private static string GetLastOnlineRangeName
    (
        LastOnlineRange range
    )
        => range switch
        {
            LastOnlineRange.Online      => Lang.Get("FCMemberManagePanel-LastOnline-Online"),
            LastOnlineRange.WithinHour  => Lang.Get("FCMemberManagePanel-LastOnline-WithinHour"),
            LastOnlineRange.WithinDay   => Lang.Get("FCMemberManagePanel-LastOnline-WithinDay"),
            LastOnlineRange.WithinWeek  => Lang.Get("FCMemberManagePanel-LastOnline-WithinWeek"),
            LastOnlineRange.WithinMonth => Lang.Get("FCMemberManagePanel-LastOnline-WithinMonth"),
            LastOnlineRange.BeyondMonth => Lang.Get("FCMemberManagePanel-LastOnline-BeyondMonth"),
            LastOnlineRange.Unknown     => Lang.Get("Unknown"),
            _                           => Lang.Get("All")
        };

    private static void DrawOnlineStatus
    (
        MemberRecord member
    )
    {
        if (member.OnlineStatus == 0) return;
        if (!LuminaGetter.TryGetRow<OnlineStatus>(member.OnlineStatus, out var row)) return;

        var icon = ITextureProvider.Instance().GetFromGameIcon(new(row.Icon)).GetWrapOrDefault();
        if (icon == null) return;

        var originY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(originY + (2f * GlobalUIScale));
        ImGui.Image(icon.Handle, new(ImGui.GetTextLineHeight()));
        ImGui.SetCursorPosY(originY);
        ImGui.SameLine();
    }

    private static void DrawJob
    (
        MemberRecord member
    )
    {
        if (member.JobIcon != null)
        {
            var originY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(originY + (2f * GlobalUIScale));
            ImGui.Image(member.JobIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeight()));
            ImGui.SetCursorPosY(originY);
            ImGui.SameLine();
        }

        ImGui.TextUnformatted(member.JobText);
    }

    private void DrawMemberContextMenu
    (
        MemberRecord member
    )
    {
        using var context = ImRaii.ContextPopupItem($"{member.ContentID}_Popup");
        if (!context) return;

        ImGui.TextUnformatted(member.Name);

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(15083)))
            OpenCharaCard(member.ContentID);

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(51)))
            OpenCharacterDetail(member.ContentID);

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2807)))
            OpenFreeCompanyProfile(member.ContentID);

        DrawRankMenu([member.ContentID]);

        if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2801)))
            RequestConfirm([member.ContentID]);
    }

    private void DrawMultiContextMenu()
    {
        using var popup = ImRaii.ContextPopupItem("Multi_Popup");
        if (!popup) return;

        ImGui.TextUnformatted(Lang.Get("FCMemberManagePanel-SelectedMembers", selectedContentIDs.Count));
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Disabled(selectedContentIDs.Count == 0))
        {
            var targets = selectedContentIDs.ToArray();

            DrawRankMenu(targets);

            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2801)))
                RequestConfirm(targets);
        }
    }

    private void DrawRankMenu
    (
        ulong[] targets
    )
    {
        using var menu = ImRaii.Menu(LuminaWrapper.GetAddonText(2656));
        if (!menu) return;

        var infoProxy = InfoProxyFreeCompany.Instance();

        if (infoProxy != null)
        {
            foreach (var (rankIndex, name) in infoProxy->GetAssignableRanks())
            {
                if (ImGui.MenuItem($"{name}##Rank{rankIndex}"))
                    EnqueueMemberAction(targets, AgentFreeCompany.MemberActionType.Promote, rankIndex);
            }
        }
    }

    private void DrawConfirmPopup()
    {
        if (pendingTargets == null) return;

        using var modal = ImGuiOm.PopupModal
        (
            $"{Info.Title}##ConfirmKickPopup",
            ref requestConfirmPopup,
            ImGuiWindowFlags.AlwaysAutoResize
        );
        if (!modal) return;

        ImGui.TextUnformatted
        (
            pendingTargets.Length == 1 ?
                Lang.Get("FCMemberManagePanel-ConfirmKick",      GetMemberName(pendingTargets[0])) :
                Lang.Get("FCMemberManagePanel-ConfirmKickMulti", pendingTargets.Length)
        );

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(LuminaWrapper.GetAddonText(1), new(120f * GlobalUIScale, 0f)))
        {
            EnqueueMemberAction(pendingTargets, AgentFreeCompany.MemberActionType.Dismiss, 0);
            pendingTargets      = null;
            requestConfirmPopup = false;
        }

        ImGui.SameLine();

        if (ImGui.Button(LuminaWrapper.GetAddonText(2), new(120f * GlobalUIScale, 0f)))
        {
            pendingTargets      = null;
            requestConfirmPopup = false;
        }
    }

    private void OnFreeCompanyMemberAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        if (type == AddonEvent.PostSetup)
            Overlay.IsOpen = true;

        if (type is AddonEvent.PostSetup or AddonEvent.PreFinalize)
            ResetMembers();
    }

    private void SyncMembers()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;

        var memberProxy = agent->InfoProxyFreeCompanyMember;
        if (memberProxy == null) return;

        currentPage = agent->CurrentMemberPageIndex;

        var infoProxy = InfoProxyFreeCompany.Instance();

        if (infoProxy != null)
            totalMemberCount = infoProxy->TotalMembers;

        var source = memberProxy->CharDataSpan;

        if (source.Length == 0)
        {
            members.Clear();
            return;
        }

        for (var i = 0; i < source.Length; i++)
        {
            var data = source[i];

            if (string.IsNullOrWhiteSpace(data.NameString)) continue;

            var contentID = data.ContentId;
            var record = members.TryGetValue(contentID, out var existed) ?
                             existed :
                             new() { ContentID = contentID };

            record.Index        = i;
            record.OnlineStatus = GetOnlineStatusIconID(data.State);
            record.Name         = data.NameString;
            record.RankText = infoProxy == null ?
                                  string.Empty :
                                  infoProxy->GetMemberRankNameText(data.ExtraFlags);
            record.RankIndex = (byte)(data.ExtraFlags >> MEMBER_RANK_INDEX_SHIFT);
            record.IsOnline  = ((ulong)data.State & ONLINE_STATE_MASK) != 0;
            record.JobIcon = data.Job == 0 ?
                                 null :
                                 ITextureProvider.Instance().GetFromGameIcon(new(62100U + data.Job));
            record.JobText = data.Job == 0 ?
                                 string.Empty :
                                 LuminaGetter.GetRowOrDefault<ClassJob>(data.Job).Name.ToString() ?? string.Empty;

            var locationText = agent->GetMemberLocationText(i);

            if (record.LocationText != locationText)
            {
                record.LocationText = locationText;
                record.OfflineMinutes = record.IsOnline ?
                                            0 :
                                            ParseOfflineMinutes(locationText);
            }

            members[contentID] = record;
        }
    }

    private void ResetMembers()
    {
        members.Clear();
        selectedContentIDs.Clear();
        pendingTargets      = null;
        requestConfirmPopup = false;
    }

    private int GetPageCount()
        => Math.Min(PAGE_LIMIT, Math.Max(1, ((int)totalMemberCount + ((int)PAGE_SIZE - 1)) / (int)PAGE_SIZE));

    private List<MemberRecord> FilteredMembers()
    {
        var query = members.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(x => x.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        if (rankFilter.Count > 0)
            query = query.Where(x => rankFilter.Contains(x.RankIndex));

        if (lastOnlineFilter != LastOnlineRange.All)
            query = query.Where(MatchesLastOnlineFilter);

        var list = query.ToList();
        list.Sort
        ((a, b) => isDescending ?
                       b.Index.CompareTo(a.Index) :
                       a.Index.CompareTo(b.Index)
        );
        return list;
    }

    private bool MatchesLastOnlineFilter
    (
        MemberRecord member
    )
        => lastOnlineFilter switch
        {
            LastOnlineRange.Online      => member.IsOnline,
            LastOnlineRange.Unknown     => member is { IsOnline: false, OfflineMinutes: null },
            LastOnlineRange.WithinHour  => IsWithinOfflineMinutes(member, 60),
            LastOnlineRange.WithinDay   => IsWithinOfflineMinutes(member, 1440),
            LastOnlineRange.WithinWeek  => IsWithinOfflineMinutes(member, 10080),
            LastOnlineRange.WithinMonth => IsWithinOfflineMinutes(member, 43200),
            LastOnlineRange.BeyondMonth => IsBeyondOfflineMinutes(member, 43200),
            _                           => true
        };

    private static bool IsWithinOfflineMinutes
    (
        MemberRecord member,
        int          limit
    )
        => member.IsOnline || member.OfflineMinutes <= limit;

    private static bool IsBeyondOfflineMinutes
    (
        MemberRecord member,
        int          limit
    )
        => !member.IsOnline && member.OfflineMinutes > limit;

    private void SwitchPage
    (
        int page
    )
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;

        agent->SwitchMemberPage(page);

        currentPage = page;

        members.Clear();
        selectedContentIDs.Clear();
    }

    private void EnqueueMemberAction
    (
        ulong[]                           targets,
        AgentFreeCompany.MemberActionType action,
        byte                              rank
    )
    {
        TaskHelper?.Abort();

        foreach (var target in targets)
        {
            TaskHelper?.Enqueue
            (() =>
                {
                    var agent = AgentFreeCompany.Instance();
                    if (agent == null) return true;

                    agent->ExecuteMemberAction(target, action, rank);
                    return true;
                }
            );

            TaskHelper?.DelayNext(300);
        }
    }

    private void RequestConfirm
    (
        ulong[] targets
    )
    {
        if (targets.Length == 0) return;

        pendingTargets      = targets;
        requestConfirmPopup = true;
    }

    private static void OpenCharaCard
    (
        ulong contentID
    )
    {
        var agent = AgentCharaCard.Instance();
        if (agent == null) return;

        agent->OpenCharaCard(contentID);
    }

    private static void OpenCharacterDetail
    (
        ulong contentID
    )
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;

        var memberProxy = agent->InfoProxyFreeCompanyMember;
        if (memberProxy == null) return;

        var entry = memberProxy->GetEntryByContentId(contentID);
        if (entry == null) return;

        var detail = AgentDetail.Instance();
        if (detail == null) return;

        detail->OpenForCharacterData(entry);
    }

    private static void OpenFreeCompanyProfile
    (
        ulong contentID
    )
    {
        var agent = AgentFreeCompanyProfile.Instance();
        if (agent == null) return;

        agent->ShowProfile(contentID);
    }

    private string GetMemberName
    (
        ulong contentID
    )
        => members.TryGetValue(contentID, out var member) ?
               member.Name :
               contentID.ToString();

    private static uint GetOnlineStatusIconID
    (
        InfoProxyCommonList.CharacterData.OnlineStatus status
    )
    {
        // 默认的 0 无法获取图标
        if (status == InfoProxyCommonList.CharacterData.OnlineStatus.Offline)
            return 10;

        var value = (ulong)status;

        var lowestBit = value & (~value + 1);

        uint position = 0;

        while (lowestBit > 1UL)
        {
            lowestBit >>= 1;
            position++;
        }

        return position;
    }

    private static int? ParseOfflineMinutes
    (
        string locationText
    )
    {
        if (string.IsNullOrWhiteSpace(locationText)) return null;

        foreach (var (pattern, unitMinutes) in GetOfflineTimePatterns())
        {
            var match = pattern.Match(locationText);
            if (!match.Success) continue;

            if (match.Groups.Count < 2) return unitMinutes;

            return int.TryParse(match.Groups[1].Value, out var value) ?
                       value * unitMinutes :
                       null;
        }

        return null;
    }

    // 客户端使用 Addon 39 - 42 的文本渲染最后上线时间, 模板中的数字槽位由游戏填充
    private static List<(Regex Pattern, int UnitMinutes)> GetOfflineTimePatterns()
    {
        if (OfflineTimePatterns != null) return OfflineTimePatterns;

        List<(Regex, int)> patterns = [];

        foreach (var (rowID, unitMinutes) in OfflineTimeAddons)
        {
            var template = LuminaWrapper.GetAddonTextSeString(rowID).ExtractText(false, DIGIT_PLACEHOLDER);
            if (string.IsNullOrWhiteSpace(template)) continue;

            var source = Regex.Escape(template).Replace(DIGIT_PLACEHOLDER, "([0-9]+)");
            patterns.Add((new Regex($"^{source}$", RegexOptions.Compiled), unitMinutes));
        }

        return OfflineTimePatterns = patterns;
    }

    private sealed class MemberRecord
    {
        public ulong                    ContentID      { get; init; }
        public int                      Index          { get; set; }
        public uint                     OnlineStatus   { get; set; }
        public string                   Name           { get; set; } = string.Empty;
        public string                   RankText       { get; set; } = string.Empty;
        public byte                     RankIndex      { get; set; }
        public bool                     IsOnline       { get; set; }
        public int?                     OfflineMinutes { get; set; }
        public ISharedImmediateTexture? JobIcon        { get; set; }
        public string                   JobText        { get; set; } = string.Empty;
        public string                   LocationText   { get; set; } = string.Empty;
    }

    private enum LastOnlineRange
    {
        All,
        Online,
        WithinHour,
        WithinDay,
        WithinWeek,
        WithinMonth,
        BeyondMonth,
        Unknown
    }

    #region 常量

    private const uint PAGE_SIZE  = 200;
    private const int  PAGE_LIMIT = 3;

    private const string RANK_FILTER_POPUP_ID        = "MemberRankFilter_Popup";
    private const string LAST_ONLINE_FILTER_POPUP_ID = "MemberLastOnlineFilter_Popup";

    // InfoProxyFreeCompany.Ranks 的容量
    private const byte FC_RANK_COUNT = 16;

    private const int MEMBER_RANK_INDEX_SHIFT = 12;

    // 客户端判断成员是否在线的掩码: Online | AnotherWorld
    private const ulong ONLINE_STATE_MASK = 0x810000000000UL;

    private const string DIGIT_PLACEHOLDER = "@@NUM@@";

    #endregion

    private static readonly LastOnlineRange[] LastOnlineRanges =
    [
        LastOnlineRange.All,
        LastOnlineRange.Online,
        LastOnlineRange.WithinHour,
        LastOnlineRange.WithinDay,
        LastOnlineRange.WithinWeek,
        LastOnlineRange.WithinMonth,
        LastOnlineRange.BeyondMonth,
        LastOnlineRange.Unknown
    ];

    private static readonly (uint RowID, int UnitMinutes)[] OfflineTimeAddons =
    [
        (39, 5),
        (40, 1),
        (41, 60),
        (42, 1440)
    ];

    private static List<(Regex Pattern, int UnitMinutes)>? OfflineTimePatterns;
}
