using System.Numerics;
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
        Description = "右键菜单选择存储到部队储物柜，或按住配置的热键+鼠标右键快速存储",
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

    private static Config           ModuleConfig = null!;
    private static bool             IsFCChestOpen;
    private        MoveItemDelegate MoveItem     = null!;

    public delegate nint MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        TaskHelper ??= new() { TimeLimitMS = 5_000 };
        
        MoveItem ??= MoveItemSig.GetDelegate<MoveItemDelegate>();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeCompanyChest", OnFCChestAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnFCChestAddon);
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        
        CheckInitialState();
    }

    public override void ConfigUI()
    {
        ImGui.TextWrapped("快速存储热键配置：");
        ImGui.Spacing();

        if (ImGui.Checkbox("Ctrl", ref ModuleConfig.UseCtrl))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        if (ImGui.Checkbox("Shift", ref ModuleConfig.UseShift))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        if (ImGui.Checkbox("Alt", ref ModuleConfig.UseAlt))
            SaveConfig(ModuleConfig);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "热键+右键背包物品快速存储");
        
        if (!ModuleConfig.UseCtrl && !ModuleConfig.UseShift && !ModuleConfig.UseAlt)
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "未选择热键，仅右键菜单可用");
    }

    public override void Uninit()
    {
        SaveConfig(ModuleConfig);
        TaskHelper?.Abort();
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        DService.AddonLifecycle.UnregisterListener(OnFCChestAddon);
        
        base.Uninit();
    }

    private void CheckInitialState()
    {
        IsFCChestOpen = DService.Gui.GetAddonByName("FreeCompanyChest") != nint.Zero;
    }
    
    private void OnFCChestAddon(AddonEvent type, AddonArgs? args)
    {
        IsFCChestOpen = type == AddonEvent.PostSetup;
        if (type == AddonEvent.PreFinalize) 
            TaskHelper.Abort();
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.AddonName == "ArmouryBoard" || args.MenuType != ContextMenuType.Inventory || !IsFCChestOpen) 
            return;

        var invItem = ((MenuTargetInventory)args.Target).TargetItem!.Value;
        
        // 检查是否按下了配置的热键组合
        if (IsHotkeyPressed())
        {
            var addon = (AtkUnitBase*)DService.Gui.GetAddonByName("FreeCompanyChest");
            if (addon != null)
            {
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

    private bool IsHotkeyPressed()
    {
        var io = ImGui.GetIO();
        
        if (!ModuleConfig.UseCtrl && !ModuleConfig.UseShift && !ModuleConfig.UseAlt)
            return false;


        var ctrlMatch  = !ModuleConfig.UseCtrl  || io.KeyCtrl;
        var shiftMatch = !ModuleConfig.UseShift || io.KeyShift;
        var altMatch   = !ModuleConfig.UseAlt   || io.KeyAlt;


        var anyPressed = (ModuleConfig.UseCtrl && io.KeyCtrl) || 
                         (ModuleConfig.UseShift && io.KeyShift) || 
                         (ModuleConfig.UseAlt && io.KeyAlt);

        return ctrlMatch && shiftMatch && altMatch && anyPressed;
    }

    private MenuItem? CheckInventoryItem(uint itemId, bool itemHq, int itemAmount)
    {
        var addon = (AtkUnitBase*)DService.Gui.GetAddonByName("FreeCompanyChest");
        if (addon == null || !addon->IsVisible) return null;
        if (addon->UldManager.NodeList[4]->IsVisible() || addon->UldManager.NodeList[7]->IsVisible()) return null;

        if (DService.Data.GetExcelSheet<Item>()!.GetRow(itemId) is { } sheetItem)
        {
            if (sheetItem.IsUntradable) return null;
            
            var menu = new MenuItem
            {
                Name = DepositString
            };
            menu.OnClicked += _ => 
            {
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
        {
            DService.Chat.PrintError("[部队储物柜快速存储] 无法获取物品位置");
            return;
        }

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
        {
            DService.Chat.PrintError("[部队储物柜快速存储] 找不到合适的存储位置");
            return;
        }

        try
        {
            var agent = UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.FreeCompanyChest);
            MoveItem(agent, sourceInventory, sourceSlot, (InventoryType)fcPage, (uint)destSlot);
            DService.Chat.Print("[部队储物柜快速存储] 物品存储成功");
        }
        catch (System.Exception ex)
        {
            DService.Log.Error($"[部队储物柜快速存储] 存储失败: {ex.Message}");
        }
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

        if (DService.Data.GetExcelSheet<Item>()!.GetRow(itemId) is { } sheetItem)
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

    private class Config : ModuleConfiguration
    {
        public bool UseCtrl  = true;   // 默认使用Ctrl键
        public bool UseShift = false;
        public bool UseAlt   = false;
    }
} 