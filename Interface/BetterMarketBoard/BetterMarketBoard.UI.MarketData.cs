using System.Globalization;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TimeAgo;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private void DrawRightContent
    (
        MarketBoardUIContext frame
    )
    {
        var info = InfoProxy;
        if (info == null || frame.ItemID == 0) return;
        if (!frame.HasItem) return;

        var itemData = frame.ItemData;

        var itemIcon = ITextureProvider.Instance().GetFromGameIcon(new(itemData.Icon, frame.HQOnly)).GetWrapOrDefault();
        if (itemIcon == null) return;

        using var font = FontManager.Instance().UIFont.Push();

        var isItemHovered = false;

        ImGui.Image(itemIcon.Handle, marketDataTableImageSize with { X = marketDataTableImageSize.Y });
        if (ImGui.IsItemHovered())
            isItemHovered = true;

        ImGui.SameLine();

        using (ImRaii.Group())
        {
            using (FontManager.Instance().UIFont160.Push())
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted
                (
                    itemData.Name.ToString() +
                    (frame.HQOnly ?
                         "\ue03c" :
                         string.Empty)
                );

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    isItemHovered = true;
                }

                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText($"{itemData.Name.ToString()}");
                    NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}: {itemData.Name.ToString()}");
                }
            }

            if (isItemHovered)
            {
                AtkStage.Instance()->ShowItemTooltip(ScreenText->RootNode, itemData.RowId);
                isHeaderTooltip = true;
            }
            else
            {
                if (isHeaderTooltip)
                {
                    isHeaderTooltip = false;
                    AtkStage.Instance()->HideTooltip(ScreenText->Id);
                }
            }

            using (ImRaii.Group())
            using (FontManager.Instance().UIFont80.Push())
            {
                var isFavorite = config.FavoriteItems.ContainsKey(frame.ItemID);

                if (ImGui.Button
                    (
                        isFavorite ?
                            "★" :
                            "☆"
                    ))
                {
                    if (isFavorite)
                        config.FavoriteItems.Remove(frame.ItemID);
                    else
                    {
                        config.FavoriteItems[frame.ItemID] = new MarketFavoriteItem
                        {
                            ItemID = frame.ItemID
                        };
                    }

                    favoriteItemsVersion++;
                    config.Save(this);
                }

                ImGuiOm.TooltipHover(Lang.Get("Favorite"));

                var isMonitor = config.CurrentMonitorItem?.ItemID == frame.ItemID;

                ImGui.SameLine();

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.GreenYellow.ToVector4(), isMonitor))
                {
                    if (ImGui.Button(FontAwesomeIcon.Bell.ToIconString()))
                    {
                        if (isMonitor)
                            monitorProvider.ClearMonitor();
                        else
                            monitorProvider.SetMonitor(frame.ItemID, frame.HQOnly);
                    }
                }

                ImGuiOm.TooltipHover
                (
                    Lang.Get
                    (
                        isMonitor ?
                            "BetterMarketBoard-PriceMonitor-ToNotMonitor" :
                            "BetterMarketBoard-PriceMonitor-ToMonitor"
                    )
                );

                if (itemData.CanBeHq)
                {
                    ImGui.SameLine();

                    using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.GreenYellow.ToVector4(), frame.HQOnly))
                    {
                        if (ImGui.Button("\ue03c###HQOnly"))
                            provider.ToggleHQ();
                    }

                    ImGuiOm.TooltipHover
                    (
                        frame.HQOnly ?
                            $"{Lang.Get("All")}" :
                            $"{Lang.Get("BetterMarketBoard-MarketView-HQ")}"
                    );
                }

                if (!frame.IsViewingCurrentWorld)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(FontAwesomeIcon.Plane.ToIconString()))
                        ChatManager.Instance().SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(frame.SelectedWorldID)}");
                    ImGuiOm.TooltipHover(Lang.Get("BetterMarketBoard-TravelToWorld"));
                }

                ImGui.SameLine();

                if (ImGui.Button(FontAwesomeIcon.Sync.ToIconString()))
                    provider.Reload();

                ImGuiOm.TooltipHover(Lang.Get("BetterMarketBoard-ReloadMarketData"));
            }
        }

        marketDataTableImageSize = ImGui.GetItemRectSize();

        DrawAllWorldsPriceTable(frame);

        using (FontManager.Instance().UIFont80.Push())
        using (var tab = ImRaii.TabBar("###BetterMarketBoardTabs", ImGuiTabBarFlags.NoTooltip))
        {
            if (tab)
            {
                using (var marketTab = ImRaii.TabItem($"{Lang.Get("BetterMarketBoard-MarketData")}##MarketData", ImGuiTabItemFlags.Leading))
                {
                    if (marketTab)
                        DrawMarketListingsTab(frame, info);
                }

                using (var historyTab = ImRaii.TabItem($"{LuminaWrapper.GetAddonText(1165)}##History"))
                {
                    if (historyTab)
                        DrawMarketHistoryTab(frame);
                }

                using (var trendTab = ImRaii.TabItem($"{Lang.Get("BetterMarketBoard-PriceTrend")}##PriceTrend"))
                {
                    if (trendTab)
                        DrawMarketPriceTrend(frame);
                }
            }
        }
    }

    private void DrawMarketListingsTab
    (
        MarketBoardUIContext frame,
        InfoProxyItemSearch* info
    )
    {
        var (dailySales, _, _) = provider.GetItemAggregatedStats(frame.ItemID, frame.SelectedWorldID, frame.HQOnly);
        var avgPrice = provider.GetHistoryDataSet(frame.ItemID)?.AvgPrice;

        if (frame is { IsLocalMarketSearchable: true, IsViewingCurrentWorld: true } && provider.SelectedListings.Count > 0)
        {
            var totalPrice = provider.SelectedListings.Values.Aggregate(0UL, (current, l) => current + ((l.UnitPrice * l.Quantity) + l.TotalTax));
            var totalCount = provider.SelectedListings.Values.Aggregate(0U,  (current, l) => current + l.Quantity);

            var availWidth   = ImGui.GetContentRegionAvail().X;
            var spacing      = ImGui.GetStyle().ItemSpacing.X;
            var metricsWidth = MathF.Floor((availWidth - spacing) * 0.52f);
            var buttonWidth  = availWidth - spacing - metricsWidth;

            float labelHeight;
            using (FontManager.Instance().UIFont60.Push())
                labelHeight = ImGui.GetTextLineHeight();

            float valueHeight;
            using (FontManager.Instance().UIFont80.Push())
                valueHeight = ImGui.GetTextLineHeight();

            var cardHeight = (6f * GlobalUIScale * 2) + labelHeight + valueHeight + (3f * GlobalUIScale);

            var selectedMetrics = new List<MetricItem>
            {
                new
                (
                    Lang.Get("Selected"),
                    Lang.Get("BetterMarketBoard-Samples-Format", provider.SelectedListings.Count, totalCount),
                    KnownColor.LightSkyBlue.ToVector4(),
                    null
                ),
                new(Lang.Get("BetterMarketBoard-BatchPurchase-Total"), $"{totalPrice.ToChineseString()}\ue049", KnownColor.Orange.ToVector4(), null)
            };

            DrawMetricDashboard(selectedMetrics, metricsWidth);

            ImGui.SameLine(0, spacing);

            using (FontManager.Instance().UIFont80.Push())
            {
                if (ImGuiOm.HoldButton
                    (
                        "##BatchPurchaseHoldButton",
                        $"{FontAwesomeIcon.ShoppingCart.ToIconString()}  {Lang.Get("BetterMarketBoard-BatchPurchase-Button")}",
                        null,
                        true,
                        new(buttonWidth, cardHeight)
                    ))
                {
                    var listingsToBuy = provider.SelectedListings.Values.ToList();
                    provider.ClearSelectedListings();

                    foreach (var listing in listingsToBuy)
                    {
                        TaskHelper.Enqueue(() => MarketDataProvider.SendBuyRequest(listing));
                        TaskHelper.Enqueue(() => info->Listings.ToArray().FirstOrDefault(x => x.ListingId == listing.ListingId).ListingId == 0);
                    }
                }
            }
        }
        else
        {
            if (InfoProxyItemSearch.IsListingsStuck)
            {
                // 请稍后再次确认。
                ImGui.TextColored(KnownColor.Orange.ToVector4(), $"（{LuminaWrapper.GetAddonText(1998)}）");
            }
            else
            {
                var isOnlineView  = !frame.IsViewingCurrentWorld || !frame.IsLocalMarketSearchable;
                var onlineDataSet = provider.GetListingsDataSet(frame.ItemID);

                var totalListings = 0;
                var totalQty      = 0U;

                if (isOnlineView)
                {
                    if (onlineDataSet != null)
                    {
                        totalListings = onlineDataSet.TotalCount;
                        totalQty      = onlineDataSet.TotalQty;
                    }
                }
                else
                {
                    var localDataSet = provider.GetLocalListingsDataSet(info);
                    totalListings = localDataSet.TotalCount;
                    totalQty      = localDataSet.TotalQty;
                }

                var itemCount = LocalPlayerState.GetItemCount(frame.ItemID);

                var metrics = new List<MetricItem>
                {
                    new
                    (
                        LuminaWrapper.GetAddonText(358),
                        $"{itemCount}",
                        itemCount > 0 ?
                            KnownColor.LightSkyBlue.ToVector4() :
                            KnownColor.White.ToVector4(),
                        null
                    )
                };

                if (totalListings > 0)
                {
                    metrics.Add
                    (
                        new
                        (
                            Lang.Get("BetterMarketBoard-InStock"),
                            Lang.Get("BetterMarketBoard-Samples-Format", totalListings, totalQty),
                            KnownColor.LightSkyBlue.ToVector4(),
                            null
                        )
                    );
                }

                if (dailySales > 0)
                {
                    metrics.Add
                    (
                        new
                        (
                            Lang.Get("BetterMarketBoard-Tooltip-DailySales"),
                            Lang.Get("BetterMarketBoard-DailySales-Format", dailySales.ToString("0.#", CultureInfo.InvariantCulture)),
                            KnownColor.GreenYellow.ToVector4(),
                            null
                        )
                    );

                    if (totalQty > 0)
                    {
                        var turnoverDays = totalQty / dailySales;
                        metrics.Add
                        (
                            new
                            (
                                Lang.Get("BetterMarketBoard-Turnover"),
                                Lang.Get("BetterMarketBoard-Turnover-Format", turnoverDays.ToString("0.#", CultureInfo.InvariantCulture)),
                                KnownColor.Goldenrod.ToVector4(),
                                Lang.Get("BetterMarketBoard-Turnover-Help")
                            )
                        );
                    }
                }

                if (isOnlineView && onlineDataSet?.Source != null)
                {
                    var updateTime = onlineDataSet.Source.GetLastUploadTime().ToLocalTime();
                    metrics.Add
                    (
                        new
                        (
                            Lang.Get("BetterMarketBoard-DataFreshness"),
                            updateTime.TimeAgo(),
                            KnownColor.LightSkyBlue.ToVector4(),
                            $"{updateTime:yyyy-MM-dd HH:mm:ss}"
                        )
                    );
                }

                DrawMetricDashboard(metrics);

                if (isOnlineView && onlineDataSet == null)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(KnownColor.Gray.ToVector4(), $"\ue031 {LuminaWrapper.GetAddonText(2717)}");
                }
            }
        }

        ImGui.Spacing();

        using (FontManager.Instance().UIFont80.Push())
        {
            if (frame.IsViewingCurrentWorld)
            {
                if (frame.IsLocalMarketSearchable)
                    DrawLocalMarketDataTable(frame, info, avgPrice);
                else if (provider.GetListingsDataSet(frame.ItemID) is { } onlineOne)
                    DrawOnlineMarketDataTable(frame, avgPrice, onlineOne);
            }
            else if (provider.GetListingsDataSet(frame.ItemID) is { } onlineTwo)
                DrawOnlineMarketDataTable(frame, avgPrice, onlineTwo);
        }
    }

    private void DrawMarketHistoryTab
    (
        MarketBoardUIContext frame
    )
    {
        var historyItemID = frame.ItemID;
        if (historyItemID == 0) return;

        var data = provider.GetHistoryDataSet(historyItemID);

        if (data == null || data.Entries.Count == 0)
        {
            ImGui.TextDisabled(LuminaWrapper.GetAddonText(1998));
            return;
        }

        var (dailySales, _, recentPurchase) = provider.GetItemAggregatedStats(historyItemID, frame.SelectedWorldID, frame.HQOnly);

        var recentText = "-";
        if (recentPurchase != null)
            recentText = recentPurchase.Value.Time.TimeAgo();
        else if (data.Entries.Count > 0)
            recentText = data.Entries[0].SaleTime.TimeAgo();

        var metrics = new List<MetricItem>
        {
            new
            (
                Lang.Get("BetterMarketBoard-History-Samples"),
                Lang.Get("BetterMarketBoard-Samples-Format", data.TotalCount, data.TotalQty),
                KnownColor.LightSkyBlue.ToVector4(),
                null
            )
        };

        if (data.IsCanBeHQ)
        {
            if (data.AvgNQPrice > 0)
            {
                metrics.Add
                    (new(Lang.Get("BetterMarketBoard-AveragePrice-NQ"), $"{data.AvgNQPrice.ToChineseString()}\ue049", KnownColor.LightSkyBlue.ToVector4(), null));
            }

            if (data.AvgHQPrice > 0)
                metrics.Add(new(Lang.Get("BetterMarketBoard-AveragePrice-HQ"), $"{data.AvgHQPrice.ToChineseString()}\ue049", KnownColor.Orange.ToVector4(), null));

            metrics.Add
                (new(Lang.Get("BetterMarketBoard-HQRatio"), Lang.Get("BetterMarketBoard-Percent-Format", data.HQPercent), KnownColor.Goldenrod.ToVector4(), null));
        }
        else
            metrics.Add(new(Lang.Get("BetterMarketBoard-AveragePrice"), $"{data.AvgPrice.ToChineseString()}\ue049", KnownColor.Orange.ToVector4(), null));

        if (dailySales > 0)
        {
            metrics.Add
            (
                new
                (
                    Lang.Get("BetterMarketBoard-Tooltip-DailySales"),
                    Lang.Get("BetterMarketBoard-DailySales-Format", dailySales.ToString("0.#", CultureInfo.InvariantCulture)),
                    KnownColor.GreenYellow.ToVector4(),
                    null
                )
            );
        }

        metrics.Add(new(Lang.Get("BetterMarketBoard-History-Recent"), recentText, KnownColor.White.ToVector4(), null));

        DrawMetricDashboard(metrics);
        ImGui.Spacing();

        DrawOnlineMarketHistoryDataTable(data);
    }

    private void DrawLocalMarketDataTable
    (
        MarketBoardUIContext frame,
        InfoProxyItemSearch* info,
        ulong?               avgPrice
    )
    {
        var dataset       = provider.GetLocalListingsDataSet(info);
        var listingsArray = dataset.Listings;

        var isAnyHQ              = dataset.IsAnyHQ;
        var isAnyOnMannequin     = dataset.IsAnyOnMannequin;
        var isAnyMateriaEquipped = dataset.IsAnyMateria;

        var columnsCount = 9;
        if (!isAnyHQ)
            columnsCount--;
        if (!isAnyMateriaEquipped)
            columnsCount--;
        if (!isAnyOnMannequin)
            columnsCount--;

        using var table = ImRaii.Table
            ("MarketBoardDataTable", columnsCount, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new(-1, ImGui.GetContentRegionAvail().Y));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);

        ImGui.TableSetupColumn(Lang.Get("SelectAll"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetTextLineHeight() + (ImGui.GetStyle().ItemSpacing.X * 2));

        if (isAnyHQ)
            ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

        if (isAnyMateriaEquipped)
        {
            var materiaText = LuminaWrapper.GetAddonText(1937);
            ImGui.TableSetupColumn(materiaText, ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(materiaText).X);
        }

        if (isAnyOnMannequin)
            ImGui.TableSetupColumn(Lang.Get("Mannequin"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Lang.Get("Mannequin")).X);

        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(Lang.Get("Tax"),                  ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("12345678").X);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1956), ImGuiTableColumnFlags.WidthStretch, 15);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        ImGui.TableNextColumn();

        using (ImRaii.Disabled(listingsArray.Count == 0))
        {
            var selectableListings = listingsArray.Where(x => !IsOwnRetainer(x.RetainerId)).ToList();
            var selectedCount      = selectableListings.Count(x => provider.SelectedListings.ContainsKey(x.ListingId));
            var allSelected        = selectableListings.Count > 0 && selectedCount == selectableListings.Count;

            var decoBool = allSelected;
            ImGui.Checkbox("###SelectAll", ref decoBool);

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                if (!allSelected)
                {
                    foreach (var l in selectableListings)
                        provider.AddListing(l.ListingId, l);
                }
                else
                {
                    foreach (var l in selectableListings)
                        provider.RemoveListing(l.ListingId);
                }
            }
            else if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                foreach (var l in selectableListings)
                    provider.ToggleListing(l.ListingId, l);
            }

            ImGuiOm.TooltipHover
            (
                $"{Lang.Get("LeftClick")}：{Lang.Get("SelectAll")}/{Lang.Get("DeselectAll")}\n" +
                $"{Lang.Get("RightClick")}：{Lang.Get("InvertSelection")}"
            );
        }

        if (isAnyHQ)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("\ue03c");
        }

        if (isAnyMateriaEquipped)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(LuminaWrapper.GetAddonText(1937));
        }

        if (isAnyOnMannequin)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Lang.Get("Mannequin"));
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(357));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Tax"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Lang.Get("Amount"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(6936));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(LuminaWrapper.GetAddonText(1956));

        var counter    = -1;
        var benchmarks = BuildMarketBenchmarks(frame.ItemID, avgPrice, frame.NPCGilPrice);

        foreach (var listing in listingsArray)
        {
            foreach (var b in benchmarks)
            {
                if (!b.Drawn && listing.UnitPrice > b.Price)
                {
                    DrawBenchmarkSeparatorRow($"LocalBenchmark_{b.Price}_{b.Badge}", b.Badge, b.Color, b.Tooltip, b.OnClick);
                    b.Drawn = true;
                }
            }

            counter++;

            using var id = ImRaii.PushId(listing.ListingId.ToString());
            ImGui.TableNextRow();

            var isOwnRetainer = IsOwnRetainer(listing.RetainerId);
            using var rowColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), isOwnRetainer);

            ImGui.TableNextColumn();
            var isSelected = provider.SelectedListings.ContainsKey(listing.ListingId);

            using (ImRaii.Disabled(isOwnRetainer))
            {
                if (ImGui.Checkbox("###SelectCheckbox", ref isSelected))
                {
                    if (isSelected)
                        provider.ToggleListing(listing.ListingId, listing);
                    else
                        provider.RemoveListing(listing.ListingId);
                }
            }

            if (isAnyHQ)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted
                (
                    listing.IsHqItem ?
                        "\u221a" :
                        string.Empty
                );
            }

            if (isAnyMateriaEquipped)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{listing.MateriaCount}");
            }

            if (isAnyOnMannequin)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted
                (
                    listing.IsMannequin ?
                        "\u221a" :
                        string.Empty
                );
            }

            ImGui.TableNextColumn();
            DrawMarketPrice(listing.UnitPrice);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted
            (
                listing.TotalTax == 0 ?
                    "-" :
                    $"{listing.TotalTax.ToChineseString()}\ue049"
            );
            ImGuiOm.ClickToCopyAndNotify(listing.TotalTax.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.Quantity}");

            var totalPrice = (listing.UnitPrice * listing.Quantity) + listing.TotalTax;
            ImGui.TableNextColumn();

            using (ImRaii.Disabled(isOwnRetainer))
            {
                if (ImGui.Selectable
                    (
                        $"{totalPrice.ToChineseString()}\ue049",
                        ImGui.IsPopupOpen($"ExecuteBuyPopup_{listing.ListingId}"),
                        ImGuiSelectableFlags.SpanAllColumns
                    ))
                {
                    if (isSelected)
                        provider.RemoveListing(listing.ListingId);
                    else
                        provider.ToggleListing(listing.ListingId, listing);
                }
            }

            if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
            {
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !isOwnRetainer)
                    MarketDataProvider.SendBuyRequest(listing);
            }
            else if (!isOwnRetainer)
            {
                using var popup = ImRaii.ContextPopupItem($"ExecuteBuyPopup_{listing.ListingId}");

                if (popup)
                {
                    ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(357)}:");

                    ImGui.SameLine();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{listing.UnitPrice.ToChineseString()}\ue049");

                    ImGui.TextUnformatted($"{Lang.Get("Amount")}:");

                    ImGui.SameLine();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{listing.Quantity}");

                    ImGui.TextUnformatted($"{LuminaWrapper.GetAddonText(6936)}:");

                    ImGui.SameLine();
                    ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{totalPrice.ToChineseString()}\ue049");

                    if (listing.TotalTax > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"({Lang.Get("Tax")}: {listing.TotalTax.ToChineseString()}\ue049)");
                    }

                    ImGui.Separator();
                    ImGui.Spacing();

                    if (ImGui.MenuItem(LuminaWrapper.GetAddonText(9275)))
                        MarketDataProvider.SendBuyRequest(listing);
                    ImGuiOm.TooltipHover(Lang.Get("BetterMarketBoard-Purchase-Help", PluginConfig.Instance().ConflictKeyBinding));
                }
            }

            ImGui.TableNextColumn();
            var retainerName = AtkStage.Instance()->GetStringArrayData(StringArrayType.ItemSearch)->StringArray[208 + (6 * counter)];
            if (retainerName.HasValue)
                ImGui.TextUnformatted($"{retainerName.ToString()}");
        }

        foreach (var b in benchmarks)
        {
            if (!b.Drawn)
            {
                DrawBenchmarkSeparatorRow($"LocalBenchmark_End_{b.Price}_{b.Badge}", b.Badge, b.Color, b.Tooltip, b.OnClick);
                b.Drawn = true;
            }
        }
    }

    private static void DrawOnlineMarketDataTable
    (
        MarketBoardUIContext frame,
        ulong?               avgPrice,
        ListingsDataSet      dataset
    )
    {
        if (dataset.Listings.Count == 0) return;

        var isAnyHQ          = dataset.IsAnyHQ;
        var isAnyOnMannequin = dataset.IsAnyOnMannequin;

        var columnsCount = 6;
        if (!isAnyHQ)
            columnsCount--;
        if (!isAnyOnMannequin)
            columnsCount--;

        using var table = ImRaii.Table
            ("UniversalisMarketDataTable", columnsCount, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new(-1, ImGui.GetContentRegionAvail().Y));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);

        if (isAnyHQ)
            ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

        if (isAnyOnMannequin)
            ImGui.TableSetupColumn(Lang.Get("Mannequin"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Lang.Get("Mannequin")).X);

        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("12345678").X);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(6936), ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1956), ImGuiTableColumnFlags.WidthStretch, 15);

        ImGui.TableHeadersRow();

        var benchmarks = BuildMarketBenchmarks(frame.ItemID, avgPrice, frame.NPCGilPrice);

        foreach (var listing in dataset.Listings)
        {
            foreach (var b in benchmarks)
            {
                if (!b.Drawn && listing.PricePerUnit > b.Price)
                {
                    DrawBenchmarkSeparatorRow($"OnlineBenchmark_{b.Price}_{b.Badge}", b.Badge, b.Color, b.Tooltip, b.OnClick);
                    b.Drawn = true;
                }
            }

            using var id = ImRaii.PushId($"{listing.ListingID}");
            ImGui.TableNextRow();

            if (isAnyHQ)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted
                (
                    listing.HQ ?
                        "√" :
                        string.Empty
                );
            }

            if (isAnyOnMannequin)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted
                (
                    listing.OnMannequin ?
                        "√" :
                        string.Empty
                );
            }

            ImGui.TableNextColumn();
            DrawMarketPrice(listing.PricePerUnit);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.Quantity}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.Total.ToChineseString()}\ue049");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{listing.RetainerName}");
        }

        foreach (var b in benchmarks)
        {
            if (!b.Drawn)
            {
                DrawBenchmarkSeparatorRow($"OnlineBenchmark_End_{b.Price}_{b.Badge}", b.Badge, b.Color, b.Tooltip, b.OnClick);
                b.Drawn = true;
            }
        }
    }

    private static void DrawOnlineMarketHistoryDataTable
    (
        HistoryDataSet data
    )
    {
        var entries = data.Entries;

        var isAnyHQ = data.IsAnyHQ;

        var columnsCount = 4;
        if (!isAnyHQ)
            columnsCount--;

        using var table = ImRaii.Table
            ("UniversalisHistoryDataTable", columnsCount, ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new(-1, ImGui.GetContentRegionAvail().Y));
        if (!table) return;

        ImGui.TableSetupScrollFreeze(0, 1);

        if (isAnyHQ)
            ImGui.TableSetupColumn("\ue03c", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("\ue03c").X);

        ImGui.TableSetupColumn(Lang.Get("Amount"),               ImGuiTableColumnFlags.WidthFixed,   ImGui.CalcTextSize("12345678").X);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(357),  ImGuiTableColumnFlags.WidthStretch, 15);
        ImGui.TableSetupColumn(LuminaWrapper.GetAddonText(1976), ImGuiTableColumnFlags.WidthStretch, 15);

        ImGui.TableHeadersRow();

        foreach (var sale in entries)
        {
            if (sale.PricePerUnit == 0) continue;

            using var id = ImRaii.PushId($"{sale.SaleTime.Ticks}-{sale.PricePerUnit}-{sale.Quantity}");
            ImGui.TableNextRow();

            if (isAnyHQ)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted
                (
                    sale.IsHQ ?
                        "√" :
                        string.Empty
                );
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{sale.Quantity}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{sale.PricePerUnit.ToChineseString()}\ue049");
            ImGuiOm.ClickToCopyAndNotify(sale.PricePerUnit.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(sale.SaleTime.ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }
}
