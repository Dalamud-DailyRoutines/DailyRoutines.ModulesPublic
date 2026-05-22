using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private string                searchWord   = string.Empty;
    private List<AetheryteRecord> searchResult = [];
    private List<AetheryteRecord> favorites    = [];

    private          int                   selectedIndex;
    private          bool                  shouldFocusSearchBar;
    private          bool                  hasUsedArrowKeys;
    private          Vector2               lastMousePos;
    private readonly List<OverlayListItem> visibleItems = [];

    protected override void OverlayUI()
    {
        if (!hasUsedArrowKeys)
            hoveredAetheryte = null;

        var isWindowAppearing = ImGui.IsWindowAppearing();

        if (isWindowAppearing)
        {
            searchWord           = string.Empty;
            selectedIndex        = 0;
            shouldFocusSearchBar = true;
            hasUsedArrowKeys     = false;
            hoveredAetheryte     = null;
            pinnedAetheryte      = null;
            lastMousePos         = ImGui.GetMousePos();
        }

        if (!ImGui.IsWindowFocused() && pinnedAetheryte == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        var keyState = DService.Instance().KeyState;

        if (keyState[VirtualKey.ESCAPE])
        {
            Overlay.IsOpen = false;
            if (SystemMenu != null)
                SystemMenu->Close(true);
        }

        visibleItems.Clear();
        var isSearchEmpty = string.IsNullOrWhiteSpace(searchWord);

        if (isSearchEmpty)
        {
            var specialSeen  = new HashSet<(uint, byte)>();
            var specialItems = new List<AetheryteRecord>();

            // 1. 返回点 (最多1个)
            var home = AllRecords.FirstOrDefault(x => x.State == AetheryteRecordState.Home);

            if (home != null)
            {
                if (specialSeen.Add((home.RowID, home.SubIndex)))
                    specialItems.Add(home);
            }

            // 2. 免费点 (最多2个，去重)
            var freePoints     = AllRecords.Where(x => x.State == AetheryteRecordState.Free || x.State == AetheryteRecordState.FreePS).ToList();
            var freeCountAdded = 0;

            foreach (var free in freePoints)
            {
                if (freeCountAdded >= 2) break;

                if (specialSeen.Add((free.RowID, free.SubIndex)))
                {
                    specialItems.Add(free);
                    freeCountAdded++;
                }
            }

            // 3. 收藏点 (最多3个，去重)
            var officialFavs  = AllRecords.Where(x => x.State == AetheryteRecordState.Favorite).ToList();
            var favCountAdded = 0;

            foreach (var fav in officialFavs)
            {
                if (favCountAdded >= 3) break;

                if (specialSeen.Add((fav.RowID, fav.SubIndex)))
                {
                    specialItems.Add(fav);
                    favCountAdded++;
                }
            }

            var specialCount = specialItems.Count;
            var recentLimit  = 8 - specialCount;

            var recentRecords = GetRecentRecords();
            var recentItems   = new List<AetheryteRecord>();
            var recentSeen    = new HashSet<(uint, byte)>();

            foreach (var r in recentRecords)
            {
                if (recentItems.Count >= recentLimit) break;
                if (!specialSeen.Contains((r.RowID, r.SubIndex)) && recentSeen.Add((r.RowID, r.SubIndex))) recentItems.Add(r);
            }

            foreach (var r in recentItems) visibleItems.Add(new OverlayListItem { Record = r, IsShowMore = false, DrawSeparatorBefore = false, Name = r.Name });

            for (var i = 0; i < specialItems.Count; i++)
            {
                var r       = specialItems[i];
                var drawSep = i == 0 && recentItems.Count > 0;
                visibleItems.Add(new OverlayListItem { Record = r, IsShowMore = false, DrawSeparatorBefore = drawSep, Name = r.Name });
            }

            visibleItems.Add
            (
                new OverlayListItem
                {
                    IsShowMore          = true,
                    DrawSeparatorBefore = false,
                    Name                = Lang.Get("BetterTeleport-ShowMore")
                }
            );
        }
        else
        {
            var matches = records.Values
                                 .SelectMany(x => x)
                                 .Concat(houseRecords)
                                 .Where
                                 (x => x.ToString().Contains(searchWord, StringComparison.OrdinalIgnoreCase) ||
                                       (config.Remarks.TryGetValue(GetConfigKey(x), out var remark) &&
                                        remark.Contains(searchWord, StringComparison.OrdinalIgnoreCase))
                                 )
                                 .ToList();

            var searchCount = Math.Min(8, matches.Count);

            for (var i = 0; i < searchCount; i++)
            {
                var r = matches[i];
                visibleItems.Add(new OverlayListItem { Record = r, IsShowMore = false, Name = r.Name });
            }

            visibleItems.Add(new OverlayListItem { IsShowMore = true, Name = Lang.Get("BetterTeleport-ShowMore") });
        }

        if (selectedIndex < 0)
            selectedIndex = 0;
        if (visibleItems.Count > 0 && selectedIndex >= visibleItems.Count)
            selectedIndex = visibleItems.Count - 1;

        ImGui.SetNextItemWidth(-1f);

        if (shouldFocusSearchBar)
        {
            ImGui.SetWindowFocus();
            ImGui.SetKeyboardFocusHere();
            AtkStage.Instance()->ClearFocus();

            shouldFocusSearchBar = false;
        }

        ImGui.InputTextWithHint("###BetterTeleportQuickSearch", Lang.Get("PleaseSearch"), ref searchWord, 128);

        var isSearchBarFocused = ImGui.IsItemFocused();

        ImGui.Spacing();

        for (var i = 0; i < visibleItems.Count; i++)
            DrawSearchItem(i, visibleItems[i], selectedIndex == i);

        var conflictKey = PluginConfig.Instance().ConflictKeyBinding.Keyboard;
        if ((GetAsyncKeyState((int)conflictKey) & 0x8000) != 0 && hoveredAetheryte != null)
            pinnedAetheryte = hoveredAetheryte;

        if (Overlay.IsOpen && visibleItems.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            {
                selectedIndex    = (selectedIndex + 1) % visibleItems.Count;
                hasUsedArrowKeys = true;
                if (visibleItems[selectedIndex].Record != null)
                    hoveredAetheryte = visibleItems[selectedIndex].Record;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            {
                selectedIndex    = (selectedIndex - 1 + visibleItems.Count) % visibleItems.Count;
                hasUsedArrowKeys = true;
                if (visibleItems[selectedIndex].Record != null)
                    hoveredAetheryte = visibleItems[selectedIndex].Record;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Enter))
            {
                if (hasUsedArrowKeys)
                    TriggerListItem(visibleItems[selectedIndex]);
                else if (isSearchBarFocused)
                    TriggerListItem(visibleItems[selectedIndex]);
                else
                    shouldFocusSearchBar = true;
            }

            var io = ImGui.GetIO();

            if (io.KeyCtrl)
            {
                for (var i = 0; i < 9; i++)
                {
                    var key       = (ImGuiKey)((int)ImGuiKey.Key1    + i);
                    var numpadKey = (ImGuiKey)((int)ImGuiKey.Keypad1 + i);

                    if (ImGui.IsKeyPressed(key) || ImGui.IsKeyPressed(numpadKey))
                    {
                        if (i == 8)
                        {
                            var showMoreItem = visibleItems.FirstOrDefault(x => x.IsShowMore);
                            if (showMoreItem.Name != null) TriggerListItem(showMoreItem);
                        }
                        else if (i < visibleItems.Count) TriggerListItem(visibleItems[i]);
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawBottomToolbar();

        DrawHoveredTooltip();
    }

    protected override void OverlayOnClose() =>
        config.Save(this);


    private void DrawSearchItem(int index, OverlayListItem item, bool isSelected)
    {
        if (item.DrawSeparatorBefore)
        {
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (item is { IsShowMore: false, Record: not null })
        {
            DrawAetheryteItem(item.Record, index, isSelected);
            return;
        }

        // 处理 ShowMore 项的绘制
        var startPos   = ImGui.GetCursorScreenPos();
        var width      = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var padding    = ImGui.GetStyle().ItemSpacing.X;
        var itemHeight = (lineHeight * 2.2f) + padding;

        ImGui.PushID($"item_{index}");

        if (ImGui.InvisibleButton("##ItemBtn", new Vector2(width, itemHeight)))
        {
            hasUsedArrowKeys = false;
            TriggerListItem(item);
        }

        var isHovered = ImGui.IsItemHovered();
        ImGui.PopID();

        if (isHovered)
        {
            var currentMousePos = ImGui.GetMousePos();

            if (!hasUsedArrowKeys) selectedIndex = index;
            else if (currentMousePos != lastMousePos)
            {
                hasUsedArrowKeys = false;
                selectedIndex    = index;
            }

            lastMousePos = currentMousePos;
        }

        var drawList = ImGui.GetWindowDrawList();

        if (isSelected || isHovered)
        {
            var bgCol = ImGui.GetColorU32(isSelected ? ImGuiCol.HeaderActive : ImGuiCol.HeaderHovered);
            drawList.AddRectFilled(startPos, startPos + new Vector2(width, itemHeight), bgCol, 4f);
        }

        var contentStartX = startPos.X + padding + 6f;
        var nameText      = item.Name;
        var titleY        = startPos.Y + ((itemHeight - ImGui.GetTextLineHeight()) / 2f);
        drawList.AddText(new Vector2(contentStartX, titleY), ImGui.GetColorU32(ImGuiCol.Text), nameText);

        var rightEndX = startPos.X + width - padding - 6f;
        var hotkeyLabel = "Ctrl+9";

        var badgeSize   = ImGui.CalcTextSize(hotkeyLabel);
        var badgeWidth  = badgeSize.X + 8f;
        var badgeHeight = badgeSize.Y + 4f;
        var badgePos    = new Vector2(rightEndX - badgeWidth, startPos.Y + ((itemHeight - badgeHeight) / 2));

        var badgeBgCol = ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(badgePos, badgePos + new Vector2(badgeWidth, badgeHeight), badgeBgCol, 4f);
        drawList.AddRect(badgePos, badgePos       + new Vector2(badgeWidth, badgeHeight), ImGui.GetColorU32(ImGuiCol.Border), 4f);

        drawList.AddText(badgePos + new Vector2(4f, 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), hotkeyLabel);

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, itemHeight + 2f));
    }

    private void TriggerListItem(OverlayListItem item)
    {
        if (item.IsShowMore)
        {
            if (fullWindow != null) fullWindow.IsOpen = true;
            Overlay.IsOpen = false;
        }
        else if (item.Record != null)
            HandleTeleport(item.Record);
    }

    private List<AetheryteRecord> GetRecentRecords()
    {
        var result = new List<AetheryteRecord>();

        var module = TeleportHistoryModule.Instance();

        foreach (var rec in module->History)
        {
            var found = AllRecords.FirstOrDefault(x => x.RowID == rec.AetheryteId && x.SubIndex == rec.SubIndex);
            if (found != null)
                result.Add(found);
        }

        return result;
    }

    private struct OverlayListItem
    {
        public AetheryteRecord? Record;
        public bool             IsShowMore;
        public bool             DrawSeparatorBefore;
        public string           Name;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
