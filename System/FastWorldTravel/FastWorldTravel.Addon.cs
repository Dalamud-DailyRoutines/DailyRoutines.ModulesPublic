using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Enums;
using OmenTools.Info.Lumina;
using OmenTools.Info.Lumina.ExtraSheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public partial class FastWorldTravel
{
    private AddonDRFastWorldTravel? travelAddon;

    private static bool IsAbleToCrossDC() =>
        GameState.IsCN ?
            IDalamudPluginInterface.Instance().IsPluginEnabled("DCTravelerX") && IsDCTravelerValid :
            GameState.CurrentWorldData.PhysicalDataCenter.RowId == 1;

    private void OpenTravelAddon
    (
        bool toggle = false
    )
    {
        if (travelAddon is { IsOpen: true })
        {
            if (toggle)
                travelAddon.Close();

            return;
        }

        travelAddon?.Dispose();
        travelAddon = new(this)
        {
            InternalName = "DRFastWorldTravel",
            Title        = LuminaWrapper.GetAddonText(12510)
        };
        travelAddon.Open();
    }

    private class AddonDRFastWorldTravel
    (
        FastWorldTravel module
    ) : NativeAddon
    {
        private readonly Dictionary<uint, TextButtonNode> worldToButtons      = [];
        private readonly Dictionary<uint, uint>           worldToLogMessageID = [];

        private HorizontalListNode teleportWidget;
        private bool               lastCrossDCState;
        private bool               lastForegroundState;
        private Task               dcTravelInfoRequest = Task.CompletedTask;
        private long               nextStateUpdate;
        private long               nextWorldListRequest;
        private long               nextDCTravelInfoRequest;

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            lastCrossDCState    = IsAbleToCrossDC();
            lastForegroundState = GameState.IsForeground;

            teleportWidget = new()
            {
                Position           = ContentStartPosition,
                FitToContentHeight = true,
                FitToContentWidth  = true,
                ItemSpacing        = 25f
            };
            teleportWidget.AttachNode(this);
            RebuildWorldList();

            RequestWaitTimeInfo();
            nextWorldListRequest = Environment.TickCount64 + 5_000;
            UpdateWorldStates();
        }

        protected override unsafe void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (!IsOpen)
                return;

            var now = Environment.TickCount64;
            if (now < nextStateUpdate)
                return;

            nextStateUpdate = now + 1_000;

            if (ICondition.Instance().IsBoundByDuty)
            {
                Close();
                return;
            }

            var canCrossDC = IsAbleToCrossDC();

            if (lastCrossDCState != canCrossDC)
            {
                lastCrossDCState = canCrossDC;
                RebuildWorldList();
                nextDCTravelInfoRequest = 0;
            }

            if (now >= nextWorldListRequest)
            {
                RequestWaitTimeInfo();
                nextWorldListRequest = now + 5_000;
            }

            if (GameState.IsCN && canCrossDC)
            {
                var isForeground = GameState.IsForeground;

                if (lastForegroundState != isForeground)
                {
                    lastForegroundState     = isForeground;
                    nextDCTravelInfoRequest = 0;
                }

                if (dcTravelInfoRequest.IsCompleted && now >= nextDCTravelInfoRequest)
                {
                    nextDCTravelInfoRequest = now +
                                              (isForeground ?
                                                   15_000 :
                                                   60_000);
                    dcTravelInfoRequest = RequestWaitTimeInfoUpdateCN();
                }
            }

            UpdateWorldStates();
        }

        protected override unsafe void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            worldToButtons.Clear();
            worldToLogMessageID.Clear();
        }

        private void RebuildWorldList()
        {
            worldToButtons.Clear();
            teleportWidget.Clear();

            var currentDC  = GameState.CurrentDataCenter;
            var physicalDC = GameState.CurrentWorldData.PhysicalDataCenter.RowId;
            var worlds = GameState.IsCN ?
                             Sheets.CNWorlds :
                             Sheets.Worlds;
            var dataCenters = worlds.Where
                                    (x => x.Value.DataCenter.RowId == currentDC ||
                                          (lastCrossDCState && (GameState.IsCN || x.Value.PhysicalDataCenter.RowId == physicalDC))
                                    )
                                    .OrderBy(x => x.Value.Name.ToString())
                                    .GroupBy(x => x.Value.DataCenter.RowId)
                                    .OrderBy(x => x.Key != currentDC);

            foreach (var dataCenter in dataCenters)
                teleportWidget.AddNode(CreateDataCenterColumn(dataCenter.Key, dataCenter));

            SetWindowSize(ContentStartPosition + teleportWidget.Size + new Vector2(16f));
        }

        private CenteredVerticalListNode CreateDataCenterColumn
        (
            uint                                       dcID,
            IEnumerable<KeyValuePair<uint, WorldTemp>> worlds
        )
        {
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
                Size = new(COLUMN_WIDTH * 0.75f, 4f)
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
                    Size    = new(column.Width, 40f),
                    String  = rented.ToReadOnlySeString(),
                    OnClick = () => OnWorldClick(worldID)
                };

                button.LabelNode.TextOutlineColor = KnownColor.Black.ToVector4();
                button.LabelNode.TextFlags        = TextFlags.Edge;
                column.AddNode(button);
                worldToButtons.Add(worldID, button);
            }

            return column;
        }

        private void OnWorldClick
        (
            uint worldID
        )
        {
            UpdateWorldStates();
            var error = GetWorldTravelError(worldID);

            if (!error.IsEmpty)
            {
                NotifyHelper.ToastError(error);
                return;
            }

            if (module.TravelToWorld(worldID))
                Close();
        }

        private static unsafe void RequestWaitTimeInfo()
        {
            AgentWorldTravel.Instance()->WorldListTimer = 0;
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestWorldTravel);
        }

        private static async Task RequestWaitTimeInfoUpdateCN()
        {
            try
            {
                await RequestDCTravelInfo.InvokeFunc();
            }
            catch (Exception exception)
            {
                DLog.Error("获取超域旅行状态失败", exception);
            }
        }

        private unsafe void UpdateWorldStates()
        {
            worldToLogMessageID.Clear();
            foreach (var world in AgentWorldTravel.Instance()->AllWorldsSpan)
                worldToLogMessageID.TryAdd(world.WorldID, world.LogMessageID);

            foreach (var (worldID, node) in worldToButtons)
            {
                var error       = GetWorldTravelError(worldID);
                var isAvailable = error.IsEmpty;

                node.TextTooltip = error;
                node.AddColor    = Vector3.Zero;
                node.MultiplyColor = new
                (
                    isAvailable ?
                        1f :
                        0.5f
                );
                node.Alpha = isAvailable ?
                                 1f :
                                 0.6f;
            }
        }

        private ReadOnlySeString GetWorldTravelError
        (
            uint worldID
        )
        {
            if (worldID == GameState.CurrentWorld)
                return ISeStringEvaluator.Instance().EvaluateFromLogMessage(9407);

            var isCrossDC = LuminaWrapper.GetWorldDC(worldID) != GameState.CurrentDataCenter;
            if (isCrossDC && !IsAbleToCrossDC())
                return Lang.Get("FastWorldTravel-Notification-CrossDCTravel");

            if (worldToLogMessageID.TryGetValue(worldID, out var logMessageID) && logMessageID != 0)
                return ISeStringEvaluator.Instance().EvaluateFromLogMessage(logMessageID);

            if (GameState.IsCN && isCrossDC && GetDCTravelWaitTime.InvokeFunc(worldID) != 0)
                return ISeStringEvaluator.Instance().EvaluateFromLogMessage(9066);

            return default;
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

        #region 常量

        private const float COLUMN_WIDTH = 150f;

        #endregion
    }
}
