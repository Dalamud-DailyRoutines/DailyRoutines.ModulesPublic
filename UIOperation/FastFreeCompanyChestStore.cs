using System;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class FastFreeCompanyChestStore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FastFreeCompanyChestStoreTitle"),
        Description = GetLoc("FastFreeCompanyChestStoreDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["YLCHEN"]
    };
    
    private delegate         nint             MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);
    private static readonly MoveItemDelegate MoveItem = new CompSig("40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF").GetDelegate<MoveItemDelegate>();
    
    private static int CurrentItemQuantity;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InputNumeric",     OnInputNumericAddon);
        
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        
        DService.AddonLifecycle.UnregisterListener(OnFCChestAddon);
        DService.AddonLifecycle.UnregisterListener(OnInputNumericAddon);

        CurrentItemQuantity = -1;

        base.Uninit();
    }

    private void OnFCChestAddon(AddonEvent type, AddonArgs? args) => 
        TaskHelper.Abort();

    private static void OnInputNumericAddon(AddonEvent type, AddonArgs? args)
    {
        if (CurrentItemQuantity == -1) return;
        
        Callback(InputNumeric, true, CurrentItemQuantity);
        CurrentItemQuantity = -1;
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!IsAddonAndNodesReady(FreeCompanyChest)                         ||
            args.AddonName == "ArmouryBoard"                                ||
            args.Target is not MenuTargetInventory { TargetItem: { } item } ||
            !args.AddonName.StartsWith("Inventory")                         ||
            !LuminaGetter.TryGetRow<Item>(item.ItemId, out var itemData)    ||
            itemData.IsUntradable)
            return;

        if (IsConflictKeyPressed())
        {
            ExecuteDepositTask(item.ItemId, item.IsHq, item.Quantity, "热键快速存储");
            return;
        }
        
        args.AddMenuItem(new StoreMenu(item.ItemId, item.IsHq, item.Quantity).Get());
    }

    private void ExecuteDepositTask(uint itemID, bool itemHq, int itemAmount, string taskName)
    {
        CurrentItemQuantity = itemAmount;
        
        TaskHelper.Abort();
        TaskHelper.Enqueue(() =>
        {
            if (!IsAddonAndNodesReady(FreeCompanyChest))
                return false;

            var (sourceInventory, sourceSlot) = GetSelectedItemSource();
            if (sourceInventory != InventoryType.Invalid)
                DepositItem(itemID, FreeCompanyChest, itemHq, itemAmount, sourceInventory, sourceSlot);

            return true;
        }, taskName);
    }

    private static (InventoryType SourceInventory, ushort SourceSlot) GetSelectedItemSource()
    {
        try
        {
            var agent = AgentInventoryContext.Instance();
            if (agent == null || agent->TargetInventorySlot == null)
                throw new Exception();

            var sourceInventory = agent->TargetInventoryId;
            var sourceSlot      = (ushort)agent->TargetInventorySlotId;
            var slot            = agent->TargetInventorySlot;
            var itemID          = slot->ItemId;

            return itemID > 0 ? (SourceInventory: sourceInventory, sourceSlot) : (SourceInventory: InventoryType.Invalid, (ushort)0);
        }
        catch
        {
            return (InventoryType.Invalid, 0);
        }
    }

    private static void DepositItem(uint itemID, AtkUnitBase* addon, bool itemHQ, int itemAmount, InventoryType sourceInventory, uint sourceSlot)
    {
        var fcPage   = GetCurrentFCPage(addon);
        var destSlot = FindFirstSuitableSlot(fcPage, itemID, itemAmount, itemHQ);
        if (destSlot == -1) return;
        
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.FreeCompanyChest);
        MoveItem(agent, sourceInventory, sourceSlot, fcPage, (uint)destSlot);
    }

    private static InventoryType GetCurrentFCPage(AtkUnitBase* addon) => 
        addon == null ? InventoryType.FreeCompanyPage1 : (InventoryType)(20000 + addon->AtkValues[2].UInt);

    private static short FindFirstSuitableSlot(InventoryType fcPage, uint itemID, int stack, bool itemHQ)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return -1;

        var container = manager->GetInventoryContainer(fcPage);
        if (container == null || !container->IsLoaded) return -1;

        if (!LuminaGetter.TryGetRow<Item>(itemID, out var sheetItem))
            return -1;

        // 寻找相同物品的槽位进行堆叠
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if ((item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) && !itemHQ) ||
                (!item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) && itemHQ))
                continue;

            if (item->ItemId == itemID && item->Quantity + stack <= sheetItem.StackSize)
                return item->Slot;
        }

        // 如果没有可堆叠的，寻找空槽位
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item->ItemId == 0)
                return item->Slot;
        }

        return -1;
    }

    private class StoreMenu(uint ItemID, bool IsItemHQ, int ItemCount) : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("FastFreeCompanyChest-StoreIn");

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) => 
            ModuleManager.GetModule<FastFreeCompanyChestStore>().ExecuteDepositTask(ItemID, IsItemHQ, ItemCount, "右键菜单存储");
    }
}
