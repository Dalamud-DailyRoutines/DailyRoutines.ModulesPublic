using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class ShopDisplayRealItemIcon : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ShopDisplayRealItemIconTitle"),
        Description = Lang.Get("ShopDisplayRealItemIconDescription"),
        Category    = ModuleCategory.Interface,
        ModulesPair =
        [
            "OptimizedCollectablesShop",
            "OptimizedFreeShop"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    protected override void Init()
    {
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
            ["ShopExchangeCurrency", "ShopExchangeItem"],
            OnShopExchange
        );
        IAddonLifecycle.Instance().RegisterListener
        (
            AddonEvent.PostRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem"],
            OnShopExchange
        );
        IAddonLifecycle.Instance().RegisterListener
        (
            AddonEvent.PreRefresh,
            ["ShopExchangeCurrency", "ShopExchangeItem"],
            OnShopExchange
        );
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnShop);
        IAddonLifecycle.Instance().UnregisterListener(OnInclusionShop);
        IAddonLifecycle.Instance().UnregisterListener(OnGrandCompanyExchange);
        IAddonLifecycle.Instance().UnregisterListener(OnShopExchange);
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
