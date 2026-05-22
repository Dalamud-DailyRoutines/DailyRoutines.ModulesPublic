using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private BetterTeleportFullWindow? fullWindow;

    private readonly Dictionary<(uint RowID, byte SubIndex), float> hoverProgress = [];

    private bool isNeedToLoseFocusSearchBar;

    private string                fullSearchWord   = string.Empty;
    private List<AetheryteRecord> fullSearchResult = [];

    public void DrawFullWindowUI()
    {
        hoveredAetheryte = null;

        switch (isRefreshing)
        {
            case false when !TaskHelper.IsBusy && records.Count == 0:
                OnZoneChanged(0);
                return;
            case true:
                return;
        }

        var isSearchEmpty = string.IsNullOrWhiteSpace(fullSearchWord);

        var searchBarID = "###SearchFull";

        if (isNeedToLoseFocusSearchBar)
        {
            searchBarID                = "###SearchFull_LoseFocus";
            isNeedToLoseFocusSearchBar = false;
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

        ImGui.Spacing();

        if (fullSearchResult.Count > 0 || !isSearchEmpty)
        {
            using var child = ImRaii.Child("###SearchResultChildFull", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false, ImGuiWindowFlags.NoBackground);

            if (child)
            {
                if (fullSearchResult.Count != 0)
                {
                    foreach (var aetheryte in fullSearchResult.ToList())
                        DrawAetheryteItem(aetheryte);
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
                    using var tabItem = ImRaii.TabItem($"{Lang.Get("Favorite")}##TabItemFull");

                    if (tabItem)
                    {
                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child("###FavoriteChildFull", childSize, false, ImGuiWindowFlags.NoBackground);

                        if (child)
                        {
                            foreach (var aetheryte in favorites.ToList())
                                DrawAetheryteItem(aetheryte);
                        }
                    }
                }

                var agentLobby = AgentLobby.Instance();

                if (agentLobby != null)
                {
                    foreach (var (name, aetherytes) in records.ToList())
                    {
                        using var tabItem = ImRaii.TabItem($"{name}##TabItemFull");
                        if (!tabItem) continue;

                        var       childSize = new Vector2(0, -ImGui.GetFrameHeightWithSpacing());
                        using var child     = ImRaii.Child($"###{name}ChildFull", childSize, false, ImGuiWindowFlags.NoBackground);
                        if (!child) continue;

                        var source      = name == LuminaWrapper.GetAddonText(832) ? houseRecords.Concat(aetherytes) : aetherytes;
                        var lastName    = string.Empty;
                        var lastGroupID = -1;

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

                            DrawAetheryteItem(aetheryte);
                        }
                    }
                }

                using (var settingTab = ImRaii.TabItem(FontAwesomeIcon.Cog.ToIconString()))
                {
                    if (settingTab)
                    {
                        isSettingOn = true;

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
                    }
                    else
                        ImGuiOm.TooltipHover(LuminaWrapper.GetAddonText(8516));
                }

                if (!isSettingOn)
                    DrawBottomToolbar();
            }
        }

        DrawHoveredTooltip();
    }
    
    private class BetterTeleportFullWindow
    (
        BetterTeleport module
    ) : Window($"{LuminaWrapper.GetAddonText(8513)}###BetterTeleportFullWindow")
    {
        public override void Draw() =>
            module.DrawFullWindowUI();

        public override void OnClose() =>
            module.config.Save(module);
    }
}
