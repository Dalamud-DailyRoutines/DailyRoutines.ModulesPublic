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
    private CustomOverlay?           overlaySmallCard;
    private NotoriousMonsterScanner? scanner;
    private int                      smallCardMobIndex;
    private int                      selectedTrainIndex;
    private string                   newListNameInput    = string.Empty;
    private string                   startAetheryteInput = string.Empty;
    private string                   trainSearchInput    = string.Empty;
    private string                   renameTrainInput    = string.Empty;

    protected override unsafe void Init()
    {
        config                       =   Config.Load(this) ?? new();
        spawnPointUpdateCancellation =   new();
        TaskHelper                   ??= new() { TimeoutMS = 180_000, ShowDebug = IS_SHOW_DEBUG };
        relayTaskHelper              ??= new() { ShowDebug = IS_SHOW_DEBUG };

        overlaySmallCard       =  new(DrawSmallCard, Lang.Get("AutoMarksFinder-SmallCardTitle"));
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;

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
        scanZoneCombo                = null;
        scanResultTrainIndex         = -1;
        trainSearchInput             = string.Empty;
        renameTrainInput             = string.Empty;
        FrameworkManager.Instance().Unreg(UpdateAutoBroadcast);
        StopAutoBroadcast();
        relayTaskHelper?.Dispose();
        relayTaskHelper = null;
        aliveMonitor?.Dispose();
        aliveMonitor = null;

        config.Save(this);

        ClearScanner();

        if (overlaySmallCard != null)
        {
            WindowManager.Instance().RemoveWindow(overlaySmallCard);
            overlaySmallCard.Dispose();
            overlaySmallCard = null;
        }

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
        overlaySmallCard.IsOpen ^= true;

    private void ClearScanner()
    {
        scanner?.Dispose();
        scanner = null;
    }

    private bool DrawTrainsSelectCombo
    (
        float   width,
        ref int selectedIndex
    )
    {
        var changed = false;
        var selected = selectedIndex < 0 || selectedIndex >= config.Trains.Count ?
                           null :
                           config.Trains[selectedIndex];

        ImGui.SetNextItemWidth(width);
        ImGui.SetNextWindowSizeConstraints(ScaledVector2(760, 400), ScaledVector2(760, 400));
        using var combo = ImRaii.Combo
        (
            "###TrainsSelectCombo",
            selected == null ?
                $"({Lang.Get("None")})" :
                $"{selected.Name} ({selected.MonstersFound.Count})",
            ImGuiComboFlags.HeightLarge
        );
        if (!combo) return changed;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("###TrainSearch", Lang.Get("AutoMarksFinder-SearchRoutes"), ref trainSearchInput, 128);

        ImGui.SetNextItemWidth(-ImGui.GetFrameHeightWithSpacing());
        var create = ImGui.InputTextWithHint
        (
            "###AddNewTrainList",
            Lang.Get("AutoMarksFinder-NewTrainListTip"),
            ref newListNameInput,
            128,
            ImGuiInputTextFlags.EnterReturnsTrue
        );
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newListNameInput)))
            create |= ImGuiOm.ButtonIcon("###CreateTrain", FontAwesomeIcon.Plus, Lang.Get("Add"));

        if (create &&
            !string.IsNullOrWhiteSpace(newListNameInput))
        {
            var activeTrain = SelectedTrain;
            var resultTrain = scanResultTrainIndex >= 0 && scanResultTrainIndex < config.Trains.Count ?
                                  config.Trains[scanResultTrainIndex] :
                                  null;
            var train = new NotoriousMonsterList(newListNameInput.Trim());
            config.Trains.Insert(0, train);
            selectedTrainIndex   = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, activeTrain));
            scanResultTrainIndex = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, resultTrain));
            selectedIndex        = 0;
            selected             = train;
            newListNameInput     = string.Empty;
            trainSearchInput     = string.Empty;
            changed              = true;
            config.Save(this);
        }

        ImGui.Separator();

        using var table = ImRaii.Table
        (
            "###Routes",
            5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
            ScaledVector2(0, 300)
        );
        if (!table)
            return changed;

        ImGui.TableSetupColumn(Lang.Get("Name"),                              ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Lang.Get("Zone"),                              ImGuiTableColumnFlags.WidthFixed, 70  * GlobalUIScale);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-RouteMonsterCount"), ImGuiTableColumnFlags.WidthFixed, 90  * GlobalUIScale);
        ImGui.TableSetupColumn(Lang.Get("AutoMarksFinder-CreationTime"),      ImGuiTableColumnFlags.WidthFixed, 160 * GlobalUIScale);
        ImGui.TableSetupColumn("###Actions",                                  ImGuiTableColumnFlags.WidthFixed, 2   * ImGui.GetFrameHeightWithSpacing());
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < config.Trains.Count; i++)
        {
            var train = config.Trains[i];
            if (!train.Name.Contains(trainSearchInput, StringComparison.OrdinalIgnoreCase))
                continue;

            train.GeneratedDateTime ??= train.GeneratedTime.ToUTCDateTimeFromUnixSeconds().ToLocalTime();

            using var rowID = ImRaii.PushId(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (ImGui.Selectable(train.Name, ReferenceEquals(selected, train)))
            {
                selectedIndex = i;
                changed       = true;
                selected      = train;
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.MonstersFound.Select(monster => monster.ZoneID).Distinct().Count()}");

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                foreach (var area in train.MonstersFound.GroupBy(monster => (monster.ZoneID, monster.Instance)))
                    ImGui.TextUnformatted($"{FormatZoneInfo(LuminaWrapper.GetZonePlaceName(area.Key.ZoneID), area.Key.Instance)} ({area.Count()})");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.MonstersFound.Count(monster => monster.IsAlive)}/{train.MonstersFound.Count}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{train.GeneratedDateTime:g}");
            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon("###RenameTrain", FontAwesomeIcon.Edit, Lang.Get("Rename")))
            {
                renameTrainInput = train.Name;
                ImGui.OpenPopup("###RenameTrainPopup");
            }

            using (var renamePopup = ImRaii.Popup("###RenameTrainPopup"))
            {
                if (renamePopup)
                {
                    ImGui.SetNextItemWidth(260 * GlobalUIScale);
                    var rename = ImGui.InputText("###TrainName", ref renameTrainInput, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(renameTrainInput)))
                        rename |= ImGui.Button(Lang.Get("Rename"));

                    if (rename &&
                        !string.IsNullOrWhiteSpace(renameTrainInput))
                    {
                        train.Name = renameTrainInput.Trim();
                        config.Save(this);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###DeleteTrain", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
                ImGui.OpenPopup("###DeleteTrainPopup");

            using var deletePopup = ImRaii.Popup("###DeleteTrainPopup");

            if (deletePopup)
            {
                ImGui.TextUnformatted(train.Name);

                if (ImGui.Button(Lang.Get("Delete")))
                {
                    var activeTrain = SelectedTrain;
                    var resultTrain = scanResultTrainIndex >= 0 && scanResultTrainIndex < config.Trains.Count ?
                                          config.Trains[scanResultTrainIndex] :
                                          null;
                    config.Trains.RemoveAt(i);
                    selectedTrainIndex   = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, activeTrain));
                    scanResultTrainIndex = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, resultTrain));
                    selectedIndex        = config.Trains.FindIndex(candidate => ReferenceEquals(candidate, selected));
                    aliveMonitor?.RemoveTrain(train);

                    if (ReferenceEquals(activeTrain, train))
                    {
                        StopAutoBroadcast();
                        smallCardMobIndex = 0;
                    }

                    config.Save(this);
                    aliveMonitor?.RefreshTracking();
                    ImGui.CloseCurrentPopup();
                    return true;
                }
            }
        }

        if (changed)
            aliveMonitor?.RefreshTracking();

        return changed;
    }

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
