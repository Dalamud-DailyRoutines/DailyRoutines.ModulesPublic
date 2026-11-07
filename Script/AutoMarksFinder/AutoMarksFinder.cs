using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Network;
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

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        NeedAuth = true
    };

    private NotoriousMonsterList? SelectedTrain => selection.Train;

    private Config config = null!;

    private HuntRelay         relay         = null!;
    private HuntAutoBroadcast autoBroadcast = null!;
    private HuntMovement      movement      = null!;
    private MonsterScanner?   scanner;

    private readonly HuntSelection selection           = new();
    private readonly HuntSelection scanResultSelection = new();

    private MonsterAliveMonitor? aliveMonitor;

    protected override unsafe void Init()
    {
        config                       = Config.Load(this) ?? new();
        spawnPointUpdateCancellation = new();
        movement                     = new(config);
        relay                        = new(config, movement);
        autoBroadcast                = new(this);
        selection.Select(config.Trains.FirstOrDefault());

        Overlay = new(this, Lang.Get("AutoMarksFinder-SmallCardTitle"))
        {
            Flags              = ImGuiWindowFlags.NoScrollbar,
            RespectCloseHotkey = false
        };

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get("AutoMarksFinder-HelpMessage")
            }
        );

        aliveMonitor      =  new(this);
        selection.Changed += aliveMonitor.RefreshTracking;

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

        scanMapRenderer.OnCustomMapDraw        =  DrawScanMapCoverage;
        scanMapRenderer.OnCustomForegroundDraw =  DrawScanMapForeground;
        WindowManager.Instance().PostDraw      += DrawScanRoutePreview;
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        WindowManager.Instance().PostDraw -= DrawScanRoutePreview;

        spawnPointUpdateCancellation?.Cancel();
        spawnPointUpdateCancellation?.Dispose();
        spawnPointUpdateCancellation = null;

        scanZoneCombo = null;

        autoBroadcast.Dispose();
        relay.Dispose();

        if (aliveMonitor != null)
        {
            selection.Changed -= aliveMonitor.RefreshTracking;
            aliveMonitor.Dispose();
        }

        aliveMonitor = null;

        config.Save(this);

        scanner?.Dispose();
        scanner = null;

        movement.Dispose();

        selection.Select(null);
        scanResultSelection.Select(null);
        startAetheryteCombo = null;
    }

    private void OnCommand
    (
        string command,
        string args
    ) =>
        Overlay.IsOpen ^= true;

    #region 常量

    private const string COMMAND = "mf";
    private const string SPAWN_POINTS_URL =
        "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoMarksFinder/SpawnPoints.json";

    private static readonly string[] SUPPORTED_RELAY_COMMANDS =
    [
        "/e",
        "/p",
        "/sh",
        "/y",
        "/b",
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
