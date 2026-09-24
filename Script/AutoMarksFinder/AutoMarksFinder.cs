using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.Modules.AutoMarksFinder;

public partial class AutoMarksFinder : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoMarksFinderTitle"),
        Description         = Lang.Get("AutoMarksFinderDescription"),
        Category            = ModuleCategory.Script,
        ModulesPair    = ["NoUIFade", "NoFallDamage", "InstantTeleport"],
        ModulesPrerequisite = ["FastInstanceZoneChange"],
        Author              = ["AtmoOmen", "KirisameVanilla"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config                   config = null!;
    private CustomOverlay?           overlaySmallCard;
    private NotoriousMonsterScanner? scanner;
    private int                      smallCardMobIndex;
    private int                      selectedTrainIndex;
    private string                   newListNameInput    = string.Empty;
    private string                   startAetheryteInput = string.Empty;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 180_000, ShowDebug = IS_SHOW_DEBUG };

        overlaySmallCard       =  new(DrawSmallCard, Lang.Get("AutoMarksFinder-SmallCardTitle"));
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        overlaySmallCard.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("AutoMarksFinder-HelpMessage") });

        WindowManager.Instance().PostDraw += DrawScanRoutePreview;
    }

    protected override void Uninit()
    {
        WindowManager.Instance().PostDraw -= DrawScanRoutePreview;

        if (config != null)
            config.Save(this);

        ClearScanner();

        overlaySmallCard?.Dispose();
        overlaySmallCard = null;

        WindowManager.Instance().RemoveWindow(overlaySmallCard);
        CommandManager.Instance().RemoveCommand(COMMAND);

        selectedTrainIndex  = 0;
        smallCardMobIndex   = 0;
        newListNameInput    = string.Empty;
        startAetheryteInput = string.Empty;
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
                config.Trains.Add(new(newListNameInput));
                config.Trains = [.. config.Trains.OrderByDescending(x => x.GeneratedTime)];
                config.Save(ModuleManager.Instance().GetModule<AutoMarksFinder>());
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
            }

            ImGuiOm.TooltipHover(Lang.Get("AutoMarksFinder-CreationTime", train.GeneratedDateTime));

            using var context = ImRaii.ContextPopupItem(train.ToString());

            if (context)
            {
                if (ImGui.MenuItem(Lang.Get("Delete")))
                    config.Trains.RemoveAt(i);
            }
        }

        return changed;
    }

    #region 工具

    private static string FormatPattern
    (
        string          pattern,
        params object[] args
    )
        => string.Format(pattern, args);

    private static List<NotoriousMonsterScanPoint> SortPoints
    (
        List<NotoriousMonsterScanPoint> points
    )
    {
        if (points is not { Count: > 0 })
            return [];

        var currentPath     = new List<NotoriousMonsterScanPoint>(points.Count);
        var remainingPoints = new List<NotoriousMonsterScanPoint>(points);
        var currentPosition = IObjectTable.Instance().LocalPlayer.Position;

        while (remainingPoints.Count > 0)
        {
            var nearestIndex = 0;
            var minDistance  = Vector3.Distance(currentPosition, remainingPoints[0].Position);

            for (var i = 1; i < remainingPoints.Count; i++)
            {
                var distance = Vector3.Distance(currentPosition, remainingPoints[i].Position);

                if (distance < minDistance)
                {
                    minDistance  = distance;
                    nearestIndex = i;
                }
            }

            currentPath.Add(remainingPoints[nearestIndex]);
            currentPosition = remainingPoints[nearestIndex].Position;
            remainingPoints.RemoveAt(nearestIndex);
        }

        const int MAX_ITERATIONS = 500;

        bool improved;
        var  iteration = 0;

        do
        {
            improved = false;

            for (var i = 0; i < currentPath.Count - 1; i++)
            for (var j = i + 1; j < currentPath.Count; j++)
            {
                var oldDistance = Vector3.Distance(currentPath[i].Position, currentPath[i + 1].Position) +
                                  (j + 1 < currentPath.Count ?
                                       Vector3.Distance(currentPath[j].Position, currentPath[j + 1].Position) :
                                       0f);

                var newDistance = Vector3.Distance(currentPath[i].Position, currentPath[j].Position) +
                                  (j + 1 < currentPath.Count ?
                                       Vector3.Distance(currentPath[i + 1].Position, currentPath[j + 1].Position) :
                                       0f);

                if (newDistance < oldDistance)
                {
                    var start = i + 1;
                    var end   = j;

                    while (start < end)
                    {
                        (currentPath[start], currentPath[end]) = (currentPath[end], currentPath[start]);
                        start++;
                        end--;
                    }

                    improved = true;
                }
            }

            iteration++;
        }
        while (improved && iteration < MAX_ITERATIONS);

        return currentPath;
    }

    private static void EnqueueMount
    (
        TaskHelper taskHelper
    ) => taskHelper.Enqueue
    (
        () =>
        {
            if (!Throttler.Shared.Throttle("AutoMarksFinder-UseMount") || ICondition.Instance()[ConditionFlag.Casting]) return false;
            if (ICondition.Instance()[ConditionFlag.Mounted]) return true;

            UseActionManager.Instance().UseAction(ActionType.Mount, 1);
            return false;
        },
        "上坐骑"
    );

    private static bool IsAbleToTeleport() =>
        !LocalPlayerState.Instance().IsMoving                     &&
        !ICondition.Instance().Any(ConditionFlag.Casting) &&
        !ICondition.Instance().IsBetweenAreas             &&
        UIModule.IsScreenReady();

    #endregion

    #region 数据

#if DEBUG
    private const bool IS_SHOW_DEBUG = true;
#else
    private const bool IS_SHOW_DEBUG = false;
#endif

    private const string COMMAND = "/pdrmf";

    private static readonly FrozenSet<string> SupportedRelayCommands =
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
        "/cwl8", "/ls1", "/ls2", "/ls3", "/ls4", "/ls5", "/ls6",
        "/ls7", "/ls8"
    ];

    #endregion

    private enum NotoriousMonsterRank : byte
    {
        B = 1,
        A = 2,
        S = 3
    }

    private class Config : ModuleConfig
    {
        public int             DelayMS       = 5000;
        public HashSet<string> RelayCommands = [];

        public string RelayMacroPattern = "检测到恶名精英 {1} 于 {0} {2}";

        public Dictionary<uint, List<NotoriousMonsterScanPoint>> ScanPoints       = [];
        public HashSet<NotoriousMonsterRank>                     SelectedRanks    = [];
        public HashSet<uint>                                     SelectedZones    = [];
        public List<NotoriousMonsterList>                        Trains           = [];
        public float                                             UIScale          = 0.6f;
        public string                                            WaitMacroPattern = "请在 {0} {2} 稍作等候";
    }
}
