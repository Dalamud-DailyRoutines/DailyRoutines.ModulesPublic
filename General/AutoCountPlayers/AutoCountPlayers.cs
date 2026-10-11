using DailyRoutines.Common.Info.Models;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCountPlayers : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCountPlayersTitle"),
        Description = Lang.Get("AutoCountPlayersDescription"),
        Category    = ModuleCategory.General
    };
    
    private static bool IsPlayerSearchLocation =>
        IsContentSearchZone || IsPlayerSearchZone;

    private static bool IsContentSearchZone =>
        ContentMemberListValidZones.Contains(GameState.TerritoryIntendedUse);

    private static bool IsPlayerSearchZone =>
        GameState.TerritoryType          > 0  &&
        GameState.ContentFinderCondition == 0 &&
        Sheets.PlayerSearchPlaceNames.ContainsKey(GameState.TerritoryTypeData.PlaceNameZone.RowId);

    private Hook<InfoProxyContentMember.Delegates.EndRequest>? InfoProxyContentMemberEndRequestHook;
    private Hook<InfoProxySearch.Delegates.EndRequest>?        InfoProxySearchEndRequestHook;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    private readonly Dictionary<uint, byte[]>              jobIcons          = [];
    private readonly Dictionary<uint, PlayerTargetingInfo> lastTargetingData = [];

    private string searchInput     = string.Empty;
    private string searchZoneInput = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        entry       ??= IDtrBar.Instance().Get("DailyRoutines-AutoCountPlayers");
        entry.Shown =   true;
        entry.Text  =   $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: 0";
        entry.OnClick = _ =>
        {
            EnsureOverlay();
            Overlay.IsOpen ^= true;
        };

        WindowManager.Instance().PostDraw += OnDraw;

        PlayersManager.Instance().ReceivePlayersAround      += OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe += OnPlayersTargetingMeUpdate;

        InfoProxyContentMemberEndRequestHook = InfoProxyContentMember.Instance()->VirtualTable->HookVFuncFromName
        (
            "EndRequest",
            (InfoProxyContentMember.Delegates.EndRequest)InfoProxyContentMemberRequestDetour
        );
        InfoProxyContentMemberEndRequestHook.Enable();

        InfoProxySearchEndRequestHook = InfoProxySearch.Instance()->VirtualTable->HookVFuncFromName
        (
            "EndRequest",
            (InfoProxySearch.Delegates.EndRequest)InfoProxySearchRequestDetour
        );
        InfoProxySearchEndRequestHook.Enable();

        LogMessageManager.Instance().RegPre(OnLogMessage);
        FrameworkManager.Instance().Reg(OnUpdate, throttleMS: 1_000);
        OnUpdate(IFramework.Instance());

        GameState.Instance().WarpComplete += OnWarpCompleted;
    }

    protected override void Uninit()
    {
        GameState.Instance().WarpComplete -= OnWarpCompleted;

        FrameworkManager.Instance().Unreg(OnUpdate);
        LogMessageManager.Instance().Unreg(OnLogMessage);

        WindowManager.Instance().PostDraw                   -= OnDraw;
        PlayersManager.Instance().ReceivePlayersAround      -= OnReceivePlayers;
        PlayersManager.Instance().ReceivePlayersTargetingMe -= OnPlayersTargetingMeUpdate;

        foreach (var info in lastTargetingData.Values)
        {
            var duration = DateTime.Now - info.TargetingStartTime;
            config.TargetingHistories.Add
            (
                new()
                {
                    Name        = info.Player.Name,
                    HomeWorldID = info.Player.HomeWorld.RowId,
                    JobID       = info.Player.ClassJob.RowId,
                    StartTime   = info.TargetingStartTime,
                    Duration    = duration,
                    ZoneID      = GameState.TerritoryType
                }
            );
        }

        if (lastTargetingData.Count > 0)
        {
            lastTargetingData.Clear();
            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);
            config.Save(this);
        }

        entry?.Remove();
        entry = null;
    }

    #region 事件

    private static void OnWarpCompleted
    (
        WarpType warpType
    ) =>
        Throttler.Shared.Remove("AutoCountPlayers.Zone");
    
    private static void OnLogMessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem item
    )
    {
        if (logMessageID != 81) return;
        isPrevented = true;
    }

    private static void OnUpdate
    (
        IFramework framework
    )
    {
        if (!IsPlayerSearchLocation) return;

        if (!UIModule.IsScreenReady() ||
            !Throttler.Shared.Throttle("AutoCountPlayers.Zone", 60_000))
            return;

        if (IsContentSearchZone)
        {
            if (InfoProxyContentMember.Instance() == null ||
                AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentMemberList)->IsAgentActive())
                return;

            AgentId.ContentMemberList.SendEvent(0, 1);
        }
        else if (IsPlayerSearchZone)
        {
            var searchInstance = InfoProxySearch.Instance();
            if (searchInstance == null ||
                AgentModule.Instance()->GetAgentByInternalId(AgentId.Search)->IsAgentActive())
                return;

            searchInstance->JobMask          = 0xFFFFFFFFFFFFFFFF; // all
            searchInstance->LevelMin         = 1;
            searchInstance->LevelMax         = 255;
            searchInstance->GrandCompanyMask = 0xFF;
            searchInstance->LanguageMask     = 0xFF;
            searchInstance->OnlineStatusMask = 0x800000000000;
            searchInstance->LocationIDs[0]   = (ushort)GameState.TerritoryTypeData.PlaceNameZone.RowId;
            searchInstance->LocationCount    = 1;
            for (var i = 0; i < searchInstance->Name.Length; i++)
                searchInstance->Name[i] = 0;

            searchInstance->RequestData();
        }
        else
            FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private void OnReceivePlayers
    (
        IReadOnlyList<IPlayerCharacter> characters
    )
    {
        if (entry == null) return;

        if (IsPlayerSearchLocation)
            entry.Shown = true;
        else
            entry.Shown = !ICondition.Instance()[ConditionFlag.InCombat] || GameState.IsInPVPArea;

        if (!entry.Shown)
        {
            EnsureOverlay();
            Overlay.IsOpen = false;
            return;
        }

        entry.Text = $"{Lang.Get("AutoCountPlayers-PlayersAroundCount")}: {PlayersManager.Instance().PlayersAroundCount}" +
                     (PlayersManager.Instance().PlayersTargetingMe.Count == 0 ?
                          string.Empty :
                          $" ({PlayersManager.Instance().PlayersTargetingMe.Count})");

        // 特殊场景探索
        if (IsContentSearchZone)
        {
            entry.Text.Append
            (
                $" / {Lang.Get("AutoCountPlayers-PlayersZoneCount")}: " +
                $"{InfoProxyContentMember.Instance()->EntryCount}"
            );
        }
        else if (IsPlayerSearchZone)
        {
            var count = InfoProxySearch.Instance()->CharDataSpan
                        .ToArray()
                        .Count(x => x.Job > 0 && x.Location == GameState.TerritoryType);
            entry.Text.Append
            (
                $" / {Lang.Get("AutoCountPlayers-PlayersZoneCount")}: " +
                $"{count}"
            );
        }

        if (characters.Count == 0)
        {
            entry.Tooltip = string.Empty;
            return;
        }

        var tooltip = new SeStringBuilder();

        if (PlayersManager.Instance().PlayersTargetingMe.Count > 0)
        {
            tooltip.AddUiForeground(32)
                   .AddText($"{Lang.Get("AutoCountPlayers-PlayersTargetingMe")}")
                   .AddUiForegroundOff()
                   .Add(NewLinePayload.Payload);

            PlayersManager.Instance().PlayersTargetingMe.ForEach
            (info =>
                 tooltip
                     .AddIcon(info.Player.ClassJob.Value.ToBitmapFontIcon())
                     .AddText($"{info.Player.Name}")
                     .AddIcon(BitmapFontIcon.CrossWorld)
                     .AddText($"{info.Player.HomeWorld.Value.Name}")
                     .Add(NewLinePayload.Payload)
            );
        }

        tooltip.AddUiForeground(32)
               .AddText($"{Lang.Get("AutoCountPlayers-PlayersAroundInfo")}")
               .AddUiForegroundOff()
               .Add(NewLinePayload.Payload);

        characters.ForEach
        (info => tooltip
                 .AddIcon(info.ClassJob.Value.ToBitmapFontIcon())
                 .AddText($"{info.Name}")
                 .AddIcon(BitmapFontIcon.CrossWorld)
                 .AddText($"{info.HomeWorld.Value.Name}")
                 .Add(NewLinePayload.Payload)
        );

        var message = tooltip.Build();
        if (message.Payloads.Last() is NewLinePayload)
            message.Payloads.RemoveAt(message.Payloads.Count - 1);

        entry.Tooltip = message;
    }

    private void OnPlayersTargetingMeUpdate
    (
        IReadOnlyList<PlayerTargetingInfo> targetingPlayersInfo
    )
    {
        var currentIDs     = targetingPlayersInfo.Select(x => x.Player.EntityID).ToHashSet();
        var endedTargeting = lastTargetingData.Where(x => !currentIDs.Contains(x.Key)).ToList();

        if (endedTargeting.Count > 0)
        {
            foreach (var (key, info) in endedTargeting)
            {
                var duration = DateTime.Now - info.TargetingStartTime;

                config.TargetingHistories.Add
                (
                    new()
                    {
                        Name        = info.Player.Name,
                        HomeWorldID = info.Player.HomeWorld.RowId,
                        JobID       = info.Player.ClassJob.RowId,
                        StartTime   = info.TargetingStartTime,
                        Duration    = duration,
                        ZoneID      = GameState.TerritoryType
                    }
                );

                lastTargetingData.Remove(key);
            }

            if (config.TargetingHistories.Count > 100)
                config.TargetingHistories.RemoveRange(0, config.TargetingHistories.Count - 100);

            config.Save(this);
        }

        foreach (var info in targetingPlayersInfo)
        {
            if (info.Player.ClassJob.RowId == 0) continue;
            lastTargetingData[info.Player.EntityID] = info;
        }

        if (targetingPlayersInfo.Count > 0 &&
            (GameState.ContentFinderCondition == 0 || IPartyList.Instance().Length < 2))
        {
            var newTargetingPlayers = targetingPlayersInfo.Where(info => info.IsNew)
                                                          .ToList();

            if (newTargetingPlayers.Any
                (info => Throttler.Shared.Throttle
                         (
                             $"AutoCountPlayers-Player-{info.Player.EntityID}",
                             30_000
                         ) &&
                         !info.Player.ToStruct()->IsFriend
                ))
            {
                NotifyHelper.Instance().TrayWarning
                (
                    Lang.Get
                    (
                        "AutoCountPlayers-Notification-Message",
                        new Dictionary<string, object>
                        {
                            ["playerCount"] = targetingPlayersInfo.Count
                        }
                    ),
                    Lang.Get("AutoCountPlayers-Notification-Title")
                );

                var messageTitle = Lang.Get
                (
                    "AutoCountPlayers-Message",
                    new Dictionary<string, object>
                    {
                        ["playerCount"] = targetingPlayersInfo.Count
                    }
                );

                using var rented       = new RentedSeStringBuilder();
                using var playerRented = new RentedSeStringBuilder();

                rented.Append(messageTitle);

                foreach (var player in targetingPlayersInfo)
                {
                    playerRented.AppendIcon((uint)player.Player.ClassJob.Value.ToBitmapFontIcon())
                                .Append(ReadOnlySeString.CreatePlayer(player.Player.Name, player.Player.HomeWorld.RowId));

                    var playerLink = ReadOnlySeString.CreatePlayerLink
                    (
                        player.Player.Name,
                        player.Player.HomeWorld.RowId,
                        playerRented.ToReadOnlySeString()
                    );
                    playerRented.Clear();

                    rented.AppendNewLine()
                          .Append("  ")
                          .Append(playerLink);
                }

                NotifyHelper.Instance().Chat(rented.ToReadOnlySeString(), false);
            }
        }
    }

    private void InfoProxyContentMemberRequestDetour
    (
        InfoProxyContentMember* proxy
    )
    {
        InfoProxyContentMemberEndRequestHook.Original(proxy);
        OnReceivePlayers(PlayersManager.Instance().PlayersAround);
    }

    private void InfoProxySearchRequestDetour
    (
        InfoProxySearch* proxy
    )
    {
        InfoProxySearchEndRequestHook.Original(proxy);
        OnReceivePlayers(PlayersManager.Instance().PlayersAround);
    }

    #endregion

    private static List<(uint DataCenterID, List<InfoProxyCommonList.CharacterData> Players)> GroupZonePlayersByDataCenter
    (
        InfoProxyCommonList* info,
        string               searchText
    ) =>
    [
        .. info->CharDataSpan.ToArray()
                             .Where(player => !IsPlayerSearchZone || player.Location == GameState.TerritoryType)
                             .Where(player => MatchesSearch(player.NameString, searchText))
                             .GroupBy(player => LuminaWrapper.GetWorldDC(player.HomeWorld))
                             .OrderBy(group => group.Key)
                             .Select
                             (group => (group.Key,
                                           group.OrderBy(player => player.HomeWorld)
                                                .ThenBy(player => player.NameString)
                                                .ToList())
                             )
    ];
    
    private static void NotifyPlayerPosition
    (
        IPlayerCharacter player
    )
    {
        using var rented = new RentedSeStringBuilder();

        rented.AppendIcon((uint)player.ClassJob.Value.ToBitmapFontIcon())
              .Append(ReadOnlySeString.CreatePlayer(player.Name, player.HomeWorld.RowId));

        NotifyHelper.Instance().Chat
        (
            Lang.GetSe
            (
                "AutoCountPlayers-Message-DetailedPosition",
                new Dictionary<string, object>
                {
                    ["playerLink"] = ReadOnlySeString.CreatePlayerLink(player.Name, player.HomeWorld.RowId, rented.ToReadOnlySeString()),
                    ["mapLink"]    = ReadOnlySeString.CreateMapLink(player.Position)
                }
            ),
            false
        );
    }
    
    private static bool MatchesSearch
    (
        string name,
        string searchText
    ) =>
        string.IsNullOrWhiteSpace(searchText) || name.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private class Config : ModuleConfig
    {
        public bool DisplayLineWhenTargetingMe = true;

        public bool  FilterFriend;
        public float ScaleFactor = 1;

        public List<TargetingRecord> TargetingHistories = [];
    }

    private class TargetingRecord
    {
        public string   Name        { get; set; } = string.Empty;
        public uint     HomeWorldID { get; set; }
        public uint     JobID       { get; set; }
        public uint     ZoneID      { get; set; }
        public DateTime StartTime   { get; set; }
        public TimeSpan Duration    { get; set; }
    }

    #region 常量

    private static readonly TerritoryIntendedUse[] ContentMemberListValidZones =
    [
        TerritoryIntendedUse.OccultCrescent,
        TerritoryIntendedUse.Bozja,
        TerritoryIntendedUse.Eureka
    ];

    #endregion
}
