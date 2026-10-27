using System.Globalization;
using System.Numerics;
using DailyRoutines.Common.RemoteInteraction.Enums;
using DailyRoutines.Common.Interface.Widgets;
using Dalamud.Utility.Numerics;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TimeAgo;

namespace DailyRoutines.ModulesPublic.Interface;

public partial class BetterMarketBoard
{
    private readonly Dictionary<uint, WorldPriceCardComponent> worldPriceCards = [];

    private void DrawAllWorldsPriceTable
    (
        MarketBoardUIContext frame
    )
    {
        if (allWorlds.Count == 0)
        {
            var statusText = string.IsNullOrEmpty(catalogRequestError) ?
                                 $"\ue031 {LuminaWrapper.GetAddonText(2717)}" :
                                 Lang.Get("BetterMarketBoard-CatalogRequestFailed");
            ImGui.TextDisabled(statusText);

            if (!string.IsNullOrEmpty(catalogRequestError))
                ImGuiOm.TooltipHover(catalogRequestError);

            return;
        }

        var ranks             = provider.GetWorldPriceRanks(frame.ItemID);
        var displayRegionName = provider.EffectiveRegionName;

        DrawAllWorldPricesToggleComponent
        (
            isAllWorldsPriceExpanded ?
                ImGuiDir.Up :
                ImGuiDir.Down
        );

        if (ranks == null || ranks.Valid.Count == 0)
        {
            if (frame.ItemID != 0 && !string.IsNullOrEmpty(displayRegionName))
            {
                ImGui.Spacing();
                var status = provider.GetAggregatedRequestStatus();
                var statusText = status switch
                {
                    RemoteSnapshotStatus.Failed => Lang.Get("BetterMarketBoard-Tooltip-RequestFailed"),
                    RemoteSnapshotStatus.Ready  => LuminaWrapper.GetAddonText(1998),
                    _                           => $"\ue031 {LuminaWrapper.GetAddonText(2717)}"
                };

                using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Gray.ToUInt()))
                {
                    ImGuiOm.TextCentered(statusText);
                    if (status is RemoteSnapshotStatus.Failed &&
                        !string.IsNullOrWhiteSpace(provider.GetAggregatedRequestError()))
                        ImGuiOm.TooltipHover(provider.GetAggregatedRequestError());
                }
            }

            return;
        }

        if (isAllWorldsPriceExpanded)
        {
            DrawFullPriceTable(frame);
            return;
        }

        DrawPriceOverviewTable(frame, ranks.Current.MinPrice, ranks.Expensive, ranks.Cheapest);
    }

    private enum OverviewCardType
    {
        Expensive,
        Cheapest,
        SingleOnly
    }

    private void DrawPriceOverviewTable
    (
        MarketBoardUIContext               frame,
        ulong                              currentWorldPrice,
        IReadOnlyList<RankedWorldPriceRow> expensiveWorlds,
        IReadOnlyList<RankedWorldPriceRow> cheapestWorlds
    )
    {
        var totalValidCount = expensiveWorlds.Count + cheapestWorlds.Count;
        if (totalValidCount == 0) return;

        using var font = FontManager.Instance().UIFont80.Push();

        var availWidth = ImGui.GetContentRegionAvail().X;
        var spacingX   = 4f * GlobalUIScale;
        var centerGap  = 8f * GlobalUIScale;

        float font60Height;
        using (FontManager.Instance().UIFont60.Push())
            font60Height = ImGui.GetTextLineHeight();

        float font80Height;
        using (FontManager.Instance().UIFont80.Push())
            font80Height = ImGui.GetTextLineHeight();

        var padY       = 3.5f * GlobalUIScale;
        var lineGap    = 2f   * GlobalUIScale;
        var cardHeight = (padY * 2f) + (font60Height * 2f) + font80Height + (lineGap * 2f);

        // 如果全区仅 1 个服务器有在售
        if (cheapestWorlds.Count == 0 && expensiveWorlds.Count == 1)
        {
            var singleWorld     = expensiveWorlds[0];
            var singleCardWidth = MathF.Min(240f                               * GlobalUIScale, availWidth);
            var centerOffset    = MathF.Max(0f, (availWidth - singleCardWidth) / 2f);

            if (centerOffset > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + centerOffset);

            DrawOverviewPriceCard
            (
                frame,
                singleWorld,
                OverviewCardType.SingleOnly,
                1,
                new Vector2(singleCardWidth, cardHeight),
                currentWorldPrice
            );
            ImGui.Spacing();
            return;
        }

        var totalSpacing = (4f * spacingX) + centerGap;
        var cardWidth    = MathF.Max(30f * GlobalUIScale, (availWidth - totalSpacing) / 6f);

        var expCount = expensiveWorlds.Count;

        for (var slot = 0; slot < 3; slot++)
        {
            var itemIndex = slot - (3 - expCount);
            if (slot > 0)
                ImGui.SameLine(0, spacingX);

            if (itemIndex >= 0 && itemIndex < expCount)
            {
                var world = expensiveWorlds[itemIndex];
                var rank  = expCount - itemIndex;
                DrawOverviewPriceCard
                (
                    frame,
                    world,
                    OverviewCardType.Expensive,
                    rank,
                    new Vector2(cardWidth, cardHeight),
                    currentWorldPrice
                );
            }
            else
                DrawOverviewEmptySlot(new Vector2(cardWidth, cardHeight));
        }

        var cheapCount = cheapestWorlds.Count;

        for (var slot = 0; slot < 3; slot++)
        {
            ImGui.SameLine
            (
                0,
                slot == 0 ?
                    centerGap :
                    spacingX
            );

            if (slot < cheapCount)
            {
                var world = cheapestWorlds[slot];
                var rank  = slot + 1;
                DrawOverviewPriceCard
                (
                    frame,
                    world,
                    OverviewCardType.Cheapest,
                    rank,
                    new Vector2(cardWidth, cardHeight),
                    currentWorldPrice
                );
            }
            else
                DrawOverviewEmptySlot(new Vector2(cardWidth, cardHeight));
        }

        ImGui.Spacing();
    }

    private void DrawOverviewPriceCard
    (
        MarketBoardUIContext frame,
        RankedWorldPriceRow  world,
        OverviewCardType     cardType,
        int                  rank,
        Vector2              cardSize,
        ulong                currentWorldPrice
    )
    {
        var isCurrentWorld = world.WorldID == GameState.CurrentWorld;
        var isSelected = frame.IsViewingCurrentWorld ?
                             isCurrentWorld :
                             world.WorldID == frame.SelectedWorldID;
        var isRank1 = rank == 1;

        var startPos = ImGui.GetCursorScreenPos();
        var maxPos   = startPos + cardSize;
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 4f * GlobalUIScale;

        if (ImGui.InvisibleButton($"##OverviewCard_{world.WorldID}", cardSize))
            provider.SelectWorld(world.WorldID);

        var isHovered = ImGui.IsItemHovered();

        if (isHovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (!isCurrentWorld && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ChatManager.Instance().SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(world.WorldID)}");

        Vector4 bgColor;
        Vector4 borderColor;
        Vector4 priceColor;

        if (isSelected)
        {
            bgColor = KnownColor.DeepSkyBlue.ToVector4().WithW
            (
                isHovered ?
                    0.30f :
                    0.20f
            );
            borderColor = KnownColor.DeepSkyBlue.ToVector4().WithW(0.90f);
            priceColor  = KnownColor.DeepSkyBlue.ToVector4();
        }
        else if (isCurrentWorld)
        {
            bgColor = KnownColor.DeepPink.ToVector4().WithW
            (
                isHovered ?
                    0.25f :
                    0.16f
            );
            borderColor = KnownColor.Pink.ToVector4().WithW
            (
                isHovered ?
                    0.95f :
                    0.75f
            );
            priceColor = KnownColor.LightPink.ToVector4();
        }
        else
        {
            switch (cardType)
            {
                case OverviewCardType.Expensive:
                {
                    if (isRank1)
                    {
                        bgColor = KnownColor.OrangeRed.ToVector4().WithW
                        (
                            isHovered ?
                                0.28f :
                                0.18f
                        );
                        borderColor = KnownColor.OrangeRed.ToVector4().WithW
                        (
                            isHovered ?
                                0.90f :
                                0.70f
                        );
                        priceColor = KnownColor.OrangeRed.ToVector4();
                    }
                    else if (rank == 2)
                    {
                        bgColor = KnownColor.IndianRed.ToVector4().WithW
                        (
                            isHovered ?
                                0.18f :
                                0.10f
                        );
                        borderColor = KnownColor.Salmon.ToVector4().WithW
                        (
                            isHovered ?
                                0.70f :
                                0.45f
                        );
                        priceColor = KnownColor.Salmon.ToVector4();
                    }
                    else
                    {
                        bgColor = KnownColor.IndianRed.ToVector4().WithW
                        (
                            isHovered ?
                                0.12f :
                                0.06f
                        );
                        borderColor = KnownColor.LightSalmon.ToVector4().WithW
                        (
                            isHovered ?
                                0.50f :
                                0.25f
                        );
                        priceColor = KnownColor.LightSalmon.ToVector4();
                    }

                    break;
                }
                case OverviewCardType.Cheapest:
                {
                    if (isRank1)
                    {
                        bgColor = KnownColor.ForestGreen.ToVector4().WithW
                        (
                            isHovered ?
                                0.35f :
                                0.22f
                        );
                        borderColor = KnownColor.GreenYellow.ToVector4().WithW
                        (
                            isHovered ?
                                0.95f :
                                0.75f
                        );
                        priceColor = KnownColor.GreenYellow.ToVector4();
                    }
                    else if (rank == 2)
                    {
                        bgColor = KnownColor.ForestGreen.ToVector4().WithW
                        (
                            isHovered ?
                                0.20f :
                                0.12f
                        );
                        borderColor = KnownColor.LimeGreen.ToVector4().WithW
                        (
                            isHovered ?
                                0.70f :
                                0.45f
                        );
                        priceColor = KnownColor.LimeGreen.ToVector4();
                    }
                    else
                    {
                        bgColor = KnownColor.ForestGreen.ToVector4().WithW
                        (
                            isHovered ?
                                0.14f :
                                0.07f
                        );
                        borderColor = KnownColor.PaleGreen.ToVector4().WithW
                        (
                            isHovered ?
                                0.50f :
                                0.25f
                        );
                        priceColor = KnownColor.PaleGreen.ToVector4();
                    }

                    break;
                }
                case OverviewCardType.SingleOnly:
                default:
                {
                    bgColor = ImGui.GetColorU32(ImGuiCol.FrameBg).ToVector4().WithW
                    (
                        isHovered ?
                            0.50f :
                            0.35f
                    );
                    borderColor = KnownColor.LightSkyBlue.ToVector4().WithW
                    (
                        isHovered ?
                            0.80f :
                            0.45f
                    );
                    priceColor = KnownColor.White.ToVector4();
                    break;
                }
            }
        }

        drawList.AddRectFilled(startPos, maxPos, bgColor.ToUInt(), rounding);
        drawList.AddRect
        (
            startPos,
            maxPos,
            borderColor.ToUInt(),
            rounding,
            ImDrawFlags.None,
            isSelected || (isRank1 && isHovered) ?
                1.5f * GlobalUIScale :
                1f   * GlobalUIScale
        );

        var topHighlight = KnownColor.White.ToVector4().WithW
        (
            isHovered ?
                0.18f :
                0.06f
        ).ToUInt();
        drawList.AddLine
        (
            new(startPos.X + rounding, startPos.Y + 0.5f),
            new(maxPos.X   - rounding, startPos.Y + 0.5f),
            topHighlight,
            1f
        );

        var padX    = 4f   * GlobalUIScale;
        var padY    = 3.5f * GlobalUIScale;
        var lineGap = 2f   * GlobalUIScale;

        float font60H;
        using (FontManager.Instance().UIFont60.Push())
            font60H = ImGui.GetTextLineHeight();

        var line1Y = startPos.Y + padY;
        var line2Y = line1Y     + font60H + lineGap;
        var line3Y = line2Y     + font60H + lineGap;

        using (FontManager.Instance().UIFont60.Push())
        {
            string badgeText;
            uint   badgeColor;

            if (isCurrentWorld)
            {
                badgeText  = Lang.Get("BetterMarketBoard-Home");
                badgeColor = KnownColor.Pink.ToUInt();
            }
            else
            {
                switch (cardType)
                {
                    case OverviewCardType.Expensive:
                        badgeText = isRank1 ?
                                        Lang.Get("Highest") :
                                        $"#{rank}";
                        badgeColor = isRank1 ?
                                         KnownColor.OrangeRed.ToUInt() :
                                         ImGui.GetColorU32(ImGuiCol.TextDisabled);
                        break;
                    case OverviewCardType.Cheapest:
                        badgeText = isRank1 ?
                                        Lang.Get("Lowest") :
                                        $"#{rank}";
                        badgeColor = isRank1 ?
                                         KnownColor.GreenYellow.ToUInt() :
                                         ImGui.GetColorU32(ImGuiCol.TextDisabled);
                        break;
                    case OverviewCardType.SingleOnly:
                    default:
                        badgeText  = Lang.Get("BetterMarketBoard-OnlyOne");
                        badgeColor = KnownColor.LightSkyBlue.ToUInt();
                        break;
                }
            }

            var badgeSize = ImGui.CalcTextSize(badgeText);
            var badgePos  = new Vector2(maxPos.X - padX - badgeSize.X, line1Y);
            drawList.AddText(badgePos, badgeColor, badgeText);

            var dcText = world.DCName;
            var dcPos  = new Vector2(startPos.X + padX, line1Y);

            drawList.PushClipRect(startPos, new Vector2(badgePos.X - (2f * GlobalUIScale), line2Y), true);
            drawList.AddText(dcPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), dcText);
            drawList.PopClipRect();
        }

        using (FontManager.Instance().UIFont60.Push())
        {
            var nameText  = world.WorldName;
            var nameSize  = ImGui.CalcTextSize(nameText);
            var nameColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var namePos   = new Vector2(startPos.X + MathF.Max(padX, (cardSize.X - nameSize.X) / 2f), line2Y);

            drawList.PushClipRect(startPos, new Vector2(maxPos.X - padX, line3Y), true);
            drawList.AddText(namePos, nameColor, nameText);
            drawList.PopClipRect();
        }

        var priceText = world.MinPrice == ulong.MaxValue ?
                            "-" :
                            $"{world.MinPrice.ToChineseString()}\ue049";

        using (FontManager.Instance().UIFont80.Push())
        {
            var priceSize = ImGui.CalcTextSize(priceText);
            var pricePos  = new Vector2(startPos.X + MathF.Max(padX, (cardSize.X - priceSize.X) / 2f), line3Y);

            drawList.PushClipRect(startPos, maxPos, true);
            drawList.AddText(pricePos, priceColor.ToUInt(), priceText);
            drawList.PopClipRect();
        }

        if (isHovered)
            DrawWorldPriceTooltip(frame, world.WorldID, world.WorldName, world.MinPrice, currentWorldPrice);
    }

    private static void DrawOverviewEmptySlot
    (
        Vector2 slotSize
    )
    {
        var startPos = ImGui.GetCursorScreenPos();
        var maxPos   = startPos + slotSize;
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 4f * GlobalUIScale;

        ImGui.Dummy(slotSize);

        var bgColor     = ImGui.GetColorU32(ImGuiCol.FrameBg, 0.15f);
        var borderColor = ImGui.GetColorU32(ImGuiCol.Border,  0.12f);

        drawList.AddRectFilled(startPos, maxPos, bgColor, rounding);
        drawList.AddRect(startPos, maxPos, borderColor, rounding, ImDrawFlags.None, 1f * GlobalUIScale);

        using (FontManager.Instance().UIFont60.Push())
        {
            var dashSize = ImGui.CalcTextSize("-");
            var dashPos  = new Vector2(startPos.X + ((slotSize.X - dashSize.X) / 2f), startPos.Y + ((slotSize.Y - dashSize.Y) / 2f));
            drawList.AddText(dashPos, ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.3f), "-");
        }
    }

    private void DrawWorldPriceTooltip
    (
        MarketBoardUIContext frame,
        uint                 worldID,
        string               worldName,
        ulong                minPrice,
        ulong                currentWorldPrice
    )
    {
        var isCurrentWorld = worldID  == GameState.CurrentWorld;
        var hasNoListing   = minPrice == ulong.MaxValue;

        using (ImRaii.Tooltip())
        {
            var titleText = isCurrentWorld ?
                                $"{worldName} ({Lang.Get("BetterMarketBoard-Tooltip-CurrentWorld")})" :
                                worldName;

            ImGui.TextColored
            (
                isCurrentWorld ?
                    KnownColor.Pink.ToVector4() :
                    KnownColor.LightSkyBlue.ToVector4(),
                titleText
            );
            ImGui.Separator();

            if (hasNoListing)
                ImGui.TextDisabled("-");
            else
            {
                ImGui.TextUnformatted($"{minPrice.ToChineseString()}\ue049");

                if (!isCurrentWorld && currentWorldPrice != 0 && currentWorldPrice != ulong.MaxValue)
                {
                    var diff        = (long)minPrice - (long)currentWorldPrice;
                    var diffPercent = (double)diff / currentWorldPrice * 100.0;

                    switch (diff)
                    {
                        case < 0:
                        {
                            var absDiff = (ulong)-diff;
                            ImGui.TextColored
                            (
                                KnownColor.GreenYellow.ToVector4(),
                                $"-{absDiff.ToChineseString()}\ue049 (-{Math.Abs(diffPercent):F1}%)"
                            );
                            break;
                        }
                        case > 0:
                        {
                            var absDiff = (ulong)diff;
                            ImGui.TextColored
                            (
                                KnownColor.OrangeRed.ToVector4(),
                                $"+{absDiff.ToChineseString()}\ue049 (+{diffPercent:F1}%)"
                            );
                            break;
                        }
                    }
                }
            }

            if (provider.GetAggregatedResponse(frame.ItemID, worldID) is { } aggResponse)
            {
                var aggResult = aggResponse.Results.FirstOrDefault(r => r.ItemID == frame.ItemID);

                if (aggResult != null)
                {
                    var scope               = GetAggregatedMarketScope(aggResult, frame.HQOnly);
                    var worldSales          = scope.DailySaleVelocity.World.Quantity ?? 0;
                    var worldAvgPrice       = scope.AverageSalePrice.World.Price;
                    var worldRecentPurchase = scope.RecentPurchase.World;

                    ImGui.Separator();

                    if (worldSales > 0)
                    {
                        ImGui.TextDisabled($"{Lang.Get("BetterMarketBoard-Tooltip-DailySales")}: ");
                        ImGui.SameLine();
                        ImGui.TextColored
                        (
                            KnownColor.LightSkyBlue.ToVector4(),
                            Lang.Get("BetterMarketBoard-DailySales-Format", worldSales.ToString("0.#", CultureInfo.InvariantCulture))
                        );
                    }

                    if (worldAvgPrice is > 0)
                    {
                        ImGui.TextDisabled($"{Lang.Get("BetterMarketBoard-AveragePrice")}: ");
                        ImGui.SameLine();
                        ImGui.TextColored(KnownColor.Orange.ToVector4(), $"{((ulong)Math.Round(worldAvgPrice.Value)).ToChineseString()}\ue049");
                    }

                    if (worldRecentPurchase is { Price: > 0, Timestamp: > 0 })
                    {
                        var timeAgoText = DateTimeOffset.FromUnixTimeMilliseconds(worldRecentPurchase.Timestamp.Value).LocalDateTime.TimeAgo();
                        ImGui.TextDisabled($"{Lang.Get("BetterMarketBoard-RecentPurchase")}: ");
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{((ulong)Math.Round(worldRecentPurchase.Price.Value)).ToChineseString()}\ue049 ({timeAgoText})");
                    }
                }
            }

            if (!isCurrentWorld)
            {
                ImGui.Separator();

                using (FontManager.Instance().UIFont60.Push())
                    ImGui.TextDisabled($"{Lang.Get("RightClick")}：{Lang.Get("BetterMarketBoard-TravelToWorld")}");
            }
        }
    }

    private void DrawAllWorldPricesToggleComponent
    (
        ImGuiDir direction
    )
    {
        using var font = FontManager.Instance().UIFont80.Push();

        var regionSelectorWidth = 150f * GlobalUIScale;

        var cursorPos = ImGui.GetCursorPos();
        var componentPos = new Vector2
        (
            ImGui.GetWindowContentRegionMax().X -
            ImGui.GetFrameHeight()              -
            regionSelectorWidth                 -
            (2 * ImGui.GetStyle().ItemSpacing.X),
            cursorPos.Y - ImGui.GetFrameHeight() - ImGui.GetStyle().FramePadding.Y
        );

        ImGui.SetCursorPos(componentPos);

        using (ImRaii.Group())
        {
            ImGui.SetNextItemWidth(regionSelectorWidth);

            using (var regionCombo = ImRaii.Combo("###RegionSelector", provider.EffectiveRegionName))
            {
                if (regionCombo)
                {
                    foreach (var regionName in allWorlds.Keys)
                    {
                        if (ImGui.Selectable(regionName.Replace('-', ' '), provider.EffectiveRegionName == regionName))
                            provider.SelectRegion(regionName);
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.ArrowButton("###RegionPriceToggle", direction))
                isAllWorldsPriceExpanded ^= true;

            ImGuiOm.TooltipHover
            (
                Lang.Get
                (
                    direction == ImGuiDir.Down ?
                        "BetterMarketBoard-ShowAllWorldPrices" :
                        "BetterMarketBoard-CollapseAllWorldPrices"
                )
            );
        }

        ImGui.SetCursorPos(cursorPos);
    }

    private void DrawFullPriceTable
    (
        MarketBoardUIContext frame
    )
    {
        var dcsInRegion = provider.GetWorldPriceRanks(frame.ItemID);
        if (dcsInRegion == null || dcsInRegion.Valid.Count == 0) return;

        using var font = FontManager.Instance().UIFont80.Push();

        var dcBadgeWidth = 60f * GlobalUIScale;
        var spacingX     = 4f  * GlobalUIScale;
        var rowHeight    = (ImGui.GetTextLineHeight() * 2f) + (6f * GlobalUIScale);
        var rounding     = 4f * GlobalUIScale;

        var currentWorldPrice = dcsInRegion.Current.MinPrice;

        foreach (var (dcName, worldPricesList) in provider.DCWorldPrices)
        {
            var availWidth       = ImGui.GetContentRegionAvail().X;
            var worldsAvailWidth = availWidth - dcBadgeWidth - spacingX;
            var worldCount       = worldPricesList.Count;
            if (worldCount == 0)
                continue;

            var capsuleWidth = MathF.Max(30f * GlobalUIScale, (worldsAvailWidth - (spacingX * (worldCount - 1))) / worldCount);

            using (ImRaii.Group())
            {
                var dcStartPos = ImGui.GetCursorScreenPos();
                var dcSize     = new Vector2(dcBadgeWidth, rowHeight);
                ImGui.InvisibleButton($"##DCBadge_{dcName}", dcSize);

                var drawList = ImGui.GetWindowDrawList();
                var dcMax    = dcStartPos + dcSize;

                drawList.AddRectFilled
                (
                    dcStartPos,
                    dcMax,
                    KnownColor.LightSkyBlue.ToVector4().WithW(0.15f).ToUInt(),
                    rounding
                );
                drawList.AddRect
                (
                    dcStartPos,
                    dcMax,
                    KnownColor.LightSkyBlue.ToVector4().WithW(0.45f).ToUInt(),
                    rounding,
                    ImDrawFlags.RoundCornersAll,
                    1f * GlobalUIScale
                );

                var dcTextSize = ImGui.CalcTextSize(dcName);
                var dcTextPos  = new Vector2(dcStartPos.X + MathF.Max(0f, (dcSize.X - dcTextSize.X) / 2f), dcStartPos.Y + ((dcSize.Y - dcTextSize.Y) / 2f));
                drawList.AddText(dcTextPos, KnownColor.LightSkyBlue.ToUInt(), dcName);

                ImGuiOm.TooltipHover($"{dcName}");

                foreach (var world in worldPricesList)
                {
                    ImGui.SameLine(0, spacingX);
                    var card = worldPriceCards.GetOrAdd(world.WorldID, static _ => new());
                    card.Draw(frame, world, new Vector2(capsuleWidth, rowHeight), currentWorldPrice);
                }
            }

            ImGui.Spacing();
        }
    }

    private sealed class WorldPriceCardComponent : CardComponentBase
    {
        private MarketBoardUIContext frame;
        private WorldPriceRow        world;
        private Vector2              targetSize;
        private ulong                currentWorldPrice;

        public void Draw
        (
            MarketBoardUIContext frameParam,
            WorldPriceRow        worldParam,
            Vector2              sizeParam,
            ulong                currentWorldPriceParam
        )
        {
            frame             = frameParam;
            world             = worldParam;
            targetSize        = sizeParam;
            currentWorldPrice = currentWorldPriceParam;

            base.Draw();
        }

        protected override bool              WrapInContentTable   => false;
        protected override bool              EnablePressAnimation => true;
        protected override bool              EnableHoverAnimation => true;
        protected override bool              EnableTopHighlight   => true;
        protected override bool              EnableGlow           => true;
        protected override float             HoverFloatOffset     => -1.5f * GlobalUIScale;
        protected override float             PressOffset          => 1.0f  * GlobalUIScale;
        protected override float             Rounding             => 4f    * GlobalUIScale;
        protected override CardCursorAdvance CursorAdvance        => CardCursorAdvance.Right;

        protected override bool IsSelected =>
            frame.IsViewingCurrentWorld ?
                world.WorldID == GameState.CurrentWorld :
                world.WorldID == frame.SelectedWorldID;

        protected override Vector4 RestingBorder
        {
            get
            {
                if (world.WorldID == GameState.CurrentWorld)
                    return KnownColor.Pink.ToVector4().WithW(0.85f);
                if (frame.Provider.MinPriceData.WorldID == world.WorldID && world.MinPrice != ulong.MaxValue)
                    return KnownColor.GreenYellow.ToVector4().WithW(0.85f);
                return KnownColor.White.ToVector4().WithW(0.08f);
            }
        }

        protected override Vector4 HoveredBorder =>
            IsSelected                                                    ? KnownColor.DeepSkyBlue.ToVector4() :
            world.WorldID                       == GameState.CurrentWorld ? KnownColor.Pink.ToVector4() :
            frame.Provider.MinPriceData.WorldID == world.WorldID          ? KnownColor.GreenYellow.ToVector4() :
                                                                            KnownColor.DodgerBlue.ToVector4().WithW(0.8f);

        protected override Vector4? SelectedBorder => KnownColor.DeepSkyBlue.ToVector4();

        protected override Vector4? CustomBackgroundColor
        {
            get
            {
                if (IsSelected)
                    return null;
                if (frame.Provider.MinPriceData.WorldID == world.WorldID && world.MinPrice != ulong.MaxValue)
                    return KnownColor.ForestGreen.ToVector4().WithW(0.35f);
                return world.WorldID == GameState.CurrentWorld ?
                           KnownColor.DeepPink.ToVector4().WithW(0.25f) :
                           KnownColor.Black.ToVector4().WithW(0.30f);
            }
        }

        protected override CardDrawContext CreateContext()
        {
            var startPos = ImGui.GetCursorScreenPos();
            return new(startPos, startPos, Vector2.Zero, targetSize.X);
        }

        protected override Vector2 GetTargetSize
        (
            CardDrawContext context,
            Vector2         contentRectSize
        ) =>
            targetSize;

        protected override void DrawContent
        (
            CardDrawContext context,
            bool            isHovered
        )
        {
            var isCurrentWorld = world.WorldID == GameState.CurrentWorld;
            var isMinPrice     = frame.Provider.MinPriceData.WorldID == world.WorldID && world.MinPrice != ulong.MaxValue;
            var isMaxPrice = frame.Provider.MaxPriceData.WorldID == world.WorldID  &&
                             world.MinPrice                      != ulong.MaxValue &&
                             frame.Provider.MinPriceData.WorldID != frame.Provider.MaxPriceData.WorldID;
            var hasNoListing = world.MinPrice == ulong.MaxValue;

            if (ImGui.InvisibleButton($"##Capsule_{world.WorldID}", targetSize))
                frame.Provider.SelectWorld(world.WorldID);

            if (!isCurrentWorld && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                ChatManager.Instance().SendMessage($"/pdr worldtravel {LuminaWrapper.GetWorldName(world.WorldID)}");

            var drawList = ImGui.GetWindowDrawList();
            var min      = context.FrameMin;
            var max      = min + targetSize;

            drawList.PushClipRect(min, max, true);

            uint nameColor;
            if (isCurrentWorld)
                nameColor = KnownColor.Pink.ToUInt();
            else if (isMinPrice)
                nameColor = KnownColor.GreenYellow.ToUInt();
            else
                nameColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

            var nameText = world.WorldName;

            using (FontManager.Instance().UIFont60.Push())
            {
                var nameSize = ImGui.CalcTextSize(nameText);
                var namePos  = new Vector2(min.X + MathF.Max(0f, (targetSize.X - nameSize.X) / 2f), min.Y + (3f * GlobalUIScale));
                drawList.AddText(namePos, nameColor, nameText);
            }

            var priceText = hasNoListing ?
                                "-" :
                                $"{world.MinPrice.ToChineseString()}\ue049";

            uint priceColor;
            if (hasNoListing)
                priceColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            else if (isMinPrice)
                priceColor = KnownColor.GreenYellow.ToUInt();
            else if (isMaxPrice)
                priceColor = KnownColor.OrangeRed.ToUInt();
            else if (isCurrentWorld)
                priceColor = KnownColor.LightPink.ToUInt();
            else
                priceColor = KnownColor.White.ToUInt();

            var priceSize = ImGui.CalcTextSize(priceText);
            var pricePos  = new Vector2(min.X + MathF.Max(0f, (targetSize.X - priceSize.X) / 2f), max.Y - priceSize.Y - (3f * GlobalUIScale));
            drawList.AddText(pricePos, priceColor, priceText);

            drawList.PopClipRect();

            if (isHovered)
                frame.Owner.DrawWorldPriceTooltip(frame, world.WorldID, world.WorldName, world.MinPrice, currentWorldPrice);
        }
    }
}
