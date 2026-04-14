using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class ShopDisplayRealItemIcon : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ShopDisplayRealItemIconTitle"),
        Description = Lang.Get("ShopDisplayRealItemIconDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private List<(uint ID, uint IconID, string Name)> collectablesShopItemDatas = [];

    protected override void Init()
    {
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Shop", OnShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "Shop", OnShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Shop", OnShop);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InclusionShop", OnInclusionShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "InclusionShop", OnInclusionShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "InclusionShop", OnInclusionShop);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "GrandCompanyExchange", OnGrandCompanyExchange);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "GrandCompanyExchange", OnGrandCompanyExchange);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "GrandCompanyExchange", OnGrandCompanyExchange);

        DService.Instance().AddonLifecycle.RegisterListener
        (
            AddonEvent.PostSetup,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );
        DService.Instance().AddonLifecycle.RegisterListener
        (
            AddonEvent.PostRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );
        DService.Instance().AddonLifecycle.RegisterListener
        (
            AddonEvent.PreRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "CollectablesShop", OnCollectablesShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "CollectablesShop", OnCollectablesShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CollectablesShop", OnCollectablesShop);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeShop", OnFreeShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRefresh,  "FreeShop", OnFreeShop);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "FreeShop", OnFreeShop);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnShop);
        DService.Instance().AddonLifecycle.UnregisterListener(OnInclusionShop);
        DService.Instance().AddonLifecycle.UnregisterListener(OnGrandCompanyExchange);
        DService.Instance().AddonLifecycle.UnregisterListener(OnShopExchange);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCollectablesShop);
        DService.Instance().AddonLifecycle.UnregisterListener(OnFreeShop);
    }

    private static void OnFreeShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[3].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[65 + i].UInt;
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[126 + i].SetUInt(itemRow.Icon);
        }
    }

    private void OnCollectablesShop(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostDraw &&
            !Throttler.Shared.Throttle("ShopDisplayRealItemIcon-OnCollectablesShop", 100)) return;

        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        if (type == AddonEvent.PostRefresh)
        {
            var itemCount = addon->AtkValues[20].UInt;
            if (itemCount == 0) return;

            List<(uint ID, uint IconID, string Name)> itemDatas = [];

            for (var i = 0; i < itemCount; i++)
            {
                var itemID = addon->AtkValues[34 + 11 * i].UInt % 50_0000;
                if (itemID == 0) continue;
                if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

                itemDatas.Add(new(itemID, itemRow.Icon, itemRow.Name.ToString()));
            }

            collectablesShopItemDatas = itemDatas;
        }

        if (collectablesShopItemDatas.Count == 0) return;

        var listComponent = (AtkComponentNode*)addon->GetNodeById(28);
        if (listComponent == null) return;

        for (var i = 0; i < 15; i++)
        {
            var listItemComponent = (AtkComponentNode*)listComponent->Component->UldManager.NodeList[16 + i];
            if (listItemComponent == null) continue;

            var nameNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(4);
            if (nameNode == null) return;

            var name = nameNode->NodeText.ToString().SanitizeSEIcon();
            var data = collectablesShopItemDatas.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (data == default) continue;

            var imageNode = (AtkImageNode*)listItemComponent->Component->UldManager.SearchNodeById(2);
            if (imageNode == null) continue;

            imageNode->LoadIconTexture(data.IconID, 0);
        }
    }

    private static void OnShopExchange(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[4].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[1064 + i].UInt;
            if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[210 + i].SetUInt(itemRow.Icon);
        }
    }

    private static void OnGrandCompanyExchange(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[1].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[317 + i].UInt;
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[167 + i].SetUInt(itemRow.Icon);
        }
    }

    private static void OnInclusionShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[298].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[300 + i * 18].UInt;
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[301 + i * 18].SetUInt(itemRow.Icon);
        }
    }

    private static void OnShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        // 0 - 出售; 1 - 回购
        var currentTab = addon->AtkValues[0].UInt;

        var itemCount = addon->AtkValues[2].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID   = 0U;
            var isItemHQ = false;

            switch (currentTab)
            {
                case 0:
                    itemID = addon->AtkValues[441 + i].UInt;
                    break;
                case 1:
                    var buybackItem = ShopEventHandler.AgentProxy.Instance()->Handler->Buyback[i];
                    isItemHQ = buybackItem.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    itemID   = buybackItem.ItemId;
                    break;
            }

            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[197 + i].SetUInt(itemRow.Icon + (isItemHQ ? 100_0000U : 0U));
        }
    }
}
