using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;
using AgentFreeCompanyChest = OmenTools.Interop.Game.Models.Native.AgentFreeCompanyChest;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFreeCompanyChest : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedFreeCompanyChestTitle"),
        Description = Lang.Get("OptimizedFreeCompanyChestDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly CompSig SendInventoryRefreshSig = new("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B DA 48 8B F1 33 D2 0F B7 FA");
    private delegate        bool    SendInventoryRefreshDelegate(InventoryManager* instance, int inventoryType);
    private                 Hook<SendInventoryRefreshDelegate>? SendInventoryRefreshHook;
    
    private delegate nint MoveItemDelegate(void* agent, InventoryType srcInv, uint srcSlot, InventoryType dstInv, uint dstSlot);
    private readonly MoveItemDelegate moveItem = new CompSig("40 53 55 56 57 41 57 48 83 EC ?? 45 33 FF").GetDelegate<MoveItemDelegate>();

    private Config config = null!;

    private CheckboxNode? fastMoveNode;

    private CheckboxNode? defaultPageNode;

    private VerticalListNode? componentNode;
    private IconImageNode?    gilIconNode;
    private TextNode?         gilItemsValueNode;
    private TextNode?         gilItemsValueCountNode;

    private bool isNeedToClose;
    private long lastTotalPrice;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        SendInventoryRefreshHook ??= SendInventoryRefreshSig.GetHook<SendInventoryRefreshDelegate>(SendInventoryRefreshDetour);
        SendInventoryRefreshHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "FreeCompanyChest", OnAddonChest);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "FreeCompanyChest", OnAddonChest);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeCompanyChest", OnAddonChest);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "InputNumeric",     OnAddonInput);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,     "ContextMenu",      OnAddonContextMenu);

        DService.Instance().ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }
    
    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnContextMenuOpened;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonContextMenu);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonChest);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonInput);

        ClearNodes();

        isNeedToClose = false;
    }

    // 打开部队储物柜时请求所有页面数据, 并生成 Node
    private void OnAddonChest(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (FreeCompanyChest == null) return;

                if (config.DefaultPage != InventoryType.Invalid)
                {
                    if (config.DefaultPage == InventoryType.FreeCompanyCrystals)
                        DService.Instance().Framework.Run(() => ((AtkComponentRadioButton*)FreeCompanyChest->GetComponentByNodeId(15))->Click());
                    else
                    {
                        if ((int)config.DefaultPage < 20000) return;

                        var index = (int)config.DefaultPage % 20000;
                        if (index > 5) return;

                        DService.Instance().Framework.Run(() => ((AtkComponentRadioButton*)FreeCompanyChest->GetComponentByNodeId((uint)(10 + index)))->Click());
                    }
                }

                break;
            case AddonEvent.PostDraw:
                if (FreeCompanyChest == null) return;

                if (fastMoveNode == null)
                {
                    fastMoveNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(5, 210),
                        IsVisible = true,
                        IsChecked = config.FastMoveItem,
                        IsEnabled = true,
                        String    = Lang.Get("OptimizedFreeCompanyChest-FastMove"),
                        OnClick = newState =>
                        {
                            config.FastMoveItem = newState;
                            config.Save(this);

                            isNeedToClose = false;
                        },
                        TextTooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                           .Append($" {Lang.Get("OptimizedFreeCompanyChest-FastMoveHelp")}")
                                                           .Build()
                                                           .Encode()
                    };
                    fastMoveNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                fastMoveNode.IsChecked = config.FastMoveItem;
                fastMoveNode.IsVisible = FreeCompanyChest->AtkValues[1].UInt == 0;

                if (defaultPageNode == null)
                {
                    defaultPageNode = new()
                    {
                        Size      = new(160.0f, 28.0f),
                        Position  = new(5, 156),
                        IsVisible = true,
                        IsChecked = false,
                        IsEnabled = true,
                        String    = Lang.Get("OptimizedFreeCompanyChest-DefaultPage"),
                        OnClick = newState =>
                        {
                            switch (newState)
                            {
                                case true when TryGetCurrentFCPage(out var currentPage):
                                    config.DefaultPage = currentPage;
                                    config.Save(this);
                                    break;
                                case false:
                                    config.DefaultPage = InventoryType.Invalid;
                                    config.Save(this);
                                    break;
                            }

                            defaultPageNode.TextTooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                                               .Append($" {Lang.Get("OptimizedFreeCompanyChest-DefaultPageHelp")}")
                                                                               .AddRange([NewLinePayload.Payload, NewLinePayload.Payload])
                                                                               .Append
                                                                               (
                                                                                   $"{Lang.Get("Current")}: {DefaultPages.GetValueOrDefault(config.DefaultPage, LuminaWrapper.GetAddonText(7))}"
                                                                               )
                                                                               .Build()
                                                                               .Encode();
                            defaultPageNode.HideTooltip();
                            defaultPageNode.ShowTooltip();
                        },
                        TextTooltip = new SeStringBuilder().AddIcon(BitmapFontIcon.ExclamationRectangle)
                                                           .Append($" {Lang.Get("OptimizedFreeCompanyChest-DefaultPageHelp")}")
                                                           .AddRange([NewLinePayload.Payload, NewLinePayload.Payload])
                                                           .Append
                                                           (
                                                               $"{Lang.Get("Current")}: {DefaultPages.GetValueOrDefault(config.DefaultPage, LuminaWrapper.GetAddonText(7))}"
                                                           )
                                                           .Build()
                                                           .Encode()
                    };
                    defaultPageNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                var gilRadioButton = FreeCompanyChest->GetNodeById(16);
                if (gilRadioButton != null)
                    gilRadioButton->SetPositionFloat(0, 185);

                if (Throttler.Shared.Throttle("OptimizedFreeCompanyChest-OnUpdateDefaultPage", 100))
                    defaultPageNode.IsChecked = TryGetCurrentFCPage(out var currentPage) && config.DefaultPage == currentPage;

                if (componentNode == null)
                {
                    componentNode = new()
                    {
                        IsVisible = true,
                        Position  = new(0, -70),
                        Size      = new(0, 60)
                    };

                    gilIconNode = new()
                    {
                        IsVisible  = true,
                        IconId     = 65002,
                        Size       = new(32),
                        Position   = new(345, 34),
                        FitTexture = true
                    };

                    gilItemsValueNode = new()
                    {
                        IsVisible        = true,
                        Position         = new(-55, 50),
                        Size             = new(395, 24),
                        String           = $"({Lang.Get("OptimizedFreeCompanyChest-ExchangableItemsTotalValue")})",
                        FontSize         = 8,
                        TextColor        = ColorHelper.GetColor(50),
                        TextFlags        = TextFlags.Edge,
                        TextOutlineColor = ColorHelper.GetColor(1),
                        AlignmentType    = AlignmentType.Right
                    };

                    gilItemsValueCountNode = new()
                    {
                        Position         = new(-55, 30),
                        Size             = new(395, 28),
                        IsVisible        = true,
                        String           = "0\ue049",
                        TextFlags        = TextFlags.Glare | TextFlags.Edge,
                        TextOutlineColor = ColorHelper.GetColor(32),
                        FontSize         = 14,
                        TextColor        = ColorHelper.GetColor(50),
                        AlignmentType    = AlignmentType.Right
                    };

                    gilIconNode.AttachNode(componentNode);
                    gilItemsValueNode.AttachNode(componentNode);
                    gilItemsValueCountNode.AttachNode(componentNode);
                    componentNode.AttachNode(FreeCompanyChest->GetNodeById(9));
                }

                if (Throttler.Shared.Throttle("OptimizedFreeCompanyChest-OnUpdateGilItemsValue", 100))
                {
                    lastTotalPrice = TryGetTotalPrice(out var totalPrice) ? totalPrice : 0;

                    componentNode.IsVisible       = lastTotalPrice > 0;
                    gilItemsValueCountNode.String = $"{lastTotalPrice.ToChineseString()}\ue049";
                }

                break;
            case AddonEvent.PreFinalize:
                isNeedToClose = false;

                ClearNodes();
                break;
        }

    }

    // 快捷存取
    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (FreeCompanyChest == null || !config.FastMoveItem) return;

        var agent = AgentFreeCompanyChest.Instance();
        if (agent == null) return;

        // 取出
        if (args.AddonName              == "FreeCompanyChest" &&
            agent->ContextInventoryType != InventoryType.Invalid)
        {
            var contextItem = agent->GetContextInventoryItem();
            if (contextItem == null || contextItem->ItemId == 0) return;

            foreach (var playerInventory in Inventories.Player)
            {
                if (TryFindFirstSuitableSlot(playerInventory, contextItem, out var slot))
                {
                    isNeedToClose = true;
                    moveItem(agent, agent->ContextInventoryType, (uint)agent->ContextInventorySlot, playerInventory, (uint)slot);
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

            isNeedToClose = true;
            moveItem(agent, sourceInventory, sourceSlot, page, (uint)slot);
        }
    }

    // 处理存取后的右键菜单关闭
    private void OnAddonContextMenu(AddonEvent type, AddonArgs args)
    {
        if (!isNeedToClose || ContextMenuAddon == null) return;

        ContextMenuAddon->IsVisible = false;
        ContextMenuAddon->Close(true);
        isNeedToClose = false;
    }

    // 自动确认数量
    private void OnAddonInput(AddonEvent type, AddonArgs args)
    {
        if (!config.FastMoveItem || InputNumeric == null || !FreeCompanyChest->IsAddonAndNodesReady()) return;

        InputNumeric->Callback((int)InputNumeric->AtkValues[3].UInt);
    }

    // 移除操作锁
    private static bool SendInventoryRefreshDetour(InventoryManager* instance, int inventoryType)
    {
        // 直接返回 true 防锁
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestInventory, (uint)inventoryType);
        return true;
    }

    private void ClearNodes()
    {
        fastMoveNode?.Dispose();
        fastMoveNode = null;

        defaultPageNode?.Dispose();
        defaultPageNode = null;

        componentNode?.Dispose();
        componentNode = null;

        gilIconNode?.Dispose();
        gilIconNode = null;

        gilItemsValueCountNode?.Dispose();
        gilItemsValueCountNode = null;

        gilItemsValueNode?.Dispose();
        gilItemsValueNode = null;
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

        if (FreeCompanyChest == null || FreeCompanyChest->GetNodeById(106)->GetVisibility())
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

        if (!FreeCompanyChest->IsAddonAndNodesReady()) return false;

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

    #endregion
    
    private class Config : ModuleConfig
    {
        public InventoryType DefaultPage  = InventoryType.Invalid;
        public bool          FastMoveItem = true;
    }

    #region 常量

    private static readonly uint[] ItemIDs =
        LuminaGetter.Get<Item>()
                    .Where(x => x.ItemSortCategory.Value.Param == 150)
                    .Select(x => x.RowId)
                    .ToArray();

    private static readonly FrozenDictionary<InventoryType, string> DefaultPages = new Dictionary<InventoryType, string>
    {
        [InventoryType.FreeCompanyPage1]    = $"{LuminaWrapper.GetFCChestName(0)} \ue090",
        [InventoryType.FreeCompanyPage2]    = $"{LuminaWrapper.GetFCChestName(0)} \ue091",
        [InventoryType.FreeCompanyPage3]    = $"{LuminaWrapper.GetFCChestName(0)} \ue092",
        [InventoryType.FreeCompanyPage4]    = $"{LuminaWrapper.GetFCChestName(0)} \ue093",
        [InventoryType.FreeCompanyPage5]    = $"{LuminaWrapper.GetFCChestName(0)} \ue094",
        [InventoryType.FreeCompanyCrystals] = $"{LuminaWrapper.GetAddonText(2990)}",
        [InventoryType.Invalid]             = $"{LuminaWrapper.GetAddonText(7)}"
    }.ToFrozenDictionary();

    #endregion
}
