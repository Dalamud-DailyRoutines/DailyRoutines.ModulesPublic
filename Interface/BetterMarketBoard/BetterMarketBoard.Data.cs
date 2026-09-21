using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using DailyRoutines.RemoteInteraction.Universalis;
using DailyRoutines.RemoteInteraction.Universalis.Models.Requests;
using DailyRoutines.RemoteInteraction.Universalis.Models.Responses;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private readonly record struct SearchCategoryGroup
    (
        ItemSearchCategory Category,
        List<Item>         Items
    );

    private sealed class CachedValue<T>
    {
        public int Version = -1;
        public T?  Value;
    }

    private sealed class HistoryDataSet
    {
        public List<HistoryEntry> Entries = [];
        public int                TotalCount;
        public uint               TotalQty;
        public ulong              AvgPrice;
        public int                HQCount;
        public int                HQPercent;
        public bool               IsCanBeHQ;
        public bool               IsAnyHQ;
        public ulong              AvgNQPrice;
        public ulong              AvgHQPrice;
    }

    private sealed class TrendDataSet
    {
        public List<HistoryEntry> Entries = [];
        public double             XMin;
        public double             XMax;
        public double             VisualMin;
        public double             VisualMax;
        public ulong              AvgHistoryPrice;
        public bool               IsCanBeHQ;
        public int                HQCount;
        public int                NQCount;
        public ulong              AvgNQPrice;
        public ulong              AvgHQPrice;
    }

    private sealed class ListingsDataSet
    {
        public List<UniversalisMarketListing> Listings = [];
        public UniversalisMarketItemData?     Source;
        public bool                           IsAnyHQ;
        public bool                           IsAnyOnMannequin;
        public int                            TotalCount;
        public uint                           TotalQty;
    }

    private sealed class LocalListingsDataSet
    {
        public List<MarketBoardListing> Listings = [];
        public bool                     IsAnyHQ;
        public bool                     IsAnyOnMannequin;
        public bool                     IsAnyMateria;
        public int                      TotalCount;
        public uint                     TotalQty;
    }

    private sealed class WorldPriceRanks
    (
        List<RankedWorldPriceRow> valid,
        List<RankedWorldPriceRow> cheapest,
        List<RankedWorldPriceRow> expensive,
        WorldPriceRow             current
    )
    {
        public List<RankedWorldPriceRow> Valid     = valid;
        public List<RankedWorldPriceRow> Cheapest  = cheapest;
        public List<RankedWorldPriceRow> Expensive = expensive;
        public WorldPriceRow             Current   = current;
    }

    private static (double VisualMin, double VisualMax) CalculateVisualPriceRange
    (
        ulong[] prices
    )
    {
        if (prices.Length == 0) return (0, 100);

        Array.Sort(prices);
        var sorted = prices;
        var min    = (double)sorted[0];
        var max    = (double)sorted[^1];

        if (sorted.Length < 4)
            return (min, max);

        var q1Index = (int)(sorted.Length * 0.25);
        var q3Index = (int)(sorted.Length * 0.75);
        var q1      = (double)sorted[q1Index];
        var q3      = (double)sorted[q3Index];
        var iqr     = q3 - q1;

        var median = (double)sorted[sorted.Length / 2];
        var upperFence = iqr > 0 ?
                             q3 + (3.0 * iqr) :
                             median * 3.0;

        var visualMax = Math.Min(max, Math.Max(upperFence, median * 1.5));
        var visualMin = Math.Max(0d, min);

        return (visualMin, visualMax);
    }

    private sealed class MarketDataProvider
    (
        BetterMarketBoard owner
    )
    {
        public uint   SelectedWorldID     { get; private set; }
        public string EffectiveRegionName { get; private set; } = string.Empty;
        public bool   HQOnly;

        public uint SelectedItemID { get; private set; }

        public long LastSelectTime { get; private set; }

        public bool IsViewingCurrentWorld =>
            SelectedWorldID == GameState.CurrentWorld;

        public WorldPriceRow MinPriceData { get; private set; }

        public WorldPriceRow MaxPriceData { get; private set; }

        public IReadOnlyDictionary<string, List<WorldPriceRow>> DCWorldPrices => cachedDCWorldPrices;

        private int onlineDataVersion;
        private int onlineHistoryVersion;
        private int itemEpoch;

        private readonly Dictionary<(uint ItemID, uint WorldID, bool HQOnly), UniversalisMarketDataResponse> onlineDataCache   = [];
        private readonly Dictionary<(uint ItemID, uint WorldID, bool HQOnly), IDisposable>                   subscriptionCache = [];

        private readonly Dictionary<(uint ItemID, uint WorldID), UniversalisAggregatedMarketDataResponse> onlineAggregatedCache       = [];
        private readonly Dictionary<(uint ItemID, uint WorldID), IDisposable>                             aggregatedSubscriptionCache = [];

        private readonly Dictionary<(uint ItemID, uint WorldID), UniversalisMarketHistoryResponse> onlineHistoryCache       = [];
        private readonly Dictionary<(uint ItemID, uint WorldID), IDisposable>                      historySubscriptionCache = [];

        private readonly Dictionary<(uint ItemID, string Scope), IDisposable> tooltipAggregatedSubscriptionCache = [];

        private readonly Dictionary<string, List<WorldPriceRow>> cachedDCWorldPrices = [];
        private          WorldPriceRanks?                        worldPriceRanks;
        private          string?                                 priceTableRegion;
        private          bool                                    priceTableHQOnly;
        private          bool                                    worldPriceTableDirty = true;

        private readonly Dictionary<(uint ItemID, uint WorldID, bool HQOnly), CachedValue<HistoryDataSet>>  historyDataCache  = [];
        private readonly Dictionary<(uint ItemID, uint WorldID, bool HQOnly), CachedValue<TrendDataSet>>    trendDataCache    = [];
        private readonly Dictionary<(uint ItemID, uint WorldID, bool HQOnly), CachedValue<ListingsDataSet>> listingsDataCache = [];

        private (int ItemEpoch, uint ItemID, bool HQOnly, uint ListingCount, int ContentHash) localListingsFingerprint;
        private LocalListingsDataSet?                                                         localListingsData;

        private bool                                                    localListingsStale;
        private long                                                    localSearchRetryDeadline;
        private (uint SearchItemId, uint EntryCount, uint ListingCount) localListingsBaseline;

        private readonly Dictionary<uint, ItemSourceInfo?> itemSourceCache  = [];
        private readonly Dictionary<uint, uint?>           npcGilPriceCache = [];

        private string?                    searchInputCache;
        private List<Item>?                searchResultRefCache;
        private List<SearchCategoryGroup>? searchGroupsCache;

        private readonly Dictionary<ulong, MarketBoardListing> selectedListings = [];

        public IReadOnlyDictionary<ulong, MarketBoardListing> SelectedListings => selectedListings;

        public void AddListing
        (
            ulong              listingID,
            MarketBoardListing listing
        ) =>
            selectedListings[listingID] = listing;

        public void ToggleListing
        (
            ulong              listingID,
            MarketBoardListing listing
        )
        {
            if (!selectedListings.Remove(listingID))
                selectedListings[listingID] = listing;
        }

        public void RemoveListing
        (
            ulong listingID
        ) =>
            selectedListings.Remove(listingID);

        public void ClearSelectedListings() =>
            selectedListings.Clear();

        #region 选择与请求

        public void SelectItem
        (
            uint    itemID,
            string? regionName = null,
            bool?   hqOnly     = null
        )
        {
            var info = InfoProxy;
            if (info == null) return;

            var targetHQOnly = hqOnly ?? HQOnly;

            if (targetHQOnly && (!LuminaGetter.TryGetRow<Item>(itemID, out var itemData) || !itemData.CanBeHq))
                targetHQOnly = false;

            var isNewItem     = SelectedItemID != itemID || HQOnly != targetHQOnly;
            var isItemChanged = SelectedItemID != itemID;

            if (isItemChanged)
                itemEpoch++;

            SelectedItemID = itemID;
            HQOnly         = targetHQOnly;
            LastSelectTime = Environment.TickCount64;
            AddToItemSearchHistory(itemID);

            if (isNewItem)
            {
                selectedListings.Clear();
                ClearAllData();
            }

            info->SearchItemId = itemID;

            RequestAllWorldsData(itemID, regionName, targetHQOnly);

            if (IsViewingCurrentWorld)
            {
                if (IsAbleToSearchMarket())
                    RequestLocalSearchData(itemID);
                else
                    info->ClearListData();
            }
        }

        public bool FollowLocalSearch
        (
            uint itemID
        )
        {
            var info = InfoProxy;
            if (info == null || itemID == 0) return false;

            var isItemChanged = SelectedItemID != itemID;

            if (isItemChanged)
            {
                itemEpoch++;
                HQOnly = false;
                selectedListings.Clear();
            }

            SelectedItemID = itemID;
            LastSelectTime = Environment.TickCount64;

            info->SearchItemId = itemID;

            if (!IsViewingCurrentWorld) return false;

            return RequestLocalSearchData(itemID);
        }

        public void SelectWorld
        (
            uint worldID
        )
        {
            if (SelectedItemID == 0) return;

            if (SelectedWorldID != worldID)
            {
                selectedListings.Clear();
                DisposeSelectedWorldData(SelectedItemID, SelectedWorldID);
                ClearDerivedCaches();
                localListingsData        = null;
                localListingsFingerprint = default;
                onlineDataVersion++;
                onlineHistoryVersion++;
            }

            SelectedWorldID = worldID;
            SelectItem(SelectedItemID, null, HQOnly);
        }

        public void SelectRegion
        (
            string regionName
        )
        {
            if (!string.Equals(EffectiveRegionName, regionName, StringComparison.Ordinal))
            {
                selectedListings.Clear();
                ClearAllData();
            }

            EffectiveRegionName = regionName;

            if (!IsViewingCurrentWorld &&
                (!owner.allWorlds.TryGetValue(regionName, out var region) ||
                 !region.Values.Any(dc => dc.ContainsKey(SelectedWorldID))))
                SelectedWorldID = GameState.CurrentWorld;

            if (SelectedItemID == 0) return;

            SelectItem(SelectedItemID, regionName, HQOnly);
        }

        public void ToggleHQ()
        {
            if (SelectedItemID == 0) return;

            SelectItem(SelectedItemID, null, !HQOnly);
        }

        public void Reload()
        {
            if (SelectedItemID == 0) return;

            ClearAllData();
            SelectItem(SelectedItemID, null, HQOnly);
        }

        public void AnchorWorld() =>
            SelectedWorldID = GameState.CurrentWorld;

        public void EnsureAnchored()
        {
            if (SelectedWorldID == 0)
                AnchorWorld();

            AnchorRegion();
        }

        public void ResyncAfterWorldChange()
        {
            SelectedWorldID     = GameState.CurrentWorld;
            EffectiveRegionName = owner.GetCurrentRegionName();

            var info = InfoProxy;
            if (info != null)
                info->ClearListData();

            selectedListings.Clear();
            ClearAllData();

            if (SelectedItemID != 0)
                SelectItem(SelectedItemID, null, HQOnly);

            localListingsStale       = true;
            localSearchRetryDeadline = Environment.TickCount64 + 60_000;
            localListingsBaseline = info == null ?
                                        default :
                                        (info->SearchItemId, info->EntryCount, info->ListingCount);
        }

        public void RetryLocalSearchIfStale()
        {
            if (!localListingsStale) return;
            if (Environment.TickCount64 > localSearchRetryDeadline) return;
            if (SelectedItemID          == 0) return;
            if (!IsViewingCurrentWorld) return;
            if (!IsAbleToSearchMarket()) return;
            if (GameState.Instance().IsMarketListingsStuck) return;

            RequestLocalSearchData(SelectedItemID);
        }

        public void AnchorRegion()
        {
            if (string.IsNullOrEmpty(EffectiveRegionName))
                EffectiveRegionName = owner.GetCurrentRegionName();
        }

        public void EnsurePriceData
        (
            uint itemID,
            bool hqOnly
        )
        {
            if (itemID == 0 || owner.allWorlds.Count == 0)
                return;

            EnsureAnchored();
            RequestAllWorldsData(itemID, null, hqOnly);
        }

        public static bool RequestLocalSearchData
        (
            uint itemID
        )
        {
            if (itemID == 0 || InfoProxy == null)
                return false;

            InfoProxy->EndRequest();
            InfoProxy->SearchItemId = itemID;
            return InfoProxy->RequestData();
        }

        public static bool SendBuyRequest
        (
            MarketBoardListing item
        )
        {
            if (IsOwnRetainer(item.RetainerId))
            {
                // 无法购买自己的雇员所出售的道具。
                RaptureLogModule.Instance()->ShowLogMessage(468);
                return false;
            }

            InfoProxy->SetLastPurchasedItem(&item);
            return InfoProxy->SendPurchaseRequestPacket();
        }

        private void AddToItemSearchHistory
        (
            uint itemID
        )
        {
            if (!LuminaGetter.TryGetRow<Item>(itemID, out _)) return;

            owner.config.HistoryItems[itemID] = new()
            {
                ItemID     = itemID,
                AccessTime = StandardTimeManager.Instance().Now
            };

            owner.config.HistoryItems = owner.config.HistoryItems
                                             .OrderByDescending(x => x.Value.AccessTime)
                                             .Take(50)
                                             .ToDictionary(x => x.Key, x => x.Value);
            owner.config.Save(ModuleManager.Instance().GetModule<BetterMarketBoard>());
        }

        public void ClearAllData()
        {
            selectedListings.Clear();
            DisposeAllSubscriptions();

            subscriptionCache.Clear();
            historySubscriptionCache.Clear();
            aggregatedSubscriptionCache.Clear();
            tooltipAggregatedSubscriptionCache.Clear();
            onlineDataCache.Clear();
            onlineAggregatedCache.Clear();
            onlineHistoryCache.Clear();
            ClearDerivedCaches();
            cachedDCWorldPrices.Clear();
            worldPriceRanks          = null;
            worldPriceTableDirty     = true;
            localListingsData        = null;
            localListingsFingerprint = default;
            localListingsStale       = false;
            localSearchRetryDeadline = 0;
            localListingsBaseline    = default;
            itemEpoch++;
            onlineDataVersion++;
            onlineHistoryVersion++;
        }

        private void ClearDerivedCaches()
        {
            historyDataCache.Clear();
            trendDataCache.Clear();
            listingsDataCache.Clear();
        }

        private void DisposeSelectedWorldData
        (
            uint itemID,
            uint worldID
        )
        {
            if (worldID == 0)
                return;

            foreach (var key in subscriptionCache.Keys
                                                 .Where(key => key.ItemID == itemID && key.WorldID == worldID)
                                                 .ToList())
            {
                subscriptionCache[key].Dispose();
                subscriptionCache.Remove(key);
                onlineDataCache.Remove(key);
            }

            var historyKey = (ItemID: itemID, WorldID: worldID);
            if (historySubscriptionCache.Remove(historyKey, out var historySubscription))
                historySubscription.Dispose();

            onlineHistoryCache.Remove(historyKey);
        }

        private void DisposeAllSubscriptions()
        {
            foreach (var subscription in subscriptionCache.Values)
            {
                if (subscription != null)
                    subscription.Dispose();
            }

            foreach (var subscription in historySubscriptionCache.Values)
            {
                if (subscription != null)
                    subscription.Dispose();
            }

            foreach (var subscription in aggregatedSubscriptionCache.Values)
            {
                if (subscription != null)
                    subscription.Dispose();
            }

            foreach (var subscription in tooltipAggregatedSubscriptionCache.Values)
            {
                if (subscription != null)
                    subscription.Dispose();
            }
        }

        private bool RequestAllWorldsData
        (
            uint    itemID,
            string? regionName = null,
            bool    hqOnly     = false
        )
        {
            if (owner.allWorlds.Count == 0) return false;

            Dictionary<string, Dictionary<uint, string>>? targetRegion = null;
            var targetRegionName = !string.IsNullOrEmpty(regionName) ?
                                       regionName :
                                       EffectiveRegionName;

            if (!string.IsNullOrEmpty(targetRegionName))
                owner.allWorlds.TryGetValue(targetRegionName, out targetRegion);

            targetRegion ??= owner.allWorlds.Values.FirstOrDefault(r => r.Values.Any(dc => dc.ContainsKey(GameState.CurrentWorld)));

            if (targetRegion == null)
                return false;

            var historyParam = new UniversalisMarketHistoryRequestParams
            {
                EntriesToReturn = 200,
                StatsWithin     = 15552000_000, // 毫秒, 半年
                EntriesWithin   = 15552000_000  // 毫秒, 半年
            };
            var marketParam = new UniversalisMarketDataRequestParams
            {
                HQ = hqOnly
            };

            var epoch  = itemEpoch;
            var result = false;

            foreach (var dc in targetRegion.Values)
            {
                foreach (var (worldID, worldName) in dc)
                {
                    var aggregatedCacheKey = (itemID, worldID);
                    _ = RemoteUniversalisAggregatedMarket.GetOrRequest([itemID], worldName);

                    if (!aggregatedSubscriptionCache.ContainsKey(aggregatedCacheKey))
                    {
                        aggregatedSubscriptionCache[aggregatedCacheKey] = RemoteUniversalisAggregatedMarket.Observe
                        (
                            [itemID],
                            worldName,
                            snapshot =>
                            {
                                if (epoch != itemEpoch)
                                    return;

                                if (!snapshot.HasValue || snapshot.Value is not { } data)
                                    return;

                                if (data.Results.All(x => x.ItemID != itemID))
                                    return;

                                onlineAggregatedCache[aggregatedCacheKey] = data;
                                worldPriceTableDirty                      = true;
                                TooltipManager.Instance().TriggerItemDetailUpdate();
                            }
                        );

                        result = true;
                    }
                }
            }

            var selectedWorldName = targetRegion.Values.SelectMany(static dc => dc)
                                                .FirstOrDefault(world => world.Key == SelectedWorldID)
                                                .Value;

            if (string.IsNullOrEmpty(selectedWorldName))
                return result;

            var marketCacheKey = (itemID, SelectedWorldID, hqOnly);
            _ = RemoteUniversalisMarket.GetOrRequest([itemID], selectedWorldName, marketParam);

            if (!subscriptionCache.ContainsKey(marketCacheKey))
            {
                subscriptionCache[marketCacheKey] = RemoteUniversalisMarket.Observe
                (
                    [itemID],
                    selectedWorldName,
                    snapshot =>
                    {
                        if (epoch != itemEpoch)
                            return;

                        if (!snapshot.HasValue || snapshot.Value is not { } data)
                            return;

                        if (SelectedWorldID != data.WorldID)
                            return;

                        onlineDataCache[marketCacheKey] = data;
                        onlineDataVersion++;
                        TooltipManager.Instance().TriggerItemDetailUpdate();
                    },
                    marketParam
                );

                result = true;
            }

            var historyCacheKey = (itemID, SelectedWorldID);
            _ = RemoteUniversalisHistory.GetOrRequest([itemID], selectedWorldName, historyParam);

            if (!historySubscriptionCache.ContainsKey(historyCacheKey))
            {
                historySubscriptionCache[historyCacheKey] = RemoteUniversalisHistory.Observe
                (
                    [itemID],
                    selectedWorldName,
                    snapshot =>
                    {
                        if (epoch != itemEpoch)
                            return;

                        if (!snapshot.HasValue || snapshot.Value is not { } data)
                            return;

                        if (SelectedWorldID != data.WorldID)
                            return;

                        onlineHistoryCache[historyCacheKey] = data;
                        onlineHistoryVersion++;
                        TooltipManager.Instance().TriggerItemDetailUpdate();
                    },
                    historyParam
                );

                result = true;
            }

            return result;
        }

        #endregion

        #region 派生数据

        private static T? GetOrBuild<T>
        (
            CachedValue<T?> slot,
            int             version,
            Func<T?>        build
        )
            where T : class
        {
            if (slot.Version != version)
            {
                slot.Value   = build();
                slot.Version = version;
            }

            return slot.Value;
        }

        private static HistoryEntry ToHistoryEntry
        (
            UniversalisHistorySale sale
        )
        {
            var saleTime = sale.GetSaleTime().ToLocalTime();
            return new(((DateTimeOffset)saleTime).ToUnixTimeSeconds(), saleTime, sale.PricePerUnit, sale.Quantity, sale.HQ);
        }

        public HistoryDataSet? GetHistoryDataSet
        (
            uint  itemID,
            bool? hqOnly = null
        )
        {
            if (itemID == 0) return null;

            var targetHQOnly = hqOnly ?? HQOnly;
            var key          = (ItemID: itemID, WorldID: SelectedWorldID, HQOnly: targetHQOnly);
            var slot         = historyDataCache.GetOrAdd(key, static _ => new());

            return GetOrBuild
            (
                slot,
                onlineHistoryVersion,
                () => BuildHistoryDataSet(itemID, key.WorldID, key.HQOnly)
            );
        }

        private HistoryDataSet? BuildHistoryDataSet
        (
            uint itemID,
            uint worldID,
            bool hqOnly
        )
        {
            if (!onlineHistoryCache.TryGetValue((itemID, worldID), out var response) ||
                !response.Items.TryGetValue(itemID, out var itemHistory)             ||
                itemHistory.Entries is not { Count: > 0 })
                return null;

            var isAnyHQ = itemHistory.Entries.Any(x => x.HQ);

            var entries = itemHistory.Entries
                                     .Where(x => x is { OnMannequin: false, PricePerUnit: > 0 } && (!hqOnly || x.HQ))
                                     .Select(ToHistoryEntry)
                                     .ToList();
            if (entries.Count == 0) return null;

            var isCanBeHQ   = LuminaGetter.TryGetRow<Item>(itemID, out var item) && item.CanBeHq;
            var totalCount  = entries.Count;
            var totalQty    = 0U;
            var totalAmount = 0.0;
            var hqCount     = 0;
            var nqAmount    = 0.0;
            var hqAmount    = 0.0;
            var nqQty       = 0U;
            var hqQty       = 0U;

            foreach (var entry in entries)
            {
                totalQty    += entry.Quantity;
                totalAmount += (double)entry.PricePerUnit * entry.Quantity;

                if (entry.IsHQ)
                {
                    hqCount++;
                    hqAmount += (double)entry.PricePerUnit * entry.Quantity;
                    hqQty    += entry.Quantity;
                }
                else
                {
                    nqAmount += (double)entry.PricePerUnit * entry.Quantity;
                    nqQty    += entry.Quantity;
                }
            }

            var avgPrice = totalQty > 0 ?
                               (ulong)Math.Round(totalAmount / totalQty) :
                               0;
            var hqPercent = (int)Math.Round((double)hqCount / totalCount * 100);

            ulong avgNQPrice = 0;
            ulong avgHQPrice = 0;

            if (isCanBeHQ)
            {
                avgNQPrice = nqQty > 0 ?
                                 (ulong)Math.Round(nqAmount / nqQty) :
                                 0;
                avgHQPrice = hqQty > 0 ?
                                 (ulong)Math.Round(hqAmount / hqQty) :
                                 0;
            }

            return new()
            {
                Entries    = entries,
                TotalCount = totalCount,
                TotalQty   = totalQty,
                AvgPrice   = avgPrice,
                HQCount    = hqCount,
                HQPercent  = hqPercent,
                IsCanBeHQ  = isCanBeHQ,
                IsAnyHQ    = isAnyHQ,
                AvgNQPrice = avgNQPrice,
                AvgHQPrice = avgHQPrice
            };
        }

        public TrendDataSet? GetTrendDataSet
        (
            uint itemID
        )
        {
            if (itemID == 0) return null;

            var key  = (ItemID: itemID, WorldID: SelectedWorldID, HQOnly);
            var slot = trendDataCache.GetOrAdd(key, static _ => new());

            return GetOrBuild
            (
                slot,
                onlineHistoryVersion,
                () => BuildTrendDataSet(itemID, key.WorldID, key.HQOnly)
            );
        }

        private TrendDataSet? BuildTrendDataSet
        (
            uint itemID,
            uint worldID,
            bool hqOnly
        )
        {
            if (!onlineHistoryCache.TryGetValue((itemID, worldID), out var response) ||
                !response.Items.TryGetValue(itemID, out var itemHistory)             ||
                itemHistory.Entries is not { Count: > 0 })
                return null;

            var historyEntries = new List<HistoryEntry>();

            foreach (var sale in itemHistory.Entries)
            {
                if (sale.OnMannequin) continue;
                if (hqOnly && !sale.HQ) continue;
                if (sale.PricePerUnit == 0) continue;

                historyEntries.Add(ToHistoryEntry(sale));
            }

            if (historyEntries.Count == 0)
                return null;

            historyEntries.Sort((left, right) => left.SaleTime.CompareTo(right.SaleTime));

            var totalHistoryQty    = historyEntries.Sum(x => x.Quantity);
            var totalHistoryAmount = historyEntries.Sum(x => (double)(x.PricePerUnit * x.Quantity));
            var avgHistoryPrice = totalHistoryQty > 0 ?
                                      (ulong)Math.Round(totalHistoryAmount / totalHistoryQty) :
                                      0;
            var isCanBeHQ = LuminaGetter.TryGetRow<Item>(itemID, out var itemData) && itemData.CanBeHq;

            ulong avgNQPrice = 0;
            ulong avgHQPrice = 0;
            var   hqCount    = 0;

            if (isCanBeHQ)
            {
                var nqAmount = 0.0;
                var hqAmount = 0.0;
                var nqQtySum = 0L;
                var hqQtySum = 0L;

                foreach (var entry in historyEntries)
                {
                    if (entry.IsHQ)
                    {
                        hqCount++;
                        hqAmount += entry.PricePerUnit * entry.Quantity;
                        hqQtySum += entry.Quantity;
                    }
                    else
                    {
                        nqAmount += entry.PricePerUnit * entry.Quantity;
                        nqQtySum += entry.Quantity;
                    }
                }

                avgNQPrice = nqQtySum > 0 ?
                                 (ulong)Math.Round(nqAmount / nqQtySum) :
                                 0;
                avgHQPrice = hqQtySum > 0 ?
                                 (ulong)Math.Round(hqAmount / hqQtySum) :
                                 0;
            }

            var nqCount = historyEntries.Count - hqCount;

            var (visualMinPrice, visualMaxPrice) = CalculateVisualPriceRange(historyEntries.Select(x => x.PricePerUnit).ToArray());
            var yMin = visualMinPrice;
            var yMax = visualMaxPrice;

            if (Math.Abs(yMax - yMin) < double.Epsilon)
            {
                var padding = Math.Max(1d, yMax * 0.05d);
                yMin =  Math.Max(0d, yMin - padding);
                yMax += padding;
            }
            else
            {
                var padding = (yMax - yMin) * 0.08d;
                yMin =  Math.Max(0d, yMin - padding);
                yMax += padding;
            }

            var xMin = historyEntries.Min(x => x.X);
            var xMax = historyEntries.Max(x => x.X);

            if (Math.Abs(xMax - xMin) < double.Epsilon)
            {
                xMin -= 3600;
                xMax += 3600;
            }
            else
            {
                var padding = (xMax - xMin) * 0.05d;
                xMin -= padding;
                xMax += padding;
            }

            return new()
            {
                Entries         = historyEntries,
                XMin            = xMin,
                XMax            = xMax,
                VisualMin       = yMin,
                VisualMax       = yMax,
                AvgHistoryPrice = avgHistoryPrice,
                IsCanBeHQ       = isCanBeHQ,
                HQCount         = hqCount,
                NQCount         = nqCount,
                AvgNQPrice      = avgNQPrice,
                AvgHQPrice      = avgHQPrice
            };
        }

        public ListingsDataSet? GetListingsDataSet
        (
            uint itemID
        )
        {
            if (itemID == 0) return null;

            var key  = (ItemID: itemID, WorldID: SelectedWorldID, HQOnly);
            var slot = listingsDataCache.GetOrAdd(key, static _ => new());

            return GetOrBuild
            (
                slot,
                onlineDataVersion,
                () => BuildListingsDataSet(itemID, key.WorldID, key.HQOnly)
            );
        }

        private ListingsDataSet? BuildListingsDataSet
        (
            uint itemID,
            uint worldID,
            bool hqOnly
        )
        {
            if (!onlineDataCache.TryGetValue((itemID, worldID, hqOnly), out var response) ||
                !response.Items.TryGetValue(itemID, out var uniItemData))
                return null;

            var listings = (uniItemData.Listings ?? [])
                           .Where(x => x.PricePerUnit > 0 && (x.HQ || !hqOnly))
                           .OrderBy(x => x.PricePerUnit)
                           .ToList();

            return new()
            {
                Listings         = listings,
                Source           = uniItemData,
                IsAnyHQ          = listings.Any(x => x.HQ),
                IsAnyOnMannequin = listings.Any(x => x.OnMannequin),
                TotalCount       = listings.Count,
                TotalQty         = listings.Aggregate(0U, (acc, l) => acc + l.Quantity)
            };
        }

        public LocalListingsDataSet GetLocalListingsDataSet
        (
            InfoProxyItemSearch* info
        )
        {
            if (localListingsStale)
            {
                var currentState = (info->SearchItemId, info->EntryCount, info->ListingCount);

                if (currentState == localListingsBaseline)
                    return EmptyLocalListings();

                localListingsStale = false;
            }

            if (!info->IsFullyReceived())
                return EmptyLocalListings();

            var validCount     = (int)Math.Min(info->EntryCount, info->ListingCount);
            var sourceListings = info->Listings.ToArray()[..validCount];
            var contentHash    = CalculateLocalListingsHash(sourceListings);
            var fingerprint    = (itemEpoch, info->SearchItemId, HQOnly, info->ListingCount, contentHash);
            if (localListingsData != null && localListingsFingerprint == fingerprint)
                return localListingsData;

            localListingsFingerprint = fingerprint;
            localListingsData        = BuildLocalListingsDataSet(info->SearchItemId, sourceListings);
            ReconcileSelectedListings(localListingsData.Listings);
            worldPriceTableDirty = true;
            return localListingsData;
        }

        private static readonly LocalListingsDataSet EmptyLocalListingsData = new();

        private static LocalListingsDataSet EmptyLocalListings() => EmptyLocalListingsData;

        private static int CalculateLocalListingsHash
        (
            IReadOnlyList<MarketBoardListing> listings
        )
        {
            var hash = new HashCode();

            foreach (var listing in listings)
            {
                hash.Add(listing.ListingId);
                hash.Add(listing.ItemId);
                hash.Add(listing.UnitPrice);
                hash.Add(listing.Quantity);
                hash.Add(listing.IsHqItem);
                hash.Add(listing.IsSellingAsSet);
                hash.Add(listing.MateriaCount);
                hash.Add(listing.TotalTax);
            }

            return hash.ToHashCode();
        }

        private void ReconcileSelectedListings
        (
            IReadOnlyList<MarketBoardListing> listings
        )
        {
            var currentListings = listings.DistinctBy(listing => listing.ListingId)
                                          .ToDictionary(listing => listing.ListingId);

            foreach (var listingID in selectedListings.Keys.ToList())
            {
                if (!currentListings.TryGetValue(listingID, out var listing))
                {
                    selectedListings.Remove(listingID);
                    continue;
                }

                if (IsOwnRetainer(listing.RetainerId))
                {
                    selectedListings.Remove(listingID);
                    continue;
                }

                selectedListings[listingID] = listing;
            }
        }

        private LocalListingsDataSet BuildLocalListingsDataSet
        (
            uint                              itemID,
            IReadOnlyList<MarketBoardListing> sourceListings
        )
        {
            var listingsArray = sourceListings
                                .Where(x => x.ItemId == itemID && x.UnitPrice != 0 && (x.IsHqItem || !HQOnly))
                                .OrderBy(x => x.UnitPrice)
                                .ToArray();

            var isAnyHQ          = listingsArray.Any(x => x.IsHqItem);
            var isAnyOnMannequin = listingsArray.Any(x => x.IsSellingAsSet);
            var isAnyMateria = LuminaGetter.TryGetRow<Item>(itemID, out var itemData) &&
                               itemData.MateriaSlotCount > 0                          &&
                               listingsArray.Any(x => x.MateriaCount > 0);

            return new()
            {
                Listings         = [.. listingsArray],
                IsAnyHQ          = isAnyHQ,
                IsAnyOnMannequin = isAnyOnMannequin,
                IsAnyMateria     = isAnyMateria,
                TotalCount       = listingsArray.Length,
                TotalQty         = listingsArray.Aggregate(0U, (acc, l) => acc + l.Quantity)
            };
        }

        public List<SearchCategoryGroup> GetSearchGroups
        (
            string input
        )
        {
            var result = owner.searcher.SearchResult;
            if (searchGroupsCache != null  &&
                searchInputCache  == input &&
                ReferenceEquals(searchResultRefCache, result))
                return searchGroupsCache;

            searchInputCache     = input;
            searchResultRefCache = result;
            searchGroupsCache =
            [
                .. result.GroupBy(x => x.ItemSearchCategory.Value.RowId)
                         .Select(g => new SearchCategoryGroup(LuminaGetter.GetRowOrDefault<ItemSearchCategory>(g.Key), [.. g]))
            ];
            return searchGroupsCache;
        }

        #endregion

        #region 价格表

        public WorldPriceRanks? GetWorldPriceRanks
        (
            uint itemID
        )
        {
            var regionName = EffectiveRegionName;

            if (string.IsNullOrEmpty(regionName)                              ||
                !owner.allWorlds.TryGetValue(regionName, out var dcsInRegion) ||
                dcsInRegion.Count == 0)
            {
                worldPriceRanks = null;
                return null;
            }

            var stateChanged = priceTableRegion != regionName ||
                               priceTableHQOnly != HQOnly     ||
                               (InfoProxy != null && InfoProxy->EntryCount > 0 && cachedDCWorldPrices.Count == 0);
            var needRebuild = stateChanged ||
                              (worldPriceTableDirty && Throttler.Shared.Throttle("BetterMarketBoard-PriceTableUpdate"));

            if (needRebuild)
            {
                priceTableRegion = regionName;
                priceTableHQOnly = HQOnly;
                cachedDCWorldPrices.Clear();

                foreach (var (dcName, worldsInDC) in dcsInRegion)
                {
                    var worldPricesList = new List<WorldPriceRow>();

                    foreach (var (worldID, worldName) in worldsInDC)
                    {
                        var minPrice = ulong.MaxValue;

                        if (worldID == GameState.CurrentWorld)
                        {
                            if (IsAbleToSearchLocalMarket() && InfoProxy != null && InfoProxy->SearchItemId == itemID && InfoProxy->IsFullyReceived(itemID))
                            {
                                var listings = InfoProxy->Listings.ToArray()
                                                                  .Where
                                                                  (x => x.ItemId    == itemID &&
                                                                        x.UnitPrice > 0       &&
                                                                        (x.IsHqItem || !HQOnly)
                                                                  )
                                                                  .ToList();
                                if (listings.Count > 0)
                                    minPrice = listings.Min(x => x.UnitPrice);
                            }
                            else
                                _ = TryGetOnlineAggregatedMinPrice(itemID, worldID, out minPrice);
                        }
                        else
                            _ = TryGetOnlineAggregatedMinPrice(itemID, worldID, out minPrice);

                        worldPricesList.Add(new(worldID, worldName, minPrice));
                    }

                    cachedDCWorldPrices[dcName] = worldPricesList.OrderBy(w => w.WorldName).ToList();
                }

                var allPrices = cachedDCWorldPrices.SelectMany(x => x.Value).ToList();

                if (allPrices.Count > 0)
                {
                    MinPriceData = allPrices.MinBy(x => x.MinPrice);

                    var validPrices = allPrices.Where(x => x.MinPrice != ulong.MaxValue).ToList();
                    MaxPriceData = validPrices.Count > 0 ?
                                       validPrices.MaxBy(x => x.MinPrice) :
                                       default;
                }
                else
                {
                    MinPriceData = default;
                    MaxPriceData = default;
                }

                worldPriceRanks      = BuildWorldPriceRanks();
                worldPriceTableDirty = false;
            }

            return worldPriceRanks;
        }

        private WorldPriceRanks BuildWorldPriceRanks()
        {
            var validWorldPrices = cachedDCWorldPrices
                                   .SelectMany
                                   (dc => dc.Value.Where(world => world.MinPrice != ulong.MaxValue)
                                            .Select(world => new RankedWorldPriceRow(dc.Key, world.WorldID, world.WorldName, world.MinPrice))
                                   )
                                   .OrderBy(x => x.MinPrice)
                                   .ThenBy(x => x.WorldName)
                                   .ToList();

            var cheapestWorlds   = validWorldPrices.Take(Math.Min(3, Math.Max(1, validWorldPrices.Count / 2))).ToList();
            var cheapestWorldIDs = cheapestWorlds.Select(x => x.WorldID).ToHashSet();
            var expensiveWorlds = validWorldPrices.Where(x => !cheapestWorldIDs.Contains(x.WorldID))
                                                  .OrderByDescending(x => x.MinPrice)
                                                  .ThenBy(x => x.WorldName)
                                                  .Take(Math.Min(3, validWorldPrices.Count - cheapestWorldIDs.Count))
                                                  .OrderBy(x => x.MinPrice)
                                                  .ThenBy(x => x.WorldName)
                                                  .ToList();

            if (validWorldPrices.Count == 1)
            {
                expensiveWorlds = [.. validWorldPrices];
                cheapestWorlds.Clear();
            }

            var currentWorldPrice = cachedDCWorldPrices.SelectMany(x => x.Value)
                                                       .FirstOrDefault(x => x.WorldID == GameState.CurrentWorld);

            return new(validWorldPrices, cheapestWorlds, expensiveWorlds, currentWorldPrice);
        }

        private bool TryGetOnlineAggregatedMinPrice
        (
            uint      itemID,
            uint      worldID,
            out ulong minPrice
        )
        {
            minPrice = ulong.MaxValue;

            if (!onlineAggregatedCache.TryGetValue((itemID, worldID), out var response))
                return false;

            var result = response.Results.FirstOrDefault(result => result.ItemID == itemID);

            if (result == null)
                return false;

            var price = GetAggregatedMarketScope(result, HQOnly).MinListing.World.Price;

            if (price is not > 0)
                return false;

            minPrice = (ulong)Math.Round(price.Value);
            return true;
        }

        #endregion

        #region 聚合统计与物品来源

        public (float DailySales, ulong? AvgPrice, (ulong Price, DateTime Time, string WorldName)? RecentPurchase) GetItemAggregatedStats
        (
            uint itemID,
            uint targetWorldID,
            bool hqOnly
        )
        {
            float                                           dailySales     = 0;
            ulong?                                          avgPrice       = null;
            (ulong Price, DateTime Time, string WorldName)? recentPurchase = null;

            if (onlineAggregatedCache.TryGetValue((itemID, targetWorldID), out var response))
            {
                var result = response.Results.FirstOrDefault(x => x.ItemID == itemID);

                if (result != null)
                {
                    var scope = GetAggregatedMarketScope(result, hqOnly);

                    dailySales = scope.DailySaleVelocity.World.Quantity ?? scope.DailySaleVelocity.Region.Quantity ?? 0;

                    if (scope.AverageSalePrice.World.Price is > 0)
                        avgPrice = (ulong)Math.Round(scope.AverageSalePrice.World.Price.Value);
                    else if (scope.AverageSalePrice.Region.Price is > 0)
                        avgPrice = (ulong)Math.Round(scope.AverageSalePrice.Region.Price.Value);

                    if (scope.RecentPurchase.World is { Price: > 0, Timestamp: > 0 })
                    {
                        recentPurchase = ((ulong)Math.Round(scope.RecentPurchase.World.Price.Value),
                                             DateTimeOffset.FromUnixTimeMilliseconds(scope.RecentPurchase.World.Timestamp.Value).LocalDateTime,
                                             LuminaWrapper.GetWorldName(targetWorldID));
                    }
                    else if (scope.RecentPurchase.Region is { Price: > 0, Timestamp: > 0 })
                    {
                        var worldName = scope.RecentPurchase.Region.WorldID is > 0 ?
                                            LuminaWrapper.GetWorldName(scope.RecentPurchase.Region.WorldID.Value) :
                                            string.Empty;
                        recentPurchase = ((ulong)Math.Round(scope.RecentPurchase.Region.Price.Value),
                                             DateTimeOffset.FromUnixTimeMilliseconds(scope.RecentPurchase.Region.Timestamp.Value).LocalDateTime,
                                             worldName);
                    }
                }
            }

            return (dailySales, avgPrice, recentPurchase);
        }

        public ulong? GetSelectedWorldMinPrice
        (
            uint itemID,
            bool hqOnly
        )
        {
            if (SelectedWorldID == GameState.CurrentWorld &&
                IsAbleToSearchLocalMarket()               &&
                InfoProxy               != null           &&
                InfoProxy->SearchItemId == itemID         &&
                InfoProxy->IsFullyReceived(itemID))
            {
                var localMinPrice = InfoProxy->Listings.ToArray()
                                                       .Where
                                                       (x => x.ItemId    == itemID &&
                                                             x.UnitPrice > 0       &&
                                                             (x.IsHqItem || !hqOnly)
                                                       )
                                                       .Select(x => x.UnitPrice)
                                                       .DefaultIfEmpty()
                                                       .Min();
                return localMinPrice > 0 ?
                           localMinPrice :
                           null;
            }

            if (onlineAggregatedCache.TryGetValue((itemID, SelectedWorldID), out var response))
            {
                var result = response.Results.FirstOrDefault(x => x.ItemID == itemID);
                var price = result == null ?
                                null :
                                GetAggregatedMarketScope(result, hqOnly).MinListing.World.Price;
                if (price is > 0)
                    return (ulong)Math.Round(price.Value);
            }

            if (!onlineDataCache.TryGetValue((itemID, SelectedWorldID, hqOnly), out var marketData) ||
                !marketData.Items.TryGetValue(itemID, out var itemData))
                return null;

            var onlineMinPrice = itemData.Listings?
                                         .Where(x => x.PricePerUnit > 0 && (x.HQ || !hqOnly))
                                         .Select(x => x.PricePerUnit)
                                         .DefaultIfEmpty()
                                         .Min() ??
                                 0;
            return onlineMinPrice > 0 ?
                       onlineMinPrice :
                       null;
        }

        public ulong? GetRegionMinPrice
        (
            uint itemID,
            bool hqOnly
        )
        {
            if (!owner.allWorlds.TryGetValue(EffectiveRegionName, out var region))
                return null;

            ulong? minPrice = null;

            foreach (var worldID in region.Values.SelectMany(static worlds => worlds.Keys))
            {
                if (!onlineAggregatedCache.TryGetValue((itemID, worldID), out var response))
                    continue;

                var result = response.Results.FirstOrDefault(x => x.ItemID == itemID);
                var price = result == null ?
                                null :
                                GetAggregatedMarketScope(result, hqOnly).MinListing.World.Price;
                if (price is not > 0)
                    continue;

                var roundedPrice = (ulong)Math.Round(price.Value);
                if (roundedPrice > 0 && (minPrice == null || roundedPrice < minPrice.Value))
                    minPrice = roundedPrice;
            }

            return minPrice;
        }

        public ulong? GetHistoryPercentilePrice
        (
            uint   itemID,
            bool   hqOnly,
            double percentile
        )
        {
            var entries = GetHistoryDataSet(itemID, hqOnly)?.Entries;
            if (entries is not { Count: > 0 })
                return null;

            var prices = entries.Select(x => x.PricePerUnit).OrderBy(x => x).ToArray();
            var index  = (int)Math.Round((prices.Length - 1) * percentile, MidpointRounding.AwayFromZero);
            return prices[index] > 0 ?
                       prices[index] :
                       null;
        }

        public UniversalisAggregatedMarketDataResponse? GetAggregatedResponse
        (
            uint itemID,
            uint worldID
        ) =>
            onlineAggregatedCache.GetValueOrDefault((itemID, worldID));

        public ItemSourceInfo? GetItemSourceInfo
        (
            uint itemID
        )
        {
            if (itemSourceCache.TryGetValue(itemID, out var cached))
                return cached;

            var result = ItemSourceInfo.Query(itemID);
            if (result is not { State: ItemSourceQueryState.Ready, Data: { } sourceInfo })
                return null;

            itemSourceCache[itemID] = sourceInfo;
            return sourceInfo;
        }

        public uint? GetNPCGilPrice
        (
            uint itemID
        )
        {
            if (npcGilPriceCache.TryGetValue(itemID, out var cached))
                return cached;

            var sourceInfo = GetItemSourceInfo(itemID);
            if (sourceInfo == null)
                return null;

            uint? price = null;

            foreach (var npcInfo in sourceInfo.NPCInfos)
            {
                foreach (var costInfo in npcInfo.CostInfos)
                {
                    const uint GIL_ITEM_ID = 1;

                    if (costInfo is not { ItemID: GIL_ITEM_ID, Cost: > 0 })
                        continue;

                    if (price == null || costInfo.Cost < price.Value)
                        price = costInfo.Cost;
                }
            }

            npcGilPriceCache[itemID] = price;
            return price;
        }

        public bool RequestTooltipAggregatedScope
        (
            uint   itemID,
            string scope
        )
        {
            if (string.IsNullOrWhiteSpace(scope))
                return false;

            var cacheKey = (itemID, scope);

            _ = RemoteUniversalisAggregatedMarket.GetOrRequest([itemID], scope);
            if (tooltipAggregatedSubscriptionCache.ContainsKey(cacheKey))
                return false;

            var epoch = itemEpoch;

            tooltipAggregatedSubscriptionCache[cacheKey] = RemoteUniversalisAggregatedMarket.Observe
            (
                [itemID],
                scope,
                snapshot =>
                {
                    if (epoch != itemEpoch)
                        return;

                    if (!snapshot.HasValue)
                        return;

                    TooltipManager.Instance().TriggerItemDetailUpdate();
                }
            );

            return true;
        }

        #endregion
    }
}
