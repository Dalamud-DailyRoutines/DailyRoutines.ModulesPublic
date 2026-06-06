using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using System.Numerics;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideGameObjects : ModuleBase
{
    private const float NEARBY_PLAYER_RANGE_HYSTERESIS = 1.5f;
    private const int   TARGETING_ME_PLAYER_KEEP_MS    = 60_000;
    private const int   RECENT_TARGET_PLAYER_KEEP_MS   = 30_000;
    private const int   RECENT_CHAT_PLAYER_KEEP_MS     = 300_000;
    private const byte  RECRUITING_ONLINE_STATUS_ID    = 26;  // 队员招募中
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHideGameObjectsTitle"),
        Description = Lang.Get("AutoHideGameObjectsDescription"),
        Category    = ModuleCategory.System
    };

    private static readonly CompSig                          UpdateObjectArraysSig = new("40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB");
    private delegate        void*                            UpdateObjectArraysDelegate(GameObjectManager* objectManager);
    private                 Hook<UpdateObjectArraysDelegate> UpdateObjectArraysHook;

    private Config config = null!;
    private bool showKeepRuleDebug;

    private readonly Lock                                 recentChatPlayersLock     = new();
    private readonly Dictionary<nint, HiddenObjectRecord> processedObjects          = [];
    private readonly Dictionary<ulong, long>              targetingMePlayers        = [];
    private readonly Dictionary<ulong, long>              recentTargetPlayers       = [];
    private readonly Dictionary<string, long>             recentChatPlayers         = [];
    private readonly HashSet<ulong>                       nearbyKeptPlayers         = [];
    private readonly HashSet<nint>                        objectsHiddenThisScan     = [];
    private readonly HashSet<ulong>                       nearbyPlayersSeenThisScan = [];

    private int zoneUpdateCount;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };
        config = Config.Load(this) ?? new();
        MigrateConfig();

        UpdateObjectArraysHook ??= UpdateObjectArraysSig.GetHook<UpdateObjectArraysDelegate>(UpdateObjectArraysDetour);
        UpdateObjectArraysHook.Enable();

        UpdateAllObjects(GameObjectManager.Instance());

        DService.Instance().Chat.ChatMessage += OnChatMessage;
        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().Chat.ChatMessage -= OnChatMessage;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        ResetAllObjects();
    }

    protected override void ConfigUI()
    {
        var defaultConfig = config.DefaultConfig;
        var changed = false;

        static bool Checkbox(string labelKey, ref bool value, string helpKey)
        {
            var itemChanged = ImGui.Checkbox(Lang.Get(labelKey), ref value);
            ImGuiOm.TooltipHover(Lang.Get(helpKey));
            return itemChanged;
        }

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Default"));

        using (ImRaii.PushId("Default"))
        using (ImRaii.PushIndent())
        {
            changed |= Checkbox("AutoHideGameObjects-HidePlayer",
                                ref defaultConfig.HidePlayer,
                                "AutoHideGameObjects-HidePlayerHelp");

            if (defaultConfig.HidePlayer)
            {
                using (ImRaii.PushIndent())
                {
                    changed |= Checkbox("AutoHideGameObjects-KeepRecruitingPlayers",
                                        ref defaultConfig.KeepRecruitingPlayers,
                                        "AutoHideGameObjects-KeepRecruitingPlayersHelp");

                    changed |= Checkbox("AutoHideGameObjects-KeepNearbyPlayers",
                                        ref defaultConfig.KeepNearbyPlayers,
                                        "AutoHideGameObjects-KeepNearbyPlayersHelp");

                    if (defaultConfig.KeepNearbyPlayers)
                    {
                        using var nearbyIndent = ImRaii.PushIndent();

                        ImGui.SetNextItemWidth(160f);
                        if (ImGui.SliderFloat
                            (
                                $"{Lang.Get("AutoHideGameObjects-KeepNearbyPlayersRange")}###AutoHideGameObjectsKeepNearbyPlayersRange",
                                ref defaultConfig.KeepNearbyPlayersRange,
                                1f,
                                50f,
                                "%.1f"
                            ))
                            defaultConfig.KeepNearbyPlayersRange =
                                Math.Clamp(defaultConfig.KeepNearbyPlayersRange, 1f, 50f);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            changed = true;
                    }

                    changed |= Checkbox("AutoHideGameObjects-KeepTargetAndFocusPlayers",
                                        ref defaultConfig.KeepTargetAndFocusPlayers,
                                        "AutoHideGameObjects-KeepTargetAndFocusPlayersHelp");

                    changed |= Checkbox("AutoHideGameObjects-KeepPlayersTargetingMe",
                                        ref defaultConfig.KeepPlayersTargetingMe,
                                        "AutoHideGameObjects-KeepPlayersTargetingMeHelp");

                    changed |= Checkbox("AutoHideGameObjects-KeepRecentChatPlayers",
                                        ref defaultConfig.KeepRecentChatPlayers,
                                        "AutoHideGameObjects-KeepRecentChatPlayersHelp");

                    ImGui.Checkbox("Debug###AutoHideGameObjectsShowKeepRuleDebug", ref showKeepRuleDebug);

                    if (showKeepRuleDebug)
                        DrawKeepRuleDebugUI();
                }
            }

            changed |= Checkbox("AutoHideGameObjects-HideUnimportantENPC",
                                ref defaultConfig.HideUnimportantENPC,
                                "AutoHideGameObjects-HideUnimportantENPCHelp");

            changed |= Checkbox("AutoHideGameObjects-HidePet",
                                ref defaultConfig.HidePet,
                                "AutoHideGameObjects-HidePetHelp");

            changed |= Checkbox("AutoHideGameObjects-HideCompanion",
                                ref defaultConfig.HideCompanion,
                                "AutoHideGameObjects-HideCompanionHelp");

            changed |= Checkbox("AutoHideGameObjects-HideOrnament",
                                ref defaultConfig.HideOrnament,
                                "AutoHideGameObjects-HideOrnamentHelp");

            changed |= Checkbox("AutoHideGameObjects-HideChocobo",
                                ref defaultConfig.HideChocobo,
                                "AutoHideGameObjects-HideChocoboHelp");
        }

        if (changed)
            SaveConfigAndRefresh();
    }

    // 调试面板
    private void DrawKeepRuleDebugUI()
    {
        ImGui.TextUnformatted
        (
            $"processedObjects={processedObjects.Count}, " +
            $"objectsHiddenThisScan={objectsHiddenThisScan.Count}, " +
            $"nearbyKeptPlayers={nearbyKeptPlayers.Count}, " +
            $"recentTargetPlayers={recentTargetPlayers.Count}, " +
            $"targetingMePlayers={targetingMePlayers.Count}, " +
            $"recentChatPlayers={GetRecentChatPlayerCount()}"
        );

        var tableSize = new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 12f);
        using var table = ImRaii.Table
        (
            "###AutoHideGameObjectsKeepRuleDebugTable",
            6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            tableSize
        );

        if (!table)
            return;

        ImGui.TableSetupColumn("Rule",     ImGuiTableColumnFlags.WidthFixed,   120f);
        ImGui.TableSetupColumn("Player",   ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("PlayerID", ImGuiTableColumnFlags.WidthFixed,   150f);
        ImGui.TableSetupColumn("Remain",   ImGuiTableColumnFlags.WidthFixed,   70f);
        ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed,   70f);
        ImGui.TableSetupColumn("TargetID", ImGuiTableColumnFlags.WidthFixed,   150f);
        ImGui.TableHeadersRow();

        var now = Environment.TickCount64;

        foreach (var (playerID, expireTime) in recentTargetPlayers.OrderBy(x => x.Value))
            DrawKeepRuleDebugRow("Target/Focus", playerID, expireTime, now);

        foreach (var (playerID, expireTime) in targetingMePlayers.OrderBy(x => x.Value))
            DrawKeepRuleDebugRow("Targeting me", playerID, expireTime, now);

        foreach (var playerID in nearbyKeptPlayers.Order())
            DrawKeepRuleDebugRow("Nearby", playerID, null, now);

        foreach (var (playerName, expireTime) in GetRecentChatPlayerSnapshot().OrderBy(x => x.Value))
            DrawRecentChatPlayerDebugRow(playerName, expireTime, now);

        if (config.DefaultConfig.KeepRecruitingPlayers)
            DrawRecruitingPlayersDebugRows();
    }

    private static void DrawRecentChatPlayerDebugRow(string playerName, long expireTime, long now)
    {
        TryFindPlayerByName(playerName, out var player);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted("Recent chat");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugPlayerName(player, playerName));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(player != null ? GetPlayerTrackingID(player).ToString() : "-");

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugRemainTimeText(expireTime, now));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugDistanceText(player));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugTargetIDText(player));
    }

    private void DrawRecruitingPlayersDebugRows()
    {
        var manager = GameObjectManager.Instance();
        if (manager == null)
            return;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 200)
                break;

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (!IsPlayerObject(gameObject, (uint)index))
                continue;

            var player = (BattleChara*)gameObject;
            if (player->OnlineStatus != RECRUITING_ONLINE_STATUS_ID)
                continue;

            var playerID = GetPlayerTrackingID(player);
            if (playerID != 0)
                DrawKeepRuleDebugRow("Recruiting", playerID, null, Environment.TickCount64);
        }
    }

    private static void DrawKeepRuleDebugRow(string rule, ulong playerID, long? expireTime, long now)
    {
        TryFindPlayerByTrackingID(playerID, out var player);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(rule);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugPlayerName(player));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(playerID.ToString());

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugRemainTimeText(expireTime, now));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugDistanceText(player));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetDebugTargetIDText(player));
    }

    private void* UpdateObjectArraysDetour(GameObjectManager* objectManager)
    {
        var orig = UpdateObjectArraysHook.Original(objectManager);
        UpdateAllObjects(objectManager);
        return orig;
    }

    private void MigrateConfig()
    {
        if (config.SplitCompanionOrnamentConfig)
            return;

        config.DefaultConfig.HideCompanion = config.DefaultConfig.HidePet;
        config.DefaultConfig.HideOrnament  = config.DefaultConfig.HidePet;
        config.SplitCompanionOrnamentConfig = true;
        config.Save(this);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!config.DefaultConfig.KeepRecentChatPlayers || !IsPlayerChatOrEmote(message.LogKind))
            return;

        var playerNames = GetPlayerNamesFromChatMessage(message);
        if (playerNames.Count == 0)
            return;

        var expireTime = Environment.TickCount64 + RECENT_CHAT_PLAYER_KEEP_MS;
        lock (recentChatPlayersLock)
        {
            foreach (var playerName in playerNames)
                recentChatPlayers[playerName] = expireTime;
        }
    }

    private static HashSet<string> GetPlayerNamesFromChatMessage(IHandleableChatMessage message)
    {
        var playerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPlayerPayloadNames(playerNames, message.Sender);
        AddPlayerPayloadNames(playerNames, message.Message);

        // 有些频道/表情文本不一定带 PlayerPayload；兜底从当前对象表里按完整角色名反查。
        AddMentionedVisiblePlayerNames(playerNames, $"{message.Sender.TextValue}\n{message.Message.TextValue}");

        return playerNames;
    }

    private static void AddPlayerPayloadNames(HashSet<string> playerNames, SeString text)
    {
        foreach (var payload in text.Payloads.OfType<PlayerPayload>())
            AddRecentChatPlayerName(playerNames, payload.PlayerName);
    }

    private void UpdateAllObjects(GameObjectManager* manager)
    {
        if (manager == null) return;

        if (!GameState.IsLoggedIn)
        {
            ClearProcessedObjects();
            return;
        }

        if (!IsActiveTerritory())
        {
            ResetAllObjects();
            return;
        }

        var isOccultCrescent = GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent;
        if (!isOccultCrescent && !HasActiveFilter(config.DefaultConfig))
        {
            ResetAllObjects();
            return;
        }

        var occultCrescentPlayerCount = 0;
        var targetAddress             = TargetManager.Target?.Address ?? nint.Zero;
        var nearbyPlayersSeen         = BeginObjectScan(isOccultCrescent);

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 629)
                break;

            if (index is > 200 and < 489)
            {
                index = 488;
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;

            TrackNearbyPlayerSeen(gameObject, (uint)index, nearbyPlayersSeen);

            if (!ShouldHideObject(gameObject, (uint)index, isOccultCrescent, targetAddress, ref occultCrescentPlayerCount))
            {
                RestoreObjectIfProcessed(gameObject, isOccultCrescent);
                continue;
            }

            HideObject(gameObject);
        }

        FinishObjectScan(manager, nearbyPlayersSeen, isOccultCrescent);
    }

    private HashSet<ulong>? BeginObjectScan(bool isOccultCrescent)
    {
        objectsHiddenThisScan.Clear();

        if (isOccultCrescent || !config.DefaultConfig.HidePlayer || !config.DefaultConfig.KeepNearbyPlayers)
            return null;

        nearbyPlayersSeenThisScan.Clear();
        return nearbyPlayersSeenThisScan;
    }

    private bool ShouldHideObject(GameObject* gameObject, uint index, bool isOccultCrescent, nint targetAddress, ref int occultCrescentPlayerCount)
    {
        if (isOccultCrescent)
            return ShouldFilterOccultCrescent(gameObject, targetAddress, ref occultCrescentPlayerCount, index);

        var wasHiddenByThisModule = IsProcessedObject(gameObject);
        return ShouldFilter(config.DefaultConfig, gameObject, index, wasHiddenByThisModule);
    }

    private void FinishObjectScan(GameObjectManager* manager, HashSet<ulong>? nearbyPlayersSeen, bool isOccultCrescent)
    {
        if (isOccultCrescent)
            return;

        RestoreStaleProcessedObjects(manager, objectsHiddenThisScan);
        RemoveStaleNearbyKeptPlayers(nearbyPlayersSeen);
    }

    private static bool IsActiveTerritory()
    {
        if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
        {
            // 在两歧塔里
            return (LocalPlayerState.Object?.Position.Y ?? -100) >= 0;
        }

        return GameState.ContentFinderCondition == 0 &&
               !GameState.IsInPVPArea                &&
               GameState.TerritoryIntendedUse   != TerritoryIntendedUse.IslandSanctuary;
    }

    private static bool HasActiveFilter(FilterConfig config) =>
        config.HidePlayer ||
        config.HidePet ||
        config.HideCompanion ||
        config.HideOrnament ||
        config.HideChocobo ||
        config.HideUnimportantENPC;

    private bool ShouldFilter(FilterConfig config, GameObject* gameObject, uint index, bool isProcessed)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (!isProcessed && ((RenderFlag)gameObject->RenderFlags).IsSet(RenderFlag.Invisible)) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        // 玩家
        if (config.HidePlayer                 &&
            IsPlayerObject(gameObject, index) &&
            !ShouldKeepPlayerVisible(config, (BattleChara*)gameObject))
            return true;

        // 非战斗宠物
        if (config.HideCompanion                         &&
            IsOddObjectSlot(index)                       &&
            gameObject->ObjectKind == ObjectKind.Companion &&
            gameObject->OwnerId    != LocalPlayerState.EntityID)
            return true;

        // 时尚配饰
        if (config.HideOrnament                         &&
            IsOddObjectSlot(index)                      &&
            gameObject->ObjectKind == ObjectKind.Ornament &&
            gameObject->OwnerId    != LocalPlayerState.EntityID)
            return true;

        // 战斗召唤物
        if (config.HidePet                                                &&
            IsBattleNPCInEvenObjectSlot(gameObject, index)                &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet &&
            gameObject->OwnerId                   != LocalPlayerState.EntityID)
            return true;

        // 陆行鸟
        if (config.HideChocobo                                              &&
            IsBattleNPCInEvenObjectSlot(gameObject, index)                  &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Buddy &&
            gameObject->OwnerId                   != LocalPlayerState.EntityID)
            return true;

        // 不重要 NPC
        if (config.HideUnimportantENPC && IsUnimportantEventNPC(gameObject, index))
            return true;

        return false;
    }

    private bool ShouldFilterOccultCrescent(GameObject* gameObject, nint targetAddress, ref int playerCount, uint index)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        if (IsPlayerObject(gameObject, index))
        {
            var player = (BattleChara*)gameObject;

            playerCount++;

            if (player->IsDead() || (nint)gameObject == targetAddress)
            {
                gameObject->RenderFlags &= ~(VisibilityFlags)256;
                processedObjects.Remove((nint)gameObject);
                return false;
            }

            if (player->IsFriend)
                return false;

            if (LocalPlayerState.IsInParty &&
                (player->IsPartyMember || player->IsAllianceMember))
                return false;

            return playerCount >= 10;
        }

        if (IsUnimportantEventNPC(gameObject, index))
            return true;

        if (IsBattleNPCInEvenObjectSlot(gameObject, index) && IsOwnedByOtherPlayer(gameObject))
            return true;

        return false;
    }

    private bool ShouldKeepPlayerVisible(FilterConfig config, BattleChara* player)
    {
        return player->IsFriend                                      ||
               (LocalPlayerState.IsInParty &&
                (player->IsPartyMember || player->IsAllianceMember)) ||
               IsTargetOrFocusPlayerKept(config, player)             ||
               IsTargetingMePlayerKept(config, player)               ||
               IsRecruitingPlayer(config, player)                    ||
               IsRecentChatPlayerKept(config, player)                ||
               IsNearbyPlayerKept(config, player);
    }

    private bool IsTargetOrFocusPlayerKept(FilterConfig config, BattleChara* player)
    {
        if (!config.KeepTargetAndFocusPlayers || player == null)
            return false;

        var playerID = GetPlayerTrackingID(player);
        if (playerID == 0)
            return false;

        var address = (nint)player;
        if (address == (TargetManager.Target?.Address ?? nint.Zero) ||
            address == (TargetManager.FocusTarget?.Address ?? nint.Zero))
        {
            // 当前目标/焦点立即保留，并在取消目标后继续保留一小段时间。
            recentTargetPlayers[playerID] = Environment.TickCount64 + RECENT_TARGET_PLAYER_KEEP_MS;
            return true;
        }

        if (!recentTargetPlayers.TryGetValue(playerID, out var expireTime))
            return false;

        if (expireTime > Environment.TickCount64)
            return true;

        recentTargetPlayers.Remove(playerID);
        return false;
    }

    private bool IsTargetingMePlayerKept(FilterConfig config, BattleChara* player)
    {
        if (!config.KeepPlayersTargetingMe || player == null)
            return false;

        var playerID = GetPlayerTrackingID(player);
        if (playerID == 0)
            return false;

        var localPlayerID = GetLocalPlayerGameObjectID();
        if (localPlayerID == 0)
            return false;

        var now = Environment.TickCount64;
        if ((ulong)player->GetTargetId() == localPlayerID)
        {
            // 发现对方当前以我为目标后，记录一个过期时间；过期前都保持可见。
            targetingMePlayers[playerID] = now + TARGETING_ME_PLAYER_KEEP_MS;
            return true;
        }

        if (!targetingMePlayers.TryGetValue(playerID, out var expireTime))
            return false;

        if (expireTime > now)
            return true;

        targetingMePlayers.Remove(playerID);
        return false;
    }

    private static bool IsRecruitingPlayer(FilterConfig config, BattleChara* player) =>
        config.KeepRecruitingPlayers && player != null && player->OnlineStatus == RECRUITING_ONLINE_STATUS_ID;

    private bool IsRecentChatPlayerKept(FilterConfig config, BattleChara* player)
    {
        if (!config.KeepRecentChatPlayers || player == null)
            return false;

        var playerName = NormalizePlayerName(((GameObject*)player)->NameString);
        if (string.IsNullOrWhiteSpace(playerName))
            return false;

        lock (recentChatPlayersLock)
        {
            if (!recentChatPlayers.TryGetValue(playerName, out var expireTime))
                return false;

            if (expireTime > Environment.TickCount64)
                return true;

            recentChatPlayers.Remove(playerName);
            return false;
        }
    }

    private bool IsNearbyPlayerKept(FilterConfig config, BattleChara* player)
    {
        if (!config.KeepNearbyPlayers || player == null)
            return false;

        var localPlayer = LocalPlayerState.Object;
        if (localPlayer == null)
            return false;

        var playerID = GetPlayerTrackingID(player);
        if (playerID == 0)
            return false;

        var range    = Math.Clamp(config.KeepNearbyPlayersRange, 1f, 50f);
        var distance = LocalPlayerState.DistanceTo3D(player->Position);

        if (nearbyKeptPlayers.Contains(playerID))
        {
            // 已经因“身边范围”保留的玩家，用稍大的退出半径防止边界来回闪烁。
            var exitRange = range + NEARBY_PLAYER_RANGE_HYSTERESIS;
            if (distance <= exitRange)
                return true;

            nearbyKeptPlayers.Remove(playerID);
            return false;
        }

        if (distance > range)
            return false;

        nearbyKeptPlayers.Add(playerID);
        return true;
    }

    private static ulong GetPlayerTrackingID(BattleChara* player)
    {
        if (player == null)
            return 0;

        return player->ContentId != 0
                   ? player->ContentId
                   : (ulong)((GameObject*)player)->GetGameObjectId();
    }

    private static ulong GetLocalPlayerGameObjectID() =>
        LocalPlayerState.Object?.GameObjectID ?? 0;

    private static bool IsPlayerChatOrEmote(XivChatType chatType) =>
        chatType is XivChatType.Say             or
                    XivChatType.Yell            or
                    XivChatType.Shout           or
                    XivChatType.TellIncoming    or
                    XivChatType.Party           or
                    XivChatType.CrossParty      or
                    XivChatType.Alliance        or
                    XivChatType.FreeCompany     or
                    XivChatType.PvPTeam         or
                    XivChatType.NoviceNetwork   or
                    XivChatType.CrossLinkShell1 or
                    XivChatType.CrossLinkShell2 or
                    XivChatType.CrossLinkShell3 or
                    XivChatType.CrossLinkShell4 or
                    XivChatType.CrossLinkShell5 or
                    XivChatType.CrossLinkShell6 or
                    XivChatType.CrossLinkShell7 or
                    XivChatType.CrossLinkShell8 or
                    XivChatType.Ls1             or
                    XivChatType.Ls2             or
                    XivChatType.Ls3             or
                    XivChatType.Ls4             or
                    XivChatType.Ls5             or
                    XivChatType.Ls6             or
                    XivChatType.Ls7             or
                    XivChatType.Ls8             ||
        chatType.ToString().Contains("Emote", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        return string.Join(' ', playerName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AddRecentChatPlayerName(HashSet<string> playerNames, string playerName)
    {
        var normalizedName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedName))
            playerNames.Add(normalizedName);
    }

    private static void AddMentionedVisiblePlayerNames(HashSet<string> playerNames, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var manager = GameObjectManager.Instance();
        if (manager == null)
            return;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 200)
                break;

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (!IsPlayerObject(gameObject, (uint)index))
                continue;

            var playerName = NormalizePlayerName(gameObject->NameString);
            if (string.IsNullOrWhiteSpace(playerName) ||
                !text.Contains(playerName, StringComparison.OrdinalIgnoreCase))
                continue;

            playerNames.Add(playerName);
        }
    }

    private int GetRecentChatPlayerCount()
    {
        lock (recentChatPlayersLock)
            return recentChatPlayers.Count;
    }

    private KeyValuePair<string, long>[] GetRecentChatPlayerSnapshot()
    {
        lock (recentChatPlayersLock)
            return recentChatPlayers.ToArray();
    }

    private static bool TryFindPlayerByTrackingID(ulong playerID, out BattleChara* player)
    {
        player = null;

        var manager = GameObjectManager.Instance();
        if (manager == null || playerID == 0)
            return false;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 200)
                break;

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (!IsPlayerObject(gameObject, (uint)index))
                continue;

            var currentPlayer = (BattleChara*)gameObject;
            if (GetPlayerTrackingID(currentPlayer) != playerID)
                continue;

            player = currentPlayer;
            return true;
        }

        return false;
    }

    private static bool TryFindPlayerByName(string playerName, out BattleChara* player)
    {
        player = null;

        var normalizedName = NormalizePlayerName(playerName);
        var manager = GameObjectManager.Instance();
        if (manager == null || string.IsNullOrWhiteSpace(normalizedName))
            return false;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (index > 200)
                break;

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (!IsPlayerObject(gameObject, (uint)index))
                continue;

            var currentPlayer = (BattleChara*)gameObject;
            var currentPlayerName = NormalizePlayerName(((GameObject*)currentPlayer)->NameString);
            if (!PlayerNameEquals(normalizedName, currentPlayerName))
                continue;

            player = currentPlayer;
            return true;
        }

        return false;
    }

    private static string GetDebugPlayerName(BattleChara* player) =>
        player == null || string.IsNullOrWhiteSpace(((GameObject*)player)->NameString)
            ? "(not in object table)"
            : ((GameObject*)player)->NameString;

    private static string GetDebugPlayerName(BattleChara* player, string fallbackName) =>
        player == null || string.IsNullOrWhiteSpace(((GameObject*)player)->NameString)
            ? $"{fallbackName} (not in object table)"
            : ((GameObject*)player)->NameString;

    private static bool PlayerNameEquals(string candidateName, string objectName)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || string.IsNullOrWhiteSpace(objectName))
            return false;

        return candidateName.Equals(objectName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDebugRemainTimeText(long? expireTime, long now)
    {
        if (expireTime == null)
            return "-";

        var remainMs = Math.Max(0, expireTime.Value - now);
        return $"{remainMs / 1000f:F1}s";
    }

    private static string GetDebugDistanceText(BattleChara* player) =>
        player == null
            ? "-"
            : $"{LocalPlayerState.DistanceTo3D(player->Position):F1}m";

    private static string GetDebugTargetIDText(BattleChara* player)
    {
        if (player == null)
            return "-";

        var targetID      = (ulong)player->GetTargetId();
        var localPlayerID = GetLocalPlayerGameObjectID();
        if (targetID == 0 || targetID == 0xE0000000)
            return "No target";

        return localPlayerID != 0 && targetID == localPlayerID
                   ? $"{targetID} (me)"
                   : targetID.ToString();
    }

    private static bool IsPlayerObject(GameObject* gameObject, uint index) =>
        gameObject             != null &&
        IsEvenObjectSlot(index)        &&
        gameObject->ObjectKind == ObjectKind.Pc;

    private static bool IsBattleNPCInEvenObjectSlot(GameObject* gameObject, uint index) =>
        gameObject             != null &&
        IsEvenObjectSlot(index)        &&
        gameObject->ObjectKind == ObjectKind.BattleNpc;

    private static bool IsEvenObjectSlot(uint index) =>
        index <= 200 && index % 2 == 0;

    private static bool IsOddObjectSlot(uint index) =>
        index <= 200 && index % 2 == 1;

    private static bool IsUnimportantEventNPC(GameObject* gameObject, uint index) =>
        gameObject != null                                                      &&
        index is >= 489 and <= 629                                              &&
        !gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.IsTargetable) &&
        gameObject->EventHandler == null;

    private static bool IsOwnedByOtherPlayer(GameObject* gameObject) =>
        gameObject          != null                      &&
        gameObject->OwnerId != LocalPlayerState.EntityID &&
        gameObject->OwnerId != 0                         &&
        gameObject->OwnerId != 0xE0000000;

    private static void TrackNearbyPlayerSeen(GameObject* gameObject, uint index, HashSet<ulong>? nearbyPlayersSeen)
    {
        if (nearbyPlayersSeen == null || !IsPlayerObject(gameObject, index))
            return;

        var playerID = GetPlayerTrackingID((BattleChara*)gameObject);
        if (playerID != 0)
            nearbyPlayersSeen.Add(playerID);
    }

    private void SaveConfigAndRefresh()
    {
        config.Save(this);
        ResetAllObjects();
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private void HideObject(GameObject* gameObject)
    {
        if (gameObject == null) return;

        var address = (nint)gameObject;
        if (address == nint.Zero)
            return;

        gameObject->RenderFlags |= (VisibilityFlags)256;
        processedObjects[address] = HiddenObjectRecord.From(gameObject);
        objectsHiddenThisScan.Add(address);
    }

    private bool IsProcessedObject(GameObject* gameObject)
    {
        if (gameObject == null) return false;

        var address = (nint)gameObject;
        if (!processedObjects.TryGetValue(address, out var record))
            return false;

        if (record.IsSameObject(gameObject))
            return true;

        processedObjects.Remove(address);
        return false;
    }

    private void RestoreObjectIfProcessed(GameObject* gameObject, bool isOccultCrescent = false)
    {
        if (isOccultCrescent)
            return;

        if (gameObject == null) return;

        var address = (nint)gameObject;
        if (!processedObjects.TryGetValue(address, out var record))
            return;

        processedObjects.Remove(address);

        if (!record.IsSameObject(gameObject))
            return;

        gameObject->RenderFlags &= ~(VisibilityFlags)256;
    }

    private void RestoreStaleProcessedObjects(GameObjectManager* manager, HashSet<nint> objectsHiddenThisScan)
    {
        if (processedObjects.Count == 0) return;

        foreach (var address in processedObjects.Keys.ToArray())
        {
            if (objectsHiddenThisScan.Contains(address))
                continue;

            // 本轮没再命中隐藏条件的旧对象，如果仍能在对象表里找到同一实例，就恢复可见。
            if (TryFindObject(manager, address, processedObjects[address], out var gameObject))
                gameObject->RenderFlags &= ~(VisibilityFlags)256;

            processedObjects.Remove(address);
        }
    }

    private static bool TryFindObject(GameObjectManager* manager, nint address, HiddenObjectRecord record, out GameObject* gameObject)
    {
        gameObject = null;
        if (manager == null || address == nint.Zero)
            return false;

        foreach (ref var entry in manager->Objects.IndexSorted)
        {
            if ((nint)entry.Value != address || !record.IsSameObject(entry.Value))
                continue;

            gameObject = entry.Value;
            return true;
        }

        return false;
    }

    private void RemoveStaleNearbyKeptPlayers(HashSet<ulong>? nearbyPlayersSeen)
    {
        if (nearbyKeptPlayers.Count == 0)
            return;

        if (nearbyPlayersSeen == null)
        {
            nearbyKeptPlayers.Clear();
            return;
        }

        foreach (var playerID in nearbyKeptPlayers.ToArray())
        {
            // 玩家不在本轮对象表里时，移除“曾经在身边”的缓存，避免下次复用旧状态。
            if (!nearbyPlayersSeen.Contains(playerID))
                nearbyKeptPlayers.Remove(playerID);
        }
    }

    // 忘记所有运行期状态
    private void ClearProcessedObjects()
    {
        processedObjects.Clear();
        nearbyKeptPlayers.Clear();
        targetingMePlayers.Clear();
        recentTargetPlayers.Clear();
        ClearRecentChatPlayers();
    }

    // 尽量把所有本模块隐藏过的对象恢复显示，然后忘记所有运行期状态
    private void ResetAllObjects()
    {
        if (processedObjects.Count == 0)
        {
            ClearProcessedObjects();
            return;
        }

        if (!DService.Instance().ClientState.IsLoggedIn)
        {
            ClearProcessedObjects();
            return;
        }

        var manager = GameObjectManager.Instance();
        if (manager == null)
        {
            ClearProcessedObjects();
            return;
        }

        foreach (ref var entry in manager->Objects.IndexSorted)
        {
            if (entry.Value       == null                                            ||
                (nint)entry.Value == (LocalPlayerState.Object?.Address ?? nint.Zero) ||
                !processedObjects.TryGetValue((nint)entry.Value, out var record)     ||
                !record.IsSameObject(entry.Value))
                continue;

            entry.Value->RenderFlags &= ~(VisibilityFlags)256;
        }

        ClearProcessedObjects();
        zoneUpdateCount = 0;
    }

    private void OnZoneChanged(uint u)
    {
        zoneUpdateCount = 0;
        ResetAllObjects();
    }

    private void OnUpdate(IFramework _)
    {
        if (DService.Instance().Condition.IsBetweenAreas) return;

        PruneExpiredPlayerRecords(targetingMePlayers);
        PruneExpiredPlayerRecords(recentTargetPlayers);
        PruneExpiredRecentChatPlayers();

        // 主要是小区域更新不及时；新增的动态保留规则开启时才需要持续刷新。
        if (!NeedsContinuousRefresh() && zoneUpdateCount > 3) return;

        zoneUpdateCount++;
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private bool NeedsContinuousRefresh()
    {
        var filterConfig = config.DefaultConfig;

        // 这些规则依赖会随时间变化的数据，所以不能只靠切区后的少量补扫。
        return filterConfig.HidePlayer &&
               (filterConfig.KeepRecruitingPlayers     ||
                filterConfig.KeepNearbyPlayers         ||
                filterConfig.KeepTargetAndFocusPlayers ||
                filterConfig.KeepPlayersTargetingMe    ||
                filterConfig.KeepRecentChatPlayers);
    }

    private static void PruneExpiredPlayerRecords(Dictionary<ulong, long> playerRecords)
    {
        if (playerRecords.Count == 0)
            return;

        var now = Environment.TickCount64;
        foreach (var playerID in playerRecords.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            playerRecords.Remove(playerID);
    }

    private void PruneExpiredRecentChatPlayers()
    {
        lock (recentChatPlayersLock)
        {
            if (recentChatPlayers.Count == 0)
                return;

            var now = Environment.TickCount64;
            foreach (var playerName in recentChatPlayers.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                recentChatPlayers.Remove(playerName);
        }
    }

    private void ClearRecentChatPlayers()
    {
        lock (recentChatPlayersLock)
            recentChatPlayers.Clear();
    }

    private class Config : ModuleConfig
    {
        public FilterConfig DefaultConfig = new();

        // 兼容旧配置: 拆分非战斗宠物/时尚配饰开关前, 它们跟随 HidePet。
        public bool SplitCompanionOrnamentConfig;
    }

    private class FilterConfig
    {
        // 陆行鸟
        public bool HideChocobo = true;

        // 宠物
        public bool HidePet = true;

        // 非战斗宠物
        public bool HideCompanion = true;

        // 时尚配饰
        public bool HideOrnament = true;

        // 玩家
        public bool HidePlayer = true;

        // 保留正在队员招募中的玩家
        public bool KeepRecruitingPlayers = true;

        // 保留一定距离内的玩家
        public bool KeepNearbyPlayers;

        public float KeepNearbyPlayersRange = 5f;

        // 保留自己的目标和焦点目标
        public bool KeepTargetAndFocusPlayers = true;

        // 保留一段时间内以自己为目标的玩家
        public bool KeepPlayersTargetingMe = true;

        // 保留最近发言或发送表情的玩家
        public bool KeepRecentChatPlayers = true;

        // 不重要 NPC
        public bool HideUnimportantENPC = true;
    }

    private readonly record struct HiddenObjectRecord(ulong GameObjectID, uint EntityID)
    {
        public static HiddenObjectRecord From(GameObject* gameObject) =>
            new((ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject                           != null         &&
            (ulong)gameObject->GetGameObjectId() == GameObjectID &&
            gameObject->EntityId                 == EntityID;
    }

    private enum RenderFlag
    {
        Invisible = 256
    }
}
