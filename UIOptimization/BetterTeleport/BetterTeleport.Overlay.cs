using System.Numerics;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport
{
    private string                searchWord   = string.Empty;
    private List<AetheryteRecord> searchResult = [];
    private List<AetheryteRecord> favorites    = [];
    private bool                  isNeedToLoseFocusSearchBar;

    private          int                   selectedIndex;
    private          bool                  isJustOpened = true;
    private          bool                  lastOverlayIsOpen;
    private readonly List<OverlayListItem> visibleItems = [];

    protected override void OverlayUI()
    {
        hoveredAetheryte = null;

        if (Overlay.IsOpen && !lastOverlayIsOpen)
        {
            isJustOpened  = true;
            searchWord    = string.Empty;
            selectedIndex = 0;
        }

        lastOverlayIsOpen = Overlay.IsOpen;

        if (DService.Instance().KeyState[VirtualKey.ESCAPE])
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

            // 1. 家点 (最多1个)
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

            visibleItems.Add(new OverlayListItem { IsShowMore = true, DrawSeparatorBefore = false, Name = Lang.Get("BetterTeleport-ShowMore") });
        }
        else
        {
            var matches = records.Values
                                 .SelectMany(x => x)
                                 .Concat(houseRecords)
                                 .Where
                                 (x => x.ToString().Contains(searchWord, StringComparison.OrdinalIgnoreCase) ||
                                       (config.Remarks.TryGetValue(x.RowID, out var remark) &&
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

        if (selectedIndex < 0) selectedIndex                                             = 0;
        if (visibleItems.Count > 0 && selectedIndex >= visibleItems.Count) selectedIndex = visibleItems.Count - 1;

        ImGui.SetNextItemWidth(-1f);

        if (isJustOpened)
        {
            ImGui.SetKeyboardFocusHere();
            isJustOpened = false;
        }

        ImGui.InputTextWithHint("###BetterTeleportQuickSearch", Lang.Get("PleaseSearch"), ref searchWord, 128);

        ImGui.Spacing();

        for (var i = 0; i < visibleItems.Count; i++) DrawSearchItem(i, visibleItems[i], selectedIndex == i);

        if (Overlay.IsOpen && visibleItems.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) selectedIndex = (selectedIndex + 1) % visibleItems.Count;

            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) selectedIndex = (selectedIndex - 1 + visibleItems.Count) % visibleItems.Count;

            if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) TriggerListItem(visibleItems[selectedIndex]);

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
        DrawBottomStatusBar();

        DrawHoveredTooltip();
    }

    protected override void OverlayOnClose() =>
        config.Save(this);

    private static void DrawBottomStatusBar()
    {
        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var height   = 24f * GlobalUIScale;

        long gil        = 0;
        var  tickets    = 0;
        var  invManager = InventoryManager.Instance();

        if (invManager != null)
        {
            gil     = invManager->GetGil();
            tickets = invManager->GetInventoryItemCount(21069);
        }

        var curX = startPos.X + (2f * GlobalUIScale);
        var curY = startPos.Y + (2f * GlobalUIScale);

        var pillBgCol     = ImGui.GetColorU32(ImGuiCol.FrameBg);
        var pillBorderCol = ImGui.GetColorU32(ImGuiCol.Border);
        var paddingX      = 8f  * GlobalUIScale;
        var pillHeight    = 20f * GlobalUIScale;

        // 1. 绘制金币 Pill
        var   gilText      = gil.ToChineseString();
        var   gilUnit      = LuminaWrapper.GetItemName(GIL_ITEM_ID);
        var   gilTextWidth = ImGui.CalcTextSize(gilText).X;
        float gilUnitWidth;
        using (FontManager.Instance().UIFont60.Push())
            gilUnitWidth = ImGui.CalcTextSize(gilUnit).X;

        var gilIcon      = DService.Instance().Texture.GetFromGameIcon(LuminaWrapper.GetItemIconID(GIL_ITEM_ID)).GetWrapOrEmpty();
        var gilPillWidth = (paddingX * 2) + (20f * GlobalUIScale) + gilTextWidth + (4f * GlobalUIScale) + gilUnitWidth;

        var gilPillPos = new Vector2(curX, curY);
        drawList.AddRectFilled(gilPillPos, gilPillPos + new Vector2(gilPillWidth, pillHeight), pillBgCol, 4f     * GlobalUIScale);
        drawList.AddRect(gilPillPos, gilPillPos       + new Vector2(gilPillWidth, pillHeight), pillBorderCol, 4f * GlobalUIScale);

        var gilDrawX    = curX + paddingX;
        var iconSize    = 14f                                      * GlobalUIScale;
        var iconOffsetY = (pillHeight - iconSize)                  / 2f;
        var textOffsetY = (pillHeight - ImGui.GetTextLineHeight()) / 2f;

        drawList.AddImage(gilIcon.Handle, new(gilDrawX, curY + iconOffsetY), new(gilDrawX + iconSize, curY + iconOffsetY + iconSize));
        gilDrawX += iconSize + (4f * GlobalUIScale);

        drawList.AddText(new Vector2(gilDrawX, curY + textOffsetY), ImGui.GetColorU32(ImGuiCol.Text), gilText);
        gilDrawX += gilTextWidth + (4f * GlobalUIScale);

        using (FontManager.Instance().UIFont60.Push())
        {
            var unitTextHeight = ImGui.GetTextLineHeight();
            drawList.AddText
                (new Vector2(gilDrawX, curY + ((pillHeight - unitTextHeight) / 2f) + (1f * GlobalUIScale)), ImGui.GetColorU32(ImGuiCol.TextDisabled), gilUnit);
        }

        curX += gilPillWidth + (8f * GlobalUIScale);

        // 2. 绘制使用券 Pill
        var   ticketText      = tickets.ToChineseString();
        var   ticketUnit      = LuminaWrapper.GetItemName(TELEPORT_TICKET_ITEM_ID);
        var   ticketTextWidth = ImGui.CalcTextSize(ticketText).X;
        float ticketUnitWidth;
        using (FontManager.Instance().UIFont60.Push()) ticketUnitWidth = ImGui.CalcTextSize(ticketUnit).X;

        var ticketIcon      = DService.Instance().Texture.GetFromGameIcon(LuminaWrapper.GetItemIconID(TELEPORT_TICKET_ITEM_ID)).GetWrapOrEmpty();
        var ticketPillWidth = (paddingX * 2) + (20f * GlobalUIScale) + ticketTextWidth + (4f * GlobalUIScale) + ticketUnitWidth;

        var ticketPillPos = new Vector2(curX, curY);
        drawList.AddRectFilled(ticketPillPos, ticketPillPos + new Vector2(ticketPillWidth, pillHeight), pillBgCol, 4f     * GlobalUIScale);
        drawList.AddRect(ticketPillPos, ticketPillPos       + new Vector2(ticketPillWidth, pillHeight), pillBorderCol, 4f * GlobalUIScale);

        var ticketDrawX = curX + paddingX;
        drawList.AddImage(ticketIcon.Handle, new Vector2(ticketDrawX, curY + iconOffsetY), new Vector2(ticketDrawX + iconSize, curY + iconOffsetY + iconSize));
        ticketDrawX += iconSize + (4f * GlobalUIScale);

        drawList.AddText(new Vector2(ticketDrawX, curY + textOffsetY), ImGui.GetColorU32(ImGuiCol.Text), ticketText);
        ticketDrawX += ticketTextWidth + (4f * GlobalUIScale);

        using (FontManager.Instance().UIFont60.Push())
        {
            var unitTextHeight = ImGui.GetTextLineHeight();
            drawList.AddText
                (new Vector2(ticketDrawX, curY + ((pillHeight - unitTextHeight) / 2f) + (1f * GlobalUIScale)), ImGui.GetColorU32(ImGuiCol.TextDisabled), ticketUnit);
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, height + (4f * GlobalUIScale)));
    }

    private void DrawSearchItem(int index, OverlayListItem item, bool isSelected)
    {
        if (item.DrawSeparatorBefore)
        {
            ImGui.Separator();
            ImGui.Spacing();
        }

        var startPos   = ImGui.GetCursorScreenPos();
        var width      = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();
        var padding    = ImGui.GetStyle().ItemSpacing.X;
        var itemHeight = (lineHeight * 2.2f) + padding;

        ImGui.PushID($"item_{index}");
        if (ImGui.InvisibleButton("##ItemBtn", new Vector2(width, itemHeight))) TriggerListItem(item);
        var isHovered = ImGui.IsItemHovered();
        ImGui.PopID();

        if (isHovered)
        {
            selectedIndex = index;
            if (item.Record != null) hoveredAetheryte = item.Record;
        }

        var drawList = ImGui.GetWindowDrawList();

        if (isSelected || isHovered)
        {
            var bgCol = ImGui.GetColorU32(isSelected ? ImGuiCol.HeaderActive : ImGuiCol.HeaderHovered);
            drawList.AddRectFilled(startPos, startPos + new Vector2(width, itemHeight), bgCol, 4f);
        }

        var contentStartX = startPos.X + padding + 6f;

        if (item is { IsShowMore: false, Record: not null })
        {
            var iconSize = 24f * GlobalUIScale;
            var iconY    = startPos.Y + ((itemHeight - iconSize) / 2);

            var iconID = (uint)(LuminaGetter.GetRow<GeneralAction>(7)?.Icon ?? 60752);
            if (item.Record.Group == 255)
                iconID = 60752;
            else if (item.Record.IsAetheryte)
                iconID = 60453;
            else
                iconID = 60430;

            var texWrap = DService.Instance().Texture.GetFromGameIcon(new(iconID)).GetWrapOrEmpty();
            if (texWrap.Handle != nint.Zero)
                drawList.AddImage(texWrap.Handle, new Vector2(contentStartX, iconY), new Vector2(contentStartX + iconSize, iconY + iconSize));

            contentStartX += iconSize + padding + 4f;
        }

        var nameText = item.Name;
        var titleY = item.IsShowMore
                         ? startPos.Y + ((itemHeight - ImGui.GetTextLineHeight()) / 2f)
                         : startPos.Y + 4f;
        drawList.AddText(new Vector2(contentStartX, titleY), ImGui.GetColorU32(ImGuiCol.Text), nameText);

        SeString? iconStr = null;

        if (item is { IsShowMore: false, Record: not null })
        {
            switch (item.Record.State)
            {
                case AetheryteRecordState.Home:
                    iconStr = HomeChar;
                    break;
                case AetheryteRecordState.Free:
                case AetheryteRecordState.FreePS:
                    iconStr = FreeChar;
                    break;
                case AetheryteRecordState.Favorite:
                    iconStr = FavoriteChar;
                    break;
            }
        }

        if (iconStr != null)
        {
            var nameWidth    = ImGui.CalcTextSize(nameText).X;
            var extraPadding = 8f * GlobalUIScale;
            ImGui.SetCursorScreenPos(new Vector2(contentStartX + nameWidth + extraPadding, titleY - (2f * GlobalUIScale)));
            ImGuiHelpers.SeStringWrapped(iconStr.Encode());
        }

        if (item is { IsShowMore: false, Record: not null })
        {
            using (FontManager.Instance().UIFont80.Push())
            {
                var subY    = titleY + lineHeight + (2f * GlobalUIScale);
                var subText = item.Record.GetZone().ExtractPlaceName();
                drawList.AddText(new Vector2(contentStartX, subY), ImGui.GetColorU32(ImGuiCol.TextDisabled), subText);
            }
        }

        var rightEndX = startPos.X + width - padding - 6f;

        string? hotkeyLabel = null;
        if (index is >= 0 and < 8)
            hotkeyLabel = $"Ctrl+{index + 1}";
        else if (item.IsShowMore || index == 8)
            hotkeyLabel = "Ctrl+9";

        if (hotkeyLabel != null)
        {
            var badgeSize   = ImGui.CalcTextSize(hotkeyLabel);
            var badgeWidth  = badgeSize.X + 8f;
            var badgeHeight = badgeSize.Y + 4f;
            var badgePos    = new Vector2(rightEndX - badgeWidth, startPos.Y + ((itemHeight - badgeHeight) / 2));

            var badgeBgCol = ImGui.GetColorU32(ImGuiCol.FrameBg);
            drawList.AddRectFilled(badgePos, badgePos + new Vector2(badgeWidth, badgeHeight), badgeBgCol, 4f);
            drawList.AddRect(badgePos, badgePos       + new Vector2(badgeWidth, badgeHeight), ImGui.GetColorU32(ImGuiCol.Border), 4f);

            drawList.AddText(badgePos + new Vector2(4f, 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), hotkeyLabel);

            rightEndX -= badgeWidth + padding;
        }

        if (item is { IsShowMore: false, Record: not null })
        {
            var cost     = item.Record.Cost;
            var costText = $"{cost}\uE049";
            var costSize = ImGui.CalcTextSize(costText);
            var costPos  = new Vector2(rightEndX - costSize.X, startPos.Y + ((itemHeight - costSize.Y) / 2));

            drawList.AddText(costPos, ImGui.GetColorU32(ImGuiCol.Text), costText);
        }

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
}
