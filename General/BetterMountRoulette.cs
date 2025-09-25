using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Utility.Raii;
using static OmenTools.Helpers.HelpersOm;
using static OmenTools.Helpers.ThrottlerHelper;
using static OmenTools.Helpers.ContentsFinderHelper;
using static OmenTools.Infos.InfosOm;
using OmenTools.Infos;
using OmenTools.Helpers;
using OmenTools;
using OmenTools.Service;
using Dalamud.Bindings.ImGui;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Interface.Textures.TextureWraps;
using static DailyRoutines.Infos.Widgets;
using static DailyRoutines.Infos.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using Lumina.Excel.Sheets;
using TinyPinyin;

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
        public HashSet<uint> PvpRouletteMounts = [];
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
            x => x.Singular.ExtractText()
        );
    }
    private unsafe bool IsMountValid(Mount mount, PlayerState* playerState)
    {
        return playerState->IsMountUnlocked(mount.RowId) &&
               mount.Icon != 0 &&
               !string.IsNullOrEmpty(mount.Singular.ExtractText());
    }
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

            DrawTab(GetLoc("BetterMountRoulette-PvpAreaTab"), GetLoc("BetterMountRoulette-PvpMountsHeader"),
                    ref pvpSearchText, ModuleConfig.PvpRouletteMounts);
        }
    }
    private void DrawTab(string tabLabel, string header, ref string searchText, HashSet<uint> selectedMounts)
    {
        using var tab = ImRaii.TabItem(tabLabel);
        if (!tab) return;
        
        ImGui.Text(header);
        ImGui.InputTextWithHint($"##Search{tabLabel}", GetLoc("Search"), ref searchText, 100);
        
        List<Mount> mountsToDraw;
        if (MountsSearcher != null)
        {
            try
            {
                MountsSearcher.Search(searchText);
                mountsToDraw = SortMountsForDisplay(MountsSearcher.SearchResult, selectedMounts);
            }
            catch (Exception ex)
            {
                DService.Log.Error(ex, "Error during mount search.");
                mountsToDraw = [];
            }
        }
        else
            mountsToDraw = [];
        var childSize = new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing(), 300 * GlobalFontScale);
        using var child = ImRaii.Child($"##MountsGrid{tabLabel}", childSize, true);
        if (child)
            DrawMountsGrid(mountsToDraw, selectedMounts);
    }
    private List<Mount> SortMountsForDisplay(List<Mount> mounts, HashSet<uint> selectedMounts)
    {
        if (mounts.Count == 0) return [];

        var selectedFilteredMounts = new List<Mount>();
        var unselectedFilteredMounts = new List<Mount>();
        
        foreach (var mount in mounts)
        {
            if (selectedMounts.Contains(mount.RowId))
                selectedFilteredMounts.Add(mount);
            else
                unselectedFilteredMounts.Add(mount);
        }

        selectedFilteredMounts.Sort((m1, m2) => m1.RowId.CompareTo(m2.RowId));
        unselectedFilteredMounts.Sort((m1, m2) => m1.RowId.CompareTo(m2.RowId));
        
        var resultList = new List<Mount>();
        resultList.AddRange(selectedFilteredMounts);
        resultList.AddRange(unselectedFilteredMounts);
        return resultList;
    }
    private void DrawMountsGrid(List<Mount> mountsToDraw, HashSet<uint> selectedMounts)
    {
        if (mountsToDraw.Count == 0) return;
        var itemWidthEstimate = 120 * GlobalFontScale;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var columnCount = Math.Max(1, (int)Math.Floor(contentWidth / itemWidthEstimate));

        using var table = ImRaii.Table("##MountsGridTable", columnCount, ImGuiTableFlags.SizingStretchSame);
        if (table)
        {
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
    }
    
    private static unsafe void OnPreUseAction(ref bool isPrevented, ref ActionType actionType, ref uint actionID, ref ulong targetID, ref uint extraParam, ref ActionManager.UseActionMode queueState, ref uint comboRouteID)
    {
        if (actionType is not ActionType.GeneralAction || !MountRouletteActionIDs.Contains(actionID))
            return;

        if (ModuleConfig == null) return;
        
        try
        {
            var isInPvp = DService.ClientState.IsPvP;
            var mountList = isInPvp ? ModuleConfig.PvpRouletteMounts : ModuleConfig.NormalRouletteMounts;

            if (mountList.Count > 0)
            {
                var randomMountID = mountList.ElementAt(random.Next(mountList.Count));
                ActionManager.Instance()->UseAction(ActionType.Mount, randomMountID);
            }
            else
                NotificationInfo(GetLoc("BetterMountRoulette-ListEmptyError"));
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex, "执行随机坐骑时出错");
        }
        
        isPrevented = true;
    }
}
