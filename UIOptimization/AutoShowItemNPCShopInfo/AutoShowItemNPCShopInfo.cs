using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Manager;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;

namespace DailyRoutines.ModulesPublic.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoShowItemNPCShopInfoTitle"),
        Description         = Lang.Get("AutoShowItemNPCShopInfoDescription"),
        Category            = ModuleCategory.Interface,
        ModulesPrerequisite = ["BetterMarketBoard", "BetterTeleport"],
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoShowItemNPCShopInfo/AutoShowItemNPCShopInfo-UI.png",
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoShowItemNPCShopInfo/AutoShowItemNPCShopInfo-Tooltip.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpen;
        TooltipManager.Instance().RegItem(OnItemTooltipUpdate);
    }

    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpen;
        TooltipManager.Instance().Unreg(OnItemTooltipUpdate);

        AddonNPCShopsSource.Addon?.Dispose();
        AddonNPCShopsSource.Addon = null;

        AddonNPCShopsDestination.Addon?.Dispose();
        AddonNPCShopsDestination.Addon = null;
    }

    private static void OnItemTooltipUpdate(ItemKind kind, uint itemID, ref List<TooltipItemModification> modifications)
    {
        if (kind is not ItemKind.Normal)
            return;

        using var builder = new RentedSeStringBuilder();
        builder.Builder
               .AppendNewLine()
               .Append($"[{Lang.Get("AutoShowItemNPCShopInfo-TooltipTitle")}]");

        var isAnyValid = false;
        
        var sourceResult = ItemSourceInfo.Query(itemID);
        if (sourceResult is { State: ItemSourceQueryState.Ready, Data: { } sourceInfo })
        {
            isAnyValid = true;
            
            var shopInfo = sourceInfo.NPCInfos
                                   .SelectMany(x => x.CostInfos)
                                   .DistinctBy(x => x.ItemID);
            foreach (var costInfo in shopInfo)
            {
                builder.Builder
                       .AppendNewLine()
                       .Append($"    {LuminaWrapper.GetItemName(costInfo.ItemID)} ")
                       .PushColorType(32)
                       .Append($"x{costInfo.Cost}")
                       .PopColorType();

                var itemCount = -1;
                if (CurrencyManager.Instance()->HasItem(costInfo.ItemID))
                    itemCount = (int)CurrencyManager.Instance()->GetItemCount(costInfo.ItemID);
                else if (LocalPlayerState.GetItemCount(costInfo.ItemID) is var playerItemCount and > 0)
                    itemCount = (int)playerItemCount;

                if (itemCount > -1)
                {
                    builder.Builder
                           .Append($"  ({Lang.Get("Current")}: ")
                           .PushColorType(itemCount >= costInfo.Cost ? 67U : 17)
                           .Append($"{itemCount}")
                           .PopColorType()
                           .Append(")");
                }
            }
        }

        var destinationResult = ItemSourceInfo.QueryExchangeItems(itemID);
        if (destinationResult is { State: ItemSourceQueryState.Ready, Data: { } destinationInfo })
        {
            if (isAnyValid)
                builder.Builder.AppendNewLine();
            
            isAnyValid = true;
            
            builder.Builder
                   .AppendNewLine()
                   .Append($"    {Lang.Get("AutoShowItemNPCShopInfo-ExchangeItemCount", destinationInfo.Items.Count)} ");
        }

        if (isAnyValid)
        {
            modifications.Add
            (
                new()
                {
                    Target = TooltipItemType.Description,
                    Type   = TooltipModificationType.Append,
                    Text   = builder.Builder.ToReadOnlySeString()
                },
                new()
                {
                    Target = TooltipItemType.ShopInfo,
                    Type   = TooltipModificationType.Contribute,
                    Text   = new()
                }
            );
        }
    }

    private static string GetLocationName(ShopNPCLocation location) =>
        location.TerritoryID == 282 ? LuminaWrapper.GetAddonText(8495) : location.GetTerritory().ExtractPlaceName();

    private static int GetOwnedItemCount(uint itemID)
    {
        if (CurrencyManager.Instance()->HasItem(itemID))
            return (int)CurrencyManager.Instance()->GetItemCount(itemID);

        return (int)LocalPlayerState.GetItemCount(itemID);
    }

    private static void OpenMarket(uint itemID)
    {
        var item = LuminaGetter.GetRowOrDefault<Item>(itemID);
        if (!item.Name.IsEmpty)
            ChatManager.Instance().SendMessage($"/pdr market {item.Name}");
    }

    private static void OpenMap(ShopNPCLocation location, string npcName)
    {
        var pos = PositionHelper.MapToWorld(new(location.MapPosition.X, location.MapPosition.Y), location.GetMap()).ToVector3(0);

        var instance = AgentMap.Instance();
        instance->SetFlagMapMarker(location.TerritoryID, location.MapID, pos);
        instance->OpenMap(location.MapID, location.TerritoryID, npcName);
    }

    private static void TeleportToLocation(ShopNPCLocation location)
    {
        var pos = PositionHelper.MapToWorld(new(location.MapPosition.X, location.MapPosition.Y), location.GetMap()).ToVector3(0);

        var instance = AgentMap.Instance();
        instance->SetFlagMapMarker(location.TerritoryID, location.MapID, pos);

        var aetheryte = MovementManager.GetNearestAetheryte(pos, location.TerritoryID);
        if (aetheryte != null)
            ChatManager.Instance().SendMessage($"/pdrtelepo {aetheryte.Name}");
    }
}
