using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private BetterTeleportFullWindow? fullWindow;

    private readonly Dictionary<(uint RowID, byte SubIndex), float> hoverProgress = [];

    private bool isNeedToLoseFocusSearchBar;

    private string                fullSearchWord   = string.Empty;
    private List<AetheryteRecord> fullSearchResult = [];
    private string                activeTabName    = string.Empty;
    private string?               tabToSelect;
    private bool                  shouldScrollToSelected;
    private bool                  isJustOpened;

    public void DrawFullWindowUI()
    {
        var isWindowAppearing = ImGui.IsWindowAppearing() || isJustOpened;
        isJustOpened = false;

        var currentMousePos = ImGui.GetMousePos();
        if (hasUsedArrowKeys && currentMousePos != lastMousePos)
            hasUsedArrowKeys = false;
        lastMousePos = currentMousePos;

        hoveredAetheryte = null;

        var isSearchEmpty = string.IsNullOrWhiteSpace(fullSearchWord);

        var defaultTab = favorites.Count > 0 ? "Favorite" : records.Keys.FirstOrDefault() ?? "Setting";

        if (string.IsNullOrEmpty(activeTabName))
        {
            activeTabName        = defaultTab;
            tabToSelect          = activeTabName;
            shouldFocusSearchBar = true;
            selectedIndex        = 0;
            hasUsedArrowKeys     = false;
        }

        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
            pinnedAetheryte == null                                       &&
            !ImGui.IsPopupOpen("AetheryteContextPopup"))
        {
            fullWindow.IsOpen = false;
            return;
        }

        List<string> availableTabs = [];
        if (favorites.Count > 0) 
            availableTabs.Add("Favorite");
        
        var agentLobby = AgentLobby.Instance();
        if (agentLobby != null)
        {
            foreach (var name in records.Keys)
                availableTabs.Add(name);
        }

        availableTabs.Add("Setting");

        if (!availableTabs.Contains(activeTabName))
            activeTabName = availableTabs.FirstOrDefault() ?? "Setting";

        // 左右方向键切换 Tab
        if (isSearchEmpty && availableTabs.Count > 1)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            {
                var curIdx = availableTabs.IndexOf(activeTabName);

                if (curIdx >= 0)
                {
                    var nextIdx = (curIdx - 1 + availableTabs.Count) % availableTabs.Count;
                    activeTabName          = availableTabs[nextIdx];
                    tabToSelect            = activeTabName;
                    selectedIndex          = 0;
                    hasUsedArrowKeys       = false;
                    hoveredAetheryte       = null;
                    shouldScrollToSelected = true;
                }
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            {
                var curIdx = availableTabs.IndexOf(activeTabName);

                if (curIdx >= 0)
                {
                    var nextIdx = (curIdx + 1) % availableTabs.Count;
                    activeTabName          = availableTabs[nextIdx];
                    tabToSelect            = activeTabName;
                    selectedIndex          = 0;
                    hasUsedArrowKeys       = false;
                    hoveredAetheryte       = null;
                    shouldScrollToSelected = true;

                }
            }
        }

        // 构建当前可选的记录列表
        List<AetheryteRecord> currentSelectableRecords = [];

        if (!isSearchEmpty)
            currentSelectableRecords.AddRange(fullSearchResult);
        else if (activeTabName == "Favorite")
            currentSelectableRecords.AddRange(favorites);
        else if (records.TryGetValue(activeTabName, out var aetherytes))
        {
            var source = activeTabName == LuminaWrapper.GetAddonText(832) ? houseRecords.Concat(aetherytes) : aetherytes;

            foreach (var aetheryte in source.ToList())
            {
                if (!aetheryte.IsUnlocked() && aetheryte.Group != 255) continue;
                if (aetheryte.Group                   == 254  &&
                    agentLobby                        != null &&
                    agentLobby->LobbyData.HomeWorldId != agentLobby->LobbyData.CurrentWorldId)
                    continue;
                currentSelectableRecords.Add(aetheryte);
            }
        }

        var searchBarID = "###SearchFull";

        if (isNeedToLoseFocusSearchBar)
        {
            searchBarID                = "###SearchFull_LoseFocus";
            isNeedToLoseFocusSearchBar = false;
        }

        if (shouldFocusSearchBar)
        {
            ImGui.SetWindowFocus();
            ImGui.SetKeyboardFocusHere();
            AtkStage.Instance()->ClearFocus();

            shouldFocusSearchBar = false;

            if (isMoving)
            {
                ChatManager.Instance().SendCommand("/automove on");
                isMoving = false;
            }
        }

        ImGui.SetNextItemWidth(isSearchEmpty ? -1f : -ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);

        if (ImGui.InputTextWithHint(searchBarID, Lang.Get("PleaseSearch"), ref fullSearchWord, 128))
        {
            fullSearchResult = !string.IsNullOrWhiteSpace(fullSearchWord)
                                   ? records.Values
                                            .SelectMany(x => x)
                                            .Where
                                            (x => x.ToString()
                                                   .Contains(fullSearchWord, StringComparison.OrdinalIgnoreCase) ||
                                                  (config.Remarks.TryGetValue(GetConfigKey(x), out var remark) &&
                                                   remark.Contains(fullSearchWord, StringComparison.OrdinalIgnoreCase))
                                            )
                                            .ToList()
                                   : [];
            selectedIndex = 0;
        }

        if (!isSearchEmpty)
        {
            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Clear", FontAwesomeIcon.Times))
            {
                fullSearchWord   = string.Empty;
                fullSearchResult = [];
            }
        }

        var isSearchBarFocused = ImGui.IsItemFocused();

        // 上下方向键与回车交互
        if (currentSelectableRecords.Count > 0)
        {
            if (selectedIndex < 0)
                selectedIndex = 0;
            if (selectedIndex >= currentSelectableRecords.Count)
                selectedIndex = currentSelectableRecords.Count - 1;

            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            {
                selectedIndex          = (selectedIndex + 1) % currentSelectableRecords.Count;
                hasUsedArrowKeys       = true;
                hoveredAetheryte       = currentSelectableRecords[selectedIndex];
                shouldScrollToSelected = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            {
                selectedIndex          = (selectedIndex - 1 + currentSelectableRecords.Count) % currentSelectableRecords.Count;
                hasUsedArrowKeys       = true;
                hoveredAetheryte       = currentSelectableRecords[selectedIndex];
                shouldScrollToSelected = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Enter) && !isWindowAppearing)
            {
                if (hasUsedArrowKeys || isSearchBarFocused)
                    HandleTeleport(currentSelectableRecords[selectedIndex]);
            }
        }

        ImGui.Spacing();

        if (fullSearchResult.Count > 0 || !isSearchEmpty)
        {
            using var child = ImRaii.Child("###SearchResultChildFull", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false, ImGuiWindowFlags.NoBackground);

            if (child)
            {
                if (fullSearchResult.Count != 0)
                {
                    var list = fullSearchResult.ToList();

                    for (var i = 0; i < list.Count; i++)
                    {
                        DrawAetheryteItem(list[i], i, selectedIndex == i, false);

                        if (selectedIndex == i && shouldScrollToSelected)
                        {
                            ImGui.SetScrollHereY();
                            shouldScrollToSelected = false;
                        }
                    }
                }
            }
        }
        else
        {
            using var tabBar = ImRaii.TabBar("###AetherytesTabBarFull", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.NoTooltip);

            if (tabBar)
            {
                var isSettingOn = false;

                if (favorites.Count > 0)
                {
                    var tabFlags                            = ImGuiTabItemFlags.None;
                    if (tabToSelect == "Favorite") tabFlags |= ImGuiTabItemFlags.SetSelected;

                    using var tabItem = ImRaii.TabItem($"{Lang.Get("Favorite")}##TabItemFull", tabFlags);

                    if (tabItem)
                    {
                        activeTabName = "Favorite";

                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child("###FavoriteChildFull", childSize, false, ImGuiWindowFlags.NoBackground);

                        if (child)
                        {
                            var list = favorites.ToList();

                            for (var i = 0; i < list.Count; i++)
                            {
                                DrawAetheryteItem(list[i], i, selectedIndex == i, false);

                                if (selectedIndex == i && shouldScrollToSelected)
                                {
                                    ImGui.SetScrollHereY();
                                    shouldScrollToSelected = false;
                                }
                            }
                        }
                    }
                }

                if (agentLobby != null)
                {
                    foreach (var (name, aetherytes) in records.ToList())
                    {
                        var tabFlags                      = ImGuiTabItemFlags.None;
                        if (tabToSelect == name) tabFlags |= ImGuiTabItemFlags.SetSelected;

                        using var tabItem = ImRaii.TabItem($"{name}##TabItemFull", tabFlags);
                        if (!tabItem) continue;

                        activeTabName = name;

                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child($"###{name}ChildFull", childSize, false, ImGuiWindowFlags.NoBackground);
                        if (!child) continue;

                        var source      = name == LuminaWrapper.GetAddonText(832) ? houseRecords.Concat(aetherytes) : aetherytes;
                        var lastName    = string.Empty;
                        var lastGroupID = -1;

                        var visibleIndex = 0;

                        foreach (var aetheryte in source.ToList())
                        {
                            if (!aetheryte.IsUnlocked() && aetheryte.Group != 255) continue;
                            if (aetheryte.Group                   == 254 &&
                                agentLobby->LobbyData.HomeWorldId != agentLobby->LobbyData.CurrentWorldId)
                                continue;

                            var isNewGroup = false;

                            if (aetheryte.Group == 0)
                            {
                                if (lastName != aetheryte.RegionName)
                                {
                                    isNewGroup = true;
                                    lastName   = aetheryte.RegionName;
                                }
                            }
                            else
                            {
                                if (lastGroupID != aetheryte.Group)
                                {
                                    isNewGroup  = true;
                                    lastGroupID = aetheryte.Group;
                                }
                            }

                            if (isNewGroup)
                            {
                                ImGui.Spacing();

                                var headerName    = aetheryte.RegionName;
                                var headerBgColor = ImGui.GetColorU32(ImGuiCol.Header);
                                var cursor        = ImGui.GetCursorScreenPos();
                                var width         = ImGui.GetContentRegionAvail().X;
                                var height        = ImGui.GetTextLineHeightWithSpacing();

                                ImGui.GetWindowDrawList().AddRectFilled(cursor, cursor + new Vector2(width, height), headerBgColor, 4f);

                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetStyle().ItemSpacing.X * 2));
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextColored(ImGui.GetColorU32(ImGuiCol.Text).ToVector4(), headerName);

                                ImGui.Spacing();
                            }

                            DrawAetheryteItem(aetheryte, visibleIndex, selectedIndex == visibleIndex, false);

                            if (selectedIndex == visibleIndex && shouldScrollToSelected)
                            {
                                ImGui.SetScrollHereY();
                                shouldScrollToSelected = false;
                            }

                            visibleIndex++;
                        }
                    }
                }

                var settingTabFlags = ImGuiTabItemFlags.None;
                if (tabToSelect == "Setting")
                    settingTabFlags |= ImGuiTabItemFlags.SetSelected;

                using (var settingTab = ImRaii.TabItem(FontAwesomeIcon.Cog.ToIconString(), settingTabFlags))
                {
                    if (settingTab)
                    {
                        activeTabName = "Setting";
                        isSettingOn   = true;

                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(8522)}");

                        using (var combo = ImRaii.Combo("###TeleportUsageTypeComboFull", TicketUsageTypes[TicketUsageType]))
                        {
                            if (combo)
                            {
                                foreach (var kvp in TicketUsageTypes)
                                {
                                    if (ImGui.Selectable($"{kvp.Value}", kvp.Key == TicketUsageType))
                                        TicketUsageType = kvp.Key;
                                }
                            }
                        }

                        ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(8528)}");

                        var gilSetting = TicketUsageGilSetting;
                        if (ImGui.InputUInt("###GilInputFull", ref gilSetting))
                            TicketUsageGilSetting = gilSetting;

                        if (ImGui.Checkbox(Lang.Get("BetterTeleport-HideAethernetInParty"), ref config.HideAethernetInParty))
                            config.Save(this);

                        var defaultPage = (int)config.DefaultPage;
                        var options     = new[] { Lang.Get("BetterTeleport-PageSearch"), Lang.Get("BetterTeleport-PageFull") };

                        if (ImGui.Combo
                            (
                                $"{Lang.Get("BetterTeleport-DefaultPage")}###BetterTeleportDefaultPageComboFull",
                                ref defaultPage,
                                options,
                                options.Length
                            ))
                        {
                            config.DefaultPage = (PageType)defaultPage;
                            config.Save(this);
                        }
                    }
                    else
                        ImGuiOm.TooltipHover(LuminaWrapper.GetAddonText(8516));
                }

                if (!isSettingOn)
                    DrawBottomToolbar();
            }
        }

        tabToSelect = null;
        DrawHoveredTooltip();
    }

    private class BetterTeleportFullWindow
    (
        BetterTeleport module
    ) : Window($"{LuminaWrapper.GetAddonText(8513)}###BetterTeleportFullWindow")
    {
        public override void OnOpen()
        {
            module.fullSearchWord         = string.Empty;
            module.fullSearchResult       = [];
            module.selectedIndex          = 0;
            module.hasUsedArrowKeys       = true;
            module.hoveredAetheryte       = null;
            module.pinnedAetheryte        = null;
            module.activeTabName          = module.favorites.Count > 0 ? "Favorite" : module.records.Keys.FirstOrDefault() ?? "Setting";
            module.tabToSelect            = module.activeTabName;
            module.shouldFocusSearchBar   = true;
            module.shouldScrollToSelected = false;
            module.isJustOpened           = true;

            module.lastMousePos = ImGui.GetMousePos();
        }

        public override void Draw() =>
            module.DrawFullWindowUI();

        public override void OnClose() =>
            module.config.Save(module);
    }
}
