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

    private uint totalMemberCount;
    private int  currentPage;

    private bool   isDescending;
    private string nameFilter = string.Empty;

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
        ImGui.Selectable(Lang.Get("Name"));

        using (var context = ImRaii.ContextPopupItem("NameSearch_Popup"))
        {
            if (context)
            {
                ImGui.SetNextItemWidth(200f * GlobalUIScale);
                ImGui.InputTextWithHint
                (
                    "###NameSearchInput",
                    Lang.Get("PleaseSearch"),
                    ref nameFilter,
                    128
                );
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted("阶级");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Job"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("FCMemberManagePanel-PositionLastTime"));

        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIcon("OpenMultiPopup", FontAwesomeIcon.EllipsisH, string.Empty, true))
            ImGui.OpenPopup("Multi_Popup");

        DrawMultiContextMenu();
    }

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
                Lang.Get("FCMemberManagePanel-ConfirmKick", GetMemberName(pendingTargets[0])) :
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
            record.JobIcon = data.Job == 0 ?
                                 null :
                                 ITextureProvider.Instance().GetFromGameIcon(new(62100U + data.Job));
            record.JobText = data.Job == 0 ?
                                 string.Empty :
                                 LuminaGetter.GetRowOrDefault<ClassJob>(data.Job).Name.ToString() ?? string.Empty;
            record.LocationText = agent->GetMemberLocationText(i);

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
        var query = string.IsNullOrWhiteSpace(nameFilter) ?
                        members.Values :
                        members.Values.Where(x => x.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        var list = query.ToList();
        list.Sort
        ((a, b) => isDescending ?
                       b.Index.CompareTo(a.Index) :
                       a.Index.CompareTo(b.Index)
        );
        return list;
    }

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

    private sealed class MemberRecord
    {
        public ulong                    ContentID    { get; init; }
        public int                      Index        { get; set; }
        public uint                     OnlineStatus { get; set; }
        public string                   Name         { get; set; } = string.Empty;
        public string                   RankText     { get; set; } = string.Empty;
        public ISharedImmediateTexture? JobIcon      { get; set; }
        public string                   JobText      { get; set; } = string.Empty;
        public string                   LocationText { get; set; } = string.Empty;
    }
    
    #region 常量
    
    private const uint PAGE_SIZE  = 200;
    private const int  PAGE_LIMIT = 3;
    
    #endregion
}
