using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private void DrawLeftPanel
    (
        MarketBoardUIContext frame
    )
    {
        var hasActiveMonitor = config.CurrentMonitorItem is { ItemID: > 0 };
        var monitorCardHeight = hasActiveMonitor ?
                                    ImGui.GetTextLineHeight() * 3.8f :
                                    0f;
        var childHeight = hasActiveMonitor ?
                              -monitorCardHeight - ImGui.GetStyle().ItemSpacing.Y :
                              -1f;

        using (var mainChild = ImRaii.Child("###LeftMainContainer", new(-1, childHeight), false, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar))
        {
            if (mainChild)
            {
                DrawSidebarSegmentedControl();

                ImGui.Spacing();

                switch (currentTab)
                {
                    case ItemSelectorTab.Search:
                        DrawItemSelectorSearch(frame);
                        break;
                    case ItemSelectorTab.Favorite:
                        DrawItemSelectorFavorite(frame);
                        break;
                    case ItemSelectorTab.History:
                        DrawItemSelectorHistory(frame);
                        break;
                    default:
                        currentTab = ItemSelectorTab.Search;
                        DrawItemSelectorSearch(frame);
                        break;
                }
            }
        }

        if (hasActiveMonitor)
            DrawSidebarActiveMonitor(monitorCardHeight);
    }

    private void DrawSidebarSegmentedControl()
    {
        const int TAB_COUNT  = 3;
        var       availWidth = ImGui.GetContentRegionAvail().X;
        var       tabHeight  = (ImGui.GetTextLineHeight() * 1.15f) + (6f * GlobalUIScale);
        var       tabWidth   = availWidth / TAB_COUNT;
        var       rounding   = 4f         * GlobalUIScale;

        var startPos = ImGui.GetCursorScreenPos();
        var maxPos   = startPos + new Vector2(availWidth, tabHeight);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(startPos, maxPos, ImGui.GetColorU32(ImGuiCol.FrameBg, 0.35f), rounding);
        drawList.AddRect(startPos, maxPos, ImGui.GetColorU32(ImGuiCol.Border,        0.20f), rounding, ImDrawFlags.None, 1f * GlobalUIScale);

        for (var i = 0; i < ItemSelectorTabs.Length; i++)
        {
            var tab        = ItemSelectorTabs[i];
            var isSelected = currentTab == tab;
            var icon = tab switch
            {
                ItemSelectorTab.Favorite => FontAwesomeIcon.Star.ToIconString(),
                ItemSelectorTab.History  => FontAwesomeIcon.History.ToIconString(),
                _                        => FontAwesomeIcon.Search.ToIconString()
            };
            var label = tab switch
            {
                ItemSelectorTab.Favorite => Lang.Get("Favorite"),
                ItemSelectorTab.History  => Lang.Get("History"),
                _                        => Lang.Get("Search")
            };
            var badge = tab == ItemSelectorTab.Favorite ?
                            config.FavoriteItems.Count :
                            0;
            var tabMin = startPos + new Vector2(i * tabWidth, 0);
            var tabMax = tabMin   + new Vector2(tabWidth,     tabHeight);

            ImGui.SetCursorScreenPos(tabMin);

            if (ImGui.InvisibleButton($"###SegmentedTab_{(int)tab}", new(tabWidth, tabHeight)))
            {
                currentTab            = tab;
                isConfirmClearHistory = false;
            }

            var isHovered = ImGui.IsItemHovered();
            if (isHovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (isSelected)
            {
                var selectBgColor = KnownColor.LightSkyBlue.ToVector4().WithW
                (
                    isHovered ?
                        0.35f :
                        0.25f
                ).ToUInt();
                var selectBorderColor = KnownColor.LightSkyBlue.ToVector4().WithW(0.85f).ToUInt();

                drawList.AddRectFilled(tabMin, tabMax, selectBgColor, rounding);
                drawList.AddRect(tabMin, tabMax, selectBorderColor, rounding, ImDrawFlags.None, 1f * GlobalUIScale);
            }
            else if (isHovered)
            {
                var hoverBgColor = ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.40f);
                drawList.AddRectFilled(tabMin, tabMax, hoverBgColor, rounding);
            }

            var textContent = badge > 0 ?
                                  Lang.Get
                                  (
                                      "BetterMarketBoard-TabLabel-Format",
                                      new Dictionary<string, object>
                                      {
                                          ["icon"]  = icon,
                                          ["label"] = label,
                                          ["count"] = badge
                                      }
                                  ) :
                                  $"{icon} {label}";

            using (FontManager.Instance().UIFont80.Push())
            {
                var textSize = ImGui.CalcTextSize(textContent);
                var textPos  = new Vector2(tabMin.X + MathF.Max(2f, (tabWidth - textSize.X) / 2f), tabMin.Y + ((tabHeight - textSize.Y) / 2f));
                var textColor = isSelected ? KnownColor.LightSkyBlue.ToUInt() :
                                isHovered  ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(ImGuiCol.TextDisabled);

                drawList.PushClipRect(tabMin, tabMax, true);
                drawList.AddText(textPos, textColor, textContent);
                drawList.PopClipRect();
            }
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, tabHeight));
    }

    private void DrawItemSelectorSearch
    (
        MarketBoardUIContext frame
    )
    {
        var inputWidth = ImGui.GetContentRegionAvail().X;
        var hasInput   = !string.IsNullOrEmpty(itemSearchInput);
        var clearBtnW  = ImGui.GetFrameHeight();

        if (hasInput)
            ImGui.SetNextItemWidth(inputWidth - clearBtnW - ImGui.GetStyle().ItemSpacing.X);
        else
            ImGui.SetNextItemWidth(inputWidth);

        if (ImGui.InputTextWithHint("###ItemSearchInput", Lang.Get("PleaseSearch"), ref itemSearchInput, 256))
            ExecuteOverlaySearch(itemSearchInput);

        if (hasInput)
        {
            ImGui.SameLine();
            if (ImGui.Button("×###ClearSearchInput", new(clearBtnW, 0)))
                ExecuteOverlaySearch(string.Empty);
            ImGuiOm.TooltipHover(Lang.Get("Clear"));
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("###SearchListContainer", new(-1, -1), false, ImGuiWindowFlags.NoBackground);
        if (!child) return;

        if (!string.IsNullOrWhiteSpace(itemSearchInput))
        {
            var groupedData = provider.GetSearchGroups(itemSearchInput);

            if (groupedData.Count == 0)
            {
                DrawEmptyState(FontAwesomeIcon.Search.ToIconString(), LuminaWrapper.GetAddonText(2717));
                return;
            }

            foreach (var (category, items) in groupedData)
            {
                var categoryID   = category.RowId;
                var categoryName = category.Name.ToString();
                if (!ITextureProvider.Instance().TryGetFromGameIcon(new((uint)category.Icon), out var texture)) continue;

                if (ImGuiOm.TreeNodeImageWithText
                    (
                        texture.GetWrapOrEmpty().Handle,
                        new(ImGui.GetTextLineHeight()),
                        $"{categoryName}##{categoryID}-Search",
                        ImGuiTreeNodeFlags.DefaultOpen
                    ))
                {
                    var clipper = new ImGuiListClipper();
                    clipper.Begin(items.Count);

                    while (clipper.Step())
                    {
                        for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                            RenderItemCard(frame, items[i]);
                    }

                    ImGui.TreePop();
                }
            }
        }
        else
        {
            foreach (var searchCategory in ValidCategories)
            {
                var name = searchCategory.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!searchCategoryToItems.TryGetValue(searchCategory.RowId, out var data) || data.Count == 0) continue;
                if (!ITextureProvider.Instance().TryGetFromGameIcon(new((uint)searchCategory.Icon), out var texture)) continue;

                if (ImGuiOm.TreeNodeImageWithText
                    (
                        texture.GetWrapOrEmpty().Handle,
                        new(ImGui.GetTextLineHeight()),
                        $"{name}##{searchCategory.RowId}-Default"
                    ))
                {
                    var clipper = new ImGuiListClipper();
                    clipper.Begin(data.Count);

                    while (clipper.Step())
                    {
                        for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                            RenderItemCard(frame, data[i]);
                    }

                    ImGui.TreePop();
                }
            }
        }
    }

    private void DrawItemSelectorFavorite
    (
        MarketBoardUIContext frame
    )
    {
        using var child = ImRaii.Child("###FavoriteContainer", new(-1, -1), false, ImGuiWindowFlags.NoBackground);
        if (!child) return;

        if (config.FavoriteItems.Count == 0)
        {
            DrawEmptyState(FontAwesomeIcon.Star.ToIconString(), LuminaWrapper.GetAddonText(2717));
            return;
        }

        var favoriteList = favoriteItemsCache;

        if (favoriteList == null || favoriteItemsCacheVersion != favoriteItemsVersion)
        {
            favoriteItemsCache        = favoriteList = [.. config.FavoriteItems.Values];
            favoriteItemsCacheVersion = favoriteItemsVersion;
        }

        var favoriteClipper = new ImGuiListClipper();
        favoriteClipper.Begin(favoriteList.Count);

        while (favoriteClipper.Step())
        {
            for (var i = favoriteClipper.DisplayStart; i < favoriteClipper.DisplayEnd; i++)
            {
                var favoriteItem = favoriteList[i];
                var itemData     = favoriteItem.GetData();
                if (itemData.RowId == 0) continue;

                RenderItemCard(frame, itemData, note: favoriteItem.Note);
            }
        }
    }

    private void DrawItemSelectorHistory
    (
        MarketBoardUIContext frame
    )
    {
        var inputWidth = ImGui.GetContentRegionAvail().X;
        var hasHistory = config.HistoryItems.Count > 0;
        var clearBtnW  = ImGui.GetFrameHeight() * 1.5f;

        ImGui.SetNextItemWidth(inputWidth - clearBtnW - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("###HistorySearchInput", Lang.Get("Search"), ref historySearchInput, 128);

        ImGui.SameLine();

        using (ImRaii.Disabled(!hasHistory))
        {
            var clearColor = isConfirmClearHistory ?
                                 KnownColor.OrangeRed.ToVector4() :
                                 KnownColor.LightSkyBlue.ToVector4();

            var clearIcon = isConfirmClearHistory ?
                                FontAwesomeIcon.Check.ToIconString() :
                                FontAwesomeIcon.Trash.ToIconString();

            using (ImRaii.PushColor(ImGuiCol.Text, clearColor, isConfirmClearHistory))
            {
                if (ImGui.Button($"{clearIcon}###ClearHistoryButton", new(clearBtnW, 0)))
                {
                    if (isConfirmClearHistory)
                    {
                        config.HistoryItems.Clear();
                        config.Save(this);
                        isConfirmClearHistory = false;
                    }
                    else
                        isConfirmClearHistory = true;
                }
            }

            if (isConfirmClearHistory && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered())
                isConfirmClearHistory = false;

            ImGuiOm.TooltipHover
            (
                isConfirmClearHistory ?
                    Lang.Get("Confirm") :
                    Lang.Get("Clear")
            );
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("###HistoryContainer", new(-1, -1), false, ImGuiWindowFlags.NoBackground);
        if (!child) return;

        if (config.HistoryItems.Count == 0)
        {
            DrawEmptyState(FontAwesomeIcon.History.ToIconString(), LuminaWrapper.GetAddonText(2717));
            return;
        }

        IEnumerable<KeyValuePair<uint, MarketHistoryItem>> query = config.HistoryItems;

        if (!string.IsNullOrWhiteSpace(historySearchInput))
        {
            query = query.Where
            (kvp =>
                {
                    var item = kvp.Value.GetData();
                    return item.RowId > 0 &&
                           item.Name.ToString().Contains(historySearchInput, StringComparison.OrdinalIgnoreCase);
                }
            );
        }

        var filteredHistory = query.ToList();

        if (filteredHistory.Count == 0)
        {
            DrawEmptyState(FontAwesomeIcon.Search.ToIconString(), LuminaWrapper.GetAddonText(2717));
            return;
        }

        var groupedHistory = filteredHistory
                             .GroupBy(kvp => kvp.Value.AccessTime.Date)
                             .OrderByDescending(g => g.Key);

        foreach (var group in groupedHistory)
        {
            var date = group.Key;

            if (ImGui.TreeNodeEx
                (
                    Lang.Get
                    (
                        "BetterMarketBoard-HistoryGroup-Format",
                        new Dictionary<string, object>
                        {
                            ["date"]  = date,
                            ["count"] = group.Count()
                        }
                    ) +
                    $"##{date:yyyyMMdd}",
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth
                ))
            {
                var groupItems     = group.OrderByDescending(x => x.Value.AccessTime).ToList();
                var historyClipper = new ImGuiListClipper();
                historyClipper.Begin(groupItems.Count);

                while (historyClipper.Step())
                {
                    for (var i = historyClipper.DisplayStart; i < historyClipper.DisplayEnd; i++)
                    {
                        var kvp = groupItems[i];
                        RenderItemCard(frame, kvp.Value.GetData(), kvp.Value.AccessTime);
                    }
                }

                ImGui.TreePop();
            }
        }
    }

    private void RenderItemCard
    (
        MarketBoardUIContext frame,
        Item                 item,
        DateTime?            accessTime = null,
        string?              note       = null
    )
    {
        var isCurrentHQ = frame.ItemID == item.RowId && frame.HQOnly;
        if (!ITextureProvider.Instance().TryGetFromGameIcon(new(item.Icon, isCurrentHQ), out var texture)) return;

        using var id = ImRaii.PushId($"{item.RowId}_{currentTab}");

        var isSelected = frame.ItemID == item.RowId;
        var isFavorite = config.FavoriteItems.ContainsKey(item.RowId);
        var isMonitor  = config.CurrentMonitorItem?.ItemID == item.RowId;

        var availWidth  = ImGui.GetContentRegionAvail().X;
        var rowHeight   = (ImGui.GetTextLineHeight() * 1.6f) + (6f * GlobalUIScale);
        var actionAreaW = 18f * GlobalUIScale;
        var padX        = 4f  * GlobalUIScale;
        var mainButtonW = availWidth - actionAreaW - (4f * GlobalUIScale);
        var cardSize    = new Vector2(availWidth, rowHeight);
        var rounding    = 4f * GlobalUIScale;

        var startPos = ImGui.GetCursorScreenPos();
        var maxPos   = startPos + cardSize;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorScreenPos(startPos);

        ImGui.InvisibleButton($"###CardButton_{item.RowId}", new(mainButtonW, rowHeight));

        var isHovered = ImGui.IsItemHovered();
        if (isHovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (isHovered)
        {
            AtkStage.Instance()->ShowItemTooltip(ScreenText->RootNode, item.RowId);
            isItemListTooltip = true;
        }
        else if (isItemListTooltip)
        {
            isItemListTooltip = false;
            AtkStage.Instance()->HideTooltip(ScreenText->Id);
        }

        if (isHovered)
        {
            var tooltipText = item.Name.ToString();

            if (item.CanBeHq)
            {
                tooltipText += isCurrentHQ ?
                                   $"\n（{Lang.Get("RightClick")}：{Lang.Get("All")}）" :
                                   $"\n（{Lang.Get("RightClick")}：{Lang.Get("BetterMarketBoard-MarketView-HQ")}）";
            }

            ImGuiOm.TooltipHover(tooltipText);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            provider.SelectItem(item.RowId, hqOnly: isCurrentHQ);

        if (item.CanBeHq && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            provider.SelectItem(item.RowId, hqOnly: !isCurrentHQ);

        Vector4 bgColor;
        Vector4 borderColor;

        if (isSelected)
        {
            bgColor = KnownColor.LightSkyBlue.ToVector4().WithW
            (
                isHovered ?
                    0.30f :
                    0.20f
            );
            borderColor = KnownColor.LightSkyBlue.ToVector4().WithW(0.85f);
        }
        else if (isHovered)
        {
            bgColor     = ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.40f).ToVector4();
            borderColor = ImGui.GetColorU32(ImGuiCol.Border,         0.35f).ToVector4();
        }
        else
        {
            bgColor     = ImGui.GetColorU32(ImGuiCol.FrameBg, 0.22f).ToVector4();
            borderColor = ImGui.GetColorU32(ImGuiCol.Border,  0.12f).ToVector4();
        }

        drawList.AddRectFilled(startPos, maxPos, bgColor.ToUInt(), rounding);
        drawList.AddRect(startPos, maxPos, borderColor.ToUInt(), rounding, ImDrawFlags.None, 1f * GlobalUIScale);

        var iconSize = rowHeight - (6f * GlobalUIScale);
        var iconPos  = new Vector2(startPos.X + padX, startPos.Y + (3f * GlobalUIScale));
        drawList.AddImage(texture.GetWrapOrEmpty().Handle, iconPos, iconPos + new Vector2(iconSize));

        var textStartX = iconPos.X                + iconSize + (6f * GlobalUIScale);
        var textMaxX   = startPos.X + mainButtonW - padX;

        var timeWidth = 0f;

        if (accessTime != null)
        {
            var timeText = $"{accessTime.Value:HH:mm}";

            using (FontManager.Instance().UIFont60.Push())
            {
                var timeSize = ImGui.CalcTextSize(timeText);
                timeWidth = timeSize.X + (4f * GlobalUIScale);
                var timePos = new Vector2(textMaxX - timeSize.X, startPos.Y + (4f * GlobalUIScale));
                drawList.AddText(timePos, ImGui.GetColorU32(ImGuiCol.TextDisabled), timeText);
            }
        }

        var contentMaxX = textMaxX - timeWidth;

        var itemName = item.Name.ToString();
        if (isCurrentHQ)
            itemName += " \ue03c";

        var namePos = new Vector2(textStartX, startPos.Y + (3f * GlobalUIScale));

        drawList.PushClipRect(startPos, maxPos with { X = contentMaxX }, true);
        drawList.AddText
        (
            namePos,
            isSelected ?
                KnownColor.LightSkyBlue.ToUInt() :
                ImGui.GetColorU32(ImGuiCol.Text),
            itemName
        );
        drawList.PopClipRect();

        var badgeY    = startPos.Y + ImGui.GetTextLineHeight() + (2f * GlobalUIScale);
        var curBadgeX = textStartX;

        using (FontManager.Instance().UIFont60.Push())
        {
            var ilvlText = Lang.Get
            (
                "BetterMarketBoard-ItemLevel-Format",
                new Dictionary<string, object>
                {
                    ["level"] = item.LevelItem.RowId
                }
            );
            curBadgeX = DrawItemCardBadge
            (
                drawList,
                ilvlText,
                curBadgeX,
                badgeY,
                KnownColor.LightSkyBlue.ToVector4().WithW(0.15f).ToUInt(),
                KnownColor.LightSkyBlue.ToUInt()
            );

            if (item.CanBeHq)
            {
                const string HQ_TEXT = "\ue03c";

                var hqBg = isCurrentHQ ?
                               KnownColor.LightSkyBlue.ToVector4().WithW(0.25f).ToUInt() :
                               ImGui.GetColorU32(ImGuiCol.FrameBg, 0.40f);
                var hqColor = isCurrentHQ ?
                                  KnownColor.LightSkyBlue.ToUInt() :
                                  KnownColor.Gray.ToUInt();

                curBadgeX = DrawItemCardBadge(drawList, HQ_TEXT, curBadgeX, badgeY, hqBg, hqColor);
            }

            if (item.ItemSearchCategory.Value.RowId > 0)
            {
                var catText = item.ItemSearchCategory.Value.Name.ToString();
                curBadgeX = DrawItemCardBadge
                (
                    drawList,
                    catText,
                    curBadgeX,
                    badgeY,
                    ImGui.GetColorU32(ImGuiCol.FrameBg, 0.40f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    contentMaxX,
                    true
                );
            }

            if (!string.IsNullOrEmpty(note))
            {
                var noteText = $"✎ {note}";

                if (curBadgeX < contentMaxX)
                {
                    drawList.PushClipRect(startPos, maxPos with { X = contentMaxX }, true);
                    DrawItemCardBadge
                    (
                        drawList,
                        noteText,
                        curBadgeX,
                        badgeY,
                        KnownColor.DarkOrange.ToVector4().WithW(0.18f).ToUInt(),
                        KnownColor.Orange.ToUInt()
                    );
                    drawList.PopClipRect();
                }
            }
        }

        var actionX = startPos.X + mainButtonW + (2f * GlobalUIScale);
        var halfH   = rowHeight / 2f;

        var favBtnPos  = new Vector2(actionX,     startPos.Y + (1f * GlobalUIScale));
        var favBtnSize = new Vector2(actionAreaW, halfH      - (2f * GlobalUIScale));
        ImGui.SetCursorScreenPos(favBtnPos);

        if (ImGui.InvisibleButton($"###FavBtn_{item.RowId}", favBtnSize))
        {
            if (isFavorite)
                config.FavoriteItems.Remove(item.RowId);
            else
                config.FavoriteItems[item.RowId] = new() { ItemID = item.RowId, Note = string.Empty };

            favoriteItemsVersion++;
            config.Save(this);
        }

        var isFavHovered = ImGui.IsItemHovered();

        if (isFavHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiOm.TooltipHover
            (
                isFavorite ?
                    Lang.Get("Unfavorite") :
                    Lang.Get("Favorite")
            );
        }

        var starIcon = FontAwesomeIcon.Star.ToIconString();
        var starColor = isFavorite   ? KnownColor.Goldenrod.ToUInt() :
                        isFavHovered ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.40f);

        using (FontManager.Instance().UIFont60.Push())
        {
            var starSize = ImGui.CalcTextSize(starIcon);
            var starPos  = new Vector2(favBtnPos.X + ((favBtnSize.X - starSize.X) / 2f), favBtnPos.Y + ((favBtnSize.Y - starSize.Y) / 2f));
            drawList.AddText(starPos, starColor, starIcon);
        }

        var monBtnPos  = new Vector2(actionX,     startPos.Y + halfH + (1f * GlobalUIScale));
        var monBtnSize = new Vector2(actionAreaW, halfH      - (2f         * GlobalUIScale));
        ImGui.SetCursorScreenPos(monBtnPos);

        if (ImGui.InvisibleButton($"###MonBtn_{item.RowId}", monBtnSize))
        {
            if (isMonitor)
                monitorProvider.ClearMonitor();
            else
                monitorProvider.SetMonitor(item.RowId, isCurrentHQ);
        }

        var isMonHovered = ImGui.IsItemHovered();

        if (isMonHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiOm.TooltipHover
            (
                isMonitor ?
                    "BetterMarketBoard-PriceMonitor-ToNotMonitor" :
                    "BetterMarketBoard-PriceMonitor-ToMonitor"
            );
        }

        var bellIcon = FontAwesomeIcon.Bell.ToIconString();
        var bellColor = isMonitor    ? KnownColor.GreenYellow.ToUInt() :
                        isMonHovered ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.40f);

        using (FontManager.Instance().UIFont60.Push())
        {
            var bellSize = ImGui.CalcTextSize(bellIcon);
            var bellPos  = new Vector2(monBtnPos.X + ((monBtnSize.X - bellSize.X) / 2f), monBtnPos.Y + ((monBtnSize.Y - bellSize.Y) / 2f));
            drawList.AddText(bellPos, bellColor, bellIcon);
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, rowHeight + ImGui.GetStyle().ItemSpacing.Y));
    }

    private static float DrawItemCardBadge
    (
        ImDrawListPtr drawList,
        string        text,
        float         x,
        float         y,
        uint          backgroundColor,
        uint          textColor,
        float         maxX       = float.PositiveInfinity,
        bool          requireFit = false
    )
    {
        var textSize = ImGui.CalcTextSize(text);
        var paddingX = 3f * GlobalUIScale;
        var min      = new Vector2(x,                                y);
        var max      = new Vector2(x + textSize.X + (paddingX * 2f), y + textSize.Y + (2f * GlobalUIScale));

        if (requireFit ?
                max.X >= maxX :
                min.X >= maxX)
            return x;

        drawList.AddRectFilled(min, max, backgroundColor, 2f * GlobalUIScale);
        drawList.AddText(new Vector2(min.X + paddingX, min.Y + (1f * GlobalUIScale)), textColor, text);
        return max.X + (4f * GlobalUIScale);
    }

    private void DrawSidebarActiveMonitor
    (
        float cardHeight
    )
    {
        var monitorItem = config.CurrentMonitorItem;
        if (monitorItem == null) return;

        var itemData = monitorItem.GetData();

        if (itemData.RowId == 0 || itemData.ItemSearchCategory.RowId == 0)
        {
            monitorProvider.ClearMonitor();
            return;
        }

        var availWidth = ImGui.GetContentRegionAvail().X;
        var startPos   = ImGui.GetCursorScreenPos();
        var maxPos     = startPos + new Vector2(availWidth, cardHeight);
        var drawList   = ImGui.GetWindowDrawList();
        var rounding   = 4f * GlobalUIScale;
        var padX       = 6f * GlobalUIScale;
        var padY       = 5f * GlobalUIScale;

        var clearBtnSize = new Vector2(16f * GlobalUIScale,              16f * GlobalUIScale);
        var clearBtnPos  = new Vector2(maxPos.X - padX - clearBtnSize.X, startPos.Y + padY);

        ImGui.SetCursorScreenPos(clearBtnPos);

        if (ImGui.InvisibleButton("###ClearMonitorItemButton", clearBtnSize))
            monitorProvider.ClearMonitor();

        var isClearHovered = ImGui.IsItemHovered();

        if (isClearHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiOm.TooltipHover(Lang.Get("BetterMarketBoard-PriceMonitor-ToNotMonitor"));
        }

        var autoBuyText = Lang.Get("BetterMarketBoard-PriceMonitor-AutoPurchase");

        Vector2 autoBuySize;
        using (FontManager.Instance().UIFont80.Push())
            autoBuySize = ImGui.CalcTextSize(autoBuyText);

        var autoBuyPadX = 4f   * GlobalUIScale;
        var autoBuyPadY = 1.5f * GlobalUIScale;
        var autoBuyW    = autoBuySize.X + (autoBuyPadX * 2f);
        var autoBuyH    = MathF.Max(clearBtnSize.Y, autoBuySize.Y + (autoBuyPadY * 2f));
        var autoBuyMax  = new Vector2(clearBtnPos.X               - (5f          * GlobalUIScale), startPos.Y + padY + autoBuyH);
        var autoBuyMin  = new Vector2(autoBuyMax.X                - autoBuyW,                      startPos.Y + padY);

        ImGui.SetCursorScreenPos(autoBuyMin);

        ImGui.InvisibleButton("###AutoBuyBadgeButton", autoBuyMax - autoBuyMin);

        var isAutoBuyHovered = ImGui.IsItemHovered();

        if (isAutoBuyHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var autoBuyTooltip = Lang.Get("BetterMarketBoard-PriceMonitor-AutoPurchase");

            if (!config.AutoBuy)
            {
                autoBuyTooltip +=
                    $"（{Lang.Get("Disabled")}）\n" +
                    $"{Lang.Get("LeftClick")}：{Lang.Get("Enable")}";
            }
            else
            {
                autoBuyTooltip +=
                    $"（{Lang.Get("Enabled")}）\n" +
                    $"{Lang.Get("LeftClick")}：{Lang.Get("Disable")}";
            }

            autoBuyTooltip += $"\n{Lang.Get("RightClick")}：{Lang.Get("Settings")}";

            ImGuiOm.TooltipHover(autoBuyTooltip);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            monitorProvider.ToggleAutoBuy();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("###MonitorAutoBuySettingsPopup");

        using (var popup = ImRaii.Popup("###MonitorAutoBuySettingsPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (popup)
                DrawMonitorAutoPurchaseSettings(monitorItem);
        }

        var headerHeight = MathF.Max(clearBtnSize.Y, autoBuyH);
        var contentTopY  = startPos.Y + padY + headerHeight + (5f * GlobalUIScale);
        var iconSize     = 36f * GlobalUIScale;

        var priceText = Lang.Get
        (
            "BetterMarketBoard-PriceMonitor-Threshold-Format",
            new Dictionary<string, object>
            {
                ["price"] = monitorItem.PriceThreshold
            }
        );
        Vector2 priceSize;
        using (FontManager.Instance().UIFont80.Push())
            priceSize = ImGui.CalcTextSize(priceText);

        var priceY       = contentTopY + ImGui.GetTextLineHeight() + (2f * GlobalUIScale);
        var priceBtnPos  = new Vector2(maxPos.X    - padX - priceSize.X - (3f * GlobalUIScale), priceY      - (1.5f * GlobalUIScale));
        var priceBtnSize = new Vector2(priceSize.X + (6f                      * GlobalUIScale), priceSize.Y + (3f   * GlobalUIScale));

        ImGui.SetCursorScreenPos(priceBtnPos);
        if (ImGui.InvisibleButton("###MonitorPriceThresholdButton", priceBtnSize))
            ImGui.OpenPopup("###MonitorPriceSettingsPopup");

        var isPriceHovered = ImGui.IsItemHovered();

        if (isPriceHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiOm.TooltipHover(Lang.Get("BetterMarketBoard-PriceMonitor-Price"));
        }

        DrawMonitorPriceSettingsPopup(monitorItem);

        var topBtnW = autoBuyMin.X - startPos.X - (2f * GlobalUIScale);
        ImGui.SetCursorScreenPos(startPos);
        var isTopClicked      = ImGui.InvisibleButton("###ActiveMonitorTopSelectButton", new Vector2(topBtnW, headerHeight + padY));
        var isTopHovered      = ImGui.IsItemHovered();
        var isTopRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);

        var contentBtnW = maxPos.X - padX - priceSize.X - (6f * GlobalUIScale) - startPos.X;
        ImGui.SetCursorScreenPos(startPos with { Y = contentTopY });
        var isContentClicked      = ImGui.InvisibleButton("###ActiveMonitorContentSelectButton", new Vector2(contentBtnW, iconSize));
        var isContentHovered      = ImGui.IsItemHovered();
        var isContentRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);

        if (isTopClicked || isContentClicked)
            provider.SelectItem(monitorItem.ItemID, hqOnly: monitorItem.HQOnly);

        if (itemData.CanBeHq && (isTopRightClicked || isContentRightClicked))
            monitorProvider.ToggleHQ();

        var isCardHovered = (isTopHovered || isContentHovered) && !isClearHovered && !isAutoBuyHovered && !isPriceHovered;

        if (isCardHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var tooltipText = itemData.Name.ToString();
            if (itemData.CanBeHq)
                tooltipText += $"\n（{Lang.Get("RightClick")}：{(monitorItem.HQOnly ? Lang.Get("All") : Lang.Get("BetterMarketBoard-MarketView-HQ"))}）";
            ImGuiOm.TooltipHover(tooltipText);
        }

        var bgColor = KnownColor.GreenYellow.ToVector4().WithW
        (
            isCardHovered ?
                0.20f :
                0.12f
        ).ToUInt();
        var borderColor = KnownColor.GreenYellow.ToVector4().WithW(0.75f).ToUInt();

        drawList.AddRectFilled(startPos, maxPos, bgColor, rounding);
        drawList.AddRect(startPos, maxPos, borderColor, rounding, ImDrawFlags.None, 1f * GlobalUIScale);

        using (FontManager.Instance().UIFont80.Push())
        {
            var headerText = $"{FontAwesomeIcon.Bell.ToIconString()} {Lang.Get("BetterMarketBoard-PriceMonitor-Item")}";
            drawList.AddText
            (
                new(startPos.X + padX, startPos.Y + padY + ((headerHeight - ImGui.GetTextLineHeight()) / 2f)),
                KnownColor.GreenYellow.ToUInt(),
                headerText
            );
        }

        using (FontManager.Instance().UIFont80.Push())
        {
            uint autoBuyBg;
            uint autoBuyBorder;
            uint autoBuyTextColor;

            if (!config.AutoBuy)
            {
                autoBuyBg        = ImGui.GetColorU32(ImGuiCol.FrameBg, 0.40f);
                autoBuyBorder    = ImGui.GetColorU32(ImGuiCol.Border,  0.25f);
                autoBuyTextColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            }
            else
            {
                autoBuyBg        = KnownColor.LightSkyBlue.ToVector4().WithW(0.20f).ToUInt();
                autoBuyBorder    = KnownColor.LightSkyBlue.ToVector4().WithW(0.75f).ToUInt();
                autoBuyTextColor = KnownColor.LightSkyBlue.ToUInt();
            }

            drawList.AddRectFilled(autoBuyMin, autoBuyMax, autoBuyBg, 3f * GlobalUIScale);
            drawList.AddRect(autoBuyMin, autoBuyMax, autoBuyBorder, 3f   * GlobalUIScale, ImDrawFlags.None, 1f * GlobalUIScale);
            drawList.AddText(new Vector2(autoBuyMin.X + autoBuyPadX, autoBuyMin.Y + autoBuyPadY), autoBuyTextColor, autoBuyText);
        }

        using (FontManager.Instance().UIFont60.Push())
        {
            var timesIcon = FontAwesomeIcon.Times.ToIconString();
            var timesSize = ImGui.CalcTextSize(timesIcon);
            var timesPos  = new Vector2(clearBtnPos.X + ((clearBtnSize.X - timesSize.X) / 2f), clearBtnPos.Y + ((clearBtnSize.Y - timesSize.Y) / 2f));
            var timesColor = isClearHovered ?
                                 KnownColor.OrangeRed.ToUInt() :
                                 ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.60f);
            drawList.AddText(timesPos, timesColor, timesIcon);
        }

        if (ITextureProvider.Instance().TryGetFromGameIcon(new(itemData.Icon, monitorItem.HQOnly), out var texture))
        {
            var iconPos = new Vector2(startPos.X + padX, contentTopY);
            drawList.AddImage(texture.GetWrapOrEmpty().Handle, iconPos, iconPos + new Vector2(iconSize));
        }

        var textStartX = startPos.X + padX + iconSize + (6f * GlobalUIScale);

        var itemName = itemData.Name.ToString();
        if (monitorItem.HQOnly)
            itemName += " \ue03c";
        drawList.PushClipRect(startPos, maxPos with { X = maxPos.X - padX }, true);
        drawList.AddText(new Vector2(textStartX, contentTopY), ImGui.GetColorU32(ImGuiCol.Text), itemName);
        drawList.PopClipRect();

        using (FontManager.Instance().UIFont80.Push())
        {
            if (isPriceHovered)
                drawList.AddRectFilled(priceBtnPos, priceBtnPos + priceBtnSize, ImGui.GetColorU32(ImGuiCol.FrameBgHovered, 0.40f), 2f * GlobalUIScale);

            drawList.AddText(new(maxPos.X - padX - priceSize.X, priceY), KnownColor.Orange.ToUInt(), priceText);
        }

        ImGui.SetCursorScreenPos(startPos + new Vector2(0, cardHeight));
    }

    private static void DrawEmptyState
    (
        string icon,
        string message
    )
    {
        var availSize = ImGui.GetContentRegionAvail();
        var centerPos = ImGui.GetCursorScreenPos() + (availSize / 2f);

        using (FontManager.Instance().UIFont160.Push())
        {
            var iconSize = ImGui.CalcTextSize(icon);
            var iconPos  = new Vector2(centerPos.X - (iconSize.X / 2f), centerPos.Y - iconSize.Y - (6f * GlobalUIScale));
            ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.40f), icon);
        }

        using (FontManager.Instance().UIFont80.Push())
        {
            var msgSize = ImGui.CalcTextSize(message);
            var msgPos  = new Vector2(centerPos.X - (msgSize.X / 2f), centerPos.Y + (6f * GlobalUIScale));
            ImGui.GetWindowDrawList().AddText(msgPos, ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.60f), message);
        }
    }

    private void DrawMonitorPriceSettingsPopup
    (
        MonitorItem monitorItem
    )
    {
        using var popup = ImRaii.Popup("###MonitorPriceSettingsPopup", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        provider.EnsurePriceData(monitorItem.ItemID, monitorItem.HQOnly);

        var threshold     = (int)monitorItem.PriceThreshold;
        var worldMin      = provider.GetSelectedWorldMinPrice(monitorItem.ItemID, monitorItem.HQOnly);
        var regionMin     = provider.GetRegionMinPrice(monitorItem.ItemID, monitorItem.HQOnly);
        var npcPrice      = provider.GetNPCGilPrice(monitorItem.ItemID);
        var avgPrice      = provider.GetItemAggregatedStats(monitorItem.ItemID, provider.SelectedWorldID, monitorItem.HQOnly).AvgPrice;
        var medianPrice   = provider.GetHistoryPercentilePrice(monitorItem.ItemID, monitorItem.HQOnly, 0.50);
        var lowPercentile = provider.GetHistoryPercentilePrice(monitorItem.ItemID, monitorItem.HQOnly, 0.25);

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{FontAwesomeIcon.Tag.ToIconString()} {Lang.Get("BetterMarketBoard-PriceMonitor-Price")}");

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("≤");

            ImGui.SameLine();

            if (ImGui.InputInt("\ue049##MonitorPriceThresholdInput", ref threshold, 100, 1000))
            {
                monitorItem.PriceThreshold = (uint)Math.Max(1, threshold);
                config.Save(this);
            }
        }

        ImGui.NewLine();

        ImGui.TextColored
        (
            KnownColor.LightSkyBlue.ToVector4(),
            $"{FontAwesomeIcon.ChartLine.ToIconString()} {Lang.Get("BetterMarketBoard-PriceMonitor-Quick-Title")}"
        );

        var benchmarks = new List<(string Label, ulong? Price, Vector4 Color, string Icon)>
        {
            (Lang.Get("BetterMarketBoard-PriceMonitor-Quick-WorldMin"), worldMin, KnownColor.GreenYellow.ToVector4(), FontAwesomeIcon.MapMarkerAlt.ToIconString()),
            (Lang.Get("BetterMarketBoard-PriceMonitor-Quick-RegionMin"), regionMin, KnownColor.LightSkyBlue.ToVector4(), FontAwesomeIcon.Globe.ToIconString()),
            (Lang.Get("BetterMarketBoard-PriceMonitor-Quick-Average"), avgPrice, KnownColor.DeepSkyBlue.ToVector4(), FontAwesomeIcon.Coins.ToIconString()),
            (Lang.Get("BetterMarketBoard-PriceMonitor-Quick-LowPercentile"), lowPercentile, KnownColor.MediumPurple.ToVector4(),
                FontAwesomeIcon.Percent.ToIconString()),
            (Lang.Get("BetterMarketBoard-PriceMonitor-Quick-Median"), medianPrice, KnownColor.SlateBlue.ToVector4(), FontAwesomeIcon.BalanceScale.ToIconString())
        };

        if (!monitorItem.HQOnly && npcPrice is > 0)
            benchmarks.Add
                ((Lang.Get("BetterMarketBoard-PriceMonitor-Quick-NPC"), npcPrice, KnownColor.DarkOrange.ToVector4(), FontAwesomeIcon.Store.ToIconString()));

        DrawBenchmarkCardsGrid(monitorItem, benchmarks);
    }

    private void DrawMonitorAutoPurchaseSettings
    (
        MonitorItem monitorItem
    )
    {
        var quantityLimit = config.AutoBuyQuantityLimit;
        var heldCount     = LocalPlayerState.GetItemCount(monitorItem.ItemID);

        ImGui.TextColored
        (
            KnownColor.LightSkyBlue.ToVector4(),
            $"{FontAwesomeIcon.CartPlus.ToIconString()} {Lang.Get("BetterMarketBoard-PriceMonitor-AutoPurchase-QuantityLimit")}"
        );
        ImGuiOm.HelpMarker(Lang.Get("BetterMarketBoard-PriceMonitor-AutoPurchase-QuantityLimit-Help"));

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            if (ImGui.InputUInt
                (
                    $"###{nameof(Config.AutoBuyQuantityLimit)}",
                    ref quantityLimit,
                    1,
                    10
                ))
                monitorProvider.SetQuantityLimit(quantityLimit);

            ImGui.TextDisabled
            (
                Lang.Get
                (
                    "BetterMarketBoard-HeldCount-Format",
                    new Dictionary<string, object>
                    {
                        ["label"]    = LuminaWrapper.GetAddonText(358),
                        ["quantity"] = heldCount
                    }
                )
            );
        }
    }

    private void DrawBenchmarkCardsGrid
    (
        MonitorItem                                                             monitorItem,
        List<(string Label, ulong? Price, Vector4 Color, string Icon)> benchmarks
    )
    {
        var cardWidth  = 190f * GlobalUIScale;
        var cardHeight = 36f  * GlobalUIScale;
        var rounding   = 4f   * GlobalUIScale;
        var padX       = 6f   * GlobalUIScale;
        var spacingX   = 6f   * GlobalUIScale;
        var spacingY   = 4f   * GlobalUIScale;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacingX, spacingY)))
        {
            for (var i = 0; i < benchmarks.Count; i++)
            {
                var (label, price, color, icon) = benchmarks[i];
                var isAvailable = price is > 0;
                var isSelected  = isAvailable && monitorItem.PriceThreshold == price!.Value;

                ImGui.InvisibleButton($"###BenchmarkCard_{i}", new Vector2(cardWidth, cardHeight));

                if (isAvailable && ImGui.IsItemClicked())
                {
                    monitorItem.PriceThreshold = (uint)Math.Min(uint.MaxValue, price!.Value);
                    config.Save(this);
                }

                var isHovered = ImGui.IsItemHovered() && isAvailable;

                if (isHovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGuiOm.TooltipHover
                    (
                        Lang.Get
                        (
                            "BetterMarketBoard-PriceMonitor-Quick-Tooltip",
                            new Dictionary<string, object>
                            {
                                ["label"] = label,
                                ["price"] = price!.Value
                            }
                        )
                    );
                }

                var min      = ImGui.GetItemRectMin();
                var max      = ImGui.GetItemRectMax();
                var drawList = ImGui.GetWindowDrawList();

                var bgAlpha     = isSelected ? 0.22f : isHovered ? 0.16f : 0.08f;
                var borderAlpha = isSelected ? 0.90f : isHovered ? 0.60f : 0.25f;
                var cardCol = isAvailable ?
                                  color :
                                  KnownColor.Gray.ToVector4();

                drawList.AddRectFilled(min, max, cardCol.WithW(bgAlpha).ToUInt(), rounding);
                drawList.AddRect
                (
                    min,
                    max,
                    cardCol.WithW(borderAlpha).ToUInt(),
                    rounding,
                    ImDrawFlags.None,
                    isSelected ?
                        1.5f * GlobalUIScale :
                        1f   * GlobalUIScale
                );

                var topY    = min.Y + (3f * GlobalUIScale);
                var bottomY = max.Y - (3f * GlobalUIScale);

                using (FontManager.Instance().UIFont60.Push())
                {
                    var headerText = $"{icon} {label}";
                    drawList.AddText
                    (
                        new Vector2(min.X + padX, topY),
                        cardCol.WithW
                        (
                            isAvailable ?
                                0.90f :
                                0.50f
                        ).ToUInt(),
                        headerText
                    );

                    if (isSelected)
                    {
                        var checkIcon = FontAwesomeIcon.Check.ToIconString();
                        var checkSize = ImGui.CalcTextSize(checkIcon);
                        drawList.AddText(new Vector2(max.X - padX - checkSize.X, topY), cardCol.ToUInt(), checkIcon);
                    }
                }

                using (FontManager.Instance().UIFont80.Push())
                {
                    var priceText = isAvailable ?
                                        Lang.Get
                                        (
                                            "BetterMarketBoard-Price-Format",
                                            new Dictionary<string, object>
                                            {
                                                ["price"] = price!.Value
                                            }
                                        ) :
                                        "-";
                    var priceCol = isAvailable ?
                                       isSelected ?
                                           cardCol.ToUInt() :
                                           ImGui.GetColorU32(ImGuiCol.Text) :
                                       ImGui.GetColorU32(ImGuiCol.TextDisabled);
                    var priceSize = ImGui.CalcTextSize(priceText);
                    drawList.AddText(new Vector2(max.X - padX - priceSize.X, bottomY - priceSize.Y), priceCol, priceText);
                }

                if (i % 2 == 0 && i + 1 < benchmarks.Count)
                    ImGui.SameLine();
            }
        }
    }
}
