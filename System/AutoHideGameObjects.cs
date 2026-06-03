using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using System.Numerics;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using Race = Lumina.Excel.Sheets.Race;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoHideGameObjects : ModuleBase
{
    private const byte MIN_RACE = 1;
    private const byte MAX_RACE = 8;
    private const byte MALE_SEX = 0;
    private const byte FEMALE_SEX = 1;
    private const float NEARBY_PLAYER_RANGE_HYSTERESIS = 1.5f;
    private const int TARGETING_ME_PLAYER_KEEP_MS = 60_000;
    private const int RECENT_TARGET_PLAYER_KEEP_MS = 30_000;
    private const int OCCULT_VISIBLE_PLAYER_LIMIT = 10;
    private const byte RECRUITING_ONLINE_STATUS_ID = 26; // OnlineStatus RowId: 队员招募中

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

    private readonly Dictionary<nint, HiddenObjectRecord> processedObjects = [];
    private readonly Dictionary<ulong, long> targetingMePlayers = [];
    private readonly Dictionary<ulong, long> recentTargetPlayers = [];
    private readonly HashSet<ulong> nearbyKeptPlayers = [];
    private readonly HashSet<nint> seenProcessedObjects = [];
    private readonly HashSet<ulong> seenNearbyPlayers = [];
    private bool isUpdatingObjects;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeoutMS = 30_000 };
        config =   Config.Load(this) ?? new();

        UpdateObjectArraysHook ??= UpdateObjectArraysSig.GetHook<UpdateObjectArraysDelegate>(UpdateObjectArraysDetour);
        UpdateObjectArraysHook.Enable();

        UpdateAllObjects(GameObjectManager.Instance());

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
    }

    protected override void Uninit()
    {
        FrameworkManager.Instance().Unreg(OnUpdate);
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;

        ResetAllObjects();
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("Default"));

        using (ImRaii.PushId("Default"))
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HidePlayer"), ref config.DefaultConfig.HidePlayer))
                SaveConfigAndRefresh();
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HidePlayerHelp"));

            if (config.DefaultConfig.HidePlayer)
            {
                DrawRaceFilterUI(config.DefaultConfig);

                using (ImRaii.PushIndent())
                {
                    if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-KeepRecruitingPlayers"),
                                       ref config.DefaultConfig.KeepRecruitingPlayers))
                        SaveConfigAndRefresh();
                    ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-KeepRecruitingPlayersHelp"));

                    if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-KeepNearbyPlayers"),
                                       ref config.DefaultConfig.KeepNearbyPlayers))
                        SaveConfigAndRefresh();
                    ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-KeepNearbyPlayersHelp"));

                    if (config.DefaultConfig.KeepNearbyPlayers)
                    {
                        using var nearbyIndent = ImRaii.PushIndent();

                        ImGui.SetNextItemWidth(160f);
                        if (ImGui.SliderFloat
                            (
                                $"{Lang.Get("AutoHideGameObjects-KeepNearbyPlayersRange")}###AutoHideGameObjectsKeepNearbyPlayersRange",
                                ref config.DefaultConfig.KeepNearbyPlayersRange,
                                1f,
                                50f,
                                "%.1f"
                            ))
                            config.DefaultConfig.KeepNearbyPlayersRange =
                                Math.Clamp(config.DefaultConfig.KeepNearbyPlayersRange, 1f, 50f);

                        if (ImGui.IsItemDeactivatedAfterEdit())
                            SaveConfigAndRefresh();
                    }

                    if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-KeepTargetAndFocusPlayers"),
                                       ref config.DefaultConfig.KeepTargetAndFocusPlayers))
                        SaveConfigAndRefresh();
                    ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-KeepTargetAndFocusPlayersHelp"));

                    if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-KeepPlayersTargetingMe"),
                                       ref config.DefaultConfig.KeepPlayersTargetingMe))
                        SaveConfigAndRefresh();
                    ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-KeepPlayersTargetingMeHelp"));
                }
            }

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HideUnimportantENPC"), ref config.DefaultConfig.HideUnimportantENPC))
                SaveConfigAndRefresh();
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HideUnimportantENPCHelp"));

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HidePet"), ref config.DefaultConfig.HidePet))
                SaveConfigAndRefresh();
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HidePetHelp"));

            if (ImGui.Checkbox(Lang.Get("AutoHideGameObjects-HideChocobo"), ref config.DefaultConfig.HideChocobo))
                SaveConfigAndRefresh();
            ImGuiOm.TooltipHover(Lang.Get("AutoHideGameObjects-HideChocoboHelp"));
        }
    }

    private void DrawRaceFilterUI(FilterConfig filterConfig)
    {
        using var indent = ImRaii.PushIndent();

        ImGui.TextUnformatted(Lang.Get("AutoHideGameObjects-RaceFilter"));

        using (var combo = ImRaii.Combo("###AutoHideGameObjectsRaceFilterMode", GetRaceFilterModeName(filterConfig.RaceFilterMode)))
        {
            if (combo)
            {
                foreach (var mode in Enum.GetValues<RaceFilterMode>())
                {
                    if (!ImGui.Selectable(GetRaceFilterModeName(mode), filterConfig.RaceFilterMode == mode))
                        continue;

                    filterConfig.RaceFilterMode = mode;
                    SaveConfigAndRefresh();
                }
            }
        }

        if (filterConfig.RaceFilterMode == RaceFilterMode.Disabled)
            return;

        if (filterConfig.RaceFilterMode == RaceFilterMode.HideNotSelected &&
            filterConfig.RaceSexFilter.Count == 0)
            ImGui.TextColored(KnownColor.Orange.ToVector4(), Lang.Get("AutoHideGameObjects-RaceFilter-EmptyHideNotSelectedWarning"));

        using (ImRaii.PushId("RaceSexFilter"))
        {
            if (ImGui.SmallButton(Lang.Get("AutoHideGameObjects-RaceFilter-SelectAll")))
            {
                SetAllRaceSexFilters(filterConfig, true);
                SaveConfigAndRefresh();
            }

            ImGui.SameLine();

            if (ImGui.SmallButton(Lang.Get("AutoHideGameObjects-RaceFilter-Clear")))
            {
                filterConfig.RaceSexFilter.Clear();
                SaveConfigAndRefresh();
            }

            ImGui.SameLine();

            if (ImGui.SmallButton(Lang.Get("AutoHideGameObjects-RaceFilter-Invert")))
            {
                InvertRaceSexFilters(filterConfig);
                SaveConfigAndRefresh();
            }

            using var table = ImRaii.Table
            (
                "AutoHideGameObjectsRaceSexFilterTable",
                3,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
            );

            if (!table)
                return;

            ImGui.TableSetupColumn(Lang.Get("AutoHideGameObjects-RaceFilter-Race"), ImGuiTableColumnFlags.WidthFixed, 96f);
            ImGui.TableSetupColumn(GetSexName(MALE_SEX), ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableSetupColumn(GetSexName(FEMALE_SEX), ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableHeadersRow();

            for (var race = MIN_RACE; race <= MAX_RACE; race++)
            {
                var raceID = (byte)race;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetRaceName(raceID));

                DrawRaceSexFilterCell(filterConfig, raceID, MALE_SEX);
                DrawRaceSexFilterCell(filterConfig, raceID, FEMALE_SEX);
            }
        }
    }

    private void DrawRaceSexFilterCell(FilterConfig filterConfig, byte race, byte sex)
    {
        ImGui.TableNextColumn();

        var selected = filterConfig.RaceSexFilter.Contains(PackRaceSex(race, sex));
        if (!ImGui.Checkbox($"###AutoHideGameObjectsRaceSexFilter{race}_{sex}", ref selected))
            return;

        SetRaceSexFilter(filterConfig, race, sex, selected);
        SaveConfigAndRefresh();
    }

    private void* UpdateObjectArraysDetour(GameObjectManager* objectManager)
    {
        var orig = UpdateObjectArraysHook.Original(objectManager);
        UpdateAllObjects(objectManager);
        return orig;
    }

    private void UpdateAllObjects(GameObjectManager* manager)
    {
        if (manager == null) return;
        if (isUpdatingObjects) return;

        isUpdatingObjects = true;
        try
        {
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

            if (!HasActiveFilter(config.DefaultConfig))
            {
                ResetAllObjects();
                return;
            }

            var playerCount = 0;
            seenProcessedObjects.Clear();
            HashSet<ulong>? currentSeenNearbyPlayers = null;
            if (config.DefaultConfig.HidePlayer && config.DefaultConfig.KeepNearbyPlayers)
            {
                seenNearbyPlayers.Clear();
                currentSeenNearbyPlayers = seenNearbyPlayers;
            }

            for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
            {
                if (index > 629)
                    break;

                if (index is > 200 and < 489)
                {
                    index = 488;
                    continue;
                }

                var entry       = manager->Objects.IndexSorted[index];
                var address     = (nint)entry.Value;
                var isProcessed = IsProcessedObject(entry.Value);

                TrackNearbyPlayer(entry.Value, (uint)index, currentSeenNearbyPlayers);

                if (GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent)
                {
                    if (!ShouldFilterOccultCrescent(entry.Value, ref playerCount, (uint)index, isProcessed))
                    {
                        RestoreObjectIfProcessed(entry.Value);
                        continue;
                    }
                }
                else
                {
                    if (!ShouldFilter(config.DefaultConfig, entry.Value, (uint)index, isProcessed))
                    {
                        RestoreObjectIfProcessed(entry.Value);
                        continue;
                    }
                }

                HideObject(entry.Value);

                if (address != nint.Zero)
                    seenProcessedObjects.Add(address);
            }

            RestoreStaleProcessedObjects(manager, seenProcessedObjects);
            RemoveStaleNearbyKeptPlayers(currentSeenNearbyPlayers);
        }
        finally
        {
            isUpdatingObjects = false;
        }
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
               GameState.TerritoryIntendedUse != TerritoryIntendedUse.IslandSanctuary;
    }

    private static bool HasActiveFilter(FilterConfig config) =>
        config.HidePlayer || config.HidePet || config.HideChocobo || config.HideUnimportantENPC;

    private bool ShouldFilter(FilterConfig config, GameObject* gameObject, uint index, bool isProcessed)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (!isProcessed && ((RenderFlag)gameObject->RenderFlags).IsSet(RenderFlag.Invisible)) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        // 玩家
        if (config.HidePlayer             &&
            index                  <= 200 &&
            index % 2              == 0   &&
            gameObject->ObjectKind == ObjectKind.Pc)
        {
            var player = (BattleChara*)gameObject;

            if (UpdatePlayerKeepStateAndShouldKeepVisible(config, player))
                return false;

            return true;
        }

        // 宠物
        if (config.HidePet                             &&
            index                  <= 200              &&
            index % 2              == 1                &&
            gameObject->ObjectKind != ObjectKind.Mount &&
            gameObject->OwnerId    != LocalPlayerState.EntityID)
            return true;
        
        // 战斗召唤物
        if (config.HidePet                                                &&
            index                                 <= 200                  &&
            index % 2                             == 0                    &&
            gameObject->ObjectKind                == ObjectKind.BattleNpc &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet &&
            gameObject->OwnerId                   != LocalPlayerState.EntityID)
            return true;

        // 陆行鸟
        if (config.HideChocobo                                                &&
            index                                 <= 200                      &&
            index % 2                             == 0                        &&
            gameObject->ObjectKind                == ObjectKind.BattleNpc     &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Buddy   &&
            gameObject->OwnerId                   != LocalPlayerState.EntityID)
            return true;

        // 不重要 NPC
        if (config.HideUnimportantENPC                                              &&
            index is >= 489 and <= 629                                              &&
            !gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.IsTargetable) &&
            gameObject->EventHandler == null)
            return true;

        return false;
    }

    private bool ShouldFilterOccultCrescent(GameObject* gameObject, ref int playerCount, uint index, bool isProcessed)
    {
        if (gameObject == null) return false;

        if (gameObject->EntityId == LocalPlayerState.EntityID) return false;

        if (!isProcessed && ((RenderFlag)gameObject->RenderFlags).IsSet(RenderFlag.Invisible)) return false;

        if (gameObject->NamePlateIconId != 0) return false;

        // 玩家
        if (config.DefaultConfig.HidePlayer &&
            index                  <= 200 &&
            index % 2              == 0   &&
            gameObject->ObjectKind == ObjectKind.Pc)
        {
            var player = (BattleChara*)gameObject;

            if (player->IsDead())
                return false;

            if (UpdatePlayerKeepStateAndShouldKeepVisible(config.DefaultConfig, player))
                return false;

            return ++playerCount > OCCULT_VISIBLE_PLAYER_LIMIT;
        }

        // 不重要 NPC
        if (config.DefaultConfig.HideUnimportantENPC                                &&
            index is >= 489 and <= 629                                              &&
            !gameObject->TargetableStatus.IsSet(ObjectTargetableFlags.IsTargetable) &&
            gameObject->EventHandler == null)
            return true;

        // 其他玩家的召唤物
        if (config.DefaultConfig.HidePet                        &&
            gameObject->ObjectKind == ObjectKind.BattleNpc      &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet &&
            index                  <= 200                       &&
            index % 2              == 0                         &&
            gameObject->OwnerId    != LocalPlayerState.EntityID &&
            gameObject->OwnerId    != 0                         &&
            gameObject->OwnerId    != 0xE0000000)
            return true;

        // 陆行鸟
        if (config.DefaultConfig.HideChocobo                    &&
            gameObject->ObjectKind == ObjectKind.BattleNpc      &&
            (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Buddy &&
            index                  <= 200                       &&
            index % 2              == 0                         &&
            gameObject->OwnerId    != LocalPlayerState.EntityID &&
            gameObject->OwnerId    != 0                         &&
            gameObject->OwnerId    != 0xE0000000)
            return true;

        return false;
    }

    private static bool ShouldFilterPlayerRace(FilterConfig config, BattleChara* player)
    {
        if (config.RaceFilterMode == RaceFilterMode.Disabled)
            return true;

        var containsSelection = MatchesPlayerRaceSexFilter(config, player);

        return config.RaceFilterMode switch
        {
            RaceFilterMode.HideSelected    => containsSelection,
            RaceFilterMode.HideNotSelected => !containsSelection,
            _                              => true
        };
    }

    private bool UpdatePlayerKeepStateAndShouldKeepVisible(FilterConfig config, BattleChara* player)
    {
        if (player->IsFriend)
            return true;

        if (LocalPlayerState.IsInParty &&
            (player->IsPartyMember || player->IsAllianceMember))
            return true;

        return IsTargetOrFocusPlayerKept(config, player)                  ||
               IsTargetingMePlayerKept(config, player)                    ||
               config.KeepRecruitingPlayers && IsRecruitingPlayer(player) ||
               IsNearbyPlayerKept(config, player)                         ||
               !ShouldFilterPlayerRace(config, player);
    }

    private static bool MatchesPlayerRaceSexFilter(FilterConfig config, BattleChara* player)
    {
        if (config.RaceSexFilter.Count == 0)
            return false;

        var customizeData = player->DrawData.CustomizeData;
        return config.RaceSexFilter.Contains(PackRaceSex(customizeData.Race, customizeData.Sex));
    }

    private bool IsRecruitingPlayer(BattleChara* player)
    {
        return player != null && player->OnlineStatus == RECRUITING_ONLINE_STATUS_ID;
    }

    private bool IsTargetOrFocusPlayerKept(FilterConfig config, BattleChara* player)
    {
        if (!config.KeepTargetAndFocusPlayers || player == null)
            return false;

        var playerID = GetPlayerTrackingID(player);
        if (playerID == 0)
            return false;

        if (IsTargetOrFocusPlayer((GameObject*)player))
        {
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

    private static bool IsTargetOrFocusPlayer(GameObject* gameObject)
    {
        if (gameObject == null)
            return false;

        var address = (nint)gameObject;
        return address == (TargetManager.Target?.Address ?? nint.Zero) ||
               address == (TargetManager.FocusTarget?.Address ?? nint.Zero);
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

        var range = Math.Clamp(config.KeepNearbyPlayersRange, 1f, 50f);
        var distanceSq = Vector3.DistanceSquared(localPlayer.Position, player->Position);

        if (nearbyKeptPlayers.Contains(playerID))
        {
            var exitRange = range + NEARBY_PLAYER_RANGE_HYSTERESIS;
            if (distanceSq <= exitRange * exitRange)
                return true;

            nearbyKeptPlayers.Remove(playerID);
            return false;
        }

        if (distanceSq > range * range)
            return false;

        nearbyKeptPlayers.Add(playerID);
        return true;
    }

    private static ulong GetPlayerTrackingID(BattleChara* player)
    {
        if (player == null)
            return 0;

        if (player->ContentId != 0)
            return player->ContentId;

        return (ulong)((GameObject*)player)->GetGameObjectId();
    }

    private static ulong GetLocalPlayerGameObjectID() =>
        LocalPlayerState.Object?.GameObjectID ?? 0;

    private static void TrackNearbyPlayer(GameObject* gameObject, uint index, HashSet<ulong>? seenNearbyPlayers)
    {
        if (seenNearbyPlayers == null ||
            gameObject        == null ||
            index             > 200   ||
            index % 2         != 0    ||
            gameObject->ObjectKind != ObjectKind.Pc)
            return;

        var playerID = GetPlayerTrackingID((BattleChara*)gameObject);
        if (playerID != 0)
            seenNearbyPlayers.Add(playerID);
    }

    private void SaveConfigAndRefresh()
    {
        config.Save(this);
        nearbyKeptPlayers.Clear();
        targetingMePlayers.Clear();
        recentTargetPlayers.Clear();
        ResetAllObjects();
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private static string GetRaceName(byte race)
    {
        if (!LuminaGetter.TryGetRow(race, out Race raceRow))
            return Lang.Get("Unknown");

        return raceRow.Masculine.ToString() ?? Lang.Get("Unknown");
    }

    private static string GetSexName(byte sex) =>
        sex == FEMALE_SEX
            ? LuminaWrapper.GetAddonText(15609)
            : LuminaWrapper.GetAddonText(15608);

    private static string GetRaceFilterModeName(RaceFilterMode mode) =>
        mode switch
        {
            RaceFilterMode.HideSelected    => Lang.Get("AutoHideGameObjects-RaceFilterMode-HideSelected"),
            RaceFilterMode.HideNotSelected => Lang.Get("AutoHideGameObjects-RaceFilterMode-HideNotSelected"),
            _                              => Lang.Get("AutoHideGameObjects-RaceFilterMode-Disabled")
        };

    private static void SetRaceSexFilter(FilterConfig config, byte race, byte sex, bool selected)
    {
        var value = PackRaceSex(race, sex);

        if (selected)
            config.RaceSexFilter.Add(value);
        else
            config.RaceSexFilter.Remove(value);
    }

    private static void SetAllRaceSexFilters(FilterConfig config, bool selected)
    {
        config.RaceSexFilter.Clear();
        if (!selected) return;

        for (var race = MIN_RACE; race <= MAX_RACE; race++)
        for (var sex = MALE_SEX; sex <= FEMALE_SEX; sex++)
            config.RaceSexFilter.Add(PackRaceSex((byte)race, (byte)sex));
    }

    private static void InvertRaceSexFilters(FilterConfig config)
    {
        for (var race = MIN_RACE; race <= MAX_RACE; race++)
        for (var sex = MALE_SEX; sex <= FEMALE_SEX; sex++)
        {
            var value = PackRaceSex((byte)race, (byte)sex);

            if (!config.RaceSexFilter.Remove(value))
                config.RaceSexFilter.Add(value);
        }
    }

    private static byte PackRaceSex(byte race, byte sex) =>
        (byte)(race | sex << 4);

    private void HideObject(GameObject* gameObject)
    {
        if (gameObject == null) return;

        gameObject->RenderFlags |= (VisibilityFlags)256;
        processedObjects[(nint)gameObject] = HiddenObjectRecord.From(gameObject);
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

    private void RestoreObjectIfProcessed(GameObject* gameObject)
    {
        if (gameObject == null) return;

        var address = (nint)gameObject;
        if (!processedObjects.TryGetValue(address, out var record))
            return;

        processedObjects.Remove(address);

        if (!record.IsSameObject(gameObject))
            return;

        gameObject->RenderFlags &= ~(VisibilityFlags)256;
    }

    private void RestoreStaleProcessedObjects(GameObjectManager* manager, HashSet<nint> seenProcessed)
    {
        if (processedObjects.Count == 0) return;

        foreach (var address in processedObjects.Keys.ToArray())
        {
            if (seenProcessed.Contains(address))
                continue;

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

    private void RemoveStaleNearbyKeptPlayers(HashSet<ulong>? seenNearbyPlayers)
    {
        if (nearbyKeptPlayers.Count == 0)
            return;

        if (seenNearbyPlayers == null)
        {
            nearbyKeptPlayers.Clear();
            return;
        }

        foreach (var playerID in nearbyKeptPlayers.ToArray())
        {
            if (!seenNearbyPlayers.Contains(playerID))
                nearbyKeptPlayers.Remove(playerID);
        }
    }

    private void ResetAllObjects()
    {
        if (processedObjects.Count == 0) return;

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

        foreach (ref var entry in GameObjectManager.Instance()->Objects.IndexSorted)
        {
            if (entry.Value       == null                                            ||
                (nint)entry.Value == (LocalPlayerState.Object?.Address ?? nint.Zero) ||
                !processedObjects.TryGetValue((nint)entry.Value, out var record)     ||
                !record.IsSameObject(entry.Value))
                continue;

            entry.Value->RenderFlags &= ~(VisibilityFlags)256;
        }

        ClearProcessedObjects();
    }

    private void OnZoneChanged(uint u)
    {
        ResetAllObjects();
        ClearProcessedObjects();
    }

    private void OnUpdate(IFramework _)
    {
        if (DService.Instance().Condition.IsBetweenAreas) return;

        PruneTargetingMePlayers();
        PruneRecentTargetPlayers();
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private void PruneTargetingMePlayers()
    {
        if (targetingMePlayers.Count == 0)
            return;

        var now = Environment.TickCount64;
        foreach (var playerID in targetingMePlayers.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            targetingMePlayers.Remove(playerID);
    }

    private void PruneRecentTargetPlayers()
    {
        if (recentTargetPlayers.Count == 0)
            return;

        var now = Environment.TickCount64;
        foreach (var playerID in recentTargetPlayers.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            recentTargetPlayers.Remove(playerID);
    }

    private void ClearProcessedObjects()
    {
        processedObjects.Clear();
        nearbyKeptPlayers.Clear();
        targetingMePlayers.Clear();
        recentTargetPlayers.Clear();
    }
    
    private class Config : ModuleConfig
    {
        public FilterConfig DefaultConfig = new();
    }

    private class FilterConfig
    {
        // 陆行鸟
        public bool HideChocobo = true;

        // 宠物
        public bool HidePet = true;

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

        // 玩家种族过滤模式
        public RaceFilterMode RaceFilterMode = RaceFilterMode.Disabled;

        // 玩家种族 / 性别过滤列表
        public HashSet<byte> RaceSexFilter = [];

        // 不重要 NPC
        public bool HideUnimportantENPC = true;
    }

    public enum RaceFilterMode
    {
        Disabled,
        HideSelected,
        HideNotSelected
    }

    private readonly record struct HiddenObjectRecord(ulong GameObjectID, uint EntityID)
    {
        public static HiddenObjectRecord From(GameObject* gameObject) =>
            new((ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject != null &&
            (ulong)gameObject->GetGameObjectId() == GameObjectID &&
            gameObject->EntityId                 == EntityID;
    }

    private enum RenderFlag
    {
        Invisible = 256
    }
}
