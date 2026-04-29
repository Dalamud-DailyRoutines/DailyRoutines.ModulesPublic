using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class FCMemberManagePanel : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("FCMemberManagePanelTitle"),
        Description = Lang.Get("FCMemberManagePanelDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    private static readonly CompSig AgentFCReceiveEventInternalSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 41 56 48 83 EC ?? 48 8B F1 48 8B DA");
    private delegate        nint AgentFCReceiveEventInternalDelegate(AgentFreeCompany* agent, nint a2);
    private static          AgentFCReceiveEventInternalDelegate? AgentFCReceiveEventInternal;
    
    private readonly Dictionary<ulong, FreeCompanyMemberInfo> characterDataDict = [];
    private readonly HashSet<FreeCompanyMemberInfo>           selectedMembers   = [];

    private uint fcTotalMembersCount;
    private int  currentFCMemberPage;

    private bool   isReverse;
    private string filterMemberName = string.Empty;

    private List<FreeCompanyMemberInfo> characterDataDisplay = [];

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 3000 };

        AgentFCReceiveEventInternal ??=
            Marshal.GetDelegateForFunctionPointer<AgentFCReceiveEventInternalDelegate>(AgentFCReceiveEventInternalSig.ScanText());

        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoResize;
        Overlay.WindowName =   Lang.Get("FCMemberManagePanelTitle");

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeCompanyMember", OnAddonMember);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyMember", OnAddonMember);
        if (FreeCompanyMember != null && FreeCompanyMember->IsAddonAndNodesReady())
            OnAddonMember(AddonEvent.PostSetup, null);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonMember);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonYesno);

        ResetAllExistedData();
    }

    protected override void OverlayPreDraw()
    {
        if (!DService.Instance().ClientState.IsLoggedIn) return;

        if (fcTotalMembersCount == 0 && Throttler.Shared.Throttle("FCMemberManagePanel-GetFCTotalMembersCount", 1_000))
        {
            var instance = InfoProxyFreeCompany.Instance();
            instance->RequestData();
            fcTotalMembersCount = instance->TotalMembers;
        }

        if (fcTotalMembersCount != 0 && Throttler.Shared.Throttle("FCMemberManagePanel-SubstituteFCMembersData", 1_000))
        {
            var agent          = AgentFreeCompany.Instance();
            var memberInstance = agent->InfoProxyFreeCompanyMember;

            currentFCMemberPage = agent->CurrentMemberPageIndex;

            if (Throttler.Shared.Throttle("FCMemberManagePanel-RerequestMembersInfo", 3_000))
            {
                var source = memberInstance->CharDataSpan;

                for (var i = 0; i < source.Length; i++)
                {
                    var newData = FreeCompanyMemberInfo.Parse(source[i], i);
                    if (string.IsNullOrWhiteSpace(newData.Name)) continue;

                    if (characterDataDict.TryGetValue(newData.ContentID, out var existingData))
                    {
                        var changes = existingData.UpdateFrom(newData);

                        if (changes != FreeCompanyMemberInfo.ChangeFlags.None)
                        {
                            existingData.Index        = newData.Index;
                            existingData.OnlineStatus = newData.OnlineStatus;
                            existingData.Name         = newData.Name;
                            existingData.JobIcon      = newData.JobIcon;
                            existingData.Job          = newData.Job;
                            existingData.Location     = newData.Location;
                        }
                    }
                    else
                        characterDataDict[newData.ContentID] = newData;
                }

                characterDataDisplay = FilterAndSortCharacterData();
            }
        }
    }

    protected override void OverlayUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("FCMemberManagePanel-CurrentPage")}:");

        var pageAmount = ((int)fcTotalMembersCount + 199) / 200;

        for (var i = 0; i < pageAmount; i++)
        {
            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.TankBlue, i == currentFCMemberPage))
            {
                using (ImRaii.Disabled(i == currentFCMemberPage))
                {
                    if (ImGui.Button(Lang.Get("FCMemberManagePanel-PageDisplay", i + 1)))
                        SwitchFreeCompanyMemberListPage(i);
                }
            }
        }

        var       tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
        using var table     = ImRaii.Table("FCMembersTable", 5, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("序号",  ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("名称",  ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("职业",  ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("测试测试测").X);
        ImGui.TableSetupColumn("位置",  ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeight());

        if (GameState.IsCN || GameState.IsTC)
            ImGui.TableSetColumnEnabled(5, false);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderRow();

        foreach (var data in characterDataDisplay)
        {
            using var id       = ImRaii.PushId(data.ContentID.ToString());
            var       selected = selectedMembers.Contains(data);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();

            if (ImGui.Selectable($"{data.Index}", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (!selectedMembers.Remove(data))
                    selectedMembers.Add(data);
            }

            DrawSingleContextMenu(data);

            ImGui.TableNextColumn();
            LuminaGetter.TryGetRow<OnlineStatus>(data.OnlineStatus, out var onlineStatusRow);

            if (data.OnlineStatus != 0)
            {
                var onlineStatusIcon = DService.Instance().Texture.GetFromGameIcon(new(onlineStatusRow.Icon)).GetWrapOrDefault();

                if (onlineStatusIcon != null)
                {
                    var origPosY = ImGui.GetCursorPosY();
                    ImGui.SetCursorPosY(origPosY + 2f * GlobalUIScale);
                    ImGui.Image(onlineStatusIcon.Handle, new(ImGui.GetTextLineHeight()));
                    ImGui.SetCursorPosY(origPosY);
                    ImGui.SameLine();
                }
            }

            ImGui.TextUnformatted($"{data.Name}");

            ImGui.TableNextColumn();

            if (data.JobIcon != null)
            {
                var origPosY = ImGui.GetCursorPosY();
                ImGui.SetCursorPosY(origPosY + 2f * GlobalUIScale);
                ImGui.Image(data.JobIcon.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeight()));
                ImGui.SetCursorPosY(origPosY);
                ImGui.SameLine();
            }

            ImGui.TextUnformatted(data.Job);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(data.Location);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled())
                ImGui.Checkbox($"{data.ContentID}_Checkbox", ref selected);
        }
    }

    private void DrawHeaderRow()
    {
        ImGui.TableNextColumn();
        var arrowButton = isReverse
                              ? ImGui.Button(FontAwesomeIcon.ArrowUp.ToIconString())
                              : ImGui.Button(FontAwesomeIcon.ArrowDown.ToIconString());
        if (arrowButton)
        {
            isReverse            ^= true;
            characterDataDisplay =  FilterAndSortCharacterData();
        }

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
                    ref filterMemberName,
                    128
                );

                if (ImGui.IsItemDeactivatedAfterEdit())
                    characterDataDisplay = FilterAndSortCharacterData();
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Job"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("FCMemberManagePanel-PositionLastTime"));

        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIcon("OpenMultiPopup", FontAwesomeIcon.EllipsisH, string.Empty, true))
            ImGui.OpenPopup("Multi_Popup");

        DrawMultiContextMenu();
    }
    
    // 不能用 ImRaii - 会导致延迟执行产生的数据错误
    private void DrawSingleContextMenu(FreeCompanyMemberInfo data)
    {
        if (ImGui.BeginPopupContextItem($"{data.ContentID}_Popup"))
        {
            ImGui.TextUnformatted($"{data.Name}");

            ImGui.Separator();
            ImGui.Spacing();

            // 冒险者铭牌
            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(15083)))
                OpenContextMenuAndClick(data.Index, LuminaWrapper.GetAddonText(15083));

            // 个人信息
            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(51)))
                OpenContextMenuAndClick(data.Index, LuminaWrapper.GetAddonText(51));

            // 部队信息
            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2807)))
                OpenContextMenuAndClick(data.Index, LuminaWrapper.GetAddonText(2807));

            // 任命
            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2656)))
                OpenContextMenuAndClick(data.Index, LuminaWrapper.GetAddonText(2656));

            // 除名
            if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2801)))
                OpenContextMenuAndClick(data.Index, LuminaWrapper.GetAddonText(2801));

            ImGui.EndPopup();
        }
    }

    private void DrawMultiContextMenu()
    {
        if (ImGui.BeginPopupContextItem("Multi_Popup"))
        {
            ImGui.TextUnformatted(Lang.Get("FCMemberManagePanel-SelectedMembers", selectedMembers.Count));

            ImGui.Separator();
            ImGui.Spacing();

            using (ImRaii.Disabled(selectedMembers.Count == 0))
            {
                // 清除已选
                if (ImGui.MenuItem(Lang.Get("Clear")))
                    selectedMembers.Clear();

                // 冒险者铭牌
                if (ImGui.MenuItem(LuminaWrapper.GetAddonText(15083)))
                    EnqueueContentMenuClicks(selectedMembers, LuminaWrapper.GetAddonText(15083));

                // 个人信息
                if (ImGui.MenuItem(LuminaWrapper.GetAddonText(51)))
                    EnqueueContentMenuClicks(selectedMembers, LuminaWrapper.GetAddonText(51), "SocialDetailB");

                // 部队信息
                if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2807)))
                    EnqueueContentMenuClicks(selectedMembers, LuminaWrapper.GetAddonText(2807));

                // 任命
                if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2656)))
                    EnqueueContentMenuClicks(selectedMembers, LuminaWrapper.GetAddonText(2656));

                // 除名
                if (ImGui.MenuItem(LuminaWrapper.GetAddonText(2801)))
                {
                    EnqueueContentMenuClicks
                    (
                        selectedMembers,
                        LuminaWrapper.GetAddonText(2801),
                        "SelectYesno",
                        () =>
                        {
                            TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes(), weight: 1);
                            return true;
                        }
                    );
                }
            }

            ImGui.EndPopup();
        }
    }

    private void OnAddonMember(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            _                    => Overlay.IsOpen
        };

        switch (type)
        {
            case AddonEvent.PostSetup:
                ResetAllExistedData();
                break;
            case AddonEvent.PreFinalize:
                var instance = InfoProxyFreeCompany.Instance();
                instance->RequestData();
                break;
        }
    }

    private void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy || args.Addon == nint.Zero) return;

        var addon = args.Addon.ToStruct();
        addon->Callback(0);
    }

    private void EnqueueContentMenuClicks
    (
        IEnumerable<FreeCompanyMemberInfo> datas,
        string                             text,
        string?                            waitAddon   = null,
        Func<bool>?                        extraAction = null
    )
    {
        TaskHelper.Abort();

        foreach (var data in datas)
        {
            TaskHelper.Enqueue(() => OpenContextMenuAndClick(data.Index, text));
            if (waitAddon != null)
                TaskHelper.Enqueue(() => AddonHelper.TryGetByName<AtkUnitBase>(waitAddon, out var addon) && addon->IsAddonAndNodesReady());

            if (extraAction != null)
                TaskHelper.Enqueue(extraAction);

            TaskHelper.DelayNext(500);
        }
    }

    private void OpenContextMenuAndClick(int dataIndex, string menuText)
    {
        AgentFreeCompany.Instance()->OpenContextMenuForMember((byte)dataIndex);
        TaskHelper.Enqueue
        (
            () =>
            {
                if (ContextMenuAddon == null || !ContextMenuAddon->IsAddonAndNodesReady()) return false;

                if (!AddonContextMenuEvent.Select(menuText))
                {
                    ContextMenuAddon->Close(true);
                    NotifyHelper.Instance().NotificationError
                    (
                        $"{Lang.Get("FCMemberManagePanel-ContextMenuItemNoFound")}: {menuText}"
                    );
                }

                return true;
            },
            weight: 2
        );
    }

    private void SwitchFreeCompanyMemberListPage(int page)
    {
        var memoryBlock = Marshal.AllocHGlobal(32);
        var agent       = AgentFreeCompany.Instance();

        try
        {
            var value1 = (AtkValue*)memoryBlock;
            value1->Type = AtkValueType.Int;
            value1->SetInt(1);

            var value2 = (AtkValue*)(memoryBlock + 16);
            value2->Type = AtkValueType.UInt;
            value2->SetUInt((uint)page);

            AgentFCReceiveEventInternal(agent, memoryBlock);
        }
        finally
        {
            Marshal.FreeHGlobal(memoryBlock);
        }

        characterDataDict.Clear();
        selectedMembers.Clear();
    }

    private void ResetAllExistedData()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;
        
        var info = agent->InfoProxyFreeCompanyMember;
        if (info == null) return;
        
        info->ClearData();

        characterDataDict.Clear();
        selectedMembers.Clear();
    }
    
    private List<FreeCompanyMemberInfo> FilterAndSortCharacterData()
    {
        var filteredList = string.IsNullOrWhiteSpace(filterMemberName)
                               ? characterDataDict.Values.ToList()
                               : characterDataDict.Values
                                                  .Where(member => member.Name.Contains(filterMemberName, StringComparison.OrdinalIgnoreCase))
                                                  .ToList();

        filteredList.Sort
        ((a, b) =>
            {
                var comparison = a.Index.CompareTo(b.Index);
                return isReverse ? -comparison : comparison;
            }
        );

        return filteredList;
    }


    private class FreeCompanyMemberInfo : IEquatable<FreeCompanyMemberInfo>, IComparable<FreeCompanyMemberInfo>
    {
        [Flags]
        public enum ChangeFlags
        {
            None         = 0,
            Index        = 1 << 0,
            OnlineStatus = 1 << 1,
            Name         = 1 << 2,
            JobIcon      = 1 << 3,
            Job          = 1 << 4,
            Location     = 1 << 5
        }

        public ulong                    ContentID    { get; set; }
        public int                      Index        { get; set; }
        public uint                     OnlineStatus { get; set; }
        public string                   Name         { get; set; }
        public ISharedImmediateTexture? JobIcon      { get; set; }
        public string                   Job          { get; set; }
        public string                   Location     { get; set; }

        public int CompareTo(FreeCompanyMemberInfo? other)
            => other is null ? 1 : Index.CompareTo(other.Index);

        public bool Equals(FreeCompanyMemberInfo? other)
            => other is not null && ContentID == other.ContentID;

        public ChangeFlags UpdateFrom(FreeCompanyMemberInfo other)
        {
            var changes = ChangeFlags.None;

            if (Index != other.Index)
            {
                Index   =  other.Index;
                changes |= ChangeFlags.Index;
            }

            if (OnlineStatus != other.OnlineStatus)
            {
                OnlineStatus =  other.OnlineStatus;
                changes      |= ChangeFlags.OnlineStatus;
            }

            if (Name != other.Name)
            {
                Name    =  other.Name;
                changes |= ChangeFlags.Name;
            }

            if (JobIcon != other.JobIcon)
            {
                JobIcon =  other.JobIcon;
                changes |= ChangeFlags.JobIcon;
            }

            if (Job != other.Job)
            {
                Job     =  other.Job;
                changes |= ChangeFlags.Job;
            }

            if (Location != other.Location)
            {
                Location =  other.Location;
                changes  |= ChangeFlags.Location;
            }

            return changes;
        }

        public static FreeCompanyMemberInfo Parse(InfoProxyCommonList.CharacterData data, int index)
        {
            var stringArray    = AtkStage.Instance()->GetStringArrayData()[36]->StringArray;
            var lastOnlineTime = string.Empty;

            try
            {
                lastOnlineTime = SeString.Parse(stringArray[1 + index * 5].Value).TextValue;
            }
            catch (Exception)
            {
                // ignored
            }

            return new FreeCompanyMemberInfo
            {
                ContentID    = data.ContentId,
                Index        = index,
                OnlineStatus = (uint)GetOrigOnlineStatusID(data.State),
                Name         = string.IsNullOrWhiteSpace(data.NameString) ? LuminaWrapper.GetAddonText(964) : data.NameString,
                JobIcon      = data.Job == 0 ? null : DService.Instance().Texture.GetFromGameIcon(new(62100U + data.Job)),
                Job          = data.Job == 0 ? string.Empty : LuminaGetter.GetRow<ClassJob>(data.Job)?.Abbreviation.ToString(),
                Location = data.Location != 0
                               ? LuminaGetter.TryGetRow<TerritoryType>(data.Location, out var zone) ? zone.PlaceName.Value.Name.ToString() : lastOnlineTime
                               : lastOnlineTime
            };
        }

        public static int GetOrigOnlineStatusID(InfoProxyCommonList.CharacterData.OnlineStatus status)
        {
            // 默认的 0 无法获取图标
            if (status == InfoProxyCommonList.CharacterData.OnlineStatus.Offline)
                return 10;

            var value = (ulong)status;

            var lowestBit = value & ~value + 1;

            var position = 0;

            while (lowestBit > 1UL)
            {
                lowestBit >>= 1;
                position++;
            }

            return position;
        }

        public override bool Equals(object? obj)
            => Equals(obj as FreeCompanyMemberInfo);

        public override int GetHashCode()
            => ContentID.GetHashCode();

        public static bool operator ==(FreeCompanyMemberInfo? left, FreeCompanyMemberInfo? right)
            => left?.Equals(right) ?? ReferenceEquals(right, null);

        public static bool operator !=(FreeCompanyMemberInfo left, FreeCompanyMemberInfo right)
            => !(left == right);

        public static bool operator <(FreeCompanyMemberInfo left, FreeCompanyMemberInfo? right)
            => ReferenceEquals(left, null) ? !ReferenceEquals(right, null) : left.CompareTo(right) < 0;

        public static bool operator <=(FreeCompanyMemberInfo left, FreeCompanyMemberInfo? right)
            => ReferenceEquals(left, null) || left.CompareTo(right) <= 0;

        public static bool operator >(FreeCompanyMemberInfo left, FreeCompanyMemberInfo? right)
            => !ReferenceEquals(left, null) && left.CompareTo(right) > 0;

        public static bool operator >=(FreeCompanyMemberInfo left, FreeCompanyMemberInfo? right)
            => ReferenceEquals(left, null) ? ReferenceEquals(right, null) : left.CompareTo(right) >= 0;
    }
}
