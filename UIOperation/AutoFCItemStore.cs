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
        Title       = "部队储物柜快速存储",
        Description = "右键菜单选择存储到部队储物柜，或按住Alt+鼠标右键快速存储",
        Category    = ModuleCategories.UIOperation,
    };

    private static readonly CompSig MoveItemSig = new("40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF");
    
    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    ];

    private static readonly InventoryType[] FCChestPages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5
    ];

    private static readonly SeString DepositString = new(new TextPayload("存储到部队储物柜"));

    private static bool             IsFCChestOpen;
    private static int              CurrentItemQuantity;
    private        MoveItemDelegate MoveItem     = null!;

    public delegate nint MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        MoveItem ??= MoveItemSig.GetDelegate<MoveItemDelegate>();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InputNumeric", OnInputNumericAddon);
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        
        CheckInitialState();
    }

    public override void ConfigUI()
    {
        ConflictKeyText();
        
        ImGui.Spacing();
        ImGui.TextWrapped("按住冲突键+右键背包物品快速存储到部队储物柜");
    }

    public override void Uninit()
    {
        TaskHelper?.Abort();
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        DService.AddonLifecycle.UnregisterListener(OnFCChestAddon);
        DService.AddonLifecycle.UnregisterListener(OnInputNumericAddon);
        
        base.Uninit();
    }

    private void CheckInitialState()
    {
        IsFCChestOpen = GetAddonByName("FreeCompanyChest") != null;
    }
    
    private void OnFCChestAddon(AddonEvent type, AddonArgs? args)
    {
        IsFCChestOpen = type == AddonEvent.PostSetup;
        if (type == AddonEvent.PreFinalize) 
            TaskHelper.Abort();
    }

    private void OnInputNumericAddon(AddonEvent type, AddonArgs? args)
    {
        if (type != AddonEvent.PostSetup) return;
        
        TaskHelper.Enqueue(() =>
        {
            var addon = GetAddonByName("InputNumeric");
            if (addon == null || !IsAddonAndNodesReady(addon)) return false;

            Callback(addon, true, CurrentItemQuantity);
            return true;
        }, "自动确认InputNumeric");
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.AddonName == "ArmouryBoard" || args.MenuType != ContextMenuType.Inventory || !IsFCChestOpen) 
            return;

        var invItem = ((MenuTargetInventory)args.Target).TargetItem!.Value;
        
        if (IsConflictKeyPressed())
        {
            var addon = GetAddonByName("FreeCompanyChest");
            if (addon != null)
            {
                // 保存当前物品数量
                CurrentItemQuantity = invItem.Quantity;
                
                TaskHelper.Enqueue(() =>
                {
                    DepositItemDirect(invItem.ItemId, addon, invItem.IsHq, invItem.Quantity);
                    return true;
                }, "热键快速存储");
            }
            return; 
        }

        // 鼠标右键物品，显示存储菜单项
        var menuItem = CheckInventoryItem(invItem.ItemId, invItem.IsHq, invItem.Quantity);
        if (menuItem != null)
            args.AddMenuItem(menuItem);
    }

    private MenuItem? CheckInventoryItem(uint itemId, bool itemHq, int itemAmount)
    {
        var addon = GetAddonByName("FreeCompanyChest");
        if (addon == null || !addon->IsVisible) return null;
        if (addon->UldManager.NodeList[4]->IsVisible() || addon->UldManager.NodeList[7]->IsVisible()) return null;

        if (LuminaGetter.TryGetRow<Item>(itemId, out var sheetItem))
        {
            if (sheetItem.IsUntradable) return null;
            
            var menu = new MenuItem
            {
                Name = DepositString
            };
            menu.OnClicked += _ => 
            {
                // 保存当前物品数量
                CurrentItemQuantity = itemAmount;
                
                TaskHelper.Enqueue(() =>
                {
                    var (sourceInventory, sourceSlot, _, _, _) = GetSelectedItem();
                    if (sourceInventory != InventoryType.Invalid)
                        DepositItem(itemId, addon, itemHq, itemAmount, sourceInventory, (uint)sourceSlot);
                    
                    return true;
                }, "右键菜单存储");
            };
            return menu;
        }

        return null;
    }

    private void DepositItemDirect(uint itemId, AtkUnitBase* addon, bool itemHq, int itemAmount)
    {
        // 获取物品的源位置
        var (sourceInventory, sourceSlot, _, _, _) = GetSelectedItem();
        if (sourceInventory == InventoryType.Invalid)
            return;

        DepositItem(itemId, addon, itemHq, itemAmount, sourceInventory, sourceSlot);
    }

    private (InventoryType sourceInventory, ushort sourceSlot, uint itemId, bool isHq, int quantity) GetSelectedItem()
    {
        try
        {
            var agentInventoryContext = (AgentInventoryContext*)AgentModule.Instance()
                ->GetAgentByInternalId(AgentId.InventoryContext);
            
            if (agentInventoryContext == null || agentInventoryContext->TargetInventorySlot == null)
                return (InventoryType.Invalid, 0, 0, false, 0);
            
            var sourceInventory = agentInventoryContext->TargetInventoryId;
            var sourceSlot      = (ushort)agentInventoryContext->TargetInventorySlotId;
            var slot            = agentInventoryContext->TargetInventorySlot;
            var itemId          = slot->ItemId;
            var isHq            = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var quantity        = slot->Quantity;
            
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
        if (MoveItem == null) return;

        var fcPage = GetCurrentFCPage(addon);
        
        var destSlot = FindFCChestSlot((InventoryType)fcPage, itemId, itemAmount, itemHq);
        if (destSlot == -1)
            return;

        var agent = UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.FreeCompanyChest);
        MoveItem(agent, sourceInventory, sourceSlot, (InventoryType)fcPage, (uint)destSlot);
    }

    private uint GetCurrentFCPage(AtkUnitBase* addon)
    {
        // 检查当前选中的部队储物柜页面
        for (var i = 101; i >= 97; i--)
        {
            var radioButton = addon->UldManager.NodeList[i];
            if (!radioButton->IsVisible()) continue;

            if (radioButton->GetAsAtkComponentNode()->Component->UldManager.NodeList[2]->IsVisible())
            {
                return i switch
                {
                    101 => (uint)InventoryType.FreeCompanyPage1,
                    100 => (uint)InventoryType.FreeCompanyPage2,
                    99  => (uint)InventoryType.FreeCompanyPage3,
                    98  => (uint)InventoryType.FreeCompanyPage4,
                    97  => (uint)InventoryType.FreeCompanyPage5,
                    _   => (uint)InventoryType.FreeCompanyPage1
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

        if (LuminaGetter.TryGetRow<Item>(itemId, out var sheetItem))
        {
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
        }

        return -1;
    }
} 