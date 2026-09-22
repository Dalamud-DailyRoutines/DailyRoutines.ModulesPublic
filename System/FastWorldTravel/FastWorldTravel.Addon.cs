using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Excel.Sheets;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic;

public partial class FastWorldTravel
{
    private class AddonDRFastWorldTravel
    (
        TaskHelper taskHelper
    ) : NativeAddon
    {
        public static AddonDRFastWorldTravel? Addon { get; set; }

        private static readonly Version MinDCTravelerXVersion = new("0.2.3.0");

        private static NodeBase TeleportWidget;

        private static bool LastOpenPluginState;

        private static bool LastForegroundState;

        private static Dictionary<uint, TextButtonNode> WorldToButtons = [];

        private static bool IsPluginEnabled =>
            IDalamudPluginInterface.Instance().IsPluginEnabled("DCTravelerX", MinDCTravelerXVersion);

        private static bool IsPluginValid =>
            IsPluginEnabled && IsDCTravelerValid;

        public static void Open
        (
            FastWorldTravel module
        )
        {
            EnsureAddon(module);
            Addon.Open();
        }

        public static void Toggle
        (
            FastWorldTravel module
        )
        {
            EnsureAddon(module);
            Addon.Toggle();
        }

        private static void EnsureAddon
        (
            FastWorldTravel module
        ) =>
            Addon ??= new(module.TaskHelper)
            {
                InternalName = "DRFastWorldTravel",
                Title = GameState.IsCN ?
                            $"Daily Routines {module.Info.Title}" :
                            LuminaWrapper.GetAddonText(12510),
                Size = new
                (
                    GameState.IsCN ?
                        710f :
                        180f,
                    480f
                )
            };

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            LastOpenPluginState = IsPluginValid;
            WorldToButtons.Clear();

            TeleportWidget          = CreateTeleportWidget();
            TeleportWidget.Position = ContentStartPosition;

            if (GameState.IsCN)
            {
                var message = SeString.Empty;

                if (!IsPluginEnabled)
                {
                    message = new SeStringBuilder().Append("超域旅行功能依赖 ")
                                                   .AddUiForeground("DCTravlerX", 32)
                                                   .Append($" 插件 (版本 {MinDCTravelerXVersion} 及以上)")
                                                   .Build();
                }
                else if (!IsDCTravelerValid)
                {
                    message = new SeStringBuilder().Append("无法连接至超域旅行 API, 请确认已安装并启用 ")
                                                   .AddUiForeground("DCTravlerX", 32)
                                                   .Append($" 插件 (版本 {MinDCTravelerXVersion} 及以上), 若已启用, 请从 XIVLauncherCN 重启游戏")
                                                   .Build();
                }

                if (message != SeString.Empty)
                {
                    var pluginHelpNode = new TextNode
                    {
                        String           = message.Encode(),
                        FontSize         = 14,
                        IsVisible        = true,
                        Size             = new(150f, 25f),
                        AlignmentType    = AlignmentType.Center,
                        Position         = new(305f, -22f),
                        TextFlags        = TextFlags.Bold | TextFlags.Edge,
                        TextColor        = ColorHelper.GetColor(50),
                        TextOutlineColor = ColorHelper.GetColor(32)
                    };
                    pluginHelpNode.AttachNode(this);
                }
            }

            TeleportWidget.AttachNode(this);

            UpdateWaitTimeInfo();
        }

        protected override unsafe void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (ICondition.Instance().IsBoundByDuty)
            {
                Close();
                return;
            }

            if (!GameState.IsCN) return;

            if (Throttler.Shared.Throttle("FastWorldTravel-OnAddonUpdate") && LastOpenPluginState != IsPluginValid)
            {
                Close();

                taskHelper.Abort();
                taskHelper.DelayNext(100);
                taskHelper.Enqueue(() => !IsOpen, "等待界面完全关闭");
                taskHelper.Enqueue(Open,          "重新打开");

                LastOpenPluginState = IsPluginValid;
                return;
            }

            if (LastForegroundState != GameState.IsForeground)
            {
                LastForegroundState = GameState.IsForeground;

                Throttler.Shared.Remove("FastWorldTravel-OnAddonUpdate-RequestQueueTime");
                Throttler.Shared.Remove("FastWorldTravel-OnAddonUpdate-UpdateQueueTime");
            }

            // 都在后台了就不要 DDOS 拂晓服务器了
            if (Throttler.Shared.Throttle
                (
                    "FastWorldTravel-OnAddonUpdate-RequestQueueTime",
                    GameState.IsForeground ?
                        15_000U :
                        60_000
                ))
                RequestWaitTimeInfoUpdate();

            if (Throttler.Shared.Throttle("FastWorldTravel-OnAddonUpdate-UpdateQueueTime", 1_000))
                UpdateWaitTimeInfo();
        }

        private void RequestWaitTimeInfoUpdate() =>
            IFramework.Instance().RunOnTick
            (async () =>
                {
                    if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;
                    await RequestDCTravelInfo.InvokeFunc();
                }
            );

        private void UpdateWaitTimeInfo()
        {
            if (!IsOpen || !IsPluginValid || WorldToButtons is not { Count: > 0 }) return;

            foreach (var (worldID, node) in WorldToButtons)
            {
                var time = GetDCTravelWaitTime.InvokeFunc(worldID);
                if (time == -1) continue;

                var builder = new SeStringBuilder();
                builder.AddText("超域传送状态:")
                       .Add(NewLinePayload.Payload)
                       .AddText("              ");

                switch (time)
                {
                    case 0:
                        builder.AddUiForeground("即刻完成 / 等待 1 分钟以内", 45);
                        break;
                    case -999:
                        builder.AddUiForeground("繁忙 / 无法通行", 518);
                        break;
                    default:
                        builder.AddText("至少需要等待 ")
                               .AddUiForeground(time.ToString(), 32)
                               .AddText(" 分钟");
                        break;
                }


                node.TextTooltip = builder.Build().Encode();
                var baseColor = time switch
                {
                    0    => KnownColor.DarkGreen.ToVector4().ToVector3(),
                    -999 => KnownColor.DarkRed.ToVector4().ToVector3(),
                    >= 5 => KnownColor.Brown.ToVector4().ToVector3(),
                    _    => ColorHelper.GetColor(32).ToVector3()
                };

                node.AddColor = baseColor;
            }
        }

        private HorizontalListNode CreateTeleportWidget()
        {
            var mainLayoutContainer = new HorizontalListNode { IsVisible = true };

            // 当前大区
            var currentDCWorlds = Sheets.Worlds
                                        .Where(x => x.Value.DataCenter.RowId == GameState.CurrentDataCenter)
                                        .OrderBy(x => x.Value.Name.ToString())
                                        .ToList();
            if (currentDCWorlds is not { Count: > 0 }) return mainLayoutContainer;

            var maxWorldCount = currentDCWorlds.Count;

            var currentDCColumn = CreateDataCenterColumn(currentDCWorlds.First().Value.DataCenter.RowId, currentDCWorlds, ref maxWorldCount);
            mainLayoutContainer.AddNode(currentDCColumn);

            if (!GameState.IsCN)
                return mainLayoutContainer;

            // 其他大区 (仅国服)
            var otherDataCenters = Sheets.CNWorlds
                                         .Where(kvp => kvp.Value.DataCenter.RowId != GameState.CurrentDataCenter)
                                         .OrderBy(x => x.Value.Name.ToString())
                                         .GroupBy(x => x.Value.DataCenter.RowId)
                                         .ToDictionary(x => x.Key, x => x.ToList());

            foreach (var dataCenter in otherDataCenters)
                maxWorldCount = Math.Max(maxWorldCount, dataCenter.Value.Count);

            foreach (var dataCenter in otherDataCenters)
            {
                mainLayoutContainer.AddDummy(25);

                var otherDCColumn = CreateDataCenterColumn(dataCenter.Key, dataCenter.Value, ref maxWorldCount);
                mainLayoutContainer.AddNode(otherDCColumn);
            }

            return mainLayoutContainer;
        }

        private unsafe SimpleComponentNode CreateDataCenterColumn
        (
            uint                            dcID,
            List<KeyValuePair<uint, World>> worlds,
            ref int                         maxWorldCount
        )
        {
            const float COLUMN_WIDTH                = 150f;
            const float HEADER_HEIGHT               = 30f;
            const float SEPARATOR_WIDTH             = 118f;
            const float SEPARATOR_HEIGHT            = 4f;
            const float TITLE_BOTTOM_SPACING        = 8f;
            const float BUTTON_WIDTH                = 140f;
            const float BUTTON_HEIGHT               = 40f;
            const float BUTTON_SPACING              = 5f;
            const float BUTTON_PANEL_TOP_PADDING    = 16f;
            const float BUTTON_PANEL_BOTTOM_PADDING = 16f;

            var dcName            = LuminaWrapper.GetDataCenterName(dcID);
            var buttonGroupHeight = (worlds.Count * BUTTON_HEIGHT) + (Math.Max(0, worlds.Count - 1) * BUTTON_SPACING);
            var buttonPanelHeight = (maxWorldCount                  * BUTTON_HEIGHT)  +
                                    (Math.Max(0, maxWorldCount - 1) * BUTTON_SPACING) +
                                    BUTTON_PANEL_TOP_PADDING                          +
                                    BUTTON_PANEL_BOTTOM_PADDING;
            var totalHeight = HEADER_HEIGHT + SEPARATOR_HEIGHT + TITLE_BOTTOM_SPACING + buttonPanelHeight;

            var column = new SimpleComponentNode
            {
                IsVisible = true,
                Size      = new(COLUMN_WIDTH, totalHeight)
            };

            var header = new TextNode
            {
                String        = dcName,
                FontSize      = 20,
                IsVisible     = true,
                Position      = new(0, 8),
                Size          = new(COLUMN_WIDTH, HEADER_HEIGHT),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Bold
            };

            if (dcID != GameState.CurrentDataCenter)
                header.ShowClickableCursor = true;

            header.AttachNode(column);

            var separator = new HorizontalLineNode
            {
                IsVisible = true,
                Size      = new(SEPARATOR_WIDTH, SEPARATOR_HEIGHT),
                Position  = new((COLUMN_WIDTH - SEPARATOR_WIDTH) / 2f, header.Position.Y + HEADER_HEIGHT + 4f)
            };
            separator.AttachNode(column);

            var buttonPanel = new SimpleComponentNode
            {
                IsVisible = true,
                Size      = new(COLUMN_WIDTH, buttonPanelHeight),
                Position  = new(0f, HEADER_HEIGHT + SEPARATOR_HEIGHT + TITLE_BOTTOM_SPACING)
            };
            buttonPanel.AttachNode(column);

            var buttonList = new VerticalListNode
            {
                IsVisible   = true,
                Size        = new(BUTTON_WIDTH, buttonGroupHeight),
                Position    = new((COLUMN_WIDTH - BUTTON_WIDTH) / 2f, (buttonPanelHeight - buttonGroupHeight) / 2f),
                ItemSpacing = BUTTON_SPACING
            };
            buttonList.AttachNode(buttonPanel);

            foreach (var (worldID, worldData) in worlds)
            {
                var worldNameBuilder = new SeStringBuilder().Append(worldData.Name.ToString());

                if (GameState.HomeWorld == worldID)
                {
                    worldNameBuilder.Append(" ");
                    worldNameBuilder.AddIcon(BitmapFontIcon.CrossWorld);
                }

                var button = new TextButtonNode
                {
                    Size      = new(BUTTON_WIDTH, BUTTON_HEIGHT),
                    IsVisible = true,
                    String    = worldNameBuilder.Build().Encode(),
                    OnClick = () =>
                    {
                        Close();
                        ChatManager.Instance().SendMessage($"/pdr worldtravel {worldData.Name.ToString()}");
                    },
                    IsEnabled = GameState.CurrentWorld != worldID && (worldData.DataCenter.RowId == GameState.CurrentDataCenter || IsPluginValid)
                };

                button.LabelNode.TextOutlineColor = KnownColor.Black.ToVector4();
                button.LabelNode.TextFlags        = TextFlags.Edge;
                buttonList.AddNode(button);

                if (GameState.IsCN)
                    WorldToButtons.Add(worldID, button);
            }

            return column;
        }
    }
}
