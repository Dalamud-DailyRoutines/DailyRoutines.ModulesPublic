using System.Numerics;
using DailyRoutines.Extensions;
using Dalamud.Utility.Numerics;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface;

public partial class BetterMarketBoard
{
    private static void DrawMetricDashboard
    (
        IReadOnlyList<MetricItem> metrics,
        float?                    customWidth = null
    )
    {
        if (metrics.Count == 0) return;

        var contentWidth = customWidth ?? ImGui.GetContentRegionAvail().X;
        var paddingY     = 6f           * GlobalUIScale;
        var cellWidth    = contentWidth / metrics.Count;

        float labelHeight;
        using (FontManager.Instance().UIFont60.Push())
            labelHeight = ImGui.GetTextLineHeight();

        float valueHeight;
        using (FontManager.Instance().UIFont80.Push())
            valueHeight = ImGui.GetTextLineHeight();

        var cardHeight = (paddingY * 2) + labelHeight + valueHeight + (3f * GlobalUIScale);

        var drawList = ImGui.GetWindowDrawList();
        var min      = ImGui.GetCursorScreenPos();
        var max      = min + new Vector2(contentWidth, cardHeight);

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg, 0.45f), 4f * GlobalUIScale);
        drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border,        0.25f), 4f * GlobalUIScale);

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric  = metrics[i];
            var cellMin = min     + new Vector2(i * cellWidth, 0);
            var cellMax = cellMin + new Vector2(cellWidth,     cardHeight);

            if (i > 0)
            {
                var divX = min.X + (i * cellWidth);
                drawList.AddLine
                (
                    new(divX, min.Y + (cardHeight * 0.2f)),
                    new(divX, max.Y - (cardHeight * 0.2f)),
                    ImGui.GetColorU32(ImGuiCol.Border, 0.35f)
                );
            }

            var hasTooltip = !string.IsNullOrEmpty(metric.Tooltip);

            var labelText = $"{metric.Label}" +
                            (hasTooltip ?
                                 " (?)" :
                                 string.Empty);

            float labelWidth;
            using (FontManager.Instance().UIFont60.Push())
                labelWidth = ImGui.CalcTextSize(labelText).X;

            var headerStartX = cellMin.X + MathF.Max(0f, (cellWidth - labelWidth) / 2f);

            using (FontManager.Instance().UIFont60.Push())
            {
                drawList.AddText
                (
                    new(headerStartX, cellMin.Y + paddingY),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    labelText
                );
            }

            using (FontManager.Instance().UIFont80.Push())
            {
                var valueSize = ImGui.CalcTextSize(metric.Value);
                var valuePos  = new Vector2(cellMin.X + MathF.Max(0f, (cellWidth - valueSize.X) / 2f), cellMin.Y + paddingY + labelHeight + (2f * GlobalUIScale));
                drawList.AddText(valuePos, ImGui.GetColorU32(metric.ValueColor), metric.Value);
            }

            if (hasTooltip && ImGui.IsMouseHoveringRect(cellMin, cellMax))
            {
                using (ImRaii.Tooltip())
                {
                    using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 40f))
                        ImGui.TextUnformatted(metric.Tooltip);
                }
            }
        }

        ImGui.Dummy(new Vector2(contentWidth, cardHeight));
    }

    private static void DrawMarketPrice
    (
        ulong price
    )
    {
        ImGui.TextUnformatted
        (
            $"{price.ToChineseString()}\ue049"
        );
        ImGuiOm.ClickToCopyAndNotify(price.ToString());
    }

    private sealed class MarketBenchmarkInfo
    {
        public ulong   Price;
        public string  Badge = string.Empty;
        public Vector4 Color;
        public string? Tooltip;
        public Action? OnClick;
        public bool    Drawn;
    }

    private static List<MarketBenchmarkInfo> BuildMarketBenchmarks
    (
        uint   itemID,
        ulong? avgPrice,
        uint?  npcGilPrice
    )
    {
        var list = new List<MarketBenchmarkInfo>();

        var hasAvg = avgPrice is > 0;
        var hasNPC = npcGilPrice is > 0;

        switch (hasAvg)
        {
            case true when hasNPC && avgPrice!.Value == npcGilPrice!.Value:
                list.Add
                (
                    new()
                    {
                        Price = avgPrice.Value,
                        Badge =
                            Lang.Get
                            (
                                "BetterMarketBoard-AverageNPCPrice-Format",
                                new Dictionary<string, object>
                                {
                                    ["price"] = avgPrice.Value.ToChineseString()
                                }
                            ),
                        Color = KnownColor.DarkOrange.ToVector4(),
                        Tooltip =
                            Lang.Get
                            (
                                "BetterMarketBoard-AverageNPCPrice-Tooltip",
                                new Dictionary<string, object>
                                {
                                    ["averagePrice"] = avgPrice.Value.ToChineseString(),
                                    ["npcPrice"]     = npcGilPrice.Value.ToChineseString()
                                }
                            ),
                        OnClick = () => OpenShopListByItemIDIPC.InvokeFunc(itemID)
                    }
                );
                return list;
            case true:
                list.Add
                (
                    new()
                    {
                        Price = avgPrice!.Value,
                        Badge = $"{Lang.Get("BetterMarketBoard-AveragePrice")}：{avgPrice.Value.ToChineseString()}\ue049",
                        Color = KnownColor.DeepSkyBlue.ToVector4()
                    }
                );
                break;
        }

        if (hasNPC)
        {
            list.Add
            (
                new()
                {
                    Price = npcGilPrice!.Value,
                    Badge = Lang.Get
                    (
                        "BetterMarketBoard-NPCPrice",
                        new Dictionary<string, object>
                        {
                            ["price"] = npcGilPrice.Value.ToChineseString()
                        }
                    ),
                    Color   = KnownColor.OrangeRed.ToVector4(),
                    Tooltip = Lang.Get("BetterMarketBoard-NPCShopInfo"),
                    OnClick = () => OpenShopListByItemIDIPC.InvokeFunc(itemID)
                }
            );
        }

        list.Sort((a, b) => a.Price.CompareTo(b.Price));
        return list;
    }

    private static void DrawBenchmarkSeparatorRow
    (
        string  id,
        string  badgeLabel,
        Vector4 accentColor,
        string? tooltip = null,
        Action? onClick = null
    )
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var rowHeight = (ImGui.GetTextLineHeight() * 1.15f) + (6f * GlobalUIScale);

        var flags = ImGuiSelectableFlags.SpanAllColumns;
        if (onClick == null)
            flags |= ImGuiSelectableFlags.Disabled;

        if (ImGui.Selectable($"###{id}", false, flags, new Vector2(0, rowHeight)) && onClick != null)
            onClick.Invoke();

        var isHovered = ImGui.IsItemHovered();
        if (isHovered && onClick != null)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var min      = ImGui.GetItemRectMin();
        var max      = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 3f * GlobalUIScale;

        var parentClipMin = drawList.GetClipRectMin();
        var parentClipMax = drawList.GetClipRectMax();

        var clipMin = min with { Y = MathF.Max(min.Y, parentClipMin.Y) };
        var clipMax = max with { Y = MathF.Min(max.Y, parentClipMax.Y) };

        if (clipMin.Y >= clipMax.Y || clipMin.X >= clipMax.X)
            return;

        drawList.PushClipRect(clipMin, clipMax, false);

        var bgAlpha = isHovered && onClick != null ?
                          0.16f :
                          0.08f;
        var bgCol = accentColor.WithW(bgAlpha).ToUInt();
        drawList.AddRectFilled(min, max, bgCol, rounding);

        var centerY = min.Y + ((max.Y - min.Y) / 2f);
        var padX    = 8f * GlobalUIScale;

        using (FontManager.Instance().UIFont60.Push())
        {
            var badgeSize = ImGui.CalcTextSize(badgeLabel);
            var badgePadX = 6f * GlobalUIScale;
            var badgePadY = 2f * GlobalUIScale;

            var badgeMin = new Vector2(min.X      + padX, centerY - (badgeSize.Y / 2f)          - badgePadY);
            var badgeMax = new Vector2(badgeMin.X + badgeSize.X   + (badgePadX   * 2f), centerY + (badgeSize.Y / 2f) + badgePadY);

            var badgeBgAlpha = isHovered && onClick != null ?
                                   0.35f :
                                   0.22f;
            var badgeBorderAlpha = isHovered && onClick != null ?
                                       1.0f :
                                       0.75f;

            drawList.AddRectFilled(badgeMin, badgeMax, accentColor.WithW(badgeBgAlpha).ToUInt(), rounding);
            drawList.AddRect(badgeMin, badgeMax, accentColor.WithW(badgeBorderAlpha).ToUInt(), rounding, ImDrawFlags.None, 1f * GlobalUIScale);
            drawList.AddText(new Vector2(badgeMin.X + badgePadX, centerY - (badgeSize.Y / 2f)), accentColor.ToUInt(), badgeLabel);

            var lineStartX = badgeMax.X + (6f * GlobalUIScale);
            var lineEndX   = max.X      - padX;

            if (lineEndX > lineStartX)
            {
                var lineAlpha = isHovered && onClick != null ?
                                    0.6f :
                                    0.35f;
                drawList.AddLine
                (
                    new Vector2(lineStartX, centerY),
                    new Vector2(lineEndX,   centerY),
                    accentColor.WithW(lineAlpha).ToUInt(),
                    1.5f * GlobalUIScale
                );
            }
        }

        drawList.PopClipRect();

        if (!string.IsNullOrEmpty(tooltip) && isHovered)
            ImGui.SetTooltip(tooltip);
    }
}
