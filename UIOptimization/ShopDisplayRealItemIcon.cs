using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class ShopDisplayRealItemIcon : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ShopDisplayRealItemIconTitle"),
        Description = GetLoc("ShopDisplayRealItemIconDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static List<(uint ID, uint IconID, string Name)> CollectablesShopItemDatas = [];

    protected override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Shop", OnShop);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Shop", OnShop);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InclusionShop", OnInclusionShop);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "InclusionShop", OnInclusionShop);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "GrandCompanyExchange", OnGrandCompanyExchange);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "GrandCompanyExchange", OnGrandCompanyExchange);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,
                                                 ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
                                                 OnShopExchange);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh,
                                                 ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
                                                 OnShopExchange);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CollectablesShop", OnCollectablesShop);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "CollectablesShop", OnCollectablesShop);
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeShop", OnFreeShop);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "FreeShop", OnFreeShop);
    }
    
    private static void OnFreeShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
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
        
        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
    }

    
    private static void OnCollectablesShop(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostDraw &&
            !Throttler.Throttle("ShopDisplayRealItemIcon-OnCollectablesShop", 100)) return;
        
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;

        if (type == AddonEvent.PostRefresh)
        {
            var itemCount = addon->AtkValues[20].UInt;
            if (itemCount == 0) return;

            List<(uint ID, uint IconID, string Name)> itemDatas = [];

            for (var i = 0; i < itemCount; i++)
            {
                var itemID = addon->AtkValues[34 + (11 * i)].UInt % 50_0000;
                if (itemID == 0) continue;
                if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;
                
                itemDatas.Add(new(itemID, itemRow.Icon, itemRow.Name.ExtractText()));
            }
            
            CollectablesShopItemDatas = itemDatas;
        }
        
        if (CollectablesShopItemDatas.Count == 0) return;
        
        var listComponent = (AtkComponentNode*)addon->GetNodeById(28);
        if (listComponent == null) return;
        
        for (var i = 0; i < 15; i++)
        {
            var listItemComponent = (AtkComponentNode*)listComponent->Component->UldManager.NodeList[16 + i];
            if (listItemComponent == null) continue;
            
            var nameNode = (AtkTextNode*)listItemComponent->Component->UldManager.SearchNodeById(4);
            if (nameNode == null) return;
            
            var name = SanitizeSeIcon(SeString.Parse(nameNode->NodeText).TextValue);
            var data = CollectablesShopItemDatas.FirstOrDefault(
                x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (data == default) continue;
            
            var imageNode = (AtkImageNode*)listItemComponent->Component->UldManager.SearchNodeById(2);
            if (imageNode == null) continue;
            
            imageNode->LoadIconTexture(data.IconID, 0);
        }
    }
    
    private static void OnShopExchange(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;
        
        var itemCount = addon->AtkValues[4].UInt;
        if (itemCount == 0) return;
        
        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[1064 + i].UInt;
            if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;
            
            addon->AtkValues[210 + i].SetUInt(itemRow.Icon);
        }
        
        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
    }
    
    private static void OnGrandCompanyExchange(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
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
        
        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
    }
    
    private static void OnInclusionShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;
        
        var itemCount = addon->AtkValues[298].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[300 + (i * 18)].UInt;
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;
            
            addon->AtkValues[301 + (i * 18)].SetUInt(itemRow.Icon);
        }
        
        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
    }

    private static void OnShop(AddonEvent type, AddonArgs args)
    {
        var addon = args.Addon.ToAtkUnitBase();
        if (addon == null) return;

        // 0 - 出售; 1 - 回购
        var currentTab = addon->AtkValues[0].UInt;
        
        var itemCount = addon->AtkValues[2].UInt;
        if (itemCount == 0) return;
        
        for (var i = 0; i < itemCount; i++)
        {
            var itemID = 0U;
            var isItemHQ = false;
            switch (currentTab)
            {
                case 0:
                    itemID = addon->AtkValues[441 + i].UInt;
                    break;
                case 1:
                    var buybackItem = ShopEventHandler.AgentProxy.Instance()->Handler->Buyback[i];
                    isItemHQ = buybackItem.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    itemID = buybackItem.ItemId;
                    break;
            }
            
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;
            
            addon->AtkValues[197 + i].SetUInt(itemRow.Icon + (isItemHQ ? 100_0000U : 0U));
        }

        addon->OnRefresh(addon->AtkValuesCount, addon->AtkValues);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnShop);
        DService.AddonLifecycle.UnregisterListener(OnInclusionShop);
        DService.AddonLifecycle.UnregisterListener(OnGrandCompanyExchange);
        DService.AddonLifecycle.UnregisterListener(OnShopExchange);
        DService.AddonLifecycle.UnregisterListener(OnCollectablesShop);
        DService.AddonLifecycle.UnregisterListener(OnFreeShop);
    }
}
