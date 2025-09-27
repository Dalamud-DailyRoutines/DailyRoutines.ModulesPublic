using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public class BetterMountRoulette : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("BetterMountRouletteTitle"),
        Description = GetLoc("BetterMountRouletteDescription"),
        Category = ModuleCategories.General,
        Author = ["XSZYYS"]
    };
    
    private static Config? ModuleConfig;
    
    private static readonly HashSet<uint> MountRouletteActionIDs = [9, 24];
    private static LuminaSearcher<Mount>? MasterMountsSearcher;
    private static MountListHandler? NormalMounts;
    private static MountListHandler? PVPMounts;
    
    private const int PageSize = 100;
    
    private class MountListHandler
    {
        public LuminaSearcher<Mount> Searcher { get; }
        public HashSet<uint> SelectedIDs { get; }
        public string SearchText = string.Empty;
        public int DisplayCount = PageSize;
        
        public MountListHandler(LuminaSearcher<Mount> searcher, HashSet<uint> selectedIDs)
        {
            Searcher = searcher;
            SelectedIDs = selectedIDs;
        }
    }
    
    private class Config : ModuleConfiguration
    {
        public HashSet<uint> NormalRouletteMounts = [];
        public HashSet<uint> PVPRouletteMounts = [];
    }
    
    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        UseActionManager.RegPreUseAction(OnPreUseAction);
        DService.ClientState.Login += OnLogin;
        if (DService.ClientState.IsLoggedIn)
            OnLogin();
    }
    
    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        DService.ClientState.Login -= OnLogin;
        MasterMountsSearcher = null;
        NormalMounts = null;
        PVPMounts = null;
    }
    
    private unsafe void OnLogin()
    {
        var allMountsSheet = LuminaGetter.Get<Mount>();
        var playerState = PlayerState.Instance();
        if (allMountsSheet == null || playerState == null) return;
        
        var unlockedMounts = allMountsSheet
            .Where(mount => playerState->IsMountUnlocked(mount.RowId) &&
                            mount.Icon != 0 &&
                            !string.IsNullOrEmpty(mount.Singular.ExtractText()))
            .ToList();
        
        MasterMountsSearcher = new LuminaSearcher<Mount>(
            unlockedMounts,
            [
                x => x.Singular.ExtractText()
            ],
            x => x.Singular.ExtractText()
        );
        
        NormalMounts = new MountListHandler(MasterMountsSearcher, ModuleConfig.NormalRouletteMounts);
        PVPMounts = new MountListHandler(MasterMountsSearcher, ModuleConfig.PVPRouletteMounts);
    }
    
    protected override void ConfigUI()
    {
        if (NormalMounts == null || PVPMounts == null)
            return;
        
        if (ImGui.Button(GetLoc("Refresh")))
            OnLogin();
        
        ImGui.SameLine();
        ImGui.TextWrapped(GetLoc("BetterMountRoulette-HelpText"));
        ImGui.Separator();
        
        using var tabBar = ImRaii.TabBar("##MountTabs");
        if (!tabBar) return;
        DrawTab(GetLoc("BetterMountRoulette-NormalAreaTab"), GetLoc("BetterMountRoulette-NormalMountsHeader"), NormalMounts);

        DrawTab(GetLoc("BetterMountRoulette-PVPAreaTab"), GetLoc("BetterMountRoulette-PVPMountsHeader"), PVPMounts);
        
        DrawSelectedMountsPreviewTab();
    }
    
    private void DrawSelectedMountsPreviewTab()
    {
        using var tab = ImRaii.TabItem(GetLoc("BetterMountRoulette-SelectedPreviewTab"));
        if (!tab) return;
        
        DrawSelectedMountsList(GetLoc("BetterMountRoulette-NormalMountsHeader"), NormalMounts);
        ImGui.Separator();
        DrawSelectedMountsList(GetLoc("BetterMountRoulette-PVPMountsHeader"), PVPMounts);
    }
    
    private void DrawSelectedMountsList(string header, MountListHandler handler)
    {
        ImGui.Text(header);

        if (handler.SelectedIDs.Count > 0)
        {
            ImGui.SameLine();

            if (ImGui.SmallButton($"{GetLoc("ClearAll")}##{header}"))
            {
                handler.SelectedIDs.Clear();
                SaveConfig(ModuleConfig);
            }
        }
        
        var childSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 150 * GlobalFontScale);
        using var child = ImRaii.Child($"##SelectedMounts{header}", childSize, true);
        if (!child) return;

        if (handler.SelectedIDs.Count == 0)
        {
            ImGui.TextDisabled(GetLoc("BetterMountRoulette-NoMountsSelected"));
            return;
        }
        
        var mountsToDraw = new List<Mount>();
        foreach (var mount in handler.Searcher.Data)
        {
            if (handler.SelectedIDs.Contains(mount.RowId))
                mountsToDraw.Add(mount);
        }

        var itemWidthEstimate = 120 * GlobalFontScale;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = Math.Max(1, (int)Math.Floor(contentWidth / itemWidthEstimate));
        
        using var table = ImRaii.Table($"##SelectedMountsTable{header}", columnCount);
        if (!table) return;

        foreach (var mount in mountsToDraw)
        {
            ImGui.TableNextColumn();
            
            var iconSize = 3 * ImGui.GetTextLineHeightWithSpacing();

            if (ImGui.SmallButton($"{GetLoc("Remove")}##{mount.RowId}{header}"))
            {
                handler.SelectedIDs.Remove(mount.RowId);
                SaveConfig(ModuleConfig);
            }
            
            ImGui.SameLine();
            
            if (DService.Texture.TryGetFromGameIcon((uint)mount.Icon, out var icon))
                ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(iconSize));
                
            ImGui.SameLine();
            var mountName = mount.Singular.ExtractText();
            var textPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(textPos.Y + (iconSize - ImGui.CalcTextSize(mountName).Y) / 2f);
            ImGui.Text(mountName);
        }
    }
    
    private void DrawTab(string tabLabel, string header, MountListHandler handler)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;
        
        ImGui.Text(header);

        var searchTextBefore = handler.SearchText;
        ImGui.InputTextWithHint($"##Search{tabLabel}", GetLoc("Search"), ref handler.SearchText, 100);
        if (searchTextBefore != handler.SearchText)
            handler.DisplayCount = PageSize;

        List<Mount> searchResult;
        int totalCount;
        bool isSearching = !string.IsNullOrEmpty(handler.SearchText);

        if (isSearching)
        {
            handler.Searcher.Search(handler.SearchText);
            searchResult = handler.Searcher.SearchResult;
            totalCount = searchResult.Count;
        }
        else
        {
            searchResult = new List<Mount>(handler.Searcher.Data);
            totalCount = searchResult.Count;
        }
        if (!isSearching && totalCount > PageSize)
        {
            ImGui.TextDisabled(GetLoc("BetterMountRoulette-DisplayLimit", Math.Min(handler.DisplayCount, totalCount), totalCount));
            ImGui.SameLine();
            ImGuiOm.HelpMarker(GetLoc("BetterMountRoulette-DisplayLimitHelp"));
        }

        var childSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 300 * GlobalFontScale);
        using var child = ImRaii.Child($"##MountsGrid{tabLabel}", childSize, true);
        if (!child) return;
        
        var mountsToDraw = searchResult.Take(handler.DisplayCount).ToList();
        DrawMountsGrid(mountsToDraw, handler.SelectedIDs);

        if (!isSearching && handler.DisplayCount < totalCount)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.Button) & 0x80FFFFFF);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.ButtonHovered) & 0x80FFFFFF);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.ButtonActive) & 0x80FFFFFF);
            if (ImGui.Button(GetLoc("LoadMore"), new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                handler.DisplayCount += PageSize;
            ImGui.PopStyleColor(3);
            ImGuiOm.TooltipHover(GetLoc("BetterMountRoulette-LoadMoreTooltip", Math.Min(handler.DisplayCount + PageSize, totalCount), totalCount));
        }
    }
    
    private void DrawMountsGrid(List<Mount> mountsToDraw, HashSet<uint> selectedMounts)
    {
        if (mountsToDraw.Count == 0) return;
        var itemWidthEstimate = 120 * GlobalFontScale;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = Math.Max(1, (int)Math.Floor(contentWidth / itemWidthEstimate));

        using var table = ImRaii.Table("##MountsGridTable", columnCount, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;
        
        foreach (var mount in mountsToDraw)
        {
            ImGui.TableNextColumn();
            var iconSize = 3 * ImGui.GetTextLineHeightWithSpacing();
            var isSelected = selectedMounts.Contains(mount.RowId);
            if (ImGui.Checkbox($"##{mount.RowId}", ref isSelected))
            {
                if (isSelected)
                    selectedMounts.Add(mount.RowId);
                else
                    selectedMounts.Remove(mount.RowId);
                SaveConfig(ModuleConfig);
            }
            
            ImGui.SameLine();
            if (DService.Texture.TryGetFromGameIcon((uint)mount.Icon, out var icon))
                ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(iconSize));
            
            ImGui.SameLine();

            var mountName = mount.Singular.ExtractText();
            var textPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(textPos.Y + (iconSize - ImGui.CalcTextSize(mountName).Y) / 2f);
            ImGui.Text(mountName);
        }
    }
    
    private static unsafe void OnPreUseAction(ref bool isPrevented, ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam, ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        if (actionType != ActionType.GeneralAction || !MountRouletteActionIDs.Contains(actionID))
            return;
        
        var mountList = GameState.IsInPVPArea ? ModuleConfig.PVPRouletteMounts : ModuleConfig.NormalRouletteMounts;

        if (mountList.Count > 0)
        {
            var randomMountID = mountList.ElementAt(Random.Shared.Next(mountList.Count));
            UseActionManager.UseAction(ActionType.Mount, randomMountID);
            isPrevented = true;
        }
    }
}
