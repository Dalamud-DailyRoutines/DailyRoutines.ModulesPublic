using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.Interface;

public partial class BetterMarketBoard
{
    private readonly record struct MetricItem
    (
        string  Label,
        string  Value,
        Vector4 ValueColor,
        string? Tooltip
    );

    private readonly record struct WorldPriceRow
    (
        uint   WorldID,
        string WorldName,
        ulong  MinPrice
    );

    private readonly record struct RankedWorldPriceRow
    (
        string DCName,
        uint   WorldID,
        string WorldName,
        ulong  MinPrice
    );

    private readonly record struct HistoryEntry
    (
        double   X,
        DateTime SaleTime,
        ulong    PricePerUnit,
        uint     Quantity,
        bool     IsHQ
    );

    private class Config : ModuleConfig
    {
        public Dictionary<string, Dictionary<string, Dictionary<uint, string>>> AllWorlds = [];

        public bool AutoBuy;
        public uint AutoBuyQuantityLimit;

        public MonitorItem? CurrentMonitorItem;

        public Dictionary<uint, MarketFavoriteItem> FavoriteItems = [];
        public Dictionary<uint, MarketHistoryItem>  HistoryItems  = [];

        public bool AppendMarketStatsTooltip = true;
    }

    private enum ItemSelectorTab
    {
        Search,
        History,
        Favorite
    }

    private class MarketHistoryItem
    {
        public uint     ItemID     { get; set; }
        public DateTime AccessTime { get; set; }

        public Item GetData() =>
            LuminaGetter.GetRow<Item>(ItemID).GetValueOrDefault();
    }

    private class MarketFavoriteItem
    {
        public uint   ItemID { get; set; }
        public string Note   { get; set; } = string.Empty;

        public Item GetData() =>
            LuminaGetter.GetRow<Item>(ItemID).GetValueOrDefault();
    }

    private class MonitorItem
    {
        public uint ItemID         { get; set; }
        public uint PriceThreshold { get; set; } = 100;
        public bool HQOnly         { get; set; }

        public Item GetData() =>
            LuminaGetter.GetRow<Item>(ItemID).GetValueOrDefault();
    }
}
