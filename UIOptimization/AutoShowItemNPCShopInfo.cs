using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoShowItemNPCShopInfo : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoShowItemNPCShopInfoTitle"),
        Description         = GetLoc("AutoShowItemNPCShopInfoDescription"),
        Category            = ModuleCategories.UIOptimization,
        ModulesPrerequisite = ["BetterMarketBoard", "BetterTeleport"],
        PreviewImageURL     = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/AutoShowItemNPCShopInfo-UI.png"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static ShopInfoContextMenu ContextMenu = new();

    private static readonly Dictionary<uint, TooltipModification> ItemModifications = [];

    protected override void Init()
    {
        DService.Instance().ContextMenu.OnMenuOpened += OnMenuOpen;
        GameTooltipManager.Instance().RegGenerateItemTooltipModifier(OnItemTooltipGenerate);
    }
    
    protected override void Uninit()
    {
        DService.Instance().ContextMenu.OnMenuOpened -= OnMenuOpen;
        
        GameTooltipManager.Instance().Unreg(generateItemModifiers: OnItemTooltipGenerate);

        foreach (var tooltipModification in ItemModifications.Values)
            GameTooltipManager.Instance().RemoveItemDetail(tooltipModification);
        ItemModifications.Clear();
        
        AddonShopsPreview.Addon?.Dispose();
        AddonShopsPreview.Addon = null;
    }
    
    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!ContextMenu.IsDisplay(args)) return;
        args.AddMenuItem(ContextMenu.Get());
    }
    
    private static void OnItemTooltipGenerate(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
    {
        var itemID = AgentItemDetail.Instance()->ItemId;
        
        if (ItemModifications.TryGetValue(itemID, out _)) return;
        if (ItemShopInfo.GetItemInfo(itemID) is not { } itemInfo) return;
        
        var text = new SeStringBuilder()
                   .Add(NewLinePayload.Payload)
                   .Add(NewLinePayload.Payload)
                   .AddUiForeground(32)
                   .AddText($"[{GetLoc("AutoShowItemNPCShopInfo-ItemDetail", itemInfo.NPCInfos.Count)}]")
                   .AddUiForegroundOff()
                   .Build();

        ItemModifications[itemID] = GameTooltipManager.Instance().AddItemDetail
        (
            itemID,
            TooltipItemType.ItemDescription,
            text,
            TooltipModifyMode.Append
        );
    }

    [IPCProvider("DailyRoutines.Modules.AutoShowItemNPCShopInfo.OpenByItemID")]
    private static bool OpenByItemID(uint itemID)
    {
        if (AddonShopsPreview.Addon is { IsOpen: true } addon &&
            addon.ShopInfo.ItemID == itemID)
            AddonShopsPreview.CloseAndClear();
        else if (ItemShopInfo.GetItemInfo(itemID) is { } itemInfo)
            AddonShopsPreview.OpenWithData(itemInfo);
        else
        {
            ChatError(GetLoc("AutoShowItemNPCShopInfo-Notification-ShopNotFound", itemID));
            return false;
        }

        return true;
    }

    private class ShopInfoContextMenu : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("AutoShowItemNPCShopInfo-ContextMenu");

        public override string Identifier { get; protected set; } = nameof(AutoShowItemNPCShopInfo);

        protected override bool WithDRPrefix { get; set; } = true;

        protected override void OnClicked(IMenuItemClickedArgs args) =>
            OpenByItemID(ContextMenuItemManager.Instance().CurrentItemID);

        public override bool IsDisplay(IMenuOpenedArgs args) => 
            ItemShopInfo.GetItemInfo(ContextMenuItemManager.Instance().CurrentItemID) is not null;
    }
    
    private class AddonShopsPreview : NativeAddon
    {
        public static AddonShopsPreview? Addon { get; set; }

        public static void CloseAndClear()
        {
            if (Addon == null) return;

            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData(ItemShopInfo shopInfo)
        {
            if (shopInfo is not { NPCInfos.Count: > 0 }) return;
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;

            CloseAndClear();

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    Addon ??= new(shopInfo)
                    {
                        InternalName = "DRItemNPCShopsPreview",
                        Title        = LuminaWrapper.GetAddonText(350),
                        Size         = new(600f, 400f)
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        public ItemShopInfo ShopInfo { get; set; }


        private static Task? OpenAddonTask;

        private AddonShopsPreview(ItemShopInfo shopInfo) =>
            ShopInfo = shopInfo;

        protected override void OnSetup(AtkUnitBase* addon)
        {
            var itemInfoRow = new HorizontalListNode
            {
                IsVisible   = true,
                Size        = new Vector2(ContentSize.X - 5, 48),
                Position    = ContentStartPosition + new Vector2(5, 0),
                ItemSpacing = 0
            };
            itemInfoRow.AttachNode(this);

            var itemIconNode = new IconImageNode
            {
                IsVisible  = true,
                IconId     = LuminaWrapper.GetItemIconID(ShopInfo.ItemID),
                Size       = new(36),
                FitTexture = true
            };
            itemInfoRow.AddNode(itemIconNode);

            itemInfoRow.AddDummy(3f);
            
            var itemNameNode = new TextNode
            {
                IsVisible     = true,
                TextFlags     = TextFlags.AutoAdjustNodeSize,
                String        = LuminaWrapper.GetItemName(ShopInfo.ItemID),
                FontSize      = 24,
                Position      = new(0, 3),
                AlignmentType = AlignmentType.TopLeft
            };
            itemInfoRow.AddNode(itemNameNode);

            if (ShopInfo.GetItem().ItemSearchCategory.RowId > 0)
            {
                itemInfoRow.AddDummy(15f);
                
                var marketButtonNode = new IconButtonNode
                {
                    IconId      = 60570,
                    TextTooltip = LuminaWrapper.GetAddonText(548),
                    Size        = new(32),
                    Position    = new Vector2(0, 2),
                    IsVisible   = true,
                    OnClick     = () => ChatManager.Instance().SendMessage($"/pdr market {ShopInfo.GetItem().Name}")
                };
                itemInfoRow.AddNode(marketButtonNode);
            }

            var source = ShopInfo.NPCInfos
                                 .Where(x => x.Location != null)
                                 .DistinctBy(x => $"{x.Name}_{x.Location.GetTerritory().ExtractPlaceName()}")
                                 .OrderBy(x => x.Location.TerritoryID == 282)
                                 .ToList();

            var scrollingAreaNode = new ScrollingAreaNode<VerticalListNode>
            {
                Position          = ContentStartPosition + new Vector2(5, 48),
                Size              = ContentSize          - new Vector2(5, 48),
                ContentHeight     = (source.Count + 0.5f) * 33,
                ScrollSpeed       = 100,
                IsVisible         = true,
                AutoHideScrollBar = true
            };
            scrollingAreaNode.AttachNode(this);

            var contentNode = scrollingAreaNode.ContentNode;
            contentNode.ItemSpacing = 3;

            contentNode.AddDummy(5);

            var testTextNode = new TextNode();

            var longestLocationText = source
                                      .Select(x => x.Location.GetTerritory().ExtractPlaceName())
                                      .MaxBy(x => x.Length);
            var locationColumnWidth = testTextNode.GetTextDrawSize(longestLocationText).X + 40f;

            var longestNameText = source
                                  .Select(x => x.Name)
                                  .MaxBy(x => x.Length);
            var nameColumnWidth = testTextNode.GetTextDrawSize(longestNameText).X + 5f;

            foreach (var npcInfo in source)
            {
                var row = new HorizontalListNode
                {
                    IsVisible = true,
                    Size      = new(contentNode.Width, 30)
                };
                contentNode.AddNode(row);

                var npcNameNode = new TextNode
                {
                    IsVisible = true,
                    String    = npcInfo.Name,
                    Position  = new(0, 4),
                    Size      = new(nameColumnWidth, 28f)
                };
                row.AddNode(npcNameNode);

                var mapMarkerButton = new IconButtonNode
                {
                    IconId      = 60561,
                    TextTooltip = LuminaWrapper.GetAddonText(467),
                    Size        = new(28),
                    Position    = new(0, -1),
                    IsVisible   = true,
                    OnClick = () =>
                    {
                        var pos = MapToWorld(new(npcInfo.Location.MapPosition.X, npcInfo.Location.MapPosition.Y), npcInfo.Location.GetMap()).ToVector3(0);

                        var instance = AgentMap.Instance();
                        instance->SetFlagMapMarker(npcInfo.Location.TerritoryID, npcInfo.Location.MapID, pos);

                        instance->OpenMap(npcInfo.Location.MapID, npcInfo.Location.TerritoryID, npcInfo.Name);
                    }
                };
                row.AddNode(mapMarkerButton);

                var locationName = npcInfo.Location.TerritoryID == 282 ? LuminaWrapper.GetAddonText(8495) : npcInfo.Location.GetTerritory().ExtractPlaceName();
                var npcLocationNode = new TextButtonNode
                {
                    IsVisible   = true,
                    String      = locationName,
                    Size        = new(locationColumnWidth, 28f),
                    IsEnabled   = npcInfo.Location.TerritoryID != 282,
                    TextTooltip = LuminaWrapper.GetAddonText(1806),
                    OnClick = () =>
                    {
                        var pos = MapToWorld(new(npcInfo.Location.MapPosition.X, npcInfo.Location.MapPosition.Y), npcInfo.Location.GetMap()).ToVector3(0);

                        var instance = AgentMap.Instance();
                        instance->SetFlagMapMarker(npcInfo.Location.TerritoryID, npcInfo.Location.MapID, pos);

                        var aetheryte = MovementManager.GetNearestAetheryte(pos, npcInfo.Location.TerritoryID);
                        if (aetheryte != null)
                            ChatManager.Instance().SendMessage($"/pdrtelepo {aetheryte.Name}");
                    }
                };
                row.AddNode(npcLocationNode);

                row.AddDummy(10f);
                
                var costInfoComponent = new HorizontalListNode
                {
                    IsVisible   = true,
                    Size        = new(100, 28),
                    ItemSpacing = 10
                };

                foreach (var costInfo in npcInfo.CostInfos)
                {
                    var costIconNode = new IconImageNode
                    {
                        IsVisible      = true,
                        Size           = new(28),
                        IconId         = LuminaWrapper.GetItemIconID(costInfo.ItemID),
                        Position       = new(-10, -4),
                        TextTooltip    = $"{LuminaWrapper.GetItemName(costInfo.ItemID)}",
                        ImageNodeFlags = ImageNodeFlags.AutoFit
                    };
                    costInfoComponent.AddNode(costIconNode);

                    var costNode = new TextNode
                    {
                        IsVisible = true,
                        TextFlags = TextFlags.AutoAdjustNodeSize,
                        String    = costInfo.ToString().Replace(LuminaWrapper.GetItemName(costInfo.ItemID), string.Empty).Trim(),
                        Position  = new(4, 6)
                    };
                    costInfoComponent.AddNode(costNode);
                }

                row.AddNode(costInfoComponent);
            }
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();

                if (SystemMenu != null)
                    SystemMenu->Close(true);
            }
        }
    }
}
