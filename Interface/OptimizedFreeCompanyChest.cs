using System.Collections.Frozen;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using AgentFreeCompanyChest = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentFreeCompanyChest;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedFreeCompanyChest : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedFreeCompanyChestTitle"),
        Description = Lang.Get("OptimizedFreeCompanyChestDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig SendInventoryRefreshSig =
        new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B DA 48 8B F1 33 D2 0F B7 FA");
    private delegate bool SendInventoryRefreshDelegate
    (
        InventoryManager* instance,
        int               inventoryType
    );
    private Hook<SendInventoryRefreshDelegate>? SendInventoryRefreshHook;

    private Config config = null!;

    private CheckboxNode? fastMoveNode;
    
    private HorizontalListNode? componentNode;
    private IconImageNode?      gilIconNode;
    private TextNode?           gilItemsValueCountNode;

    private long lastTotalPrice;

    private IAddonEventHandle[] handles = new IAddonEventHandle[6];

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        SendInventoryRefreshHook ??= SendInventoryRefreshSig.GetHook<SendInventoryRefreshDelegate>(SendInventoryRefreshDetour);
        SendInventoryRefreshHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreSetup,    "FreeCompanyChest", OnAddonChest);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "FreeCompanyChest", OnAddonChest);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnAddonChest);
        if (FreeCompanyChest->IsAddonAndNodesReady())
            OnAddonChest(AddonEvent.PostRefresh, null);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnAddonInput);

        ContextMenuManager.Instance().RegPre(OnContextMenuOpened);
    }

    protected override void Uninit()
    {
        ContextMenuManager.Instance().Unreg(OnContextMenuOpened);

        IAddonLifecycle.Instance().UnregisterListener(OnAddonChest);
        IAddonLifecycle.Instance().UnregisterListener(OnAddonInput);

        fastMoveNode?.Dispose();
        fastMoveNode = null;

        componentNode?.Dispose();
        componentNode = null;

        gilIconNode?.Dispose();
        gilIconNode = null;

        gilItemsValueCountNode?.Dispose();
        gilItemsValueCountNode = null;

        foreach (var handle in handles)
            IAddonEventManager.Instance().RemoveEvent(handle);
    }

    private void OnAddonChest
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        var addon = (AddonFreeCompanyChest*)FreeCompanyChest;
        var agent = AgentFreeCompanyChest.Instance();
        
        switch (type)
        {
            case AddonEvent.PreFinalize:
                fastMoveNode           = null;
                componentNode          = null;
                gilIconNode            = null;
                gilItemsValueCountNode = null;
                
                foreach (var handle in handles)
                    IAddonEventManager.Instance().RemoveEvent(handle);
                break;

            case AddonEvent.PreSetup:
                if (addon == null)
                    return;

                if (config.DefaultPage != InventoryType.Invalid)
                {
                    if (config.DefaultPage == InventoryType.FreeCompanyCrystals)
                    {
                        agent->TabType  = 1;
                        agent->TabIndex = 0;
                    }
                    else
                    {
                        var defaultPageInt = (int)config.DefaultPage;
                        if (defaultPageInt < 20000)
                            return;

                        var index = defaultPageInt % 20000;
                        if (index > 5)
                            return;

                        agent->TabType  = 0;
                        agent->TabIndex = (byte)index;
                    }
                }

                break;

            case AddonEvent.PostRefresh:
            {
                if (addon == null)
                    return;

                if (fastMoveNode == null)
                {
                    fastMoveNode = new()
                    {
                        String    = Lang.Get("OptimizedFreeCompanyChest-FastMove"),
                        Size      = new(160, 28),
                        Position  = new(5, 208),
                        IsChecked = config.FastMoveItem,
                        OnClick = newState =>
                        {
                            config.FastMoveItem = newState;
                            config.Save(this);
                        },
                        TextTooltip = Lang.Get("OptimizedFreeCompanyChest-FastMoveHelp")
                    };
                    fastMoveNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                fastMoveNode.Label.TextFlags |= TextFlags.WordWrap | TextFlags.MultiLine;
                fastMoveNode.IsChecked       =  config.FastMoveItem;
                fastMoveNode.IsVisible       =  agent->TabType == 0;

                // 标记收藏的页面
                using var rented = new RentedSeStringBuilder();
                for (var i = 0; i < addon->ItemTabButtons.Length; i++)
                {
                    var buttonsPtr = addon->ItemTabButtons[i];
                    if (buttonsPtr.IsNull) continue;

                    var button = buttonsPtr.Value;
                    AddNodeEvents(button, i, false);

                    rented.Clear();
                    
                    var numberChar = (char)('\ue090' + i);
                    var text       = $"{numberChar} {LuminaWrapper.GetFCChestName(0)}";
                    rented.Append(text);

                    if (config.DefaultPage != InventoryType.FreeCompanyCrystals && (int)config.DefaultPage % 20000 == i)
                        rented.AppendIcon((uint)BitmapFontIcon.GoldStar);
                    
                    button->ButtonTextNode->SetText(rented.GetViewAsSpan());
                }
                
                rented.Clear();
                rented.Append(LuminaWrapper.GetAddonText(2990));
                if (config.DefaultPage == InventoryType.FreeCompanyCrystals)
                    rented.AppendIcon((uint)BitmapFontIcon.GoldStar);
                addon->CrystalTabButton->ButtonTextNode->SetText(rented.GetViewAsSpan());
                AddNodeEvents(addon->CrystalTabButton, 5, true);
                
                if (componentNode == null)
                {
                    componentNode = new()
                    {
                        Position    = new(380, -38),
                        Size        = new(0, 60),
                        ItemSpacing = 4,
                        Alignment   = HorizontalListAnchor.Right
                    };
                    componentNode.AddNodeFlags(NodeFlags.HasCollision);

                    gilIconNode = new()
                    {
                        IconId     = 65002,
                        Size       = new(32),
                        FitTexture = true
                    };

                    gilItemsValueCountNode = new()
                    {
                        Size          = new(100, 28),
                        Position      = new(0, 6),
                        TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Glare | TextFlags.Edge,
                        String        = "0\ue049",
                        FontSize      = 14,
                        AlignmentType = AlignmentType.Right,
                        TextTooltip   = Lang.Get("OptimizedFreeCompanyChest-ExchangableItemsTotalValue")
                    };
                    AtkColors.Value.ApplyTo(gilItemsValueCountNode);

                    componentNode.AddNode([gilIconNode, gilItemsValueCountNode]);
                    componentNode.RecalculateLayout();
                    componentNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                lastTotalPrice = TryGetTotalPrice(out var totalPrice) ?
                                     totalPrice :
                                     0;

                gilItemsValueCountNode.String = $"{lastTotalPrice.ToChineseString()}\ue049";
                gilItemsValueCountNode.Width  = gilItemsValueCountNode.GetTextDrawSize(false).X - 8;

                componentNode.RecalculateLayout();

                componentNode.IsVisible = lastTotalPrice > 0;
                break;
            }
        }
        
        return;

        void AddNodeEvents
        (
            AtkComponentRadioButton* button,
            int                      index,
            bool                     isCrystal
        )
        {
            button->OwnerNode->NodeFlags.Add(NodeFlags.RespondToMouse, NodeFlags.HasCollision);
            IAddonEventManager.Instance().RemoveEvent(handles[index]);
            handles[index] = IAddonEventManager.Instance().AddEvent
            (
                (nint)addon,
                (nint)button->OwnerNode,
                AddonEventType.MouseClick,
                (_, eventData) =>
                {
                    if (!eventData.IsRightClick)
                    {
                        if (isCrystal)
                            agent->SetTab(1, 0);
                        else
                            agent->SetTab(0, (byte)index);
                        
                        return;
                    }

                    var targetInventoryType = isCrystal ?
                                                  InventoryType.FreeCompanyCrystals :
                                                  (InventoryType)(index + 20000);
                    ContextMenuManager.Instance().Open
                    (
                        new()
                        {
                            Addon     = FreeCompanyChest,
                            AddonName = "FreeCompanyChest"
                        },
                        [
                            new ContextMenuEntryInfo
                            (
                                nameof(OptimizedFreeCompanyChest),
                                _ => new ContextMenuItem
                                {
                                    Name      = Lang.Get("OptimizedFreeCompanyChest-DefaultPage"),
                                    IsEnabled = targetInventoryType != config.DefaultPage,
                                    OnClicked = _ =>
                                    {
                                        config.DefaultPage = targetInventoryType;
                                        config.Save(this);

                                        OnAddonChest(AddonEvent.PostRefresh, null);
                                    }
                                },
                                omitPrefix: true
                            )
                        ]
                    );
                }
            );
        }
    }

    // 快捷存取
    private void OnContextMenuOpened
    (
        ref bool              isPrevented,
        ContextMenuOpenedArgs args
    )
    {
        if (FreeCompanyChest == null || !config.FastMoveItem) 
            return;

        var agent = AgentFreeCompanyChest.Instance();
        if (agent == null) return;

        // 取出
        if (args.AddonName               == "FreeCompanyChest" &&
            agent->ContextItem.Container != InventoryType.Invalid)
        {
            var contextItem = agent->GetContextInventoryItem();
            if (contextItem == null || contextItem->ItemId == 0)
                return;

            foreach (var playerInventory in Inventories.Player)
            {
                if (TryFindFirstSuitableSlot(playerInventory, contextItem, out var slot))
                {
                    agent->MoveItemInChest(agent->ContextItem.Container, (uint)agent->ContextItem.Slot, playerInventory, (uint)slot);
                    agent->ContextItem.Container = InventoryType.Invalid;
                    isPrevented                  = true;
                    return;
                }
            }

            return;
        }

        // 存入
        if (args.AddonName.StartsWith("Inventory") &&
            args.TargetInventoryItem is { } inventoryItem)
        { 
            if (!TryGetSelectedItemSource(out var sourceInventory, out var sourceSlot)) 
                return;
            if (!AgentFreeCompanyChest.Instance()->TryGetCurrentInventoryType(out var page) || 
                page == InventoryType.FreeCompanyCrystals) 
                return;
            if (!TryFindFirstSuitableSlot(page, &inventoryItem, out var slot)) 
                return;

            agent->MoveItemInChest(sourceInventory, sourceSlot, page, (uint)slot);
            isPrevented = true;
        }
    }

    // 自动确认数量
    private void OnAddonInput
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!config.FastMoveItem) 
            return;
        
        var addon = args.Addon.ToStruct();
        if (addon == null)
            return;
        
        var agent = AgentFreeCompanyChest.Instance();
        if (agent == null)
            return;
        
        if (agent->InputNumericAddonID == 0 ||
            addon->Id != agent->InputNumericAddonID)
            return;
        
        InputNumeric->Callback((int)InputNumeric->AtkValues[3].UInt);
    }

    // 移除操作锁
    private static bool SendInventoryRefreshDetour
    (
        InventoryManager* instance,
        int               inventoryType
    )
    {
        // 直接返回 true 防锁
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestInventory, (uint)inventoryType);
        return true;
    }

    #region 工具

    private static bool TryGetSelectedItemSource
    (
        out InventoryType sourceInventory,
        out ushort        sourceSlot
    )
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

    private static bool TryFindFirstSuitableSlot
    (
        InventoryType  type,
        InventoryItem* srcItem,
        out short      foundSlot
    )
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

    private static bool TryGetTotalPrice
    (
        out long totalPrice
    )
    {
        totalPrice = 0;

        if (!FreeCompanyChest->IsAddonAndNodesReady()) return false;

        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        var agent = AgentFreeCompanyChest.Instance();
        if (!agent->TryGetCurrentInventoryType(out var fcPage) ||
            fcPage == InventoryType.FreeCompanyCrystals ||
            !fcPage.TryGetItems(x => x.GetBaseItemId() > 0, out var items))
            return false;

        foreach (var item in items)
        {
            var itemID = item.GetBaseItemId();
            if (!LuminaGetter.TryGetRow(itemID, out Item itemRow))
                continue;
            if (!SellableItemIDs.Contains(itemID))
                continue;
            
            var price = itemRow.PriceLow;
            totalPrice += item.GetQuantity() * price;
        }

        return totalPrice > 0;
    }

    #endregion

    private class Config : ModuleConfig
    {
        public InventoryType DefaultPage  = InventoryType.FreeCompanyPage1;
        public bool          FastMoveItem = true;
    }

    #region 常量

    private static readonly FrozenSet<uint> SellableItemIDs =
    [
        .. LuminaGetter.Get<Item>()
                       .Where(x => x.ItemSortCategory.Value.Param == 150)
                       .Select(x => x.RowId)
    ];

    #endregion
}
