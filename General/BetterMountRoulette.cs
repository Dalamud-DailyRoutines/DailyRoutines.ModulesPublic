using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
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
    private static readonly Random random = Random.Shared;
    private static string normalSearchText = string.Empty;
    private static string pvpSearchText = string.Empty;
    private static LuminaSearcher<Mount>? MountsSearcher;
    
    private class Config : ModuleConfiguration
    {
        public HashSet<uint> NormalRouletteMounts = [];
        public HashSet<uint> PVPRouletteMounts = [];
    }
    
    protected override unsafe void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        UseActionManager.RegPreUseAction(OnPreUseAction);
        DService.ClientState.Login += OnLogin;
        if (DService.ClientState.IsLoggedIn)
            OnLogin();
    }
    
    protected override void Uninit()
    {
        UseActionManager.Unreg(OnPreUseAction);
        DService.ClientState.Login -= OnLogin;
        SaveConfig(ModuleConfig);
        MountsSearcher = null;
    }
    
    private unsafe void OnLogin()
    {
        var allMountsSheet = LuminaGetter.Get<Mount>();
        var playerState = PlayerState.Instance();
        if (allMountsSheet == null || playerState == null) return;
        var unlockedMounts = new List<Mount>();
        foreach (var mount in allMountsSheet)
        {
            if (IsMountValid(mount, playerState)) 
                unlockedMounts.Add(mount);
        }
        
        MountsSearcher = new LuminaSearcher<Mount>(
            unlockedMounts,
            [
                x => x.Singular.ExtractText()
            ],
            x => x.Singular.ExtractText(),
            resultLimit: unlockedMounts.Count
        );
    }
    
    private unsafe bool IsMountValid(Mount mount, PlayerState* playerState) =>
        playerState->IsMountUnlocked(mount.RowId) &&
        mount.Icon != 0 &&
        !string.IsNullOrEmpty(mount.Singular.ExtractText());
    
    protected override void ConfigUI()
    {
        if (ModuleConfig == null) return;
        
        if (MountsSearcher == null)
        {
            ImGui.Text(GetLoc("Settings-NeedLoginToAnyCharacter"));
            return;
        }
        
        if (ImGui.Button(GetLoc("Refresh")))
            OnLogin();
        
        ImGui.SameLine();
        ImGui.TextWrapped(GetLoc("BetterMountRoulette-HelpText"));
        ImGui.Separator();
        
        using var tabBar = ImRaii.TabBar("##MountTabs");
        if (tabBar)
        {
            DrawTab(GetLoc("BetterMountRoulette-NormalAreaTab"), GetLoc("BetterMountRoulette-NormalMountsHeader"), 
                    ref normalSearchText, ModuleConfig.NormalRouletteMounts);

            DrawTab(GetLoc("BetterMountRoulette-PVPAreaTab"), GetLoc("BetterMountRoulette-PVPMountsHeader"),
                    ref pvpSearchText, ModuleConfig.PVPRouletteMounts);
            
            DrawSelectedMountsPreviewTab();
        }
    }
    
    private void DrawSelectedMountsPreviewTab()
    {
        using var tab = ImRaii.TabItem(GetLoc("BetterMountRoulette-SelectedPreviewTab"));
        if (!tab) return;
        
        DrawSelectedMountsList(GetLoc("BetterMountRoulette-NormalMountsHeader"), ModuleConfig.NormalRouletteMounts);
        ImGui.Separator();
        DrawSelectedMountsList(GetLoc("BetterMountRoulette-PVPMountsHeader"), ModuleConfig.PVPRouletteMounts);
    }
    
        private void DrawSelectedMountsList(string header, HashSet<uint> selectedMounts)
    {
        ImGui.Text(header);

        if (selectedMounts.Count > 0)
        {
            ImGui.SameLine();

            if (ImGui.SmallButton($"{GetLoc("ClearAll")}##{header}"))
            {
                selectedMounts.Clear();
                SaveConfig(ModuleConfig);
            }
        }
        var childSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 150 * GlobalFontScale);
        using var child = ImRaii.Child($"##SelectedMounts{header}", childSize, true);
        if (!child) return;

        if (selectedMounts.Count == 0)
        {
            ImGui.TextDisabled(GetLoc("BetterMountRoulette-NoMountsSelected"));
            return;
        }
        
        var mountsToDraw = new List<Mount>();
        foreach (var mount in MountsSearcher.Data)
        {
            if (selectedMounts.Contains(mount.RowId))
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
            
            var iconSize = 35 * GlobalFontScale;

            if (ImGui.SmallButton($"{GetLoc("Remove")}##{mount.RowId}{header}"))
            {
                selectedMounts.Remove(mount.RowId);
                SaveConfig(ModuleConfig);
            }
            
            ImGui.SameLine();
            
            if (DService.Texture.TryGetFromGameIcon((uint)mount.Icon, out var icon))
                ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(iconSize));
            else
                ImGui.Dummy(new Vector2(iconSize));
                
            ImGui.SameLine();
            var mountName = mount.Singular.ExtractText();
            var textPos = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(textPos.Y + (iconSize - ImGui.CalcTextSize(mountName).Y) / 2f);
            ImGui.Text(mountName);
        }
    }
    
    private void DrawTab(string tabLabel, string header, ref string searchText, HashSet<uint> selectedMounts)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;
        
        ImGui.Text(header);
        ImGui.InputTextWithHint($"##Search{tabLabel}", GetLoc("Search"), ref searchText, 100);
        
        MountsSearcher.Search(searchText);

        var childSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 300 * GlobalFontScale);
        using var child = ImRaii.Child($"##MountsGrid{tabLabel}", childSize, true);
        if (!child) return;
        DrawMountsGrid(MountsSearcher.SearchResult, selectedMounts);
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
            var iconSize = 35 * GlobalFontScale;
            var isSelected = selectedMounts.Contains(mount.RowId);
            if (ImGui.Checkbox($"##{mount.RowId}", ref isSelected))
            {
                if (isSelected)
                    selectedMounts.Add(mount.RowId);
                else
                    selectedMounts.Remove(mount.RowId);
                if (ModuleConfig != null)
                    SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            if (DService.Texture.TryGetFromGameIcon((uint)mount.Icon, out var icon))
                ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(iconSize));
            else
                ImGui.Dummy(new Vector2(iconSize));

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

        if (ModuleConfig == null) return;

        var isInPVP = GameState.IsInPVPArea;
        var mountList = isInPVP ? ModuleConfig.PVPRouletteMounts : ModuleConfig.NormalRouletteMounts;

        if (mountList.Count > 0)
        {
            var randomMountID = mountList.ElementAt(random.Next(mountList.Count));
            UseActionManager.UseAction(ActionType.Mount, randomMountID);
            isPrevented = true;
        }
    }
}
