using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Premade.Node.Simple;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource.Models;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsDestination : NativeAddon
    {
        private static Task? OpenAddonTask;

        private AddonNPCShopsDestination(ExchangeItemsInfo sourceInfo) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsDestination? Addon      { get; set; }
        public        ExchangeItemsInfo         SourceInfo { get; set; }

        public static void CloseAndClear()
        {
            if (Addon == null) return;

            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData(ExchangeItemsInfo sourceInfo)
        {
            if (sourceInfo is not { Items.Count: > 0 }) return;
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;

            CloseAndClear();

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    Addon ??= new(sourceInfo)
                    {
                        InternalName = "DRNPCShopsDestinations",
                        Title        = Lang.Get("AutoShowItemNPCShopInfo-ContextMenu-Destination"),
                        Size         = new(760f, 540f)
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var costItem  = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.CostItemID);
            var itemCount = GetOwnedItemCount(SourceInfo.CostItemID);

            var headerNode = new VerticalListNode
            {
                Size        = new(ContentSize.X - 16, 72),
                Position    = ContentStartPosition + new Vector2(8, 2),
                ItemSpacing = 4
            };
            headerNode.AttachNode(this);

            var itemInfoRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 40),
                ItemSpacing = 0
            };
            headerNode.AddNode(itemInfoRow);

            var itemIconNode = new IconImageNode
            {
                IconId     = LuminaWrapper.GetItemIconID(SourceInfo.CostItemID),
                Size       = new(36),
                FitTexture = true
            };
            itemInfoRow.AddNode(itemIconNode);

            itemInfoRow.AddDummy(6f);

            var itemNameNode = new TextNode
            {
                TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                String           = LuminaWrapper.GetItemName(SourceInfo.CostItemID),
                FontSize         = 24,
                Position         = new(0, 3),
                AlignmentType    = AlignmentType.TopLeft,
                TextColor        = ColorHelper.GetColor(8),
                TextOutlineColor = ColorHelper.GetColor(7)
            };
            itemInfoRow.AddNode(itemNameNode);

            if (costItem.ItemSearchCategory.RowId > 0)
            {
                itemInfoRow.AddDummy(12f);

                var marketButtonNode = new IconButtonNode
                {
                    IconId      = 60570,
                    TextTooltip = LuminaWrapper.GetAddonText(548),
                    Size        = new(32),
                    Position    = new Vector2(0, 3),
                    OnClick     = () => OpenMarket(SourceInfo.CostItemID)
                };
                itemInfoRow.AddNode(marketButtonNode);
            }

            var summaryRow = new HorizontalListNode
            {
                Size        = new(headerNode.Width, 26),
                ItemSpacing = 16
            };
            headerNode.AddNode(summaryRow);

            summaryRow.AddNode
            (
                new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize,
                    String           = $"{LuminaWrapper.GetAddonText(358)}: {itemCount}",
                    Position         = new(0, 3),
                    TextColor        = ColorHelper.GetColor(34),
                    TextOutlineColor = ColorHelper.GetColor(7)
                }
            );

            summaryRow.AddNode
            (
                new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize,
                    String           = Lang.Get("AutoShowItemNPCShopInfo-ExchangeItemCount", SourceInfo.Items.Count),
                    Position         = new(0, 3),
                    TextColor        = ColorHelper.GetColor(3),
                    TextOutlineColor = ColorHelper.GetColor(7)
                }
            );

            var exchangeItems = SourceInfo.Items
                                          .OrderBy(x => x.GetItemName())
                                          .ToList();

            var contentHeight = exchangeItems.Sum(CalculateExchangeItemHeight) + (Math.Max(0, exchangeItems.Count - 1) * 8f) + 12f;

            var scrollingAreaNode = new ScrollingAreaNode<VerticalListNode>
            {
                Position          = ContentStartPosition + new Vector2(6,  76),
                Size              = ContentSize          - new Vector2(12, 76),
                ContentHeight     = contentHeight,
                ScrollSpeed       = 100,
                AutoHideScrollBar = true
            };
            scrollingAreaNode.AttachNode(this);

            var contentNode = scrollingAreaNode.ContentNode;
            contentNode.ItemSpacing = 6;

            foreach (var exchangeItem in exchangeItems)
                AddExchangeItemSection(contentNode, exchangeItem);

            scrollingAreaNode.FitToContentHeight();
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

        private static float CalculateExchangeItemHeight(ExchangeItemInfo exchangeItem)
        {
            var         visibleNPCCount   = exchangeItem.NPCInfos.Count(x => x.Location != null);
            const float HEADER_HEIGHT     = 40f;
            const float ROW_HEIGHT        = 32f;
            const float ROW_SPACING       = 4f;
            var         descriptionHeight = string.IsNullOrWhiteSpace(exchangeItem.AchievementDescription) ? 0f : 18f;
            const float VERTICAL_PADDING  = 20f;

            return VERTICAL_PADDING + HEADER_HEIGHT + descriptionHeight + (visibleNPCCount * ROW_HEIGHT) + (Math.Max(0, visibleNPCCount - 1) * ROW_SPACING);
        }

        private static void AddExchangeItemSection(VerticalListNode contentNode, ExchangeItemInfo exchangeItem)
        {
            var sortedNPCInfos = exchangeItem.NPCInfos
                                             .Where(x => x.Location               != null)
                                             .OrderBy(x => x.Location.TerritoryID == 282)
                                             .ThenBy(x => GetLocationName(x.Location))
                                             .ThenBy(x => x.Name)
                                             .ToList();

            var maxLocationText = sortedNPCInfos.Count == 0
                                      ? string.Empty
                                      : sortedNPCInfos.MaxBy(x => GetLocationName(x.Location).Length) is { } longestNpc
                                          ? GetLocationName(longestNpc.Location)
                                          : string.Empty;

            var         testTextNode        = new TextNode();
            var         locationColumnWidth = Math.Clamp(testTextNode.GetTextDrawSize(maxLocationText).X + 36f, 132f, 220f);
            const float COST_COLUMN_WIDTH   = 132f;

            var sectionContainer = new ResNode
            {
                Size = new(contentNode.Width - 4, CalculateExchangeItemHeight(exchangeItem))
            };
            contentNode.AddNode(sectionContainer);

            var sectionBackground = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Offsets            = new Vector4(8f),
                MultiplyColor      = new(0.45f, 0.45f, 0.48f),
                Alpha              = 0.28f,
                Size               = sectionContainer.Size
            };
            sectionBackground.AttachNode(sectionContainer);

            var sectionNode = new VerticalListNode
            {
                Size        = sectionContainer.Size - new Vector2(20f, 20f),
                Position    = new(10f, 10f),
                ItemSpacing = 4
            };
            sectionNode.AttachNode(sectionContainer);

            var npcColumnWidth = Math.Max(180f, sectionNode.Width - locationColumnWidth - COST_COLUMN_WIDTH - 44f);

            var itemHeader = new HorizontalListNode
            {
                Size        = new(sectionNode.Width, 38),
                ItemSpacing = 0
            };
            sectionNode.AddNode(itemHeader);

            itemHeader.AddNode
            (
                new IconImageNode
                {
                    IconId     = LuminaWrapper.GetItemIconID(exchangeItem.ItemID),
                    Size       = new(32),
                    FitTexture = true,
                    Position   = new(0, 1)
                }
            );

            itemHeader.AddDummy(6f);

            var titleColumn = new VerticalListNode
            {
                Size        = new(sectionNode.Width - 90f, 36f),
                Position    = new(0, 0),
                ItemSpacing = 0
            };
            itemHeader.AddNode(titleColumn);

            titleColumn.AddNode
            (
                new TextNode
                {
                    TextFlags        = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                    AlignmentType    = AlignmentType.Left,
                    String           = exchangeItem.GetItemName(),
                    FontSize         = 16,
                    Size             = new(0, 32),
                    Position         = new(0, 6),
                    TextColor        = ColorHelper.GetColor(28),
                    TextOutlineColor = ColorHelper.GetColor(509)
                }
            );

            var exchangeTargetItem = LuminaGetter.GetRowOrDefault<Item>(exchangeItem.ItemID);

            if (exchangeTargetItem.ItemSearchCategory.RowId > 0)
            {
                itemHeader.AddDummy(10f);
                itemHeader.AddNode
                (
                    new IconButtonNode
                    {
                        IconId      = 60570,
                        TextTooltip = LuminaWrapper.GetAddonText(548),
                        Size        = new(28),
                        Position    = new(0, 3),
                        OnClick     = () => OpenMarket(exchangeItem.ItemID)
                    }
                );
            }

            if (!string.IsNullOrWhiteSpace(exchangeItem.AchievementDescription))
            {
                sectionNode.AddNode
                (
                    new TextNode
                    {
                        TextFlags        = TextFlags.AutoAdjustNodeSize,
                        String           = exchangeItem.AchievementDescription,
                        FontSize         = 12,
                        TextColor        = ColorHelper.GetColor(3),
                        TextOutlineColor = ColorHelper.GetColor(7)
                    }
                );
            }

            foreach (var npcInfo in sortedNPCInfos)
            {
                var row = new HorizontalListNode
                {
                    Size = new(sectionNode.Width, 32)
                };
                sectionNode.AddNode(row);

                row.AddNode
                (
                    new TextNode
                    {
                        TextFlags = TextFlags.AutoAdjustNodeSize,
                        String    = GetExchangeNPCDisplayText(npcInfo),
                        Position  = new(0, 4),
                        Size      = new(npcColumnWidth, 28f),
                        FontSize  = 14,
                        TextColor = ColorHelper.GetColor(2)
                    }
                );

                row.AddNode
                (
                    new IconButtonNode
                    {
                        IconId      = 60561,
                        TextTooltip = LuminaWrapper.GetAddonText(467),
                        Size        = new(28),
                        Position    = new(0, -1),
                        OnClick     = () => OpenMap(npcInfo.Location, npcInfo.Name)
                    }
                );

                row.AddNode
                (
                    new TextButtonNode
                    {
                        String      = GetLocationName(npcInfo.Location),
                        Size        = new(locationColumnWidth, 28),
                        IsEnabled   = npcInfo.Location.TerritoryID != 282,
                        TextTooltip = LuminaWrapper.GetAddonText(1806),
                        OnClick     = () => TeleportToLocation(npcInfo.Location)
                    }
                );

                row.AddDummy(8f);

                var costInfoComponent = new HorizontalListNode
                {
                    Size        = new(COST_COLUMN_WIDTH, 32),
                    ItemSpacing = 4f
                };

                foreach (var costInfo in npcInfo.CostInfos)
                    AddCostInfoNode(costInfoComponent, costInfo);

                row.AddNode(costInfoComponent);
            }
        }

        private static void AddCostInfoNode(HorizontalListNode costInfoComponent, ShopItemCostInfo costInfo)
        {
            costInfoComponent.AddNode
            (
                new IconImageNode
                {
                    TextureSize    = new(24),
                    Size           = new(24),
                    IconId         = LuminaWrapper.GetItemIconID(costInfo.ItemID),
                    TextTooltip    = LuminaWrapper.GetItemName(costInfo.ItemID),
                    ImageNodeFlags = ImageNodeFlags.AutoFit
                }
            );

            costInfoComponent.AddNode
            (
                new TextNode
                {
                    String   = costInfo.ToString().Replace(LuminaWrapper.GetItemName(costInfo.ItemID), string.Empty).Trim(),
                    Position = new(0, 3)
                }
            );
        }

        private static string GetExchangeNPCDisplayText(ExchangeItemNPCInfo npcInfo) =>
            string.IsNullOrWhiteSpace(npcInfo.ShopName) ? npcInfo.Name : $"{npcInfo.Name} ({npcInfo.ShopName})";
    }
}
