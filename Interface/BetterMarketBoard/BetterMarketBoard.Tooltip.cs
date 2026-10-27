using DailyRoutines.Common.RemoteInteraction.Enums;
using DailyRoutines.Common.RemoteInteraction.Models;
using DailyRoutines.RemoteInteraction.Universalis.Models.Responses;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TimeAgo;

namespace DailyRoutines.ModulesPublic.Interface;

public partial class BetterMarketBoard
{
    private void OnItemTooltipUpdate
    (
        ItemKind                          kind,
        uint                              itemID,
        ref List<TooltipItemModification> modifications
    )
    {
        if (!config.AppendMarketStatsTooltip)
        {
            provider.ReleaseTooltipSubscription();
            return;
        }

        if (kind is ItemKind.Collectible or ItemKind.EventItem)
        {
            provider.ReleaseTooltipSubscription();
            return;
        }

        if (itemID == 0                                         ||
            !LuminaGetter.TryGetRow<Item>(itemID, out var item) ||
            item.ItemSearchCategory.RowId == 0                  ||
            GameState.CurrentWorld        == 0)
        {
            provider.ReleaseTooltipSubscription();
            return;
        }

        var hqOnly           = kind is ItemKind.Hq && item.CanBeHq;
        var currentWorldName = LuminaWrapper.GetWorldName(GameState.CurrentWorld);
        var currentWorldScope = string.IsNullOrEmpty(currentWorldName) ?
                                    GameState.CurrentWorld.ToString() :
                                    currentWorldName;

        var currentWorldSnapshot   = provider.GetTooltipAggregatedSnapshot(itemID, currentWorldScope);
        var marketStats            = CreateTooltipMarketStats(itemID, hqOnly, currentWorldSnapshot);
        var isTooltipRequestFailed = currentWorldSnapshot.Status is RemoteSnapshotStatus.Failed;

        if (provider.CanRequestTooltipAggregatedScope())
        {
            QueueTooltipAggregatedRequests(itemID, currentWorldScope);
        }
        else
            CancelQueuedTooltipRequest();

        modifications.Add
        (
            new()
            {
                Target = TooltipItemType.Description,
                Type   = TooltipModificationType.Append,
                Text = BuildItemTooltipMarketText
                (
                    marketStats == null,
                    isTooltipRequestFailed,
                    currentWorldSnapshot.Status is RemoteSnapshotStatus.Ready,
                    provider.GetTooltipRequestError(),
                    marketStats?.CurrentWorldMinPrice,
                    marketStats?.CurrentWorldTimeText,
                    marketStats?.RegionMinWorldName ?? string.Empty,
                    marketStats?.RegionMinPrice,
                    marketStats?.RegionTimeText,
                    marketStats?.DailySales ?? 0
                )
            }
        );
    }

    private void QueueTooltipAggregatedRequests
    (
        uint   itemID,
        string scope
    )
    {
        if (tooltipRequestDebouncer.IsPending &&
            tooltipRequestItemID == itemID    &&
            tooltipRequestScope  == scope)
            return;

        tooltipRequestItemID = itemID;
        tooltipRequestScope  = scope;
        tooltipRequestDebouncer.Debounce();
    }

    private void CancelQueuedTooltipRequest()
    {
        tooltipRequestDebouncer.Cancel();
        tooltipRequestScope  = string.Empty;
        tooltipRequestItemID = 0;
    }

    private static ReadOnlySeString BuildItemTooltipMarketText
    (
        bool    isRequesting,
        bool    isFailed,
        bool    isComplete,
        string  errorMessage,
        ulong?  currentWorldMinPrice,
        string? currentWorldTimeText,
        string  regionMinWorldName,
        ulong?  regionMinPrice,
        string? regionTimeText,
        float   dailySales
    )
    {
        using var rented  = new RentedSeStringBuilder();
        var       builder = rented.Builder;

        // 标题
        builder.Append($"[{LuminaWrapper.GetAddonText(6556)}]");

        if (isRequesting)
        {
            var statusText = $"\ue031 {LuminaWrapper.GetAddonText(2717)}";
            if (isFailed)
                statusText = Lang.Get("BetterMarketBoard-Tooltip-RequestFailed");
            else if (isComplete)
                statusText = LuminaWrapper.GetAddonText(1998);

            builder.AppendNewLine()
                   .PushColorType(32)
                   .Append($"    {statusText}")
                   .PopColorType();

            if (isFailed && !string.IsNullOrWhiteSpace(errorMessage))
                builder.AppendNewLine().Append($"    {errorMessage[..Math.Min(errorMessage.Length, 160)]}");

            return builder.ToReadOnlySeString();
        }

        // 当前服务器最低价
        builder.AppendNewLine()
               .Append($"    {Lang.Get("BetterMarketBoard-Tooltip-CurrentWorld")}")
               .Append(" (")
               .AppendIcon((uint)BitmapFontIcon.CrossWorld)
               .Append($"{GameState.CurrentWorldData.Name}")
               .Append(")");

        builder.Append(": ")
               .PushColorType(32)
               .Append
               (
                   currentWorldMinPrice is > 0 ?
                       $"{currentWorldMinPrice.Value.ToChineseString()}\ue049" :
                       "-"
               )
               .PopColorType();

        if (!string.IsNullOrEmpty(currentWorldTimeText))
            builder.Append($" ({currentWorldTimeText})");

        // 当前区域最低价格
        builder.AppendNewLine()
               .Append($"    {Lang.Get("BetterMarketBoard-Tooltip-CurrentRegion")}");

        if (!string.IsNullOrEmpty(regionMinWorldName))
        {
            builder.Append(" (")
                   .AppendIcon((uint)BitmapFontIcon.CrossWorld)
                   .Append(regionMinWorldName)
                   .Append(")");
        }

        builder.Append(": ")
               .PushColorType(32)
               .Append
               (
                   regionMinPrice is > 0 ?
                       $"{regionMinPrice.Value.ToChineseString()}\ue049" :
                       "-"
               )
               .PopColorType();

        if (!string.IsNullOrEmpty(regionTimeText))
            builder.Append($" ({regionTimeText})");

        // 当前区域日销量
        builder.AppendNewLine()
               .Append($"    {Lang.Get("BetterMarketBoard-Tooltip-DailySales")}: ")
               .PushColorType(45)
               .Append
               (
                   dailySales > 0 ?
                       Lang.Get
                       (
                           "BetterMarketBoard-DailySales-Format",
                           new Dictionary<string, object>
                           {
                               ["quantity"] = ((int)dailySales).ToChineseString()
                           }
                       ) :
                       "-"
               )
               .PopColorType();

        return builder.ToReadOnlySeString();

    }

    private string GetCurrentRegionName() =>
        allWorlds.FirstOrDefault(region => region.Value.Values.Any(dc => dc.ContainsKey(GameState.CurrentWorld))).Key;

    private static TooltipMarketStats? CreateTooltipMarketStats
    (
        uint                                                    itemID,
        bool                                                    hqOnly,
        RemoteSnapshot<UniversalisAggregatedMarketDataResponse> snapshot
    )
    {
        if (!TryGetAggregatedResult(snapshot, itemID, out var result))
            return null;

        var currentWorldData     = GetAggregatedMarketScope(result, hqOnly);
        var currentWorldMinPrice = ToPrice(currentWorldData.MinListing.World.Price);
        var currentWorldTimeText = ToTimeAgo(currentWorldData.RecentPurchase.World.Timestamp);
        var regionMinListing     = currentWorldData.MinListing.Region;
        var regionMinPrice       = ToPrice(regionMinListing.Price);
        var regionMinWorldName = regionMinListing.WorldID is > 0 ?
                                     LuminaWrapper.GetWorldName(regionMinListing.WorldID.Value) :
                                     string.Empty;
        var regionTimeText = ToTimeAgo(currentWorldData.RecentPurchase.Region.Timestamp);
        var dailySales     = currentWorldData.DailySaleVelocity.Region.Quantity ?? 0;

        return new(currentWorldMinPrice, currentWorldTimeText, regionMinWorldName, regionMinPrice, regionTimeText, dailySales);

        static ulong? ToPrice
        (
            double? price
        ) =>
            price is > 0 ?
                (ulong)Math.Round(price.Value) :
                null;

        static string? ToTimeAgo
        (
            long? timestamp
        ) =>
            timestamp is > 0 ?
                DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).LocalDateTime.TimeAgo() :
                null;

    }

    private static UniversalisAggregatedMarketScope GetAggregatedMarketScope
    (
        UniversalisAggregatedMarketResult result,
        bool                              hqOnly
    ) =>
        hqOnly ?
            result.HQ :
            result.NQ;

    private static bool TryGetAggregatedResult
    (
        RemoteSnapshot<UniversalisAggregatedMarketDataResponse> snapshot,
        uint                                                    itemID,
        out UniversalisAggregatedMarketResult?                  result
    )
    {
        result = null;

        if (!snapshot.HasValue || snapshot.Value == null)
            return false;

        result = snapshot.Value.Results.FirstOrDefault(x => x.ItemID == itemID);
        return result != null;
    }

    private sealed record TooltipMarketStats
    (
        ulong?  CurrentWorldMinPrice,
        string? CurrentWorldTimeText,
        string  RegionMinWorldName,
        ulong?  RegionMinPrice,
        string? RegionTimeText,
        float   DailySales
    );

    #region 常量

    private const int TOOLTIP_REQUEST_DELAY_MS = 400;

    #endregion
}
