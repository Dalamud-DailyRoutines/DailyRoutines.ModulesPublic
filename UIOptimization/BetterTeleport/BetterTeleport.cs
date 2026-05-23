using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Common.Runtime.Hosts;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using DailyRoutines.Verification;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.BetterTeleport;

public unsafe partial class BetterTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("BetterTeleportTitle"),
        Description         = Lang.Get("BetterTeleportDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["SameAethernetTeleport"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static uint TicketUsageType
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("TelepoTicketUseType");
        set => DService.Instance().GameConfig.UiConfig.Set("TelepoTicketUseType", value);
    }

    private static uint TicketUsageGilSetting
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("TelepoTicketGilSetting");
        set => DService.Instance().GameConfig.UiConfig.Set("TelepoTicketGilSetting", value);
    }

    private IEnumerable<AetheryteRecord> AllRecords =>
        records.Values.SelectMany(x => x).Concat(houseRecords);

    private Config config = null!;

    // Icon ID - Record
    private readonly Dictionary<string, List<AetheryteRecord>> records      = [];
    private readonly List<AetheryteRecord>                     houseRecords = [];

    private bool isRefreshing;
    private bool isMoving;

    protected override void Init()
    {
        Overlay ??= new(this);
        Overlay.Flags |= ImGuiWindowFlags.AlwaysAutoResize |
                         ImGuiWindowFlags.NoTitleBar       |
                         ImGuiWindowFlags.NoResize         |
                         ImGuiWindowFlags.NoScrollbar      |
                         ImGuiWindowFlags.NoSavedSettings;
        Overlay.WindowName = "###BetterTeleportOverlay";

        Overlay.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400f * GlobalUIScale, -1),
            MaximumSize = new Vector2(400f * GlobalUIScale, -1)
        };

        fullWindow       =  new BetterTeleportFullWindow(this);
        fullWindow.Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ManagerHost.Current.AddWindow(fullWindow);

        TaskHelper ??= new() { TimeoutMS = 60_000 };

        config = Config.Load(this) ?? new();
        MigrateConfig();

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
        GameState.Instance().Login += OnLogin;

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterTeleport-CommandHelp") });

        UseActionManager.Instance().RegPreUseAction(OnPostUseAction);

        InputIDManager.Instance().RegPrePressed(OnPreInputIDPressed);
    }

    protected override void Uninit()
    {
        InputIDManager.Instance().UnregPrePressed(OnPreInputIDPressed);

        UseActionManager.Instance().Unreg(OnPostUseAction);
        CommandManager.Instance().RemoveCommand(COMMAND);

        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        GameState.Instance().Login                       -= OnLogin;

        if (fullWindow != null)
        {
            ManagerHost.Current.RemoveWindow(fullWindow);
            fullWindow = null;
        }

        recordMatcher?.Dispose();
        recordMatcher = null;
    }
    
    private void ToggleDefaultPage()
    {
        if (Overlay.IsOpen || fullWindow.IsOpen)
        {
            Overlay.IsOpen    = false;
            fullWindow.IsOpen = false;
        }
        else
        {
            if (config.DefaultPage == PageType.Search)
                Overlay.IsOpen = true;
            else
                fullWindow.IsOpen = true;
        }
    }

    private void HandleTeleport(AetheryteRecord aetheryte)
    {
        if (GameState.ContentFinderCondition != 0) return;

        TaskHelper.Abort();
        Overlay.IsOpen    = false;
        fullWindow.IsOpen = false;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var hasRedirect  = config.Positions.TryGetValue(GetConfigKey(aetheryte), out var redirected);
        var aetherytePos = hasRedirect ? redirected : aetheryte.Position;

        var isSameZone = aetheryte.ZoneID == GameState.TerritoryType;
        var distance2D = !isSameZone
                             ? 999
                             : Vector2.DistanceSquared(localPlayer->Position.ToVector2(), aetherytePos.ToVector2());
        if (distance2D <= 900) return;

        var isPosDefault = aetherytePos.Y == 0;

        NotifyHelper.Instance().NotificationInfo(Lang.Get("BetterTeleport-Notification", aetheryte.Name));

        searchWord       = string.Empty;
        pinnedAetheryte  = null;
        hoveredAetheryte = null;
        Overlay.IsOpen   = false;

        AddToRecentTeleports(aetheryte);

        switch (aetheryte.Group)
        {
            // 房区
            case 255:
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);
                return;
            // 天穹街
            case 254:
                TaskHelper.Enqueue(MovementManager.Instance().TeleportFirmament, "天穹街");
                TaskHelper.Enqueue
                (
                    () => GameState.TerritoryType  == 886  &&
                          Control.GetLocalPlayer() != null &&
                          !MovementManager.Instance().IsManagerBusy,
                    "等待天穹街"
                );
                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos), "区域内TP");
                TaskHelper.Enqueue
                (
                    () =>
                    {
                        if (MovementManager.Instance().IsManagerBusy || DService.Instance().ObjectTable.LocalPlayer == null)
                            return false;

                        MovementManager.Instance().TPGround();
                        return true;
                    },
                    "TP到地面"
                );
                return;
            // 野外大水晶直接传
            case 0:
                var direction = !isPosDefault
                                    ? new()
                                    : Vector2.Normalize(((Vector3)localPlayer->Position).ToVector2() - aetherytePos.ToVector2());
                var offset = direction * 10;

                TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos + offset.ToVector3(0)));

                if (isPosDefault)
                {
                    TaskHelper.Enqueue
                    (() =>
                        {
                            if (MovementManager.Instance().IsManagerBusy     ||
                                DService.Instance().Condition.IsBetweenAreas ||
                                !UIModule.IsScreenReady()                    ||
                                DService.Instance().Condition.Any(ConditionFlag.Mounted))
                                return false;
                            MovementManager.Instance().TPGround();
                            return true;
                        }
                    );
                }

                return;
        }

        // 当前在有小水晶的城区
        if (GameState.TerritoryType == aetheryte.ZoneID && aetheryte.Group != 0)
        {
            // 大水晶才要偏移一下
            var offset = new Vector3();

            if (aetheryte.IsAetheryte)
            {
                var direction = !isPosDefault
                                    ? new()
                                    : Vector3.Normalize((Vector3)localPlayer->Position - aetherytePos);
                offset = direction * 10;
            }

            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos + offset));

            if (isPosDefault)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (MovementManager.Instance().IsManagerBusy     ||
                            DService.Instance().Condition.IsBetweenAreas ||
                            !UIModule.IsScreenReady()                    ||
                            DService.Instance().Condition.Any(ConditionFlag.Mounted))
                            return false;
                        MovementManager.Instance().TPGround();
                        return true;
                    }
                );
            }

            return;
        }

        // 先获取当前区域任一水晶
        var aetheryteInThisZone = MovementManager.GetNearestAetheryte(Control.GetLocalPlayer()->Position, GameState.TerritoryType);

        // 获取不到水晶 / 不属于同一组水晶 / 附近没有能交互到的水晶 → 直接传
        if ((!isSameZone && aetheryte.Group == 0)        ||
            aetheryteInThisZone       == null            ||
            aetheryteInThisZone.Group != aetheryte.Group ||
            !EventFramework.Instance()->TryGetNearestEventID
            (
                x => x.EventId.ContentId is EventHandlerContent.Aetheryte,
                _ => true,
                DService.Instance().ObjectTable.LocalPlayer.Position,
                out var eventIDAetheryte
            ))
        {
            // 大水晶直接传
            if (aetheryte.IsAetheryte)
            {
                Telepo.Instance()->Teleport(aetheryte.RowID, aetheryte.SubIndex);

                if (hasRedirect)
                {
                    TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
                    TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos));
                }

                return;
            }

            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_BetweenZone(aetheryte.ZoneID, aetherytePos));

            if (isPosDefault)
            {
                TaskHelper.Enqueue
                (() =>
                    {
                        if (MovementManager.Instance().IsManagerBusy     ||
                            DService.Instance().Condition.IsBetweenAreas ||
                            !UIModule.IsScreenReady()                    ||
                            DService.Instance().Condition.Any(ConditionFlag.Mounted))
                            return false;
                        MovementManager.Instance().TPGround();
                        return true;
                    }
                );
            }

            return;
        }

        TaskHelper.Enqueue(() => !DService.Instance().Condition.IsOccupiedInEvent);
        if (!TelepotTown->IsAddonAndNodesReady())
            TaskHelper.Enqueue(() => new EventStartPackt(Control.GetLocalPlayer()->EntityId, eventIDAetheryte).Send());
        TaskHelper.Enqueue
        (() =>
            {
                AddonSelectStringEvent.Select(["都市传送网", "Aethernet", "都市転送網"]);

                var agent = AgentTelepotTown.Instance();
                if (agent == null || !agent->IsAgentActive()) return false;

                AgentId.TelepotTown.SendEvent(1, 11, (uint)aetheryte.SubIndex);
                AgentId.TelepotTown.SendEvent(1, 11, (uint)aetheryte.SubIndex);
                return true;
            }
        );

        if (hasRedirect)
        {
            TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && Control.GetLocalPlayer() != null);
            TaskHelper.Enqueue(() => MovementManager.Instance().TPSmart_InZone(aetherytePos));
        }
    }

    private void AddToRecentTeleports(AetheryteRecord aetheryte)
    {
        config.RecentTeleports.RemoveAll(x => x.AetheryteID == aetheryte.RowID && x.SubIndex == aetheryte.SubIndex);
        config.RecentTeleports.Insert(0, new RecentRecord { AetheryteID = aetheryte.RowID, SubIndex = aetheryte.SubIndex });
        if (config.RecentTeleports.Count > 20)
            config.RecentTeleports.RemoveRange(20, config.RecentTeleports.Count - 20);
        config.Save(this);
        RefreshDefaultOverlayItems();
    }

    private static bool IsWithPermission() =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium || Sheets.SpeedDetectionZones.ContainsKey(GameState.TerritoryType);

    #region 配置

    private static string GetConfigKey(AetheryteRecord record) => $"{record.RowID}_{record.SubIndex}";

    private static string GetConfigKey(uint rowID, byte subIndex) => $"{rowID}_{subIndex}";

    private void MigrateConfig()
    {
        var migrated = false;

        // 迁移 Remarks
        var oldRemarksKeys = config.Remarks.Keys.Where(k => !k.Contains('_')).ToList();

        if (oldRemarksKeys.Count > 0)
        {
            foreach (var oldKey in oldRemarksKeys)
            {
                if (uint.TryParse(oldKey, out var rowID))
                {
                    var val    = config.Remarks[oldKey];
                    var newKey = GetConfigKey(rowID, 0);
                    config.Remarks[newKey] = val;
                }

                config.Remarks.Remove(oldKey);
            }

            migrated = true;
        }

        // 迁移 Positions
        var oldPositionsKeys = config.Positions.Keys.Where(k => !k.Contains('_')).ToList();

        if (oldPositionsKeys.Count > 0)
        {
            foreach (var oldKey in oldPositionsKeys)
            {
                if (uint.TryParse(oldKey, out var rowID))
                {
                    var val    = config.Positions[oldKey];
                    var newKey = GetConfigKey(rowID, 0);
                    config.Positions[newKey] = val;
                }

                config.Positions.Remove(oldKey);
            }

            migrated = true;
        }

        if (migrated)
            config.Save(this);
    }

    public class RecentRecord
    {
        public uint AetheryteID { get; set; }
        public byte SubIndex    { get; set; }
    }

    private enum PageType
    {
        Search,
        Full
    }

    private class Config : ModuleConfig
    {
        public PageType                    DefaultPage          = PageType.Search;
        public bool                        FocusSearchOnOpen    = true;
        public bool                        CloseOnLoseFocus     = true;
        public HashSet<uint>               Favorites            = [];
        public bool                        HideAethernetInParty = true;
        public Dictionary<string, Vector3> Positions            = [];
        public Dictionary<string, string>  Remarks              = [];
        public List<RecentRecord>          RecentTeleports      = [];
    }

    #endregion

    #region 常量

    private const string COMMAND = "/pdrtelepo";

    private const ulong INVALID_HOUSE_ID = 0xFFFFFFFFFFFFFFFF;


    private static Dictionary<uint, string> TicketUsageTypes
    {
        get
        {
            if (field != null)
                return field;

            field = [];

            for (var i = 0U; i < 5; i++)
            {
                var addonOffset       = i + 8523U;
                var optionDescription = LuminaWrapper.GetAddonText(addonOffset);
                field[i] = optionDescription;
            }

            return field;
        }
    }

    #endregion
}
