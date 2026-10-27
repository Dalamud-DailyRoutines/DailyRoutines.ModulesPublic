using System.Numerics;
using DailyRoutines.Common.RemoteInteraction.Enums;
using Dalamud.Utility.Numerics;
using OmenTools.Interop.Game.Lumina;
using TimeAgo;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private void DrawMarketPriceTrend
    (
        MarketBoardUIContext frame
    )
    {
        var historyItemID = frame.ItemID;
        if (historyItemID == 0) return;

        var data = provider.GetTrendDataSet(historyItemID);

        if (data == null || data.Entries.Count == 0)
        {
            var statusText = provider.HistoryRequestStatus switch
            {
                RemoteSnapshotStatus.Loading or RemoteSnapshotStatus.Empty => $"\ue031 {LuminaWrapper.GetAddonText(2717)}",
                RemoteSnapshotStatus.Failed                                => Lang.Get("BetterMarketBoard-Tooltip-RequestFailed"),
                _                                                          => LuminaWrapper.GetAddonText(1998)
            };
            ImGui.TextDisabled(statusText);
            if (provider.HistoryRequestStatus is RemoteSnapshotStatus.Failed &&
                !string.IsNullOrWhiteSpace(provider.HistoryRequestError))
                ImGuiOm.TooltipHover(provider.HistoryRequestError);

            return;
        }

        var (dailySales, _, _) = provider.GetItemAggregatedStats(historyItemID, frame.SelectedWorldID, frame.HQOnly);

        var actualMinPrice = data.Entries.Min(x => x.PricePerUnit);
        var actualMaxPrice = data.Entries.Max(x => x.PricePerUnit);

        var trendMetrics = new List<MetricItem>
        {
            new
            (
                Lang.Get("BetterMarketBoard-Trend-Max"),
                $"{actualMaxPrice.ToChineseString()}\ue049",
                KnownColor.Salmon.ToVector4(),
                null
            ),
            new
            (
                Lang.Get("BetterMarketBoard-Trend-Min"),
                $"{actualMinPrice.ToChineseString()}\ue049",
                KnownColor.GreenYellow.ToVector4(),
                null
            )
        };

        if (data.IsCanBeHQ)
        {
            if (data.AvgNQPrice > 0)
            {
                trendMetrics.Add
                (
                    new
                    (
                        Lang.Get("BetterMarketBoard-AveragePrice-NQ"),
                        $"{data.AvgNQPrice.ToChineseString()}\ue049",
                        KnownColor.LightSkyBlue.ToVector4(),
                        null
                    )
                );
            }

            if (data.AvgHQPrice > 0)
            {
                trendMetrics.Add
                (
                    new
                    (
                        Lang.Get("BetterMarketBoard-AveragePrice-HQ"),
                        $"{data.AvgHQPrice.ToChineseString()}\ue049",
                        KnownColor.Orange.ToVector4(),
                        null
                    )
                );
            }
        }
        else
        {
            trendMetrics.Add
            (
                new
                (
                    Lang.Get("BetterMarketBoard-AveragePrice"),
                    $"{data.AvgHistoryPrice.ToChineseString()}\ue049",
                    KnownColor.Orange.ToVector4(),
                    null
                )
            );
        }

        trendMetrics.Add
        (
            new
            (
                Lang.Get("BetterMarketBoard-Trend-Volume"),
                Lang.Get
                (
                    "BetterMarketBoard-Samples-Format",
                    new Dictionary<string, object>
                    {
                        ["count"]    = data.Entries.Count,
                        ["quantity"] = data.Entries.Sum(x => x.Quantity)
                    }
                ),
                KnownColor.LightSkyBlue.ToVector4(),
                null
            )
        );

        if (dailySales > 0)
        {
            trendMetrics.Add
            (
                new
                (
                    Lang.Get("BetterMarketBoard-Tooltip-DailySales"),
                    Lang.Get
                    (
                        "BetterMarketBoard-DailySales-Format",
                        new Dictionary<string, object>
                        {
                            ["quantity"] = dailySales
                        }
                    ),
                    KnownColor.White.ToVector4(),
                    null
                )
            );
        }

        DrawMetricDashboard(trendMetrics);
        ImGui.Spacing();

        var xMin = data.XMin;
        var xMax = data.XMax;
        var yMin = data.VisualMin;
        var yMax = data.VisualMax;

        using (ImRaii.PushColor(ImPlotCol.AxisBg, new Vector4(0.02f, 0.02f, 0.02f, 0.5f)))
        using (ImRaii.PushColor(ImPlotCol.FrameBg, new Vector4(0.04f, 0.04f, 0.04f, 0.3f)))
        using (ImRaii.PushColor(ImPlotCol.AxisGrid, new Vector4(1f, 1f, 1f, 0.06f)))
        using (ImRaii.PushColor(ImPlotCol.LegendBg, new Vector4(0.05f, 0.05f, 0.05f, 0.85f)))
        using (ImRaii.PushColor(ImPlotCol.LegendBorder, new Vector4(1f, 1f, 1f, 0.15f)))
        using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 2f))
        using (ImRaii.PushStyle(ImPlotStyleVar.MinorAlpha, 0.2f))
        using (var plot = ImRaii.Plot
               (
                   $"##MarketHistoryPriceTrend_{historyItemID}_{provider.SelectedWorldID}",
                   new(-1, ImGui.GetContentRegionAvail().Y),
                   ImPlotFlags.NoTitle | ImPlotFlags.NoMouseText
               ))
        {
            if (!plot) return;

            ImPlot.SetupAxes((byte*)null, (byte*)null, ImPlotAxisFlags.None, ImPlotAxisFlags.None);
            ImPlot.SetupAxesLimits(xMin, xMax, yMin, yMax, ImPlotCond.Once);

            var dtStart     = DateTimeOffset.FromUnixTimeSeconds((long)xMin).LocalDateTime;
            var dtEnd       = DateTimeOffset.FromUnixTimeSeconds((long)xMax).LocalDateTime;
            var isCrossYear = dtStart.Year != dtEnd.Year;
            var timeSpan    = xMax - xMin;
            var totalDays   = (dtEnd.Date - dtStart.Date).TotalDays;

            string GetTickFormat
            (
                bool compact
            )
            {
                if (isCrossYear)
                {
                    if (totalDays > 365)
                        return "yyyy-MM";

                    return compact ?
                               "yy-MM-dd" :
                               "yyyy-MM-dd";
                }

                return totalDays switch
                {
                    >= 7              => "MM-dd",
                    >= 1 when compact => "MM-dd",
                    >= 1              => "MM-dd HH:mm",
                    _                 => "HH:mm"
                };
            }

            var plotWidth       = ImGui.GetContentRegionAvail().X;
            var sampleLabel     = dtStart.ToString(GetTickFormat(false));
            var sampleWidth     = ImGui.CalcTextSize(sampleLabel).X + (30f * GlobalUIScale);
            var xTickCount      = Math.Clamp((int)(plotWidth / sampleWidth), 2, 5);
            var isCompact       = xTickCount <= 3;
            var finalTickFormat = GetTickFormat(isCompact);

            var xTickValues = new double[xTickCount];
            var xTickLabels = new string[xTickCount];

            for (var i = 0; i < xTickCount; i++)
            {
                var t = xMin + (timeSpan * i / (xTickCount - 1));
                xTickValues[i] = t;
                var dt = DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime;
                xTickLabels[i] = dt.ToString(finalTickFormat);
            }

            ImPlot.SetupAxisTicks(ImAxis.X1, ref xTickValues[0], xTickCount, xTickLabels, false);

            var plotHeight  = ImGui.GetContentRegionAvail().Y;
            var yTickCount  = Math.Clamp((int)(plotHeight / (35f * GlobalUIScale)), 3, 5);
            var yTickValues = new double[yTickCount];
            var yTickLabels = new string[yTickCount];
            var ySpan       = yMax - yMin;

            for (var i = 0; i < yTickCount; i++)
            {
                var p = yMin + (ySpan * i / (yTickCount - 1));
                yTickValues[i] = p;
                yTickLabels[i] = ((ulong)Math.Round(p)).ToChineseString();
            }

            ImPlot.SetupAxisTicks(ImAxis.Y1, ref yTickValues[0], yTickCount, yTickLabels, false);

            var currentPlot = ImPlot.GetCurrentPlot();
            var nqItem      = currentPlot.Items.GetItem(Lang.Get("NQ"));
            var hqItem      = currentPlot.Items.GetItem(Lang.Get("HQ"));
            var isNQVisible = nqItem == null || nqItem->Show != 0;
            var isHQVisible = hqItem == null || hqItem->Show != 0;

            var historyEntries = data.Entries;

            if (data.IsCanBeHQ)
            {
                var hqCount = data.HQCount;
                var nqCount = data.NQCount;
                var hqXs    = new double[hqCount];
                var hqYs    = new double[hqCount];
                var nqXs    = new double[nqCount];
                var nqYs    = new double[nqCount];
                var hqIndex = 0;
                var nqIndex = 0;

                foreach (var entry in historyEntries)
                {
                    if (entry.IsHQ)
                    {
                        hqXs[hqIndex] = entry.X;
                        hqYs[hqIndex] = entry.PricePerUnit;
                        hqIndex++;
                    }
                    else
                    {
                        nqXs[nqIndex] = entry.X;
                        nqYs[nqIndex] = entry.PricePerUnit;
                        nqIndex++;
                    }
                }

                if (data.AvgNQPrice > 0 && isNQVisible)
                {
                    var avgXs = new[] { xMin, xMax };
                    var avgYs = new double[] { data.AvgNQPrice, data.AvgNQPrice };
                    using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.LightSkyBlue.ToVector4().WithW(0.35f)))
                    using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 1.2f))
                        ImPlot.PlotLine(Lang.Get("BetterMarketBoard-AveragePrice-NQ"), ref avgXs[0], ref avgYs[0], 2);

                    ImPlot.TagY
                    (
                        data.AvgNQPrice,
                        KnownColor.LightSkyBlue.ToVector4(),
                        $"{data.AvgNQPrice.ToChineseString()}\ue049"
                    );
                }

                if (data.AvgHQPrice > 0 && isHQVisible)
                {
                    var avgXs = new[] { xMin, xMax };
                    var avgYs = new double[] { data.AvgHQPrice, data.AvgHQPrice };
                    using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.Orange.ToVector4().WithW(0.35f)))
                    using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 1.2f))
                        ImPlot.PlotLine(Lang.Get("BetterMarketBoard-AveragePrice-HQ"), ref avgXs[0], ref avgYs[0], 2);

                    ImPlot.TagY
                    (
                        data.AvgHQPrice,
                        KnownColor.Orange.ToVector4(),
                        $"{data.AvgHQPrice.ToChineseString()}\ue049"
                    );
                }

                if (nqCount > 0)
                {
                    if (isNQVisible)
                    {
                        using (ImRaii.PushColor(ImPlotCol.Fill, KnownColor.LightSkyBlue.ToVector4().WithW(0.12f)))
                            ImPlot.PlotShaded("##NQShade", ref nqXs[0], ref nqYs[0], nqCount, yMin);
                    }

                    using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.LightSkyBlue.ToVector4()))
                    using (ImRaii.PushColor(ImPlotCol.MarkerFill, KnownColor.LightSkyBlue.ToVector4()))
                    using (ImRaii.PushColor(ImPlotCol.MarkerOutline, KnownColor.White.ToVector4().WithW(0.6f)))
                    {
                        ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 3.5f * GlobalUIScale);
                        ImPlot.PlotLine(Lang.Get("NQ"), ref nqXs[0], ref nqYs[0], nqCount);
                    }
                }

                if (hqCount > 0)
                {
                    if (isHQVisible)
                    {
                        using (ImRaii.PushColor(ImPlotCol.Fill, KnownColor.Orange.ToVector4().WithW(0.12f)))
                            ImPlot.PlotShaded("##HQShade", ref hqXs[0], ref hqYs[0], hqCount, yMin);
                    }

                    using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.Orange.ToVector4()))
                    using (ImRaii.PushColor(ImPlotCol.MarkerFill, KnownColor.Orange.ToVector4()))
                    using (ImRaii.PushColor(ImPlotCol.MarkerOutline, KnownColor.White.ToVector4().WithW(0.6f)))
                    {
                        ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 4f * GlobalUIScale);
                        ImPlot.PlotLine(Lang.Get("HQ"), ref hqXs[0], ref hqYs[0], hqCount);
                    }
                }
            }
            else
            {
                if (data.AvgHistoryPrice > 0 && isNQVisible)
                {
                    var avgXs = new[] { xMin, xMax };
                    var avgYs = new double[] { data.AvgHistoryPrice, data.AvgHistoryPrice };
                    using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.Orange.ToVector4().WithW(0.35f)))
                    using (ImRaii.PushStyle(ImPlotStyleVar.LineWeight, 1.2f))
                        ImPlot.PlotLine(Lang.Get("BetterMarketBoard-AveragePrice"), ref avgXs[0], ref avgYs[0], 2);

                    ImPlot.TagY
                    (
                        data.AvgHistoryPrice,
                        KnownColor.Orange.ToVector4(),
                        $"{data.AvgHistoryPrice.ToChineseString()}\ue049"
                    );
                }

                var xs = new double[historyEntries.Count];
                var ys = new double[historyEntries.Count];

                for (var i = 0; i < historyEntries.Count; i++)
                {
                    xs[i] = historyEntries[i].X;
                    ys[i] = historyEntries[i].PricePerUnit;
                }

                if (isNQVisible)
                {
                    using (ImRaii.PushColor(ImPlotCol.Fill, KnownColor.LightSkyBlue.ToVector4().WithW(0.12f)))
                        ImPlot.PlotShaded("##PriceShade", ref xs[0], ref ys[0], historyEntries.Count, yMin);
                }

                using (ImRaii.PushColor(ImPlotCol.Line, KnownColor.LightSkyBlue.ToVector4()))
                using (ImRaii.PushColor(ImPlotCol.MarkerFill, KnownColor.LightSkyBlue.ToVector4()))
                using (ImRaii.PushColor(ImPlotCol.MarkerOutline, KnownColor.White.ToVector4().WithW(0.6f)))
                {
                    ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 3.5f * GlobalUIScale);
                    ImPlot.PlotLine(Lang.Get("NQ"), ref xs[0], ref ys[0], historyEntries.Count);
                }
            }

            if (!ImPlot.IsPlotHovered()) return;

            var mouse               = ImPlot.GetPlotMousePos();
            var xRange              = xMax - xMin;
            var yRange              = yMax - yMin;
            if (xRange <= 0) xRange = 1;
            if (yRange <= 0) yRange = 1;

            var minDistanceSq = double.MaxValue;
            var closestIndex  = -1;

            for (var i = 0; i < historyEntries.Count; i++)
            {
                var entry = historyEntries[i];
                if (!(entry.IsHQ ?
                          isHQVisible :
                          isNQVisible))
                    continue;

                var dx     = (entry.X            - mouse.X) / xRange;
                var dy     = (entry.PricePerUnit - mouse.Y) / yRange;
                var distSq = (dx * dx) + (dy * dy);

                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    closestIndex  = i;
                }
            }

            if (closestIndex >= 0 && minDistanceSq < 0.05)
            {
                var hoverEntry = historyEntries[closestIndex];

                using (ImRaii.Tooltip())
                {
                    if (hoverEntry.IsHQ)
                        ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("HQ"));
                    else
                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("NQ"));

                    ImGui.Separator();

                    ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(357)}: ");
                    ImGui.SameLine();
                    ImGui.TextColored
                    (
                        KnownColor.Orange.ToVector4(),
                        $"{hoverEntry.PricePerUnit.ToChineseString()}\ue049"
                    );

                    ImGui.TextDisabled($"{Lang.Get("Amount")}: ");
                    ImGui.SameLine();
                    ImGui.TextUnformatted
                    (
                        $"{hoverEntry.Quantity}"
                    );

                    ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(6936)}: ");
                    ImGui.SameLine();
                    ImGui.TextColored
                    (
                        KnownColor.GreenYellow.ToVector4(),
                        $"{(hoverEntry.PricePerUnit * hoverEntry.Quantity).ToChineseString()}\ue049"
                    );

                    ImGui.Separator();

                    ImGui.TextDisabled($"{LuminaWrapper.GetAddonText(1976)}: ");
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{hoverEntry.SaleTime:yyyy-MM-dd HH:mm} ({hoverEntry.SaleTime.TimeAgo()})");
                }
            }
        }
    }
}
