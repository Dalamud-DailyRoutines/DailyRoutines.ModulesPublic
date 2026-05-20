using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic.AutoShowItemNPCShopInfo;

public unsafe partial class AutoShowItemNPCShopInfo
{
    private class AddonNPCShopsSource : NativeAddon
    {
        private static Task? OpenAddonTask;

        private AddonNPCShopsSource(ItemSourceInfo sourceInfo) =>
            SourceInfo = sourceInfo;

        public static AddonNPCShopsSource? Addon { get; set; }

        public ItemSourceInfo SourceInfo { get; set; }

        public static void CloseAndClear()
        {
            if (Addon == null) return;

            Addon.Dispose();
            Addon = null;
        }

        public static void OpenWithData(ItemSourceInfo sourceInfo)
        {
            if (sourceInfo is not { NPCInfos.Count: > 0 }) return;
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;

            CloseAndClear();

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    Addon ??= new(sourceInfo)
                    {
                        InternalName = "DRNPCShopsSource",
                        Title        = LuminaWrapper.GetAddonText(350),
                        Size         = new(600f, 400f)
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var item = LuminaGetter.GetRowOrDefault<Item>(SourceInfo.ItemID);

            var itemInfoRow = new HorizontalListNode
            {
                Size        = new Vector2(ContentSize.X - 5, 48),
                Position    = ContentStartPosition + new Vector2(5, 0),
                ItemSpacing = 0
            };
            itemInfoRow.AttachNode(this);

            var itemIconNode = new IconImageNode
            {
                IconId     = LuminaWrapper.GetItemIconID(SourceInfo.ItemID),
                Size       = new(36),
                FitTexture = true
            };
            itemInfoRow.AddNode(itemIconNode);

            itemInfoRow.AddDummy(3f);

            var itemNameNode = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize,
                String        = LuminaWrapper.GetItemName(SourceInfo.ItemID),
                FontSize      = 24,
                Position      = new(0, 3),
                AlignmentType = AlignmentType.TopLeft
            };
            itemInfoRow.AddNode(itemNameNode);

            if (item.ItemSearchCategory.RowId > 0)
            {
                itemInfoRow.AddDummy(15f);

                var marketButtonNode = new IconButtonNode
                {
                    IconId      = 60570,
                    TextTooltip = LuminaWrapper.GetAddonText(548),
                    Size        = new(32),
                    Position    = new Vector2(0, 2),
                    OnClick     = () => OpenMarket(SourceInfo.ItemID)
                };
                itemInfoRow.AddNode(marketButtonNode);
            }

            var source = SourceInfo.NPCInfos
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
                    Size = new(contentNode.Width, 30)
                };
                contentNode.AddNode(row);

                var npcNameNode = new TextNode
                {
                    String           = npcInfo.Name,
                    Position         = new(0, 4),
                    Size             = new(nameColumnWidth, 28f),
                    TextFlags        = TextFlags.Edge,
                    TextColor        = ColorHelper.GetColor(1),
                    TextOutlineColor = ColorHelper.GetColor(6)
                };
                row.AddNode(npcNameNode);

                var mapMarkerButton = new IconButtonNode
                {
                    IconId      = 60561,
                    TextTooltip = LuminaWrapper.GetAddonText(467),
                    Size        = new(28),
                    Position    = new(0, -1),
                    OnClick     = () => OpenMap(npcInfo.Location, npcInfo.Name)
                };
                row.AddNode(mapMarkerButton);

                var locationName = GetLocationName(npcInfo.Location);
                var npcLocationNode = new TextButtonNode
                {
                    String      = locationName,
                    Size        = new(locationColumnWidth, 28f),
                    IsEnabled   = npcInfo.Location.TerritoryID != 282,
                    TextTooltip = LuminaWrapper.GetAddonText(1806),
                    OnClick     = () => TeleportToLocation(npcInfo.Location)
                };
                row.AddNode(npcLocationNode);

                row.AddDummy(10f);

                var costInfoComponent = new HorizontalListNode
                {
                    Size        = new(100, 28),
                    ItemSpacing = 10
                };

                foreach (var costInfo in npcInfo.CostInfos)
                {
                    var costIconNode = new IconImageNode
                    {
                        Size           = new(28),
                        IconId         = LuminaWrapper.GetItemIconID(costInfo.ItemID),
                        Position       = new(-10, -4),
                        TextTooltip    = $"{LuminaWrapper.GetItemName(costInfo.ItemID)}",
                        ImageNodeFlags = ImageNodeFlags.AutoFit
                    };
                    costInfoComponent.AddNode(costIconNode);

                    var costNode = new TextNode
                    {
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
