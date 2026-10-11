using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class ShopDisplayRealItemIcon : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ShopDisplayRealItemIconTitle"),
        Description = Lang.Get("ShopDisplayRealItemIconDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig CollectablesShopItemFillSig = new
    (
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F9 49 8B F0 49 8B 08 48 8B EA"
    );
    private delegate void CollectablesShopItemFillDelegate
    (
        AtkUnitBase*                                addon,
        AtkComponentListItemPopulator.ListItemInfo* itemInfo,
        AtkResNode**                                nodeList
    );
    private Hook<CollectablesShopItemFillDelegate> CollectablesShopItemFillHook;

    protected override void Init()
    {
        CollectablesShopItemFillHook = CollectablesShopItemFillSig.GetHook<CollectablesShopItemFillDelegate>(CollectablesShopItemFillDetour);
        CollectablesShopItemFillHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "Shop", OnShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,  "Shop", OnShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "Shop", OnShop);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "InclusionShop", OnInclusionShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,  "InclusionShop", OnInclusionShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "InclusionShop", OnInclusionShop);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "GrandCompanyExchange", OnGrandCompanyExchange);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,  "GrandCompanyExchange", OnGrandCompanyExchange);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "GrandCompanyExchange", OnGrandCompanyExchange);

        IAddonLifecycle.Instance().RegisterListener
        (
            AddonEvent.PostSetup,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );
        IAddonLifecycle.Instance().RegisterListener
        (
            AddonEvent.PostRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );
        IAddonLifecycle.Instance().RegisterListener
        (
            AddonEvent.PreRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem", "ShopExchangeCoin"],
            OnShopExchange
        );

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "FreeShop", OnFreeShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRefresh,  "FreeShop", OnFreeShop);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "FreeShop", OnFreeShop);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnShop);
        IAddonLifecycle.Instance().UnregisterListener(OnInclusionShop);
        IAddonLifecycle.Instance().UnregisterListener(OnGrandCompanyExchange);
        IAddonLifecycle.Instance().UnregisterListener(OnShopExchange);
        IAddonLifecycle.Instance().UnregisterListener(OnFreeShop);
    }

    private void CollectablesShopItemFillDetour
    (
        AtkUnitBase*                                addon,
        AtkComponentListItemPopulator.ListItemInfo* itemInfo,
        AtkResNode**                                nodeList
    )
    {
        CollectablesShopItemFillHook.Original(addon, itemInfo, nodeList);

        var listItem = itemInfo->ListItem;
        if (listItem == null || listItem->UIntValues.Count < 3) return;

        var itemID = listItem->UIntValues[2] % 50_0000;
        if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) return;

        var imageNode = nodeList[0]->GetAsAtkImageNode();
        if (imageNode == null) return;

        imageNode->LoadIconTexture(itemRow.Icon, 0);
    }

    private static void OnFreeShop
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[76].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[138 + i].UInt;
            if (itemID == 0) continue;
            if (!LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[199 + i].SetUInt(itemRow.Icon);
        }
    }

    private static void OnShopExchange
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = args.Addon.ToStruct();
        if (addon == null) return;

        var itemCount = addon->AtkValues[4].UInt;
        if (itemCount == 0) return;

        for (var i = 0; i < itemCount; i++)
        {
            var itemID = addon->AtkValues[1066 + i].UInt;
            if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) continue;

            addon->AtkValues[212 + i].SetUInt(itemRow.Icon);
        }
    }

    private static void OnGrandCompanyExchange
    (
        AddonEvent type,
        AddonArgs  args
    )
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

    private static void OnInclusionShop
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = args.Addon.ToStruct();
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
    }

    private static void OnShop
    (
        AddonEvent type,
        AddonArgs  args
    )
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

            addon->AtkValues[197 + i].SetUInt
            (
                itemRow.Icon +
                (isItemHQ ?
                     100_0000U :
                     0U)
            );
        }
    }
}
