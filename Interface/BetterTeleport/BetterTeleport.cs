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
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic.Interface.BetterTeleport;

public unsafe partial class BetterTeleport : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("BetterTeleportTitle"),
        Description         = Lang.Get("BetterTeleportDescription"),
        Category            = ModuleCategory.Interface,
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
        favorites = AetheryteRecordManager.Instance().AllRecords
                          .Where(x => config.Favorites.Contains(x.ToString()))
                          .OrderBy(x => x.RowID)
                          .ToList();
    }

    private void HandleTeleport(AetheryteRecord aetheryte, string? searchText = null)
    {
        if (GameState.ContentFinderCondition != 0) return;

        var searchTerms = GetSearchSelectionTerms(searchText).ToList();

        TaskHelper.Abort();
        Overlay.IsOpen    = false;
        fullWindow.IsOpen = false;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var hasRedirect  = config.Positions.TryGetValue(aetheryte.ToString(), out var redirected);
        var aetherytePos = hasRedirect ? redirected : aetheryte.Position;

        var isSameZone = aetheryte.ZoneID == GameState.TerritoryType;
        var distance2D = !isSameZone
                             ? 999
                             : Vector2.DistanceSquared(localPlayer->Position.ToVector2(), aetherytePos.ToVector2());
        if (distance2D <= 900) return;

        if (aetherytePos.Y == 0)
            aetherytePos = aetherytePos.WithY(500);

        NotifyHelper.Instance().NotificationInfo(Lang.Get("BetterTeleport-Notification", aetheryte.Name));

        RememberSearchSelection(aetheryte, searchTerms);

        searchWord       = string.Empty;
        pinnedAetheryte  = null;
        hoveredAetheryte = null;
        Overlay.IsOpen   = false;

        AddToRecentTeleports(aetheryte);
        
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

    private void AddToRecentTeleports(AetheryteRecord aetheryte)
    {
        var key = aetheryte.ToString();
        config.RecentTeleports.RemoveAll(x => x.Key == key);
        config.RecentTeleports.Insert(0, new RecentRecord { Key = key });
        if (config.RecentTeleports.Count > 20)
            config.RecentTeleports.RemoveRange(20, config.RecentTeleports.Count - 20);
        config.Save(this);
        RefreshDefaultOverlayItems();
    }

    private static IEnumerable<string> GetSearchSelectionTerms(string? searchText)
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

    private void RememberSearchSelection(AetheryteRecord aetheryte, IEnumerable<string> searchTerms)
    {
        var terms = searchTerms.Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var term in terms)
        {
            if (!config.SearchSelections.TryGetValue(term, out var recordsForTerm))
            {
                recordsForTerm                = [];
                config.SearchSelections[term] = recordsForTerm;
            }

            var existing = recordsForTerm.FirstOrDefault(x => x.Key == aetheryte.ToString());

            if (existing == null)
            {
                recordsForTerm.Add
                (
                    new SearchSelectionRecord
                    {
                        Key                 = aetheryte.ToString(),
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
                                  .OrderByDescending(x => x.Value.Count == 0 ? 0 : x.Value.Max(r => r.LastUsedUnixSeconds))
                                  .Skip(MAX_SEARCH_SELECTION_TERMS)
                                  .Select(x => x.Key)
                                  .ToList())
            config.SearchSelections.Remove(key);
    }

    private List<AetheryteRecord> SortSearchMatches(string searchText, List<AetheryteRecord> matches)
    {
        var normalized = NormalizeSearchSelectionTerm(searchText);
        if (string.IsNullOrEmpty(normalized) || config.SearchSelections.Count == 0)
            return matches;

        return matches.Select((record, index) => new { record, index, score = GetSearchSelectionScore(normalized, record) })
                      .OrderByDescending(x => x.score)
                      .ThenBy(x => x.index)
                      .Select(x => x.record)
                      .ToList();
    }

    private double GetSearchSelectionScore(string normalizedSearchText, AetheryteRecord record)
    {
        double score = 0;

        foreach (var (term, selections) in config.SearchSelections)
        {
            var relationWeight = GetSearchTermRelationWeight(normalizedSearchText, term);
            if (relationWeight <= 0) continue;

            var selection = selections.FirstOrDefault(x => x.Key == record.ToString());
            if (selection == null) continue;

            var countScore   = Math.Min(selection.Count, 20) * 100;
            var recencyScore = selection.LastUsedUnixSeconds / 86_400d;
            score = Math.Max(score, (countScore + recencyScore) * relationWeight);
        }

        return score;
    }

    private static double GetSearchTermRelationWeight(string searchText, string storedTerm)
    {
        if (searchText == storedTerm) return 1;
        if (searchText.Length < 2 || storedTerm.Length < 2) return 0;
        if (searchText.StartsWith(storedTerm, StringComparison.Ordinal) || storedTerm.StartsWith(searchText, StringComparison.Ordinal))
            return 0.75;
        if (searchText.Contains(storedTerm, StringComparison.Ordinal) || storedTerm.Contains(searchText, StringComparison.Ordinal))
            return 0.4;

        return 0;
    }

    private static string NormalizeSearchSelectionTerm(string? searchText) =>
        string.Join
        (
            ' ',
            (searchText ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );

    private static int CompareSearchSelectionRecords(SearchSelectionRecord a, SearchSelectionRecord b)
    {
        var byCount = b.Count.CompareTo(a.Count);
        if (byCount != 0) return byCount;

        return b.LastUsedUnixSeconds.CompareTo(a.LastUsedUnixSeconds);
    }

    private static bool IsWithPermission() =>
        !(GameState.IsCN || GameState.IsTC) || AuthState.IsPremium || Sheets.SpeedDetectionZones.ContainsKey(GameState.TerritoryType);

    #region 配置

    private void MigrateConfig()
    {
        if (!File.Exists(ConfigFilePath)) return;

        var allRecords = AetheryteRecordManager.Instance().AllRecords.ToList();
        if (allRecords.Count == 0) return;

        var jsonObj = JObject.Parse(File.ReadAllText(ConfigFilePath));
        var migrated = false;

        // 迁移 Remarks (v1: 纯数字 RowID / v2: RowID_SubIndex → v3: AetheryteRecord.ToString())
        if (jsonObj["Remarks"] is JObject remarks)
        {
            var oldKeys = remarks.Properties()
                                 .Where(p => !p.Name.StartsWith("AetheryteRecord.", StringComparison.Ordinal))
                                 .Select(p => p.Name)
                                 .ToList();

            if (oldKeys.Count > 0)
            {
                foreach (var oldKey in oldKeys)
                {
                    var value = remarks[oldKey]!;
                    remarks.Remove(oldKey);
                    var newKey = MigrateOldKeyToToString(oldKey, allRecords);
                    if (newKey != null) remarks[newKey] = value;
                }

                migrated = true;
            }
        }

        // 迁移 Positions (同上)
        if (jsonObj["Positions"] is JObject positions)
        {
            var oldKeys = positions.Properties()
                                  .Where(p => !p.Name.StartsWith("AetheryteRecord.", StringComparison.Ordinal))
                                  .Select(p => p.Name)
                                  .ToList();

            if (oldKeys.Count > 0)
            {
                foreach (var oldKey in oldKeys)
                {
                    var value = positions[oldKey]!;
                    positions.Remove(oldKey);
                    var newKey = MigrateOldKeyToToString(oldKey, allRecords);
                    if (newKey != null) positions[newKey] = value;
                }

                migrated = true;
            }
        }

        // 迁移 Favorites (v1/v2: HashSet<uint> → v3: HashSet<string>)
        if (jsonObj["Favorites"] is JArray favorites)
        {
            var hasOld = favorites.Any(x => x.Type == JTokenType.Integer ||
                                            (x.Type == JTokenType.String &&
                                             !x.Value<string>()!.StartsWith("AetheryteRecord.", StringComparison.Ordinal)));
            if (hasOld)
            {
                var newFavorites = new JArray();
                foreach (var item in favorites)
                {
                    uint? rowID = item.Type == JTokenType.Integer
                                      ? item.Value<uint>()
                                      : item.Type == JTokenType.String && uint.TryParse(item.Value<string>(), out var parsed)
                                          ? parsed
                                          : null;

                    if (rowID.HasValue)
                    {
                        var record = allRecords.FirstOrDefault(x => x.RowID == rowID.Value);
                        if (record != null) newFavorites.Add(record.ToString());
                    }
                    else if (item.Type == JTokenType.String)
                    {
                        newFavorites.Add(item.Value<string>()!);
                    }
                }

                jsonObj["Favorites"] = newFavorites;
                migrated = true;
            }
        }

        // 迁移 RecentTeleports (v1/v2: AetheryteID+SubIndex → v3: Key)
        if (jsonObj["RecentTeleports"] is JArray recentTeleports &&
            recentTeleports.Any(x => x["AetheryteID"] != null))
        {
            var newList = new JArray();
            foreach (var item in recentTeleports)
            {
                var aetheryteID = item["AetheryteID"]?.Value<uint>();
                var subIndex    = item["SubIndex"]?.Value<byte>() ?? 0;
                if (!aetheryteID.HasValue) continue;

                var record = allRecords.FirstOrDefault(x => x.RowID == aetheryteID.Value && x.SubIndex == subIndex);
                if (record != null)
                    newList.Add(new JObject { ["Key"] = record.ToString() });
            }

            jsonObj["RecentTeleports"] = newList;
            migrated = true;
        }

        // 迁移 SearchSelections (v1/v2: AetheryteID+SubIndex → v3: Key)
        if (jsonObj["SearchSelections"] is JObject searchSelections &&
            searchSelections.Properties().Any(p => p.Value is JArray arr && arr.Any(x => x["AetheryteID"] != null)))
        {
            foreach (var prop in searchSelections.Properties().ToList())
            {
                if (prop.Value is not JArray arr) continue;
                var newArr = new JArray();
                foreach (var item in arr)
                {
                    var aetheryteID = item["AetheryteID"]?.Value<uint>();
                    var subIndex    = item["SubIndex"]?.Value<byte>() ?? 0;
                    if (!aetheryteID.HasValue) continue;

                    var record = allRecords.FirstOrDefault(x => x.RowID == aetheryteID.Value && x.SubIndex == subIndex);
                    if (record == null) continue;

                    newArr.Add(new JObject
                    {
                        ["Key"]                 = record.ToString(),
                        ["Count"]               = item["Count"]?.Value<int>() ?? 1,
                        ["LastUsedUnixSeconds"] = item["LastUsedUnixSeconds"]?.Value<long>() ?? 0
                    });
                }

                prop.Value = newArr;
            }

            migrated = true;
        }

        if (!migrated) return;

        File.WriteAllText(ConfigFilePath, jsonObj.ToString(Formatting.Indented));
    }

    private static string? MigrateOldKeyToToString(string oldKey, List<AetheryteRecord> allRecords)
    {
        uint rowID;
        byte subIndex;

        if (oldKey.Contains('_'))
        {
            var parts = oldKey.Split('_');
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out rowID) ||
                !byte.TryParse(parts[1], out subIndex))
                return null;
        }
        else
        {
            if (!uint.TryParse(oldKey, out rowID)) return null;
            subIndex = 0;
        }

        return allRecords.FirstOrDefault(x => x.RowID == rowID && x.SubIndex == subIndex)?.ToString();
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
