using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoFCItemStore : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = "部队储物柜快速存储",
        Description = "右键菜单选择存储到部队储物柜，或热键+鼠标右键快速存储",
        Category = ModuleCategories.UIOperation,
    };

    private static readonly CompSig MoveItemSig = new("40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF");

    private static readonly SeString DepositString = new(new TextPayload("存储到部队储物柜"));

    private static int CurrentItemQuantity;
    private MoveItemDelegate MoveItem = null!;

    public delegate nint MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };

        MoveItem ??= MoveItemSig.GetDelegate<MoveItemDelegate>();
        
        if (MoveItem == null)
        {
            DService.Log.Error("AutoFCItemStore: Failed to initialize MoveItem delegate");
            return;
        }

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputNumericAddon);
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    public override void Uninit()
    {
        TaskHelper?.Abort();
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        DService.AddonLifecycle.UnregisterListener(OnFCChestAddon);
        DService.AddonLifecycle.UnregisterListener(OnInputNumericAddon);

        base.Uninit();
    }

    private void OnFCChestAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                break;
            case AddonEvent.PreFinalize:
                TaskHelper.Abort();
                break;
        }
    }



    private void OnInputNumericAddon(AddonEvent type, AddonArgs? args)
    {
        if (type != AddonEvent.PostSetup) return;

        TaskHelper.Enqueue(() =>
        {
            if (!IsAddonAndNodesReady(InputNumeric))
                return false;

            Callback(InputNumeric, true, CurrentItemQuantity);
            return true;
        }, "自动确认存入数量");
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.AddonName == "ArmouryBoard" || args.MenuType != ContextMenuType.Inventory)
            return;

        if (!IsAddonAndNodesReady(FreeCompanyChest))
            return;

        var invItem = ((MenuTargetInventory)args.Target).TargetItem!.Value;

        if (IsConflictKeyPressed())
        {
            ExecuteDepositTask(invItem.ItemId, invItem.IsHq, invItem.Quantity, "热键快速存储");
            return;
        }

        var menuItem = CreateDepositMenuItem(invItem.ItemId, invItem.IsHq, invItem.Quantity);
        if (menuItem != null)
            args.AddMenuItem(menuItem);
    }

    private void ExecuteDepositTask(uint itemId, bool itemHq, int itemAmount, string taskName)
    {
        if (TaskHelper.IsBusy)
            return;

        CurrentItemQuantity = itemAmount;

        TaskHelper.Enqueue(() =>
        {
            if (!IsAddonAndNodesReady(FreeCompanyChest))
                return false;

            var (sourceInventory, sourceSlot, _, _, _) = GetSelectedItem();
            if (sourceInventory != InventoryType.Invalid)
                DepositItem(itemId, FreeCompanyChest, itemHq, itemAmount, sourceInventory, (uint)sourceSlot);

            return true;
        }, taskName);
    }

    private MenuItem? CreateDepositMenuItem(uint itemId, bool itemHq, int itemAmount)
    {
        if (!LuminaGetter.TryGetRow<Item>(itemId, out var sheetItem) || sheetItem.IsUntradable)
            return null;

        var menu = new MenuItem
        {
            Name = DepositString
        };
        
        menu.OnClicked += _ => ExecuteDepositTask(itemId, itemHq, itemAmount, "右键菜单存储");
        
        return menu;
    }

    private (InventoryType sourceInventory, ushort sourceSlot, uint itemId, bool isHq, int quantity) GetSelectedItem()
    {
        try
        {
            var agentInventoryContext = AgentInventoryContext.Instance();

            if (agentInventoryContext == null || agentInventoryContext->TargetInventorySlot == null)
                return (InventoryType.Invalid, 0, 0, false, 0);

            var sourceInventory = agentInventoryContext->TargetInventoryId;
            var sourceSlot = (ushort)agentInventoryContext->TargetInventorySlotId;
            var slot = agentInventoryContext->TargetInventorySlot;
            var itemId = slot->ItemId;
            var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var quantity = slot->Quantity;

            return itemId > 0
                ? (sourceInventory, sourceSlot, itemId, isHq, quantity)
                : (InventoryType.Invalid, 0, 0, false, 0);
        }
        catch
        {
            return (InventoryType.Invalid, 0, 0, false, 0);
        }
    }

    private void DepositItem(uint itemId, AtkUnitBase* addon, bool itemHq, int itemAmount, InventoryType sourceInventory, uint sourceSlot)
    {
        var fcPage = GetCurrentFCPage(addon);
        var destSlot = FindFCChestSlot((InventoryType)fcPage, itemId, itemAmount, itemHq);
        
        if (destSlot == -1)
            return;

        var agent = UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.FreeCompanyChest);
        MoveItem(agent, sourceInventory, sourceSlot, (InventoryType)fcPage, (uint)destSlot);
    }

    private uint GetCurrentFCPage(AtkUnitBase* addon)
    {
        const int FCChestTabStartIndex = 97;
        const int FCChestTabEndIndex = 101;
        const int TabSelectedIndicatorNode = 2;
        
        // 检查当前选中的部队储物柜页面
        for (var i = FCChestTabEndIndex; i >= FCChestTabStartIndex; i--)
        {
            var radioButton = addon->UldManager.NodeList[i];
            if (!radioButton->IsVisible()) continue;

            if (radioButton->GetAsAtkComponentNode()->Component->UldManager.NodeList[TabSelectedIndicatorNode]->IsVisible())
            {
                return i switch
                {
                    101 => (uint)InventoryType.FreeCompanyPage1,
                    100 => (uint)InventoryType.FreeCompanyPage2,
                    99 => (uint)InventoryType.FreeCompanyPage3,
                    98 => (uint)InventoryType.FreeCompanyPage4,
                    97 => (uint)InventoryType.FreeCompanyPage5,
                    _ => (uint)InventoryType.FreeCompanyPage1
                };
            }
        }
        return (uint)InventoryType.FreeCompanyPage1;
    }

    private short FindFCChestSlot(InventoryType fcPage, uint itemId, int stack, bool itemHq)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return -1;

        var container = inventoryManager->GetInventoryContainer(fcPage);
        if (container == null || !container->IsLoaded) return -1;

        if (!LuminaGetter.TryGetRow<Item>(itemId, out var sheetItem))
            return -1;

        // 寻找相同物品的槽位进行堆叠
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if ((item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) && !itemHq) ||
                (!item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) && itemHq))
                continue;

            if (item->ItemId == itemId && (item->Quantity + stack) <= sheetItem.StackSize)
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
}