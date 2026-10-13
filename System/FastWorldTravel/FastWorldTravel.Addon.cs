using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.Enums;
using OmenTools.Info.Lumina;
using OmenTools.Info.Lumina.ExtraSheets;
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
        
        private static HorizontalListNode TeleportWidget;

        private static bool LastOpenPluginState;

        private static bool LastForegroundState;

        private static Dictionary<uint, TextButtonNode> WorldToButtons = [];

        private static bool IsPluginEnabled =>
            IDalamudPluginInterface.Instance().IsPluginEnabled("DCTravelerX");

        public static bool IsAbleToCrossDC
        {
            get
            {
                if (GameState.IsCN)
                    return IsPluginEnabled && IsDCTravelerValid;
                // 日本
                if (GameState.CurrentWorldData.PhysicalDataCenter.RowId == 1)
                    return true;
                
                return false;
            }
        }

        public static void Open
        (
            FastWorldTravel module
        )
        {
            if (Addon is { IsOpen: true } )
                return;
            
            Addon?.Dispose();
            EnsureAddon(module);
            
            Addon.Open();
        }

        public static void Toggle
        (
            FastWorldTravel module
        )
        {
            if (Addon != null)
            {
                var isOpen = Addon.IsOpen;
                
                Addon.Dispose();
                Addon = null;
                
                if (isOpen)
                    return;
            }
            
            EnsureAddon(module);
            Addon.Open();
        }

        private static void EnsureAddon
        (
            FastWorldTravel module
        ) =>
            Addon = new(module.TaskHelper)
            {
                InternalName = "DRFastWorldTravel",
                Title        = LuminaWrapper.GetAddonText(12510)
            };

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            LastOpenPluginState = IsAbleToCrossDC;
            WorldToButtons.Clear();

            TeleportWidget          = CreateTeleportWidget();
            TeleportWidget.Position = ContentStartPosition;

            TeleportWidget.AttachNode(this);
            
            SetWindowSize(ContentStartPosition + TeleportWidget.Size + new Vector2(16f));

            RequestWaitTimeInfo();
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

            if (Throttler.Shared.Throttle("FastWorldTravel-OnAddonUpdate") && LastOpenPluginState != IsAbleToCrossDC)
            {
                Close();

                taskHelper.Abort();
                taskHelper.DelayNext(100);
                taskHelper.Enqueue(() => !IsOpen, "等待界面完全关闭");
                taskHelper.Enqueue(Open,          "重新打开");

                LastOpenPluginState = IsAbleToCrossDC;
                return;
            }

            if (LastForegroundState != GameState.IsForeground)
            {
                LastForegroundState = GameState.IsForeground;

                Throttler.Shared.Remove("FastWorldTravel-OnAddonUpdate-RequestQueueTime");
                Throttler.Shared.Remove("FastWorldTravel-OnAddonUpdate-UpdateQueueTime");
            }

            if (Throttler.Shared.Throttle("FastWorldTravel.OnAddonUpdate.UpdateWaitInfo", 5_000))
                RequestWaitTimeInfo();
            
            // 都在后台了就不要 DDOS 拂晓服务器了
            if (Throttler.Shared.Throttle
                (
                    "FastWorldTravel.OnAddonUpdate.RequestQueueTime",
                    GameState.IsForeground ?
                        15_000U :
                        60_000
                ))
                RequestWaitTimeInfoUpdateCN();

            if (Throttler.Shared.Throttle("FastWorldTravel.OnAddonUpdate.UpdateQueueTime", 1_000))
                UpdateWaitTimeInfo();
        }

        private HorizontalListNode CreateTeleportWidget()
        {
            var mainLayoutContainer = new HorizontalListNode
            {
                FitToContentHeight = true,
                FitToContentWidth  = true,
                ItemSpacing        = 25f,
            };
            
            // 当前大区
            var currentDCWorlds = Sheets.Worlds
                                        .Where(x => x.Value.DataCenter.RowId == GameState.CurrentDataCenter)
                                        .OrderBy(x => x.Value.Name.ToString())
                                        .ToList();
            if (currentDCWorlds is not { Count: > 0 }) 
                return mainLayoutContainer;
            
            var currentDCColumn = CreateDataCenterColumn(currentDCWorlds.First().Value.DataCenter.RowId, currentDCWorlds);
            mainLayoutContainer.AddNode(currentDCColumn);
            
            if (!IsAbleToCrossDC)
                return mainLayoutContainer;

            // 国服
            if (GameState.IsCN)
            {
                var otherDataCenters = Sheets.CNWorlds
                                             .Where(kvp => kvp.Value.DataCenter.RowId != GameState.CurrentDataCenter)
                                             .OrderBy(x => x.Value.Name.ToString())
                                             .GroupBy(x => x.Value.DataCenter.RowId)
                                             .ToDictionary(x => x.Key, x => x.ToList());
                foreach (var dataCenter in otherDataCenters)
                {
                    var otherDCColumn = CreateDataCenterColumn(dataCenter.Key, dataCenter.Value);
                    mainLayoutContainer.AddNode(otherDCColumn);
                }
            }
            else
            {
                var otherDataCenters = Sheets.Worlds
                                             .Where(x => x.Value.PhysicalDataCenter.RowId == GameState.CurrentWorldData.PhysicalDataCenter.RowId)
                                             .Where(kvp => kvp.Value.DataCenter.RowId != GameState.CurrentDataCenter)
                                             .OrderBy(x => x.Value.Name.ToString())
                                             .GroupBy(x => x.Value.DataCenter.RowId)
                                             .ToDictionary(x => x.Key, x => x.ToList());
                foreach (var dataCenter in otherDataCenters)
                {
                    var otherDCColumn = CreateDataCenterColumn(dataCenter.Key, dataCenter.Value);
                    mainLayoutContainer.AddNode(otherDCColumn);
                }
            }

            return mainLayoutContainer;
        }

        private unsafe CenteredVerticalListNode CreateDataCenterColumn
        (
            uint                                dcID,
            List<KeyValuePair<uint, WorldTemp>> worlds
        )
        {
            const float COLUMN_WIDTH     = 150f;
            const float SEPARATOR_WIDTH  = 0.75f * COLUMN_WIDTH;
            
            var column = new CenteredVerticalListNode
            {
                FitContents = true,
                Width       = COLUMN_WIDTH,
                ItemSpacing = 5f
            };
            
            var header = new TextNode
            {
                String        = LuminaWrapper.GetDataCenterName(dcID),
                FontSize      = 20,
                Size          = new(COLUMN_WIDTH, 32),
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Bold
            };
            column.AddNode(header);
            
            var separator = new HorizontalLineNode
            {
                Size = new(SEPARATOR_WIDTH, 4f),
            };
            column.AddNode(separator);
            
            column.AddDummy();

            using var rented = new RentedSeStringBuilder();
            
            foreach (var (worldID, worldData) in worlds)
            {
                rented.Clear();

                rented.Append(worldData.Name);
                
                if (GameState.HomeWorld == worldID)
                    rented.AppendIcon((uint)BitmapFontIcon.CrossWorld);

                var button = new TextButtonNode
                {
                    Size      = new(column.Width, 40f),
                    String    = rented.ToReadOnlySeString(),
                    OnClick = () =>
                    {
                        Close();
                        ChatManager.Instance().SendMessage($"/pdr worldtravel {worldData.Name.ToString()}");
                    },
                    IsEnabled = GameState.CurrentWorld != worldID && (worldData.DataCenter.RowId == GameState.CurrentDataCenter || IsAbleToCrossDC)
                };

                button.LabelNode.TextOutlineColor = KnownColor.Black.ToVector4();
                button.LabelNode.TextFlags        = TextFlags.Edge;
                column.AddNode(button);

                WorldToButtons.Add(worldID, button);
            }

            return column;
        }
        
        private static unsafe void RequestWaitTimeInfo()
        {
            AgentWorldTravel.Instance()->WorldListTimer = 0;
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestWorldTravel);
        }

        private void RequestWaitTimeInfoUpdateCN() =>
            IFramework.Instance().RunOnTick
            (async () =>
                {
                    if (!IsOpen || !IsAbleToCrossDC || WorldToButtons is not { Count: > 0 }) return;
                    await RequestDCTravelInfo.InvokeFunc();
                }
            );

        private unsafe void UpdateWaitTimeInfo()
        {
            if (!IsOpen ||
                WorldToButtons is not { Count: > 0 })
                return;
            
            using var rented = new RentedSeStringBuilder();

            var clientWorlds = AgentWorldTravel.Instance()->AllWorldsSpan.ToArray();

            foreach (var (worldID, node) in WorldToButtons)
            {
                var              isAbleToGoTo = true;
                ReadOnlySeString tooltip      = new();

                if (clientWorlds.FirstOrDefault(x => x.WorldID == worldID) is { WorldID: > 0 } clientWorldData)
                {
                    isAbleToGoTo = clientWorldData.LogMessageID == 0;
                    if (clientWorldData.LogMessageID > 0)
                        tooltip = LuminaWrapper.GetLogMessageText(clientWorldData.LogMessageID);
                }

                if (GameState.IsCN && isAbleToGoTo)
                {
                    var time = GetDCTravelWaitTime.InvokeFunc(worldID);
                    isAbleToGoTo = time == 0;
                }
                
                node.TextTooltip = tooltip;
                node.AddColor = isAbleToGoTo switch
                {
                    true  => KnownColor.DarkGreen.ToVector4().ToVector3(),
                    false => KnownColor.DarkRed.ToVector4().ToVector3(),
                };
            }
        }

        private class CenteredVerticalListNode : VerticalListNode
        {
            protected override void AdjustNode
            (
                NodeBase node
            )
            {
                if (node is HorizontalLineNode lineNode)
                    lineNode.X = (Width - lineNode.Width) / 2f;
            }
        }
    }
}
