using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Lumina;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMarksFinderTitle"),
        Description = Lang.Get("AutoMarksFinderDescription"),
        Category    = ModuleCategory.Script,
        ModulesPair =
        [
            "NoUIFade",
            "NoFallDamage",
            "InstantTeleport"
        ],
        ModulesPrerequisite = ["FastInstanceZoneChange"],
        Author              = ["AtmoOmen", "KirisameVanilla"]
    };

    public override ModuleBehaviour Behaviour { get; } = new() { NeedAuth = true };

    private Config                   config = null!;
    private MonsterAliveMonitor?     aliveMonitor;
    private int                      smallCardMobIndex;
    private int                      selectedTrainIndex;
    private string                   newListNameInput    = string.Empty;
    private string                   startAetheryteInput = string.Empty;
    private string                   renameTrainInput    = string.Empty;

    protected override unsafe void Init()
    {
        config                       =   Config.Load(this) ?? new();
        spawnPointUpdateCancellation =   new();
        TaskHelper                   ??= new() { TimeoutMS = 180_000, ShowDebug = IS_SHOW_DEBUG };
        relayTaskHelper              ??= new() { ShowDebug = IS_SHOW_DEBUG };

        Overlay = new(this, Lang.Get("AutoMarksFinder-SmallCardTitle"))
        {
            Flags              = ImGuiWindowFlags.NoScrollbar,
            RespectCloseHotkey = false
        };

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoMarksFinder-HelpMessage") });

        aliveMonitor = new(this);
        OnSpawnNPCHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(PacketDispatcher.MemberFunctionPointers),
            nameof(PacketDispatcher.HandleSpawnNpcPacket),
            (PacketDispatcher.Delegates.HandleSpawnNpcPacket)OnSpawnNPCDetour
        );
        OnSpawnNPCHook.Enable();
        OnActorControlHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(PacketDispatcher.MemberFunctionPointers),
            nameof(PacketDispatcher.HandleActorControlPacket),
            (PacketDispatcher.Delegates.HandleActorControlPacket)OnActorControlDetour
        );
        OnActorControlHook.Enable();
        FrameworkManager.Instance().Reg(UpdateAutoBroadcast, AUTO_BROADCAST_TICK_MS);
        scanMapRenderer.OnCustomMapDraw        =  DrawScanMapCoverage;
        scanMapRenderer.OnCustomForegroundDraw =  DrawScanMapForeground;
        WindowManager.Instance().PostDraw      += DrawScanRoutePreview;
    }

    protected override void Uninit()
    {
        WindowManager.Instance().PostDraw -= DrawScanRoutePreview;
        
        spawnPointUpdateCancellation?.Cancel();
        spawnPointUpdateCancellation?.Dispose();
        spawnPointUpdateCancellation = null;

        scanZoneCombo = null;

        FrameworkManager.Instance().Unreg(UpdateAutoBroadcast);
        StopAutoBroadcast();
        
        relayTaskHelper?.Dispose();
        relayTaskHelper = null;
        
        aliveMonitor?.Dispose();
        aliveMonitor = null;

        config.Save(this);

        MonsterScanner.ClearScanner();

        CommandManager.Instance().RemoveCommand(COMMAND);

        selectedTrainIndex            = 0;
        smallCardMobIndex             = 0;
        newListNameInput              = string.Empty;
        startAetheryteInput           = string.Empty;
        autoBroadcast.DepartTimeInput = string.Empty;
    }

    private void OnCommand
    (
        string command,
        string args
    ) =>
        Overlay.IsOpen ^= true;

    #region 工具

    private static bool IsAbleToTeleport() =>
        !LocalPlayerState.Instance().IsMoving             &&
        !ICondition.Instance().Any(ConditionFlag.Casting) &&
        !ICondition.Instance().IsBetweenAreas             &&
        UIModule.IsScreenReady();

    #endregion

    private class Config : ModuleConfig
    {
        public List<string> RelayCommands = [];

        public string AutoRelayBeforeStartPattern = string.Empty;
        public string AutoRelayAfterEndPattern    = string.Empty;

        public string RelayPositionPattern      = Lang.Get("AutoMarksFinder-Relay-Default-Position");
        public string RelayWaitAetherytePattern = Lang.Get("AutoMarksFinder-Relay-Default-WaitAetheryte");

        public Dictionary<uint, List<NotoriousMonsterScanPoint>> ScanPoints    = [];
        public HashSet<uint>                                     SelectedZones = [];
        public List<NotoriousMonsterList>                        Trains        = [];
    }

    #region 常量

#if DEBUG
    private const bool IS_SHOW_DEBUG = true;
#else
    private const bool IS_SHOW_DEBUG = false;
#endif

    private const string COMMAND = "/pdrmf";
    private const string SPAWN_POINTS_URL =
        "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoMarksFinder/SpawnPoints.json";

    private static readonly string[] SUPPORTED_RELAY_COMMANDS =
    [
        "/sh",
        "/y",
        "/b",
        "/p",
        "/fc",
        "/cwl1",
        "/cwl2",
        "/cwl3",
        "/cwl4",
        "/cwl5",
        "/cwl6",
        "/cwl7",
        "/cwl8",
        "/ls1",
        "/ls2",
        "/ls3",
        "/ls4",
        "/ls5",
        "/ls6",
        "/ls7",
        "/ls8"
    ];

    #endregion
}
