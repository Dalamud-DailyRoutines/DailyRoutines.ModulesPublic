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

    private static bool IsNeedToModify;
    
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

        IsNeedToModify = false;
    }

    protected override void ConfigUI()
    {
        if (NormalMounts == null || PVPMounts == null)
            return;
        
        using var tabBar = ImRaii.TabBar("##MountTabs");
        if (!tabBar) return;
        
        DrawTab(GetLoc("General"), NormalMounts);

        DrawTab("PVP", PVPMounts);
    }

    private void DrawTab(string tabLabel, MountListHandler handler)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;
        
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
            var mountList = GameState.IsInPVPArea ? ModuleConfig.PVPRouletteMounts : ModuleConfig.NormalRouletteMounts;
            if (mountList.Count > 0)
                IsNeedToModify = true;
        }

        if (IsNeedToModify && actionType == ActionType.Mount)
        {
            try
            {
                var mountList = GameState.IsInPVPArea ? ModuleConfig.PVPRouletteMounts : ModuleConfig.NormalRouletteMounts;
                if (mountList.Count > 0)
                {
                    var mountListAsList = mountList.ToList();
                    var randomMountID   = mountListAsList[Random.Shared.Next(mountListAsList.Count)];
                    actionID = randomMountID;
                }
            }
            finally
            {
                IsNeedToModify = false;
            }
        }
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> NormalRouletteMounts = [];
        public HashSet<uint> PVPRouletteMounts    = [];
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
