using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
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

    protected override void Init()
    {
        config          =   Config.Load(this) ?? new();
        TaskHelper      ??= new() { TimeoutMS = 180_000, ShowDebug = IS_SHOW_DEBUG };
        relayTaskHelper ??= new() { ShowDebug = IS_SHOW_DEBUG };

        overlaySmallCard       =  new(DrawSmallCard, Lang.Get("AutoMarksFinder-SmallCardTitle"));
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoMarksFinder-HelpMessage") });

        WindowManager.Instance().PostDraw += DrawScanRoutePreview;

        aliveMonitor = new(this);
        FrameworkManager.Instance().Reg(UpdateAutoBroadcast, AUTO_BROADCAST_TICK_MS);
    }

    protected override void Uninit()
    {
        WindowManager.Instance().PostDraw -= DrawScanRoutePreview;

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

    private void StopScanner()
    {
        vnavmeshIPC.StopPathfind();
        TaskHelper.Abort();
        scanner?.Dispose();
    }

    private void ClearScanner()
    {
        StopScanner();
        scanner = null;
    }

    private bool DrawTrainsSelectCombo
    (
        float   width,
        ref int selectedIndex
    )
    {
        var changed = false;
        var selected = selectedIndex == -1 || selectedIndex > config.Trains.Count - 1 ?
                           null :
                           config.Trains[selectedIndex];

        ImGui.SetNextItemWidth(width);
        using var combo = ImRaii.Combo
        (
            "###TrainsSelectCombo",
            selected == null ?
                $"({Lang.Get("None")})" :
                selected.Name
        );
        if (!combo) return changed;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        if (ImGui.InputTextWithHint
            (
                "###AddNewTrainList",
                Lang.Get("AutoMarksFinder-NewTrainListTip"),
                ref newListNameInput,
                128,
                ImGuiInputTextFlags.EnterReturnsTrue
            ))
        {
            if (!string.IsNullOrWhiteSpace(newListNameInput))
            {
                var selectedTrain = selected;
                config.Trains.Add(new(newListNameInput));
                config.Trains = [.. config.Trains.OrderByDescending(x => x.GeneratedTime)];
                if (selectedTrain != null)
                    selectedIndex = config.Trains.FindIndex(x => ReferenceEquals(x, selectedTrain));
                else
                    selectedIndex = -1;
                config.Save(this);
            }
        }

        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 0; i < config.Trains.Count; i++)
        {
            var train = config.Trains[i];
            train.GeneratedDateTime ??= train.GeneratedTime.ToUTCDateTimeFromUnixSeconds().ToLocalTime();

            if (ImGui.Selectable($"{train.Name}", selected?.Equals(train) ?? false))
            {
                selectedIndex = i;
                changed       = true;
                selected      = train;
            }

            ImGuiOm.TooltipHover(Lang.Get("AutoMarksFinder-CreationTime", train.GeneratedDateTime));

            using var context = ImRaii.ContextPopupItem(train.ToString());

            if (context)
            {
                if (ImGui.MenuItem(Lang.Get("Delete")))
                {
                    config.Trains.RemoveAt(i);
                    aliveMonitor?.RemoveTrain(train);

                    if (config.Trains.Count == 0)
                        selectedIndex = -1;
                    else if (selectedIndex > i)
                        selectedIndex--;
                    else if (ReferenceEquals(selected, train))
                    {
                        selectedIndex = Math.Min(i, config.Trains.Count - 1);
                        StopAutoBroadcast();
                    }

                    config.Save(this);
                    aliveMonitor?.RefreshTracking();
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
        public int          DelayMS       = 5000;
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
