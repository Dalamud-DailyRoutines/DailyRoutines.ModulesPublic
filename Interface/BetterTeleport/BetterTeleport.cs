using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Common.Runtime.Hosts;
using DailyRoutines.Extensions;
using DailyRoutines.Verification;
using Dalamud.Interface.Windowing;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class BetterTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("BetterTeleportTitle"),
        Description         = Lang.Get("BetterTeleportDescription"),
        Category            = ModuleCategory.Interface,
        ModulesPrerequisite = ["SameAethernetTeleport"]
    };

    public override ModuleBehaviour Behaviour { get; } = new() { NeedAuth = true };

    public override string ModuleName => nameof(BetterTeleport);

    private static uint TicketUsageType
    {
        get => IGameConfig.Instance().UiConfig.GetUInt("TelepoTicketUseType");
        set => IGameConfig.Instance().UiConfig.Set("TelepoTicketUseType", value);
    }

    private static uint TicketUsageGilSetting
    {
        get => IGameConfig.Instance().UiConfig.GetUInt("TelepoTicketGilSetting");
        set => IGameConfig.Instance().UiConfig.Set("TelepoTicketGilSetting", value);
    }

    private Config config = null!;

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

        MigrateConfig();
        config = Config.Load(this) ?? new();

        AddonTeleportGetRegionIconID = AddonTeleportGetRegionIconIDSig.GetDelegate<AddonTeleportGetRegionIconIDDelegate>();

        AgentTeleportGetRegion     = AgentTeleportGetRegionSig.GetDelegate<AgentTeleportGetRegionDelegate>();
        AgentTeleportGetTimelineID = AgentTeleportGetTimelineIDSig.GetDelegate<AgentTeleportGetTimelineIDDelegate>();

        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterTeleport-CommandHelp") });

        UseActionManager.Instance().RegPreUseAction(OnPostUseAction);

        InputIDManager.Instance().RegPrePressed(OnPreInputIDPressed);
    }

    protected override void Uninit()
    {
        InputIDManager.Instance().UnregPrePressed(OnPreInputIDPressed);

        UseActionManager.Instance().Unreg(OnPostUseAction);
        CommandManager.Instance().RemoveCommand(COMMAND);

        if (fullWindow != null)
        {
            ManagerHost.Current.RemoveWindow(fullWindow);
            fullWindow = null;
        }

        recordMatcher?.Dispose();
        recordMatcher = null;
    }

    private void RefreshFavoritesInfo()
    {
        if (config.Favorites.Count == 0) return;
        favorites =
        [
            .. AetheryteRecordManager.Instance().AllRecords
                                     .Where(x => config.Favorites.Contains(x.UniqueID))
                                     .OrderBy(x => x.RowID)
        ];
    }

    private void HandleTeleport
    (
        AetheryteRecord aetheryte,
        string?         searchText = null
    )
    {
        if (GameState.ContentFinderCondition != 0              ||
            aetheryte is { Cost: uint.MaxValue, RowID: not 0 } ||
            !aetheryte.IsUnlocked())
            return;

        var searchTerms = GetSearchSelectionTerms(searchText).ToList();

        TaskHelper.Abort();
        Overlay.IsOpen    = false;
        fullWindow.IsOpen = false;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var hasRedirect = config.Positions.TryGetValue(aetheryte.UniqueID, out var redirected);
        var aetherytePos = hasRedirect ?
                               redirected :
                               aetheryte.Position;

        var isSameZone = aetheryte.ZoneID == GameState.TerritoryType;
        var distance2D = !isSameZone ?
                             999 :
                             Vector2.DistanceSquared(localPlayer->Position.ToVector2(), aetherytePos.ToVector2());
        if (distance2D <= 900)
            return;

        if (aetherytePos.Y == 0)
            aetherytePos = aetherytePos.WithY(500);

        searchWord       = string.Empty;
        pinnedAetheryte  = null;
        hoveredAetheryte = null;

        Overlay.IsOpen    = false;
        fullWindow.IsOpen = false;

        RememberSearchSelection(aetheryte, searchTerms);
        AddToRecentTeleports(aetheryte);

        NotifyHelper.Instance().Chat
        (
            Lang.Get
            (
                "BetterTeleport-Notification",
                new Dictionary<string, object>
                {
                    ["destination"] = aetheryte.Name
                }
            )
        );

        TaskHelper.Enqueue(aetheryte.TeleportTo);

        if (hasRedirect)
        {
            TaskHelper.Enqueue(() => GameState.TerritoryType == aetheryte.ZoneID && !UIModule.IsScreenReady());
            TaskHelper.Enqueue
            (() =>
                {
                    if (UIModule.IsScreenReady())
                        return true;

                    MovementManager.Instance().TPSmart_InZone(aetherytePos);
                    return false;
                }
            );
        }
    }

    private void AddToRecentTeleports
    (
        AetheryteRecord aetheryte
    )
    {
        var key = aetheryte.UniqueID;
        config.RecentTeleports.RemoveAll(x => x.Key == key);
        config.RecentTeleports.Insert(0, new RecentRecord { Key = key });
        if (config.RecentTeleports.Count > 20)
            config.RecentTeleports.RemoveRange(20, config.RecentTeleports.Count - 20);
        config.Save(this);
        RefreshDefaultOverlayItems();
    }

    private static IEnumerable<string> GetSearchSelectionTerms
    (
        string? searchText
    )
    {
        var normalized = NormalizeSearchSelectionTerm(searchText);
        if (string.IsNullOrEmpty(normalized)) yield break;

        yield return normalized;

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1) yield break;

        foreach (var part in parts)
        {
            if (part.Length >= 2)
                yield return part;
        }
    }

    private void RememberSearchSelection
    (
        AetheryteRecord     aetheryte,
        IEnumerable<string> searchTerms
    )
    {
        var terms = searchTerms.Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) return;

        var now = StandardTimeManager.Instance().UTCNowOffset.ToUnixTimeSeconds();

        foreach (var term in terms)
        {
            if (!config.SearchSelections.TryGetValue(term, out var recordsForTerm))
            {
                recordsForTerm                = [];
                config.SearchSelections[term] = recordsForTerm;
            }

            var existing = recordsForTerm.FirstOrDefault(x => x.Key == aetheryte.UniqueID);

            if (existing == null)
            {
                recordsForTerm.Add
                (
                    new SearchSelectionRecord
                    {
                        Key                 = aetheryte.UniqueID,
                        Count               = 1,
                        LastUsedUnixSeconds = now
                    }
                );
            }
            else
            {
                existing.Count++;
                existing.LastUsedUnixSeconds = now;
            }

            recordsForTerm.Sort(CompareSearchSelectionRecords);

            if (recordsForTerm.Count > MAX_SEARCH_SELECTION_RECORDS_PER_TERM)
                recordsForTerm.RemoveRange(MAX_SEARCH_SELECTION_RECORDS_PER_TERM, recordsForTerm.Count - MAX_SEARCH_SELECTION_RECORDS_PER_TERM);
        }

        TrimSearchSelections();
    }

    private void TrimSearchSelections()
    {
        if (config.SearchSelections.Count <= MAX_SEARCH_SELECTION_TERMS) return;

        foreach (var key in config.SearchSelections
                                  .OrderByDescending
                                  (x => x.Value.Count == 0 ?
                                            0 :
                                            x.Value.Max(r => r.LastUsedUnixSeconds)
                                  )
                                  .Skip(MAX_SEARCH_SELECTION_TERMS)
                                  .Select(x => x.Key)
                                  .ToList())
            config.SearchSelections.Remove(key);
    }

    private List<AetheryteRecord> SortSearchMatches
    (
        string                searchText,
        List<AetheryteRecord> matches
    )
    {
        var normalized = NormalizeSearchSelectionTerm(searchText);
        if (string.IsNullOrEmpty(normalized) || config.SearchSelections.Count == 0)
            return matches;

        return
        [
            .. matches.Select((record, index) => new { record, index, score = GetSearchSelectionScore(normalized, record) })
                      .OrderByDescending(x => x.score)
                      .ThenBy(x => x.index)
                      .Select(x => x.record)
        ];
    }

    private double GetSearchSelectionScore
    (
        string          normalizedSearchText,
        AetheryteRecord record
    )
    {
        double score = 0;

        foreach (var (term, selections) in config.SearchSelections)
        {
            var relationWeight = GetSearchTermRelationWeight(normalizedSearchText, term);
            if (relationWeight <= 0) continue;

            var selection = selections.FirstOrDefault(x => x.Key == record.UniqueID);
            if (selection == null) continue;

            var countScore   = Math.Min(selection.Count, 20) * 100;
            var recencyScore = selection.LastUsedUnixSeconds / 86_400d;
            score = Math.Max(score, (countScore + recencyScore) * relationWeight);
        }

        return score;
    }

    private static double GetSearchTermRelationWeight
    (
        string searchText,
        string storedTerm
    )
    {
        if (searchText == storedTerm) return 1;
        if (searchText.Length < 2 || storedTerm.Length < 2) return 0;
        if (searchText.StartsWith(storedTerm, StringComparison.Ordinal) || storedTerm.StartsWith(searchText, StringComparison.Ordinal))
            return 0.75;
        if (searchText.Contains(storedTerm, StringComparison.Ordinal) || storedTerm.Contains(searchText, StringComparison.Ordinal))
            return 0.4;

        return 0;
    }

    private static string NormalizeSearchSelectionTerm
    (
        string? searchText
    ) =>
        string.Join
        (
            ' ',
            (searchText ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );

    private static int CompareSearchSelectionRecords
    (
        SearchSelectionRecord a,
        SearchSelectionRecord b
    )
    {
        var byCount = b.Count.CompareTo(a.Count);
        return byCount != 0 ?
                   byCount :
                   b.LastUsedUnixSeconds.CompareTo(a.LastUsedUnixSeconds);
    }

    private static bool IsWithPermission() =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsTester || Sheets.SpeedDetectionZones.ContainsKey(GameState.TerritoryType);

    #region 配置

    private void MigrateConfig()
    {
        if (!File.Exists(ConfigFilePath)) return;

        var allRecords = AetheryteRecordManager.Instance().AllRecords.ToList();
        if (allRecords.Count == 0) return;

        var jsonObj  = JObject.Parse(File.ReadAllText(ConfigFilePath));
        var migrated = false;

        var legacyKeys = allRecords.SelectMany
                                   (record => new[]
                                        {
                                            record.RowID.ToString(),
                                            $"{record.RowID}_{record.SubIndex}",
                                            record.ToString()
                                        }
                                        .Select(key => new { Key = key, RecordKey = record.UniqueID })
                                   )
                                   .GroupBy(x => x.Key, StringComparer.Ordinal)
                                   .Where(group => group.Select(x => x.RecordKey).Distinct(StringComparer.Ordinal).Count() == 1)
                                   .ToDictionary(group => group.Key, group => group.First().RecordKey, StringComparer.Ordinal);

        if (jsonObj["Remarks"] is JObject remarks)
        {
            var oldKeys = remarks.Properties()
                                 .Where(p => legacyKeys.ContainsKey(p.Name))
                                 .Select(p => p.Name)
                                 .ToList();

            if (oldKeys.Count > 0)
            {
                foreach (var oldKey in oldKeys)
                {
                    var value  = remarks[oldKey];
                    var newKey = legacyKeys[oldKey];
                    remarks[newKey] ??= value;
                    remarks.Remove(oldKey);
                }

                migrated = true;
            }
        }

        if (jsonObj["Positions"] is JObject positions)
        {
            var oldKeys = positions.Properties()
                                   .Where(p => legacyKeys.ContainsKey(p.Name))
                                   .Select(p => p.Name)
                                   .ToList();

            if (oldKeys.Count > 0)
            {
                foreach (var oldKey in oldKeys)
                {
                    var value  = positions[oldKey];
                    var newKey = legacyKeys[oldKey];
                    positions[newKey] ??= value;
                    positions.Remove(oldKey);
                }

                migrated = true;
            }
        }

        if (jsonObj["Favorites"] is JArray favorites)
        {
            for (var index = 0; index < favorites.Count; index++)
            {
                if (!legacyKeys.TryGetValue(favorites[index].ToString(), out var newKey))
                    continue;

                favorites[index] = newKey;
                migrated         = true;
            }
        }

        var historyRecords = new List<JObject>();
        if (jsonObj["RecentTeleports"] is JArray recentTeleports)
            historyRecords.AddRange(recentTeleports.OfType<JObject>());

        if (jsonObj["SearchSelections"] is JObject searchSelections)
            historyRecords.AddRange
            (
                searchSelections.Properties()
                                .Select(x => x.Value)
                                .OfType<JArray>()
                                .SelectMany(x => x.OfType<JObject>())
            );

        foreach (var item in historyRecords)
        {
            var oldKey = item["Key"]?.Value<string>();
            if (oldKey == null && item["AetheryteID"] is { } aetheryteID)
                oldKey = $"{aetheryteID.Value<uint>()}_{item["SubIndex"]?.Value<byte>() ?? 0}";
            if (oldKey == null || !legacyKeys.TryGetValue(oldKey, out var newKey))
                continue;

            item["Key"] = newKey;
            item.Remove("AetheryteID");
            item.Remove("SubIndex");
            migrated = true;
        }

        if (!migrated) return;

        File.WriteAllText(ConfigFilePath, jsonObj.ToString(Formatting.Indented));
    }

    public class RecentRecord
    {
        public string Key { get; set; } = string.Empty;
    }

    public class SearchSelectionRecord
    {
        public string Key                 { get; set; } = string.Empty;
        public int    Count               { get; set; }
        public long   LastUsedUnixSeconds { get; set; }
    }

    private enum PageType
    {
        Search,
        Full
    }

    private class Config : ModuleConfig
    {
        public PageType DefaultPage          = PageType.Search;
        public bool     FocusSearchOnOpen    = true;
        public bool     CloseOnLoseFocus     = true;
        public bool     HideAethernetInParty = true;

        public HashSet<string>                                 Favorites        = [];
        public Dictionary<string, Vector3>                     Positions        = [];
        public Dictionary<string, string>                      Remarks          = [];
        public List<RecentRecord>                              RecentTeleports  = [];
        public Dictionary<string, List<SearchSelectionRecord>> SearchSelections = [];
    }

    #endregion

    #region 常量

    private const string COMMAND = "/pdrtelepo";

    private const int MAX_SEARCH_SELECTION_TERMS            = 200;
    private const int MAX_SEARCH_SELECTION_RECORDS_PER_TERM = 8;


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
