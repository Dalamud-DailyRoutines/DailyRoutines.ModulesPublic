using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.Modules;

public unsafe class FCMemberManagePanel : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("FCMemberManagePanelTitle"),
        Description = GetLoc("FCMemberManagePanelDescription"),
        Category = ModuleCategories.UIOptimization,
    };

    private static TaskHelper? ContextTaskHelper;
    
    private static readonly Throttler<string> Throttler = new();

    private static readonly Dictionary<ulong, FreeCompanyMemberInfo> CharacterDataDict = [];
    private static          List<FreeCompanyMemberInfo>              CharacterDataDisplay => FilterAndSortCharacterData();
    private static readonly HashSet<FreeCompanyMemberInfo>           SelectedMembers      = [];

    private static readonly CompSig AgentFCReceiveEventInternalSig =
        new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 41 56 48 83 EC ?? 48 8B F1 48 8B DA");
    private delegate nint AgentFCReceiveEventInternalDelegate(AgentFreeCompany* agent, nint a2);
    private static AgentFCReceiveEventInternalDelegate? AgentFCReceiveEventInternal;

    private static readonly CompSig OpenFCMemberContextMenuSig = new("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 48 8D 4F 10 E8 ?? ?? ?? ?? 44 8B 43 20");
    private delegate void OpenFCMemberContextMenuDelegate(AgentFreeCompany* agent, ushort index);
    private static OpenFCMemberContextMenuDelegate? OpenFCMemberContextMenu;
    
    private static uint FCTotalMembersCount;
    private static int  CurrentFCMemberPage;
    
    private static bool   IsReverse;
    private static string FilterMemberName = string.Empty;

    protected override void Init()
    {
        ContextTaskHelper ??= new() { TimeLimitMS = 3000 };

        OpenFCMemberContextMenu ??=
            Marshal.GetDelegateForFunctionPointer<OpenFCMemberContextMenuDelegate>(OpenFCMemberContextMenuSig.ScanText());

        AgentFCReceiveEventInternal ??= 
            Marshal.GetDelegateForFunctionPointer<AgentFCReceiveEventInternalDelegate>(AgentFCReceiveEventInternalSig.ScanText());
        
        Overlay       ??= new(this);
        Overlay.Flags &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags &=  ~ImGuiWindowFlags.NoResize;
        Overlay.WindowName = Lang.Get("FCMemberManagePanelTitle");
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyMember", OnAddonMember);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyMember", OnAddonMember);
        if (FreeCompanyMember != null && IsAddonAndNodesReady(FreeCompanyMember)) 
            OnAddonMember(AddonEvent.PostSetup, null);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonMember);
        DService.AddonLifecycle.UnregisterListener(OnAddonYesno);

        ContextTaskHelper?.Abort();
        ContextTaskHelper = null;
        
        base.Uninit();
        
        ResetAllExistedData();
    }

    protected override void OverlayPreDraw()
    {
        if (!DService.ClientState.IsLoggedIn) return;
        
        if (FCTotalMembersCount == 0 && Throttler.Throttle("GetFCTotalMembersCount", 1_000))
        {
            var instance = InfoProxyFreeCompany.Instance();
            instance->RequestData();
            FCTotalMembersCount = instance->TotalMembers;
        }

        if (FCTotalMembersCount != 0 && Throttler.Throttle("SubstituteFCMembersData", 1_000))
        {
            var agent          = AgentFreeCompany.Instance();
            var memberInstance = agent->InfoProxyFreeCompanyMember;

            CurrentFCMemberPage = agent->CurrentMemberPageIndex;
            
            if (Throttler.Throttle("Re-RequestMembersInfo", 3_000))
            {
                var source = memberInstance->CharDataSpan;
                for (var i = 0; i < source.Length; i++)
                {
                    var newData = FreeCompanyMemberInfo.Parse(source[i], i);
                    if (string.IsNullOrWhiteSpace(newData.Name)) continue;
                    
                    if (CharacterDataDict.TryGetValue(newData.ContentID, out var existingData))
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
                        CharacterDataDict[newData.ContentID] = newData;
                }
            }
        }
    }

    protected override void OverlayUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{Lang.Get("FCMemberManagePanel-CurrentPage")}:");
        
        var pageAmount = ((int)FCTotalMembersCount + 199) / 200;
        for (var i = 0; i < pageAmount; i++)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.TankBlue, i == CurrentFCMemberPage))
            {
                using (ImRaii.Disabled(i == CurrentFCMemberPage))
                {
                    if (ImGui.Button(Lang.Get("FCMemberManagePanel-PageDisplay", i + 1)))
                        SwitchFreeCompanyMemberListPage(i);
                }
            }
        }
        
        var       tableSize = ImGui.GetContentRegionAvail() with { Y = 0 };
        using var table     = ImRaii.Table("FCMembersTable", 5, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;
        
        ImGui.TableSetupColumn("序号", ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("测试测试测").X);
        ImGui.TableSetupColumn("位置", ImGuiTableColumnFlags.WidthStretch, 25);
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeight());

        if (DService.ClientState.ClientLanguage == (ClientLanguage)4)
            ImGui.TableSetColumnEnabled(5, false);
        
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawHeaderRow();
        
        foreach (var data in CharacterDataDisplay)
        {
            using var id       = ImRaii.PushId(data.ContentID.ToString());
            var       selected = SelectedMembers.Contains(data);
            
            ImGui.TableNextRow();
            
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{data.Index}", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (!SelectedMembers.Remove(data)) 
                    SelectedMembers.Add(data);
            }
            
            DrawSingleContextMenu(data);
            
            ImGui.TableNextColumn();
            LuminaGetter.TryGetRow<OnlineStatus>(data.OnlineStatus, out var onlineStatusRow);
            if (data.OnlineStatus != 0)
            {
                var onlineStatusIcon  = DService.Texture.GetFromGameIcon(new(onlineStatusRow.Icon)).GetWrapOrDefault();
                if (onlineStatusIcon != null)
                {
                    var origPosY = ImGui.GetCursorPosY();
                    ImGui.SetCursorPosY(origPosY + (2f * GlobalFontScale));
                    ImGui.Image(onlineStatusIcon.ImGuiHandle, new(ImGui.GetTextLineHeight()));
                    ImGui.SetCursorPosY(origPosY);
                    ImGui.SameLine();
                }
            }
            ImGui.Text($"{data.Name}");
            
            ImGui.TableNextColumn();
            if (data.JobIcon != null)
            {
                var origPosY = ImGui.GetCursorPosY();
                ImGui.SetCursorPosY(origPosY + (2f * GlobalFontScale));
                ImGui.Image(data.JobIcon.GetWrapOrEmpty().ImGuiHandle, new(ImGui.GetTextLineHeight()));
                ImGui.SetCursorPosY(origPosY);
                ImGui.SameLine();
            }
            ImGui.Text(data.Job);
            
            ImGui.TableNextColumn();
            ImGui.Text(data.Location);
            
            ImGui.TableNextColumn();
            using (ImRaii.Disabled())
                ImGui.Checkbox($"{data.ContentID}_Checkbox", ref selected);
        }
    }

    private static void DrawHeaderRow()
    {
        ImGui.TableNextColumn();
        var arrowButton = IsReverse
                              ? ImGui.ArrowButton("IndexButton", ImGuiDir.Up)
                              : ImGui.ArrowButton("IndexButton", ImGuiDir.Down);
        if (arrowButton)
            IsReverse ^= true;
        
        ImGui.TableNextColumn();
        ImGui.Selectable(Lang.Get("Name"));
        if (ImGui.BeginPopupContextItem("NameSearch_Popup"))
        {
            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            ImGui.InputTextWithHint("###NameSearchInput", Lang.Get("PleaseSearch"), 
                                    ref FilterMemberName, 128);
            ImGui.EndPopup();
        }
        
        ImGui.TableNextColumn();
        ImGui.Text(Lang.Get("Job"));
        
        ImGui.TableNextColumn();
        ImGui.Text(Lang.Get("FCMemberManagePanel-PositionLastTime"));
        
        ImGui.TableNextColumn();
        if (ImGuiOm.ButtonIcon("OpenMultiPopup", FontAwesomeIcon.EllipsisH, string.Empty, true))
            ImGui.OpenPopup("Multi_Popup");

        DrawMultiContextMenu();
    }
    
    private static List<FreeCompanyMemberInfo> FilterAndSortCharacterData()
    {
        var filteredList = string.IsNullOrWhiteSpace(FilterMemberName)
                               ? CharacterDataDict.Values.ToList()
                               : CharacterDataDict.Values
                                                  .Where(member => member.Name.Contains(FilterMemberName, StringComparison.OrdinalIgnoreCase))
                                                  .ToList();

        filteredList.Sort((a, b) =>
        {
            var comparison = a.Index.CompareTo(b.Index);
            return IsReverse ? -comparison : comparison;
        });

        return filteredList;
    }
    
    // 不能用 ImRaii - 会导致延迟执行产生的数据错误
    private static void DrawSingleContextMenu(FreeCompanyMemberInfo data)
    {
        if (ImGui.BeginPopupContextItem($"{data.ContentID}_Popup"))
        {
            ImGui.Text($"{data.Name}");
        
            ImGui.Separator();
            ImGui.Spacing();
        
            // 冒险者铭牌
            if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(15083)!.Value.Text.ExtractText()))
                OpenContextMenuAndClick(data.Index, LuminaGetter.GetRow<Addon>(15083)!.Value.Text.ExtractText());
        
            // 个人信息
            if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(51)!.Value.Text.ExtractText()))
                OpenContextMenuAndClick(data.Index, LuminaGetter.GetRow<Addon>(51)!.Value.Text.ExtractText());
        
            // 部队信息
            if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2807)!.Value.Text.ExtractText()))
                OpenContextMenuAndClick(data.Index, LuminaGetter.GetRow<Addon>(2807)!.Value.Text.ExtractText());
            
            // 任命
            if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2656)!.Value.Text.ExtractText()))
                OpenContextMenuAndClick(data.Index, LuminaGetter.GetRow<Addon>(2656)!.Value.Text.ExtractText());
            
            // 除名
            if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2801)!.Value.Text.ExtractText()))
                OpenContextMenuAndClick(data.Index, LuminaGetter.GetRow<Addon>(2801)!.Value.Text.ExtractText());

            ImGui.EndPopup();
        }
    }
    
    private static void DrawMultiContextMenu()
    {
        if (ImGui.BeginPopupContextItem("Multi_Popup"))
        {
            ImGui.Text(Lang.Get("FCMemberManagePanel-SelectedMembers", SelectedMembers.Count));
        
            ImGui.Separator();
            ImGui.Spacing();
            
            using (ImRaii.Disabled(SelectedMembers.Count == 0))
            {
                // 清除已选
                if (ImGui.MenuItem(Lang.Get("Clear")))
                    SelectedMembers.Clear();
                
                // 冒险者铭牌
                if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(15083)!.Value.Text.ExtractText()))
                    EnqueueContentMenuClicks(SelectedMembers, LuminaGetter.GetRow<Addon>(15083)!.Value.Text.ExtractText());
        
                // 个人信息
                if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(51)!.Value.Text.ExtractText()))
                    EnqueueContentMenuClicks(SelectedMembers, LuminaGetter.GetRow<Addon>(51)!.Value.Text.ExtractText(), "SocialDetailB");
        
                // 部队信息
                if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2807)!.Value.Text.ExtractText()))
                    EnqueueContentMenuClicks(SelectedMembers, LuminaGetter.GetRow<Addon>(2807)!.Value.Text.ExtractText());
            
                // 任命
                if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2656)!.Value.Text.ExtractText()))
                    EnqueueContentMenuClicks(SelectedMembers, LuminaGetter.GetRow<Addon>(2656)!.Value.Text.ExtractText());
            
                // 除名
                if (ImGui.MenuItem(LuminaGetter.GetRow<Addon>(2801)!.Value.Text.ExtractText()))
                {
                    EnqueueContentMenuClicks(SelectedMembers, LuminaGetter.GetRow<Addon>(2801)!.Value.Text.ExtractText(), "SelectYesno",
                                             () =>
                                             {
                                                 ContextTaskHelper.Enqueue(() => ClickSelectYesnoYes(), null, null, null, 1);
                                                 return true;
                                             });
                }
            }

            ImGui.EndPopup();
        }
    }
    
    private void OnAddonMember(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            _                      => Overlay.IsOpen
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

    private static void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!ContextTaskHelper.IsBusy || args.Addon == nint.Zero) return;

        var addon = args.Addon.ToAtkUnitBase();
        Callback(addon, true, 0);
    }

    private static void EnqueueContentMenuClicks(
        IEnumerable<FreeCompanyMemberInfo> datas, string text, string? waitAddon = null, Func<bool?>? extraAction = null)
    {
        ContextTaskHelper.Abort();
        foreach (var data in datas)
        {
            ContextTaskHelper.Enqueue(() => OpenContextMenuAndClick(data.Index, text));
            if (waitAddon != null)
                ContextTaskHelper.Enqueue(() => TryGetAddonByName<AtkUnitBase>(waitAddon, out var addon) && IsAddonAndNodesReady(addon));
            
            if (extraAction != null)
                ContextTaskHelper.Enqueue(extraAction);
            
            ContextTaskHelper.DelayNext(500);
        }
    }

    private static void OpenContextMenuAndClick(int dataIndex, string menuText)
    {
        OpenContextMenuByIndex(dataIndex);
        ContextTaskHelper.Enqueue(() =>
        {
            if (InfosOm.ContextMenu == null || !IsAddonAndNodesReady(InfosOm.ContextMenu)) return false;
            
            if (!ClickContextMenu(menuText))
            {
                InfosOm.ContextMenu->Close(true);
                NotificationError(
                    $"{Lang.Get("FCMemberManagePanel-ContextMenuItemNoFound")}: {menuText}");
            }
            return true;
        }, null, null, null, 2);
    }
    
    private static void OpenContextMenuByIndex(int dataIndex) 
        => OpenFCMemberContextMenu(AgentFreeCompany.Instance(), (ushort)dataIndex);

    private static void SwitchFreeCompanyMemberListPage(int page)
    {
        var memoryBlock = Marshal.AllocHGlobal(32);
        var agent       = AgentFreeCompany.Instance();

        try
        {
            var value1 = (AtkValue*)memoryBlock;
            value1->Type = ValueType.Int;
            value1->SetInt(1);
            
            var value2 = (AtkValue*)(memoryBlock + 16);
            value2->Type  = ValueType.UInt;
            value2->SetUInt((uint)page);
            
            AgentFCReceiveEventInternal(agent, memoryBlock);
        }
        finally
        {
            Marshal.FreeHGlobal(memoryBlock);
        }

        CharacterDataDict.Clear();
        SelectedMembers.Clear();
        Throttler.Clear();
    }

    private static void ResetAllExistedData()
    {
        var agent = AgentFreeCompany.Instance();
        if (agent == null) return;
        var info = agent->InfoProxyFreeCompanyMember;
        if (info == null) return;
        info->ClearData();
        
        CharacterDataDict.Clear();
        SelectedMembers.Clear();
        Throttler.Clear();
    }

    public class FreeCompanyMemberInfo : IEquatable<FreeCompanyMemberInfo>, IComparable<FreeCompanyMemberInfo>
    {
        public ulong                    ContentID    { get; set; }
        public int                      Index        { get; set; }
        public uint                     OnlineStatus { get; set; }
        public string                   Name         { get; set; }
        public ISharedImmediateTexture? JobIcon      { get; set; }
        public string                   Job          { get; set; }
        public string                   Location     { get; set; }
        
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
                lastOnlineTime = SeString.Parse(stringArray[1 + (index * 5)]).TextValue;
            }
            catch (Exception)
            {
                // ignored
            }

            return new FreeCompanyMemberInfo
            {
                ContentID = data.ContentId,
                Index     = index,
                OnlineStatus = (uint)GetOrigOnlineStatusID(data.State),
                Name    = string.IsNullOrWhiteSpace(data.NameString) ? LuminaGetter.GetRow<Addon>(964)!.Value.Text.ExtractText() : data.NameString,
                JobIcon = data.Job == 0 ? null : DService.Texture.GetFromGameIcon(new(62100U + data.Job)),
                Job     = data.Job == 0 ? string.Empty : LuminaGetter.GetRow<ClassJob>(data.Job)?.Abbreviation.ExtractText(),
                Location = data.Location != 0
                               ? LuminaGetter.TryGetRow<TerritoryType>(data.Location, out var zone) ? zone.PlaceName.Value.Name.ExtractText() : lastOnlineTime
                               : lastOnlineTime,
            };
        }
        
        public static int GetOrigOnlineStatusID(InfoProxyCommonList.CharacterData.OnlineStatus status)
        {
            // 默认的 0 无法获取图标
            if (status == InfoProxyCommonList.CharacterData.OnlineStatus.Offline)
                return 10;
    
            var value = (ulong)status;
    
            var lowestBit = value & (~value + 1);
    
            var position = 0;
            while (lowestBit > 1UL)
            {
                lowestBit >>= 1;
                position++;
            }
    
            return position;
        }

        public bool Equals(FreeCompanyMemberInfo? other) 
            => other is not null && this.ContentID == other.ContentID;

        public override bool Equals(object? obj) 
            => Equals(obj as FreeCompanyMemberInfo);

        public override int GetHashCode() 
            => ContentID.GetHashCode();

        public int CompareTo(FreeCompanyMemberInfo? other) 
            => other is null ? 1 : this.Index.CompareTo(other.Index);

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
