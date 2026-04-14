using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastContentsFinderRegister : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("FastContentsFinderRegisterTitle"),
        Description         = Lang.Get("FastContentsFinderRegisterDescription"),
        Category            = ModuleCategory.Recruitment,
        ModulesPrerequisite = ["ContentFinderCommand"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "ContentsFinder", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnAddon);
        if (ContentsFinder != null)
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        ContentFinderDataManager.ClearCache();
    }

    protected override void OverlayUI()
    {
        if (ContentsFinder == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (!ContentsFinder->IsAddonAndNodesReady()) return;

        var isLoading = ContentsFinder->AtkValues[1].Bool;
        if (isLoading) return;

        if (Throttler.Shared.Throttle("UpdateContentFinderData", 100))
            ContentFinderDataManager.UpdateCacheData();

        var cachedData = ContentFinderDataManager.GetCachedData();
        if (cachedData == null || cachedData.Items.Count == 0) return;

        HideLevelNodes();

        foreach (var item in cachedData.Items)
        {
            ImGui.SetNextWindowPos(item.Position);

            if (ImGui.Begin($"FastContentsFinderRouletteOverlay-{item.NodeID}", WINDOW_FLAGS))
            {
                if (cachedData.InDutyQueue)
                {
                    if (DService.Instance().Texture.TryGetFromGameIcon(new(61502), out var explorerTexture))
                    {
                        if (ImGui.ImageButton(explorerTexture.GetWrapOrEmpty().Handle, new(item.Height)))
                            ContentsFinderHelper.CancelDutyApply();
                        ImGuiOm.TooltipHover($"{Lang.Get("Cancel")}");
                    }
                }
                else
                {
                    var sharedPrefix = $"{item.Level} {item.Name}";

                    using (ImRaii.Group())
                    {
                        using (ImRaii.Disabled(item.IsLocked))
                        {
                            if (DService.Instance().Texture.TryGetFromGameIcon(new(60081), out var joinTexture))
                            {
                                if (ImGui.ImageButton(joinTexture.GetWrapOrEmpty().Handle, new(item.Height)))
                                {
                                    ChatManager.Instance().SendMessage($"/pdrduty {(cachedData.CurrentTab == 0 ? "r" : "n")} {item.CleanName}");
                                    ChatManager.Instance().SendMessage($"/pdrduty {(cachedData.CurrentTab != 0 ? "r" : "n")} {item.CleanName}");
                                }

                                ImGuiOm.TooltipHover($"{sharedPrefix}");
                            }

                            if (cachedData.CurrentTab != 0)
                            {
                                if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                                {
                                    if (DService.Instance().Texture.TryGetFromGameIcon(new(60648), out var explorerTexture))
                                    {
                                        ImGui.SameLine();
                                        if (ImGui.ImageButton(explorerTexture.GetWrapOrEmpty().Handle, new(item.Height)))
                                            ChatManager.Instance().SendMessage($"/pdrduty n {item.CleanName} explorer");
                                        ImGuiOm.TooltipHover($"{sharedPrefix} ({LuminaWrapper.GetAddonText(13038)})");
                                    }
                                }
                                else
                                {
                                    if (DService.Instance().Texture.TryGetFromGameIcon(new(60641), out var unrestTexture))
                                    {
                                        ImGui.SameLine();
                                        if (ImGui.ImageButton(unrestTexture.GetWrapOrEmpty().Handle, new(item.Height)))
                                            ChatManager.Instance().SendMessage($"/pdrduty n {item.CleanName} unrest");
                                        ImGuiOm.TooltipHover
                                        (
                                            $"{sharedPrefix} ({LuminaWrapper.GetAddonText(10008)})\n" +
                                            $"[{Lang.Get("FastContentsFinderRegister-HoldConflictKeyToToggle")}]"
                                        );
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
        public uint    NodeID    { get; init; }
        public string  Name      { get; init; } = string.Empty;
        public string  Level     { get; init; } = string.Empty;
        public Vector2 Position  { get; init; }
        public float   Height    { get; init; }
        public bool    IsLocked  { get; init; }
        public bool    IsVisible { get; set; }
        public string  CleanName { get; init; } = string.Empty;
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
        private static ContentFinderCacheData? cachedData;

        public static ContentFinderCacheData? GetCachedData()
        {
            if (cachedData != null && StandardTimeManager.Instance().Now - cachedData.LastUpdateTime > TimeSpan.FromSeconds(5))
                cachedData = null;

            return cachedData;
        }

        public static void UpdateCacheData()
        {
            if (ContentsFinder == null) return;
            if (ContentsFinder->AtkValues == null || ContentsFinder->AtkValues[1].Bool || ContentsFinder->AtkValues[26].UInt > 10)
                return;

            try
            {
                var newData = new ContentFinderCacheData
                {
                    CurrentTab     = ContentsFinder->AtkValues[26].UInt,
                    InDutyQueue    = DService.Instance().Condition[ConditionFlag.InDutyQueue],
                    LastUpdateTime = StandardTimeManager.Instance().Now
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
                    if (listItemComponent               == null                  ||
                        listItemComponent->Y            >= 300                   ||
                        listItemComponent->ScreenY      < listComponent->ScreenY ||
                        listItemComponent->ScreenY + 20 > otherPFNode->ScreenY) continue;

                    var nameNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(5);
                    if (nameNode == null) continue;

                    var name = nameNode->NodeText.StringPtr.HasValue ? nameNode->NodeText.ToString() : string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var lockNode = (AtkImageNode*)listItemComponent->Component->UldManager.SearchNodeById(3);
                    if (lockNode == null) continue;

                    var levelNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(18);
                    if (levelNode == null) continue;

                    var level = levelNode->NodeText.StringPtr.HasValue ? levelNode->NodeText.ToString() : string.Empty;
                    if (string.IsNullOrWhiteSpace(level)) continue;

                    var nodeStateLevel = levelNode->AtkResNode.GetNodeState();
                    var itemData = new ContentFinderItemData
                    {
                        NodeID    = listItemComponent->NodeId,
                        Name      = name,
                        Level     = level,
                        Position  = nodeStateLevel.TopLeft - new Vector2(0, 9f),
                        Height    = nodeStateLevel.Height * 0.75f,
                        IsLocked  = lockNode->IsVisible(),
                        IsVisible = levelNode->IsVisible(),
                        CleanName = name.Replace(" ", string.Empty)
                    };

                    items.Add(itemData);
                }

                newData.Items = items;

                cachedData = newData;
            }
            catch
            {
                // ignored
            }
        }

        public static void ClearCache() =>
            cachedData = null;
    }
    
    #region 常量

    private const ImGuiWindowFlags WINDOW_FLAGS =
        ImGuiWindowFlags.NoDecoration       |
        ImGuiWindowFlags.AlwaysAutoResize   |
        ImGuiWindowFlags.NoSavedSettings    |
        ImGuiWindowFlags.NoMove             |
        ImGuiWindowFlags.NoDocking          |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav              |
        ImGuiWindowFlags.NoBackground;

    #endregion
}
