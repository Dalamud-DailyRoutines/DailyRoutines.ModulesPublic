using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.AutoShowItemNPCShopInfo;

public partial class AutoShowItemNPCShopInfo
{
    [IPCProvider("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenByItemID")]
    private static bool OpenByItemID(uint itemID)
    {
        var shopResult     = ItemSourceInfo.Query(itemID);
        var exchangeResult = ItemSourceInfo.QueryExchangeItems(itemID);

        var opened          = false;
        var hasPendingQuery = false;

        opened |= TryOpenShopInfo(itemID, shopResult, false);
        opened |= TryOpenExchangeInfo(itemID, exchangeResult, false);

        if (shopResult.State is not (ItemSourceQueryState.Ready or ItemSourceQueryState.NotFound))
            hasPendingQuery = true;

        if (exchangeResult.State is not (ItemSourceQueryState.Ready or ItemSourceQueryState.NotFound))
            hasPendingQuery = true;

        if (opened)
            return true;

        if (hasPendingQuery)
            return false;

        NotifyHelper.Instance().ChatError(Lang.Get("AutoShowItemNPCShopInfo-Notification-InfoNotFound", itemID));
        return false;
    }

    [IPCProvider("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenShopInfoByItemID")]
    private static bool OpenShopInfoByItemID(uint itemID) =>
        TryOpenShopInfo(itemID, ItemSourceInfo.Query(itemID), true);

    [IPCProvider("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenExchangeInfoByItemID")]
    private static bool OpenExchangeInfoByItemID(uint itemID) =>
        TryOpenExchangeInfo(itemID, ItemSourceInfo.QueryExchangeItems(itemID), true);

    private static bool TryOpenShopInfo(uint itemID, ItemSourceQueryResult result, bool showError)
    {
        if (AddonNPCShopsSource.Addon is { IsOpen: true } addon &&
            addon.SourceInfo.ItemID == itemID)
        {
            AddonNPCShopsSource.CloseAndClear();
            return true;
        }

        if (result is { State: ItemSourceQueryState.Ready, Data: { } itemInfo })
        {
            AddonNPCShopsSource.OpenWithData(itemInfo);
            return true;
        }

        if (showError && result.State == ItemSourceQueryState.NotFound)
            NotifyHelper.Instance().ChatError(Lang.Get("AutoShowItemNPCShopInfo-Notification-InfoNotFound", itemID));

        return false;
    }

    private static bool TryOpenExchangeInfo(uint itemID, ExchangeItemsQueryResult result, bool showError)
    {
        if (AddonNPCShopsDestination.Addon is { IsOpen: true } addon &&
            addon.SourceInfo.CostItemID == itemID)
        {
            AddonNPCShopsDestination.CloseAndClear();
            return true;
        }

        if (result is { State: ItemSourceQueryState.Ready, Data: { } exchangeInfo })
        {
            AddonNPCShopsDestination.OpenWithData(exchangeInfo);
            return true;
        }

        if (showError && result.State == ItemSourceQueryState.NotFound)
            NotifyHelper.Instance().ChatError(Lang.Get("AutoShowItemNPCShopInfo-Notification-InfoNotFound", itemID));

        return false;
    }
}
