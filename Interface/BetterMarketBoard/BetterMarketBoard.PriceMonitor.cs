using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe partial class BetterMarketBoard
{
    private sealed class PriceMonitorProvider
    (
        BetterMarketBoard owner
    )
    {
        public void SetMonitor
        (
            uint itemID,
            bool hqOnly = false
        )
        {
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemData) || itemData.ItemSearchCategory.RowId == 0)
                return;

            owner.config.CurrentMonitorItem = new MonitorItem
            {
                ItemID         = itemID,
                PriceThreshold = 100,
                HQOnly         = hqOnly && itemData.CanBeHq
            };
            owner.config.Save(owner);
        }

        public void ClearMonitor()
        {
            owner.config.CurrentMonitorItem = null;
            owner.config.Save(owner);
        }

        public void ToggleHQ()
        {
            var monitorItem = owner.config.CurrentMonitorItem;
            if (monitorItem == null                                                 ||
                !LuminaGetter.TryGetRow<Item>(monitorItem.ItemID, out var itemData) ||
                !itemData.CanBeHq)
                return;

            monitorItem.HQOnly = !monitorItem.HQOnly;
            owner.config.Save(owner);

            if (owner.provider.SelectedItemID == monitorItem.ItemID)
                owner.provider.SelectItem(monitorItem.ItemID, hqOnly: monitorItem.HQOnly);
        }

        public void ToggleAutoBuy()
        {
            owner.config.AutoBuy = !owner.config.AutoBuy;
            owner.config.Save(owner);
        }

        public void SetQuantityLimit
        (
            uint quantityLimit
        )
        {
            owner.config.AutoBuyQuantityLimit = quantityLimit;
            owner.config.Save(owner);
        }

        public void Update()
        {
            var monitorItem = owner.config.CurrentMonitorItem;

            if (monitorItem == null                                                 ||
                !LuminaGetter.TryGetRow<Item>(monitorItem.ItemID, out var itemData) ||
                itemData.ItemSearchCategory.RowId == 0)
                return;

            if (!IsAbleToSearchMarket() ||
                owner.Overlay.IsOpen    ||
                GameState.TerritoryIntendedUse != TerritoryIntendedUse.Town)
                return;

            var taskHelper = owner.TaskHelper;

            taskHelper.Abort();
            taskHelper.Enqueue(() => MarketDataProvider.RequestLocalSearchData(monitorItem.ItemID));
            taskHelper.Enqueue(() => InfoProxyItemSearch.Instance()->IsFullyReceived(monitorItem.ItemID));
            taskHelper.Enqueue
            (() =>
                {
                    var listingsToSnipe = InfoProxy->Listings
                                          .ToArray()
                                          .Where
                                          (x => x.ItemId    == monitorItem.ItemID         &&
                                                x.UnitPrice > 0                           &&
                                                x.UnitPrice <= monitorItem.PriceThreshold &&
                                                (!monitorItem.HQOnly || x.IsHqItem)       &&
                                                !IsOwnRetainer(x.RetainerId)
                                          )
                                          .OrderBy(x => x.UnitPrice)
                                          .ToList();

                    if (listingsToSnipe.Count == 0)
                    {
                        owner.TaskHelper.Abort();
                        return;
                    }

                    var payloadID = owner.itemIDToPayloadID.GetOrAdd
                    (
                        monitorItem.ItemID,
                        itemID =>
                        {
                            LinkPayloadManager.Instance().Reg
                            (
                                (_, _) =>
                                {
                                    owner.provider.SelectItem(itemID, hqOnly: monitorItem.HQOnly);
                                    owner.Overlay.IsOpen = true;
                                },
                                out var registeredPayloadID
                            );
                            return registeredPayloadID;
                        }
                    );
                    if (!LinkPayloadManager.Instance().TryGetPayload(payloadID, out var linkPayload))
                        return;

                    // 市场通知
                    var message0 = Lang.GetSe
                    (
                        "BetterMarketBoard-PriceMonitor-Notification-Found",
                        new Dictionary<string, object>
                        {
                            ["item"]      = ReadOnlySeString.CreateItem(monitorItem.ItemID, monitorItem.HQOnly),
                            ["price"]     = listingsToSnipe.First().UnitPrice,
                            ["threshold"] = monitorItem.PriceThreshold
                        }
                    );

                    var message1 = ISeStringEvaluator.Instance().EvaluateFromAddon(371, [message0]);

                    using var rented  = new RentedSeStringBuilder();
                    var       builder = rented.Builder;

                    builder
                        .AppendDalamudSeString(linkPayload)
                        .Append(message1)
                        .AppendDalamudSeString(RawPayload.LinkTerminator);

                    NotifyHelper.Instance().Chat(builder.ToReadOnlySeString());

                    if (owner.config.AutoBuy)
                    {
                        var heldCount = LocalPlayerState.GetItemCount(monitorItem.ItemID);
                        var quantityLimit = owner.config.AutoBuyQuantityLimit == 0 ?
                                                uint.MaxValue :
                                                owner.config.AutoBuyQuantityLimit;

                        if (heldCount <= quantityLimit)
                        {
                            var totalCount        = 0U;
                            var totalListingCount = 0U;

                            foreach (var listing in listingsToSnipe)
                            {
                                totalCount        += listing.Quantity;
                                totalListingCount += 1;

                                if (totalCount + heldCount > quantityLimit)
                                    break;

                                owner.TaskHelper.Enqueue
                                (
                                    () =>
                                    {
                                        if (MarketDataProvider.SendBuyRequest(listing))
                                            return;

                                        taskHelper.Abort();
                                    },
                                    weight: 1
                                );
                            }

                            // 自动购买通知
                            var autoBuyMessage = Lang.GetSe
                            (
                                "BetterMarketBoard-PriceMonitor-Notification-AutoBuy",
                                new Dictionary<string, object>
                                {
                                    ["item"]     = ReadOnlySeString.CreateItemLink(monitorItem.ItemID, monitorItem.HQOnly),
                                    ["count"]    = totalListingCount,
                                    ["quantity"] = totalCount
                                }
                            );

                            NotifyHelper.Toast(autoBuyMessage);
                            NotifyHelper.Instance().Chat(autoBuyMessage);
                        }
                    }
                }
            );
        }
    }
}
