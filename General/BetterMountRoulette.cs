using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Hooking;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Helpers;
using Dalamud.Game;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Infos;
using static DailyRoutines.Infos.Widgets;


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
    
    private static readonly CompSig UseActionSig = new("E8 ?? ?? ?? ?? B0 01 EB B6");
    private unsafe delegate byte UseActionDelegate(ActionManager* actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, bool* isGroundTarget);
    private static Hook<UseActionDelegate>? useActionHook;
    private static Config? moduleConfig;
    private static readonly uint[] MountRouletteActionIDs = [
        9,  
        24  
    ];
    private static List<Mount>? unlockedMounts;
    private static readonly Random random = new();
    private string normalSearchText = string.Empty;
    private string pvpSearchText = string.Empty;
    private List<Mount> sortedNormalMounts = new();
    private List<Mount> sortedPvpMounts = new();
    private bool normalListDirty = true;
    private bool pvpListDirty = true;
    private delegate void MarkDirtyAction();
    private void MarkNormalListAsDirty() => normalListDirty = true;
    private void MarkPvpListAsDirty() => pvpListDirty = true;
    private class Config : ModuleConfiguration
    {
        public HashSet<uint> NormalRouletteMounts = [];
        public HashSet<uint> PvpRouletteMounts = [];
    }

    protected override unsafe void Init()
    {
        moduleConfig = LoadConfig<Config>() ?? new Config();
        useActionHook ??= UseActionSig.GetHook<UseActionDelegate>(OnUseActionDetour);
        useActionHook.Enable();
        var playerState = PlayerState.Instance();
        if (playerState == null)
        {
            DService.Log.Error("无法获取 PlayerState，坐骑列表为空。");
            unlockedMounts = new List<Mount>();
            return;
        }
        
        var allMountsSheet = DService.Data.GetExcelSheet<Mount>();
        var tempMountList = new List<Mount>();
        foreach (var mount in allMountsSheet)
        {
            if (mount.Icon != 0 && !string.IsNullOrEmpty(mount.Singular.ToString()) && playerState->IsMountUnlocked(mount.RowId))
                tempMountList.Add(mount);
        }

        tempMountList.Sort((mount1, mount2) => mount1.RowId.CompareTo(mount2.RowId));
        unlockedMounts = tempMountList;
    }

    protected override void ConfigUI()
    {
        if (moduleConfig == null || unlockedMounts == null)
        {
            ImGui.Text(GetLoc("BetterMountRoulette-Loading"));
            return;
        }

        ImGui.TextWrapped(GetLoc("BetterMountRoulette-HelpText"));
        ImGui.Separator();
        
        using var tabBar = ImRaii.TabBar("##MountTabs");
        if (tabBar)
        {
            using (var tab = ImRaii.TabItem(GetLoc("BetterMountRoulette-NormalAreaTab")))
            {
                if (tab)
                {
                    ImGui.Text(GetLoc("BetterMountRoulette-NormalMountsHeader"));
                    if (ImGui.InputTextWithHint("##NormalSearch", GetLoc("Search"), ref normalSearchText, 100))
                        normalListDirty = true;
                    if (normalListDirty)
                    {
                        UpdateSortedMounts(unlockedMounts, moduleConfig.NormalRouletteMounts, normalSearchText,
                            ref sortedNormalMounts);
                        normalListDirty = false;
                    }

                    using var child = ImRaii.Child("##NormalMountsGrid", new Vector2(-1, 300 * GlobalFontScale), true);
                    if (child)
                        DrawMountsGrid(sortedNormalMounts, moduleConfig.NormalRouletteMounts, MarkNormalListAsDirty);
                }
            }
        }

        using (var tab = ImRaii.TabItem(GetLoc("BetterMountRoulette-PvpAreaTab")))
        {
            if (tab)
            {
                ImGui.Text(GetLoc("BetterMountRoulette-PvpMountsHeader"));
                if (ImGui.InputTextWithHint("##PvpSearch", GetLoc("Search"), ref pvpSearchText, 100))
                    pvpListDirty = true;
                
                if (pvpListDirty)
                {
                    UpdateSortedMounts(unlockedMounts, moduleConfig.PvpRouletteMounts, pvpSearchText, ref sortedPvpMounts);
                    pvpListDirty = false;
                }
                
                using var child = ImRaii.Child("##PvpMountsGrid", new Vector2(-1, 300 * GlobalFontScale), true);
                if (child)
                    DrawMountsGrid(sortedPvpMounts, moduleConfig.PvpRouletteMounts, MarkPvpListAsDirty);
            }
        }
    }

    private void UpdateSortedMounts(List<Mount> allMounts, HashSet<uint> selectedMounts, string searchText, ref List<Mount> sortedList)
    {
        List<Mount> filteredMounts;
        if (string.IsNullOrEmpty(searchText))
            filteredMounts = allMounts;
        else
        {
            filteredMounts = new List<Mount>();
            foreach (var mount in allMounts)
            {
                if (mount.Singular.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    filteredMounts.Add(mount);
            }
        }

        sortedList.Clear();
        var selectedFilteredMounts = new List<Mount>();
        var unselectedFilteredMounts = new List<Mount>();

        foreach (var mount in filteredMounts)
        {
            if (selectedMounts.Contains(mount.RowId))
                selectedFilteredMounts.Add(mount);
            else
                unselectedFilteredMounts.Add(mount);
        }

        sortedList.AddRange(selectedFilteredMounts);
        sortedList.AddRange(unselectedFilteredMounts);
    }
    private void DrawMountsGrid(List<Mount> mountsToDraw, HashSet<uint> selectedMounts, MarkDirtyAction markAsDirty)
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
                ImGui.PushID(mount.RowId.ToString());
                var iconSize = 35 * GlobalFontScale;
                var isSelected = selectedMounts.Contains(mount.RowId);
                if (ImGui.Checkbox(string.Empty, ref isSelected))
                {
                    if (isSelected)
                        selectedMounts.Add(mount.RowId);
                    else
                        selectedMounts.Remove(mount.RowId);
                    if (moduleConfig != null)
                        SaveConfig(moduleConfig);
                    markAsDirty();
                }
                ImGui.SameLine();
                IDalamudTextureWrap? icon = null;
                try
                {
                    icon = DService.Texture.GetFromGameIcon((uint)mount.Icon).GetWrapOrDefault();
                }
                catch
                {
                    // ignored
                }
                if (icon != null)
                    ImGui.Image(icon.Handle, new Vector2(iconSize));
                else
                    ImGui.Dummy(new Vector2(iconSize));

                ImGui.SameLine();

                var textPos = ImGui.GetCursorPos();
                ImGui.SetCursorPosY(textPos.Y + (iconSize - ImGui.CalcTextSize(mount.Singular.ToString()).Y) / 2f);
                ImGui.Text(mount.Singular.ToString());
                ImGui.SetCursorPosY(textPos.Y);

                ImGui.PopID();
            }
        }
    }

    private unsafe byte OnUseActionDetour(ActionManager* actionManager, uint actionType, uint actionID, long targetedActorID, uint param, uint useType, int pvp, bool* isGroundTarget)
    {
        var isRouletteAction = false;
        foreach (var id in MountRouletteActionIDs)
        {
            if (id == actionID)
            {
                isRouletteAction = true;
                break;
            }
        }

        if (isRouletteAction)
        {
            try
            {
                var isInPvp = DService.ClientState.IsPvP;
                if (moduleConfig != null)
                {
                    var mountList = isInPvp ? moduleConfig.PvpRouletteMounts : moduleConfig.NormalRouletteMounts;

                    if (mountList.Count > 0)
                    {
                        var mountsAsList = new List<uint>(mountList);
                        var randomMountID = mountsAsList[random.Next(mountsAsList.Count)];
                        actionManager->UseAction(ActionType.Mount, randomMountID);
                        return 0;
                    }
                    else
                    {
                        ChatHelper.SendMessage(GetLoc("BetterMountRoulette-ListEmptyError"));
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                DService.Log.Error(ex, "执行坐骑随机时出错");
            }
        }

        return useActionHook.Original(actionManager, actionType, actionID, targetedActorID, param, useType, pvp,
            isGroundTarget);
    }
}

