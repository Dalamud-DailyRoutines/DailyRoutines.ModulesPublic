using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastContentsFinderRegister : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastContentsFinderRegisterTitle"),
        Description = GetLoc("FastContentsFinderRegisterDescription"),
        Category    = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["ContentFinderCommand"]
    };

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground;
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ContentsFinder", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        if (ContentsFinder != null) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void OverlayUI()
    {
        if (ContentsFinder == null)
        {
            Overlay.IsOpen = false;
            return;
        }
        
        if (!IsAddonAndNodesReady(ContentsFinder)) return;

        var isLoading = ContentsFinder->AtkValues[1].Bool;
        if (isLoading) return;

        if (Throttler.Throttle("UpdateContentFinderData", 100))
            ContentFinderDataManager.UpdateCacheData();

        var cachedData = ContentFinderDataManager.GetCachedData();
        if (cachedData == null || cachedData.Items.Count == 0) return;

        var lineHeight = ImGui.GetTextLineHeight() - ImGui.GetStyle().FramePadding.Y;

        HideLevelNodes();
        foreach (var item in cachedData.Items)
        {
            ImGui.SetNextWindowPos(item.Position);
            if (ImGui.Begin($"FastContentsFinderRouletteOverlay-{item.NodeId}",
                            ImGuiWindowFlags.NoDecoration    | ImGuiWindowFlags.AlwaysAutoResize   |
                            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove             |
                            ImGuiWindowFlags.NoDocking       | ImGuiWindowFlags.NoFocusOnAppearing |
                            ImGuiWindowFlags.NoNav           | ImGuiWindowFlags.NoBackground))
            {
                if (cachedData.InDutyQueue)
                {
                    if (DService.Texture.TryGetFromGameIcon(new(61502), out var explorerTexture))
                    {
                        if (ImGui.ImageButton(explorerTexture.GetWrapOrEmpty().ImGuiHandle, new(lineHeight)))
                            CancelDutyApply();
                        ImGuiOm.TooltipHover($"{GetLoc("Cancel")}");
                    }
                }
                else
                {
                    var sharedPrefix = $"{item.Level} {item.Name}";

                    using (ImRaii.Group())
                    {
                        using (ImRaii.Disabled(item.IsLocked))
                        {
                            if (DService.Texture.TryGetFromGameIcon(new(60081), out var joinTexture))
                            {
                                if (ImGui.ImageButton(joinTexture.GetWrapOrEmpty().ImGuiHandle, new(lineHeight)))
                                {
                                    ChatHelper.SendMessage($"/pdrduty {(cachedData.CurrentTab == 0 ? "r" : "n")} {item.CleanName}");
                                    ChatHelper.SendMessage($"/pdrduty {(cachedData.CurrentTab != 0 ? "r" : "n")} {item.CleanName}");
                                }                                
                                ImGuiOm.TooltipHover($"{sharedPrefix}");
                            }
                            
                            if (cachedData.CurrentTab != 0)
                            {
                                if (IsConflictKeyPressed())
                                {
                                    if (DService.Texture.TryGetFromGameIcon(new(60648), out var explorerTexture))
                                    {
                                        ImGui.SameLine();
                                        if (ImGui.ImageButton(explorerTexture.GetWrapOrEmpty().ImGuiHandle, new(lineHeight)))
                                            ChatHelper.SendMessage($"/pdrduty n {item.CleanName} explorer");
                                        ImGuiOm.TooltipHover($"{sharedPrefix} ({LuminaGetter.GetRow<Addon>(13038)!.Value.Text.ExtractText()})");
                                    }
                                }
                                else
                                {
                                    if (DService.Texture.TryGetFromGameIcon(new(60641), out var unrestTexture))
                                    {
                                        ImGui.SameLine();
                                        if (ImGui.ImageButton(unrestTexture.GetWrapOrEmpty().ImGuiHandle, new(lineHeight)))
                                            ChatHelper.SendMessage($"/pdrduty n {item.CleanName} unrest");
                                        ImGuiOm.TooltipHover($"{sharedPrefix} ({LuminaGetter.GetRow<Addon>(10008)!.Value.Text.ExtractText()})\n" +
                                                             $"[{GetLoc("FastContentsFinderRegister-HoldConflictKeyToToggle")}]");
                                    }
                                }
                            }
                        }
                    }
                }
                ImGui.End();
            }
        }
    }

    private static void HideLevelNodes()
    {
        if (ContentsFinder == null) return;

        try
        {
            var listComponent = (AtkComponentNode*)ContentsFinder->GetNodeById(52);
            if (listComponent == null) return;

            var treelistComponent = (AtkComponentTreeList*)listComponent->Component;
            if (treelistComponent == null) return;

            var listLength = treelistComponent->ListLength;
            if (listLength == 0) return;

            for (var i = 0; i < Math.Min(listLength, 45); i++)
            {
                var offset = 3 + i;
                if (offset >= listComponent->Component->UldManager.NodeListCount) break;

                var listItemComponent = (AtkComponentNode*)listComponent->Component->UldManager.NodeList[offset];
                if (listItemComponent == null) continue;

                var levelNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(18);
                if (levelNode == null) continue;

                if (levelNode->IsVisible())
                    levelNode->ToggleVisibility(false);
            }
        }
        catch
        {
            // ignored
        }
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        
        ContentFinderDataManager.ClearCache();
        
        base.Uninit();
    }

    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

        switch (type)
        {
            case AddonEvent.PostSetup:
                ContentFinderDataManager.UpdateCacheData();
                break;
            case AddonEvent.PreFinalize:
                ContentFinderDataManager.ClearCache();
                break;
        }
    }

    // 数据结构定义
    public class ContentFinderItemData
    {
        public uint    NodeId    { get; set; }
        public string  Name      { get; set; } = string.Empty;
        public string  Level     { get; set; } = string.Empty;
        public Vector2 Position  { get; set; }
        public bool    IsLocked  { get; set; }
        public bool    IsVisible { get; set; }
        public string  CleanName { get; set; } = string.Empty;
    }

    public class ContentFinderCacheData
    {
        public uint                        CurrentTab     { get; set; }
        public List<ContentFinderItemData> Items          { get; set; } = [];
        public bool                        InDutyQueue    { get; set; }
        public DateTime                    LastUpdateTime { get; set; } = DateTime.MinValue;
    }

    // 数据管理器
    private static class ContentFinderDataManager
    {
        private static          ContentFinderCacheData? cachedData;
        private static readonly Lock                    lockObject = new();

        public static ContentFinderCacheData? GetCachedData()
        {
            lock (lockObject)
            {
                if (cachedData != null && DateTime.Now - cachedData.LastUpdateTime > TimeSpan.FromSeconds(5))
                    cachedData = null;
                
                return cachedData;
            }
        }

        public static void UpdateCacheData()
        {
            if (ContentsFinder == null) return;
            if (ContentsFinder->AtkValues == null || ContentsFinder->AtkValues[1].Bool|| ContentsFinder->AtkValues[26].UInt > 10)
                return;

            try
            {
                var newData = new ContentFinderCacheData
                {
                    CurrentTab     = ContentsFinder->AtkValues[26].UInt,
                    InDutyQueue    = DService.Condition[ConditionFlag.InDutyQueue],
                    LastUpdateTime = DateTime.Now
                };

                var listComponent = (AtkComponentNode*)ContentsFinder->GetNodeById(52);
                if (listComponent == null) return;

                var treelistComponent = (AtkComponentTreeList*)listComponent->Component;
                if (treelistComponent == null) return;

                var otherPFNode = (AtkTextNode*)ContentsFinder->GetNodeById(57);
                if (otherPFNode == null) return;

                var listLength = treelistComponent->ListLength;
                if (listLength == 0) return;

                var items = new List<ContentFinderItemData>();

                for (var i = 0; i < Math.Min(listLength, 16); i++)
                {
                    var offset = 3 + i;
                    if (offset >= listComponent->Component->UldManager.NodeListCount) break;

                    var listItemComponent = (AtkComponentNode*)listComponent->Component->UldManager.NodeList[offset];
                    if (listItemComponent == null ||
                        listItemComponent->Y >= 300 ||
                        listItemComponent->ScreenY < listComponent->ScreenY ||
                        listItemComponent->ScreenY + 20 > otherPFNode->ScreenY) continue;

                    var nameNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(5);
                    if (nameNode == null) continue;

                    var name = string.Empty;
                    try { name = nameNode->NodeText.ToString(); }
                    catch { name = string.Empty; }
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var lockNode = (AtkImageNode*)listItemComponent->Component->UldManager.SearchNodeById(3);
                    if (lockNode == null) continue;

                    var levelNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(18);
                    if (levelNode == null) continue;

                    var level = string.Empty;
                    try { level = levelNode->NodeText.ToString(); }
                    catch { level = string.Empty; }
                    if (string.IsNullOrWhiteSpace(level)) continue;

                    var itemData = new ContentFinderItemData
                    {
                        NodeId    = listItemComponent->NodeId,
                        Name      = name,
                        Level     = level,
                        Position  = new Vector2(levelNode->ScreenX + (newData.CurrentTab == 0 ? 24f : 0f), levelNode->ScreenY - 8f),
                        IsLocked  = lockNode->IsVisible(),
                        IsVisible = levelNode->IsVisible(),
                        CleanName = name.Replace(" ", string.Empty)
                    };

                    items.Add(itemData);
                }

                newData.Items = items;

                lock (lockObject)
                    cachedData = newData;
            }
            catch
            {
                // ignored
            }
        }

        public static void ClearCache()
        {
            lock (lockObject)
                cachedData = null;
        }
    }
}
