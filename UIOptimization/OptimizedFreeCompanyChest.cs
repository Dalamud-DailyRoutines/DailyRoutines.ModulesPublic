using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFreeCompanyChest : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedFreeCompanyChestTitle"),
        Description = GetLoc("OptimizedFreeCompanyChestDescription"),
        Category    = ModuleCategories.UIOptimization
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig SendInventoryRefreshSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B DA 48 8B F1 33 D2 0F B7 FA");
    private delegate        bool                                SendInventoryRefreshDelegate(InventoryManager* instance, int inventoryType);
    private static          Hook<SendInventoryRefreshDelegate>? SendInventoryRefreshHook;
    
    private delegate        nint             MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);
    private static readonly MoveItemDelegate MoveItem = new CompSig("40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF").GetDelegate<MoveItemDelegate>();
    
    private static readonly uint[] ItemIDs = 
        LuminaGetter.Get<Item>().Where(x => x.ItemSortCategory.Value.Param == 150).Select(x => x.RowId).ToArray();

    private static readonly Dictionary<InventoryType, string> DefaultPages = new()
    {
        [InventoryType.FreeCompanyPage1]    = $"{LuminaWrapper.GetFCChestName(0)} \ue090",
        [InventoryType.FreeCompanyPage2]    = $"{LuminaWrapper.GetFCChestName(0)} \ue091",
        [InventoryType.FreeCompanyPage3]    = $"{LuminaWrapper.GetFCChestName(0)} \ue092",
        [InventoryType.FreeCompanyPage4]    = $"{LuminaWrapper.GetFCChestName(0)} \ue093",
        [InventoryType.FreeCompanyPage5]    = $"{LuminaWrapper.GetFCChestName(0)} \ue094",
        [InventoryType.FreeCompanyCrystals] = $"{LuminaWrapper.GetAddonText(2990)}",
        [InventoryType.Invalid]             = $"{LuminaWrapper.GetAddonText(7)}",
    };
    
    private static Config ModuleConfig = null!;

    private static CheckboxNode? FastMoveNode;

    private static CheckboxNode? DefaultPageNode;

    private static VerticalListNode? ComponentNode;
    private static IconImageNode?    GilIconNode;
    private static TextNode?         GilItemsValueNode;
    private static TextNode?         GilItemsValueCountNode;

    private static bool IsNeedToClose;
    private static long LastTotalPrice;
    
    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        SendInventoryRefreshHook ??= SendInventoryRefreshSig.GetHook<SendInventoryRefreshDelegate>(SendInventoryRefreshDetour);
        SendInventoryRefreshHook.Enable();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeCompanyChest", OnAddonChest);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "FreeCompanyChest", OnAddonChest);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnAddonChest);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InputNumeric",     OnAddonInput);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,     "ContextMenu",      OnAddonContextMenu);
        
        DService.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    // 打开部队储物柜时请求所有页面数据, 并生成 Node
    private void OnAddonChest(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (FreeCompanyChest == null) return;
                
                if (ModuleConfig.DefaultPage != InventoryType.Invalid)
                {
                    if (ModuleConfig.DefaultPage == InventoryType.FreeCompanyCrystals)
                        DService.Framework.Run(() => ((AtkComponentRadioButton*)FreeCompanyChest->GetComponentByNodeId(15))->ClickRadioButton(FreeCompanyChest));
                    else
                    {
                        if ((int)ModuleConfig.DefaultPage < 20000) return;
                        
                        var index = (int)ModuleConfig.DefaultPage % 20000;
                        if (index > 5) return;

                        DService.Framework.Run(() => ((AtkComponentRadioButton*)FreeCompanyChest->GetComponentByNodeId((uint)(10 + index)))->ClickRadioButton(FreeCompanyChest));
                    }
                }
                break;
            case AddonEvent.PostDraw:
                if (FreeCompanyChest == null) return;
                
                if (FastMoveNode == null)
                {
                    FastMoveNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(5, 210),
                        IsVisible = true,
                        IsChecked = ModuleConfig.FastMoveItem,
                        IsEnabled = true,
                        SeString  = GetLoc("OptimizedFreeCompanyChest-FastMove"),
                        OnClick = newState =>
                        {
                            ModuleConfig.FastMoveItem = newState;
                            ModuleConfig.Save(this);

                            IsNeedToClose = false;
                        },
                        Tooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                       .Append($" {GetLoc("OptimizedFreeCompanyChest-FastMoveHelp")}")
                                                       .Build()
                                                       .Encode(),
                    };
                    FastMoveNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }
                FastMoveNode.IsChecked = ModuleConfig.FastMoveItem;
                FastMoveNode.IsVisible = FreeCompanyChest->AtkValues[1].UInt == 0;
                
                if (DefaultPageNode == null)
                {
                    DefaultPageNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(5, 156),
                        IsVisible = true,
                        IsChecked = false,
                        IsEnabled = true,
                        SeString  = GetLoc("OptimizedFreeCompanyChest-DefaultPage"),
                        OnClick = newState =>
                        {
                            switch (newState)
                            {
                                case true when TryGetCurrentFCPage(out var currentPage):
                                    ModuleConfig.DefaultPage = currentPage;
                                    ModuleConfig.Save(this);
                                    break;
                                case false:
                                    ModuleConfig.DefaultPage = InventoryType.Invalid;
                                    ModuleConfig.Save(this);
                                    break;
                            }

                            DefaultPageNode.Tooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                                           .Append($" {GetLoc("OptimizedFreeCompanyChest-DefaultPageHelp")}")
                                                                           .AddRange([NewLinePayload.Payload, NewLinePayload.Payload])
                                                                           .Append(
                                                                               $"{GetLoc("Current")}: {DefaultPages.GetValueOrDefault(ModuleConfig.DefaultPage, LuminaWrapper.GetAddonText(7))}")
                                                                           .Build()
                                                                           .Encode();
                            DefaultPageNode.HideTooltip();
                            DefaultPageNode.ShowTooltip();
                        },
                        Tooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                       .Append($" {GetLoc("OptimizedFreeCompanyChest-DefaultPageHelp")}")
                                                       .AddRange([NewLinePayload.Payload, NewLinePayload.Payload])
                                                       .Append(
                                                           $"{GetLoc("Current")}: {DefaultPages.GetValueOrDefault(ModuleConfig.DefaultPage, LuminaWrapper.GetAddonText(7))}")
                                                       .Build()
                                                       .Encode(),
                    };
                    DefaultPageNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }
                
                var gilRadioButton = FreeCompanyChest->GetNodeById(16);
                if (gilRadioButton != null)
                    gilRadioButton->SetPositionFloat(0, 180);
                
                if (Throttler.Throttle("OptimizedFreeCompanyChest-OnUpdateDefaultPage", 100))
                    DefaultPageNode.IsChecked = TryGetCurrentFCPage(out var currentPage) && ModuleConfig.DefaultPage == currentPage;
                
                if (ComponentNode == null)
                {
                    ComponentNode = new()
                    {
                        IsVisible = true,
                        Position  = new(0, -70),
                        Size      = new(0, 60)
                    };

                    GilIconNode = new()
                    {
                        IsVisible = true,
                        IconId    = 65002,
                        Size      = new(32),
                        Position = new(345, 34)
                    };

                    GilItemsValueNode = new()
                    {
                        IsVisible        = true,
                        Position         = new(-55, 50),
                        Size             = new(395, 24),
                        SeString         = $"({GetLoc("OptimizedFreeCompanyChest-ExchangableItemsTotalValue")})",
                        FontSize         = 8,
                        TextFlags        = TextFlags.Edge | TextFlags.Emboss,
                        TextOutlineColor = KnownColor.Black.ToVector4(),
                        AlignmentType    = AlignmentType.Right,
                    };

                    GilItemsValueCountNode = new()
                    {
                        Position         = new(-55, 30),
                        Size             = new(395, 28),
                        IsVisible        = true,
                        SeString         = "0\ue049",
                        TextFlags        = TextFlags.Glare | TextFlags.Edge,
                        TextOutlineColor = ConvertByteColorToVector4(new() { R = 240, G = 142, B = 55, A = 255 }),
                        FontSize         = 14,
                        AlignmentType    = AlignmentType.Right,
                    };
                    
                    GilIconNode.AttachNode(ComponentNode);
                    GilItemsValueNode.AttachNode(ComponentNode);
                    GilItemsValueCountNode.AttachNode(ComponentNode);
                    ComponentNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                if (Throttler.Throttle("OptimizedFreeCompanyChest-OnUpdateGilItemsValue", 100))
                {
                    LastTotalPrice = TryGetTotalPrice(out var totalPrice) ? totalPrice : 0;

                    ComponentNode.IsVisible         = LastTotalPrice > 0;
                    GilItemsValueCountNode.String = $"{FormatNumber(LastTotalPrice)}\ue049";
                }

                break;
            case AddonEvent.PreFinalize:
                IsNeedToClose = false;

                ClearNodes();
                break;
        }
        
    }
    
    // 快捷存取
    private static void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (FreeCompanyChest == null || !ModuleConfig.FastMoveItem) return;
        
        var agent = AgentFreeCompanyChest.Instance();
        if (agent == null) return;
        
        // 取出
        if (args.AddonName == "FreeCompanyChest" && 
            agent->ContextInventoryType != InventoryType.Invalid)
        {
            var contextItem = agent->GetContextInventoryItem();
            if (contextItem == null || contextItem->ItemId == 0) return;
            
            foreach (var playerInventory in PlayerInventories)
            {
                if (TryFindFirstSuitableSlot(playerInventory, contextItem, out var slot))
                {
                    IsNeedToClose = true;
                    MoveItem(agent, agent->ContextInventoryType, (uint)agent->ContextInventorySlot, playerInventory, (uint)slot);
                    agent->ContextInventoryType = InventoryType.Invalid;
                    return;
                }
            }

            return;
        }

        // 存入
        if (args.AddonName.StartsWith("Inventory") &&
            args.Target is MenuTargetInventory { TargetItem: { } inventoryItem })
        {
            if (!TryGetSelectedItemSource(out var sourceInventory, out var sourceSlot)) return;
            if (!TryGetCurrentFCPage(out var page) || page == InventoryType.FreeCompanyCrystals) return;
            if (!TryFindFirstSuitableSlot(page, (InventoryItem*)inventoryItem.Address, out var slot)) return;

            IsNeedToClose = true;
            MoveItem(agent, sourceInventory, sourceSlot, page, (uint)slot);
        }
    }
    
    // 处理存取后的右键菜单关闭
    private static void OnAddonContextMenu(AddonEvent type, AddonArgs args)
    {
        if (!IsNeedToClose || InfosOm.ContextMenuXIV == null) return;

        InfosOm.ContextMenuXIV->IsVisible = false;
        InfosOm.ContextMenuXIV->Close(true);
        IsNeedToClose = false;
    }
    
    // 自动确认数量
    private static void OnAddonInput(AddonEvent type, AddonArgs args)
    {
        if (!ModuleConfig.FastMoveItem || InputNumeric == null || !IsAddonAndNodesReady(FreeCompanyChest)) return;

        Callback(InputNumeric, true, (int)InputNumeric->AtkValues[3].UInt);
    }

    // 移除操作锁
    private static bool SendInventoryRefreshDetour(InventoryManager* instance, int inventoryType)
    {
        // 直接返回 true 防锁
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RequestInventory, (uint)inventoryType);
        return true;
    }

    private static void ClearNodes()
    {
        FastMoveNode?.DetachNode();
        FastMoveNode = null;
        
        DefaultPageNode?.DetachNode();
        DefaultPageNode = null;
        
        ComponentNode?.DetachNode();
        ComponentNode = null;
        
        GilIconNode?.DetachNode();
        GilIconNode = null;
        
        GilItemsValueCountNode?.DetachNode();
        GilItemsValueCountNode = null;
        
        GilItemsValueNode?.DetachNode();
        GilItemsValueNode = null;
    }
    
    #region 工具

    private static bool TryGetSelectedItemSource(out InventoryType sourceInventory, out ushort sourceSlot)
    {
        sourceInventory = InventoryType.Invalid;
        sourceSlot      = 0;

        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventorySlot == null)
            return false;

        var slot = agent->TargetInventorySlot;
        if (slot->ItemId <= 0)
            return false;

        sourceInventory = agent->TargetInventoryId;
        sourceSlot      = (ushort)agent->TargetInventorySlotId;
        return true;
    }
    
    private static bool TryGetCurrentFCPage(out InventoryType page)
    {
        page = InventoryType.Invalid;
    
        if (FreeCompanyChest == null || GetNodeVisible(FreeCompanyChest->GetNodeById(106)))
            return false;

        if (FreeCompanyChest->AtkValues[1].UInt != 0)
        {
            page = InventoryType.FreeCompanyCrystals;
            return true;
        }

        page = (InventoryType)(20000 + FreeCompanyChest->AtkValues[2].UInt);
        return true;
    }
    
    private static bool TryFindFirstSuitableSlot(InventoryType type, InventoryItem* srcItem, out short foundSlot)
    {
        foundSlot = -1;
        
        if (srcItem == null || srcItem->ItemId == 0) return false;
        
        var manager = InventoryManager.Instance();
        if (manager == null) return false;
    
        var container = manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded) return false;
    
        if (!LuminaGetter.TryGetRow<Item>(srcItem->GetBaseItemId(), out var sheetItem))
            return false;

        // 可以堆叠
        if (sheetItem.StackSize > 1)
        {
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;

                if (item->GetBaseItemId()              == srcItem->GetBaseItemId() &&
                    item->Flags                        == srcItem->Flags           &&
                    item->Quantity + srcItem->Quantity <= sheetItem.StackSize)
                {
                    foundSlot = (short)i;
                    return true;
                }
            }
        }
        
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item->ItemId == 0)
            {
                foundSlot = (short)i;
                return true;
            }
        }
    
        return false;
    }
    
    private static bool TryGetTotalPrice(out long totalPrice)
    {
        totalPrice = 0;

        if (!IsAddonAndNodesReady(FreeCompanyChest)) return false;
        
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        if (!TryGetCurrentFCPage(out var fcPage) || fcPage == InventoryType.FreeCompanyCrystals) return false;
        
        foreach (var item in ItemIDs)
        {
            if (!LuminaGetter.TryGetRow(item, out Item itemData)) continue;
            
            var itemCount = manager->GetItemCountInContainer(item, fcPage);
            if (itemCount == 0) continue;
            
            var price = itemData.PriceLow;
            totalPrice += itemCount * price;
        }
        
        return totalPrice > 0;
    }
    
    private static string FormatNumber(long number) =>
        Lang.CurrentLanguage is not ("ChineseSimplified" or "ChineseTraditional") ? 
            number.ToString(CultureInfo.InvariantCulture) : 
            FormatNumberByChineseNotation(number, Lang.CurrentLanguage);

    #endregion

    protected override void Uninit()
    {
        DService.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        
        DService.AddonLifecycle.UnregisterListener(OnAddonContextMenu);
        DService.AddonLifecycle.UnregisterListener(OnAddonChest);
        DService.AddonLifecycle.UnregisterListener(OnAddonInput);

        ClearNodes();
        
        IsNeedToClose = false;
    }

    private class Config : ModuleConfiguration
    {
        public bool          FastMoveItem = true;
        public InventoryType DefaultPage  = InventoryType.Invalid;
    }
}
