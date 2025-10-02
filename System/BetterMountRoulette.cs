using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class BetterMountRoulette : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("BetterMountRouletteTitle"),
        Description = GetLoc("BetterMountRouletteDescription"),
        Category    = ModuleCategories.System,
        Author      = ["XSZYYS"]
    };

    private static Config ModuleConfig = null!;

    private static LuminaSearcher<Mount>? MasterMountsSearcher;

    private static MountListHandler? NormalMounts;
    private static MountListHandler? PVPMounts;
    private static Dictionary<string, MountListHandler> CustomMounts = [];

    private static string ZoneSearchInput = string.Empty;
    private static string NewTabNameInput = string.Empty;
    private static string? RenamingTabName;
    private static bool IsAddingNewTab;
    
    private static HashSet<uint>? MountsListToUse;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        UseActionManager.RegPreUseAction(OnPreUseAction);

        DService.ClientState.Login += OnLogin;
        if (DService.ClientState.IsLoggedIn)
            OnLogin();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        DService.ClientState.Login            -= OnLogin;
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        
        MasterMountsSearcher = null;
        NormalMounts         = null;
        PVPMounts            = null;
        CustomMounts.Clear();

        IsAddingNewTab  = false;
        MountsListToUse = null;
    }

    protected override void ConfigUI()
    {
        if (NormalMounts == null || PVPMounts == null)
            return;

        using var tabBar = ImRaii.TabBar("##MountTabs", ImGuiTabBarFlags.Reorderable);
        if (!tabBar) return;
        
        DrawTab(GetLoc("General"), NormalMounts);

        DrawTab("PVP", PVPMounts);

        DrawCustomTabs();

        HandleNewTabAddition();
    }

    private void DrawTab(string tabLabel, MountListHandler handler)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;

        DrawSearchAndMountsGrid(tabLabel, handler);
    }
    
    private void DrawCustomTabs()
    {
        for (var i = 0; i < ModuleConfig.CustomMountLists.Count; i++)
        {
            var list = ModuleConfig.CustomMountLists[i];
            if (!CustomMounts.TryGetValue(list.Name, out var handler)) continue;

            var pOpen = true;
            var tabFlags = RenamingTabName == list.Name ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;

            using var id = ImRaii.PushId(i);
            using var tab = ImRaii.TabItem(list.Name, ref pOpen, tabFlags);
            if (tab)
            {
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyCtrl) // 按住ctrl+鼠标左键进行重命名
                    RenamingTabName = list.Name;

                HandleTabRenaming(list, handler);

                DrawCustomTab(list, handler);
            }

            if (!pOpen)
            {
                CustomMounts.Remove(list.Name);
                ModuleConfig.CustomMountLists.RemoveAt(i);
                SaveConfig(ModuleConfig);
                return;
            }
        }
    }
    
    private void HandleTabRenaming(CustomMountList list, MountListHandler handler)
    {
        if (RenamingTabName != list.Name) return;

        var tempName = list.Name;
        ImGui.SetKeyboardFocusHere();
        if (ImGui.InputText("###Rename", ref tempName, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
        {
            if (!string.IsNullOrWhiteSpace(tempName) && !IsNameExists(tempName))
            {
                CustomMounts.Remove(list.Name);
                CustomMounts[tempName] = handler;
                list.Name = tempName;
                SaveConfig(ModuleConfig);
            }
            RenamingTabName = null;
        }
        if (ImGui.IsItemDeactivated())
            RenamingTabName = null;
    }
    
    private void HandleNewTabAddition()
    {
        if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
            IsAddingNewTab = true;

        if (!IsAddingNewTab) return;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.SetKeyboardFocusHere();
        if (ImGui.InputText("###NewTabName", ref NewTabNameInput, 64, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (!string.IsNullOrWhiteSpace(NewTabNameInput) && !IsNameExists(NewTabNameInput))
            {
                var newList = new CustomMountList { Name = NewTabNameInput };
                ModuleConfig.CustomMountLists.Add(newList);
                CustomMounts[NewTabNameInput] = new MountListHandler(MasterMountsSearcher, newList.MountIDs);
                SaveConfig(ModuleConfig);
            }

            IsAddingNewTab = false;
            NewTabNameInput = string.Empty;
        }
        if (ImGui.IsItemDeactivated())
            IsAddingNewTab = false;
    }

    private void DrawSearchAndMountsGrid(string tabLabel, MountListHandler handler)
    {
        // 搜索框
        var searchText = handler.SearchText;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint($"##Search{tabLabel}", GetLoc("Search"), ref searchText, 128))
        {
            handler.SearchText = searchText;
            handler.Searcher.Search(searchText);
        }

        // 显示坐骑区域
        var       childSize = new Vector2(ImGui.GetContentRegionAvail().X, 400 * GlobalFontScale);
        using var child     = ImRaii.Child($"##MountsGrid{tabLabel}", childSize, true);
        if (!child) return;

        DrawMountsGrid(handler.Searcher.SearchResult, handler);
    }

    private void DrawCustomTab(CustomMountList customList, MountListHandler handler)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Zone")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        var customListZoneIDs = customList.ZoneIDs;
        if (ZoneSelectCombo(ref customListZoneIDs, ref ZoneSearchInput))
            SaveConfig(ModuleConfig);

        ImGui.Separator();
        
        DrawSearchAndMountsGrid(customList.Name, handler);
    }

    private void DrawMountsGrid(List<Mount> mountsToDraw, MountListHandler handler)
    {
        if (mountsToDraw.Count == 0) return;
        
        var itemWidthEstimate = 150f * GlobalFontScale;
        var contentWidth      = ImGui.GetContentRegionAvail().X;
        var columnCount       = Math.Max(1, (int)Math.Floor(contentWidth / itemWidthEstimate));
        var iconSize          = 3 * ImGui.GetTextLineHeightWithSpacing();

        using var table = ImRaii.Table("##MountsGridTable", columnCount, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;

        foreach (var mount in mountsToDraw)
        {
            if (!ImageHelper.TryGetGameIcon(mount.Icon, out var texture)) continue;
            
            ImGui.TableNextColumn();
            
            var cursorPos   = ImGui.GetCursorPos();
            var contentSize  = new Vector2(ImGui.GetContentRegionAvail().X, 4 * ImGui.GetTextLineHeightWithSpacing());
            
            using (ImRaii.Group())
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((contentSize.X - iconSize) / 2));
                ImGui.Image(texture.Handle, new(iconSize));

                var mountName = mount.Singular.ExtractText();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((contentSize.X - ImGui.CalcTextSize(mountName).X) / 2));
                ImGui.Text(mountName);
            }
            
            ImGui.SetCursorPos(cursorPos);
            using (ImRaii.PushColor(ImGuiCol.Button, ButtonNormalColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, ButtonActiveColor))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ButtonHoveredColor))
            using (ImRaii.PushColor(ImGuiCol.Button, ButtonSelectedColor, handler.SelectedIDs.Contains(mount.RowId)))
            {
                if (ImGui.Button($"##{mount.RowId}_{cursorPos}", contentSize))
                {
                    if (!handler.SelectedIDs.Add(mount.RowId))
                        handler.SelectedIDs.Remove(mount.RowId);
                    SaveConfig(ModuleConfig);
                }
                
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
        }
    }
    
    private static void OnZoneChanged(ushort obj) => 
        OnLogin();
    
    private static unsafe void OnLogin()
    {
        var unlockedMounts = LuminaGetter.Get<Mount>()
                                         .Where(mount => PlayerState.Instance()->IsMountUnlocked(mount.RowId) &&
                                                         mount.Icon != 0                                      &&
                                                         !string.IsNullOrEmpty(mount.Singular.ExtractText()))
                                         .ToList();

        MasterMountsSearcher = new LuminaSearcher<Mount>(
            unlockedMounts,
            [
                x => x.Singular.ExtractText()
            ],
            x => x.Singular.ExtractText()
        );

        NormalMounts = new(MasterMountsSearcher, ModuleConfig.NormalRouletteMounts);
        PVPMounts    = new(MasterMountsSearcher, ModuleConfig.PVPRouletteMounts);
        
        CustomMounts.Clear();
        foreach (var list in ModuleConfig.CustomMountLists)
            CustomMounts[list.Name] = new MountListHandler(MasterMountsSearcher, list.MountIDs);
    }

    private bool IsNameExists(string name)
    {
        foreach (var list in ModuleConfig.CustomMountLists)
        {
            if (list.Name.Equals(name, StringComparison.Ordinal))
                return true;
        }
        
        return false;
    }

    private static void OnPreUseAction(
        ref bool                        isPrevented,
        ref ActionType                  actionType,
        ref uint                        actionID,
        ref ulong                       targetID,
        ref uint                        extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint                        comboRouteID)
    {
        if (!DService.Condition[ConditionFlag.Mounted] && actionType == ActionType.GeneralAction && MountRouletteActionIDs.Contains(actionID))
        {
            MountsListToUse = null;
            var currentZone = DService.ClientState.TerritoryType;

            foreach (var list in ModuleConfig.CustomMountLists)
            {
                if (list.ZoneIDs.Contains(currentZone) && list.MountIDs.Count > 0)
                {
                    MountsListToUse = list.MountIDs;
                    break;
                }
            }

            if (MountsListToUse == null)
            {
                MountsListToUse = GameState.IsInPVPArea
                                      ? ModuleConfig.PVPRouletteMounts
                                      : ModuleConfig.NormalRouletteMounts;
            }
        }

        if (MountsListToUse != null && actionType == ActionType.Mount)
        {
            try
            {
                if (MountsListToUse.Count > 0)
                {
                    var mountListAsList = MountsListToUse.ToList();
                    var randomMountID   = mountListAsList[Random.Shared.Next(mountListAsList.Count)];
                    actionID = randomMountID;
                }
            }
            finally
            {
                MountsListToUse = null;
            }
        }
    }

    private class CustomMountList : IEquatable<CustomMountList>
    {
        public string        Name     { get; set; } = string.Empty;
        public HashSet<uint> ZoneIDs  { get; set; } = [];
        public HashSet<uint> MountIDs { get; set; } = [];

        public bool Equals(CustomMountList? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return Name == other.Name;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((CustomMountList)obj);
        }
        public override int GetHashCode() => Name.GetHashCode();
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> NormalRouletteMounts     = [];
        public HashSet<uint> PVPRouletteMounts        = [];
        public List<CustomMountList> CustomMountLists = [];
    }

    private class MountListHandler(LuminaSearcher<Mount> searcher, HashSet<uint> selectedIDs)
    {
        public LuminaSearcher<Mount> Searcher     { get; }       = searcher;
        public HashSet<uint>         SelectedIDs  { get; }       = selectedIDs;
        public string                SearchText   { get; set; }  = string.Empty;
        public int                   DisplayCount { get; init; } = searcher.Data.Count;
    }

    #region 数据

    private const int PageSize = 100;
    
    private static readonly HashSet<uint> MountRouletteActionIDs = [9, 24];
    
    private static readonly Vector4 ButtonNormalColor   = ImGuiCol.Button.ToVector4().WithAlpha(0f);
    private static readonly Vector4 ButtonActiveColor   = ImGuiCol.ButtonActive.ToVector4().WithAlpha(0.8f);
    private static readonly Vector4 ButtonHoveredColor  = ImGuiCol.ButtonHovered.ToVector4().WithAlpha(0.4f);
    private static readonly Vector4 ButtonSelectedColor = ImGuiCol.Button.ToVector4().WithAlpha(0.6f);

    #endregion
}
