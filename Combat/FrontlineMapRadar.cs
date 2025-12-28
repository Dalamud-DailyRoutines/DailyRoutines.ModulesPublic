using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using LuminaMap = Lumina.Excel.Sheets.Map;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class FrontlineMapRadar : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("FrontlineMapRadarTitle"),
        Description = GetLoc("FrontlineMapRadarDescription"),
        Category    = ModuleCategories.Combat,
        Author      = ["Rorinnn"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static Config ModuleConfig = null!;

    private static readonly CompSig                 SetDataSig = new("E8 ?? ?? ?? ?? 48 8B 53 ?? 8B 86");
    private delegate        MapMarkerData*          SetDataDelegate(
        MapMarkerData* self,
        uint levelID,
        Utf8String* tooltipString,
        uint iconID,
        float x, float y, float z,
        float radius,
        ushort territoryTypeID,
        uint mapID,
        uint placeNameZoneID,
        uint placeNameID,
        ushort recommendedLevel,
        sbyte eventState);
    private static          Hook<SetDataDelegate>?  SetDataHook;

    #region 地图标记数据

    private static readonly Vector2[]                                  MapPosSize              = new Vector2[2];
    private static readonly Dictionary<Vector3, MapMarkerInfo>         MapMarkers              = [];
    private static readonly Dictionary<Vector3, ControlPointState>     ControlPointStates      = [];
    private static readonly HashSet<Vector3>                           VisibleMarkerPositions  = [];

    #endregion

    #region 玩家雷达数据

    private static readonly List<PlayerRadarInfo>                      PlayerList              = [];
    private static readonly Dictionary<PlayerIconType, float>          CachedDotIconSizes      = [];
    private static readonly List<ClassJob>                             CachedJobs              =
    [
        ..LuminaGetter.Get<ClassJob>()
            .Where(x => x.RowId >= 19 && x.RowId <= 42 && x.RowId != 26 && x.RowId != 29 && x.RowId != 36)
            .OrderBy(x => x.Role switch
            {
                1 => 0, // Tank
                4 => 1, // Healer
                2 => 2, // Melee DPS
                3 => 3, // Ranged DPS
                _ => 4
            })
            .ThenBy(x => x.RowId)
    ];

    #endregion

    #region 地图状态

    private static Vector2? MapOrigin;
    private static float    GlobalUIScale = 1f;

    private static float    MapScale
    {
        get
        {
            var addon = GetAddonByName("AreaMap");
            return addon is not null ? *(float*)((byte*)addon + 980) : 1f;
        }
    }

    #endregion

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();
        RefreshCachedIconSizes();

        SetDataHook ??= SetDataSig.GetHook<SetDataDelegate>(SetDataDetour);
        SetDataHook?.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        PlayersManager.ReceivePlayersAround += OnPlayersAroundUpdate;
        FrameworkManager.Reg(OnFrameworkUpdate);
        WindowManager.Draw += DrawOverlay;
    }

    protected override void Uninit()
    {
        SetDataHook?.Dispose();

        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        PlayersManager.ReceivePlayersAround -= OnPlayersAroundUpdate;
        FrameworkManager.Unreg(OnFrameworkUpdate);
        WindowManager.Draw -= DrawOverlay;

        MapMarkers.Clear();
        ControlPointStates.Clear();
        VisibleMarkerPositions.Clear();
        PlayerList.Clear();
    }

    protected override void ConfigUI()
    {
        using var tabBar = ImRaii.TabBar("###FrontlineMapRadarConfigTabs");
        if (!tabBar) return;

        using (var tabItem = ImRaii.TabItem(GetLoc("FrontlineMapRadar-PVPMapMarkersSettings")))
        {
            if (tabItem)
                DrawMarkerConfigUI();
        }

        using (var tabItem = ImRaii.TabItem(GetLoc("FrontlineMapRadar-PVPPlayerRadarSettings")))
        {
            if (tabItem)
                DrawPVPRadarConfigUI();
        }

        using (var tabItem = ImRaii.TabItem(GetLoc("FrontlineMapRadar-NonPVPPlayerRadarSettings")))
        {
            if (tabItem)
                DrawNonPVPRadarConfigUI();
        }
    }

    #region 配置界面

    private void DrawMarkerConfigUI()
    {
        var configChanged = false;

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderFloat(GetLoc("FontScale"), ref ModuleConfig.TextScale, 1f, 2f, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            configChanged = true;

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderInt(GetLoc("Offset"), ref ModuleConfig.TextOffsetY, -30, 30);
        if (ImGui.IsItemDeactivatedAfterEdit())
            configChanged = true;

        configChanged |= ImGui.Checkbox(GetLoc("FrontlineMapRadar-ShowCountdown"), ref ModuleConfig.ShowCountdown);
        configChanged |= ImGui.Checkbox(GetLoc("FrontlineMapRadar-ShowHealthPercent"), ref ModuleConfig.ShowHealthPercent);
        configChanged |= ImGui.Checkbox(GetLoc("FrontlineMapRadar-ShowControlPointScore"), ref ModuleConfig.ShowControlPointScore);

        if (configChanged)
            SaveConfig(ModuleConfig);
    }

    private void DrawPVPRadarConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderFloat(GetLoc("FrontlineMapRadar-DotRadius"), ref ModuleConfig.DotRadius, 0.5f, 5f, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            RefreshCachedIconSizes();
            SaveConfig(ModuleConfig);
        }

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderFloat(GetLoc("FrontlineMapRadar-JobIconSize"), ref ModuleConfig.JobIconSize, 1f, 5f, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FrontlineMapRadar-HideLoadingRange"), ref ModuleConfig.HideLoadingRangeInPVP))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("FrontlineMapRadar-HideFriendlyCharacters"), ref ModuleConfig.HideFriendlyInPVP))
        {
            PlayerList.Clear();
            SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("FrontlineMapRadar-HighlightJobsInPVP"));
        ImGuiOm.HelpMarker(GetLoc("FrontlineMapRadar-HighlightJobsInPVP-Help"));

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.SliderInt(LuminaWrapper.GetAddonText(15020), ref ModuleConfig.JobIconStyle, 1, 4);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.JobIconStyle = Math.Clamp(ModuleConfig.JobIconStyle, 1, 4);
            SaveConfig(ModuleConfig);
        }

        DrawJobSelectionTable();
    }

    private void DrawNonPVPRadarConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("FrontlineMapRadar-ValidOutsideFrontline"), ref ModuleConfig.ShowOutsideFrontline))
        {
            if (!ModuleConfig.ShowOutsideFrontline && !IsFrontlineTerritory())
                PlayerList.Clear();
            SaveConfig(ModuleConfig);
        }

        DrawPlayerCategorySettings(
            PlayerIconType.Friend, LuminaWrapper.GetAddonText(2941), KnownColor.Orange,
            ref ModuleConfig.ShowFriendDots, ref ModuleConfig.ShowFriendNames, ref ModuleConfig.ShowFriendJobIcons);

        DrawPlayerCategorySettings(
            PlayerIconType.Party, LuminaWrapper.GetAddonText(654), KnownColor.Cyan,
            ref ModuleConfig.ShowPartyDots, ref ModuleConfig.ShowPartyNames, ref ModuleConfig.ShowPartyJobIcons);

        DrawPlayerCategorySettings(
            PlayerIconType.Alliance, LuminaWrapper.GetAddonText(648), KnownColor.Green,
            ref ModuleConfig.ShowAllianceDots, ref ModuleConfig.ShowAllianceNames, ref ModuleConfig.ShowAllianceJobIcons);

        DrawPlayerCategorySettings(
            PlayerIconType.Other, LuminaWrapper.GetAddonText(832), KnownColor.White,
            ref ModuleConfig.ShowOtherDots, ref ModuleConfig.ShowOtherNames, ref ModuleConfig.ShowOtherJobIcons);
    }

    private void DrawPlayerCategorySettings(
        PlayerIconType iconType, string categoryText, KnownColor color,
        ref bool showDotIcons, ref bool showNames, ref bool showJobIcons)
    {
        ImGui.TextColored(color.ToVector4(), categoryText);
        using (ImRaii.PushIndent())
        {
            var dotsChanged = ImGui.Checkbox($"{LuminaWrapper.GetAddonText(12425)}##{iconType}Dots", ref showDotIcons);
            ImGui.SameLine();
            var namesChanged = ImGui.Checkbox($"{LuminaWrapper.GetAddonText(293)}##{iconType}Names", ref showNames);
            ImGui.SameLine();
            var iconsChanged = ImGui.Checkbox($"{GetLoc("FrontlineMapRadar-ShowJobIcons")}##{iconType}JobIcons", ref showJobIcons);

            if (dotsChanged || namesChanged || iconsChanged)
            {
                PlayerList.Clear();
                SaveConfig(ModuleConfig);
            }
        }
    }

    private void DrawJobSelectionTable()
    {
        const int columns = 4;
        using var table = ImRaii.Table("JobsTable", columns, ImGuiTableFlags.SizingFixedFit);
        if (!table) return;

        for (var i = 0; i < CachedJobs.Count; i++)
        {
            var job = CachedJobs[i];

            if (i % columns == 0)
                ImGui.TableNextRow();

            ImGui.TableNextColumn();

            var jobID = job.RowId;
            var isChecked = ModuleConfig.HighlightedJobs.Contains(jobID);

            if (DService.Texture.TryGetFromGameIcon(new(GetJobIconBaseID() + jobID), out var jobIcon))
            {
                var iconSize = ImGui.GetTextLineHeightWithSpacing();
                ImGui.Image(jobIcon.GetWrapOrEmpty().Handle, new Vector2(iconSize));
                ImGui.SameLine();
            }

            if (ImGui.Checkbox($"{job.Name.ExtractText()}##Job{jobID}", ref isChecked))
            {
                if (isChecked)
                    ModuleConfig.HighlightedJobs.Add(jobID);
                else
                    ModuleConfig.HighlightedJobs.Remove(jobID);
                SaveConfig(ModuleConfig);
            }
        }
    }

    #endregion

    #region 事件处理

    private static void OnZoneChanged(ushort territoryID)
    {
        MapMarkers.Clear();
        ControlPointStates.Clear();
        VisibleMarkerPositions.Clear();
        PlayerList.Clear();
    }

    private static void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsValidToUpdate())
            return;

        if (!IsFrontlineTerritory())
            return;

        UpdateVisibleMarkerPositions();
        UpdateControlPointScores();
    }

    private static void OnPlayersAroundUpdate(IReadOnlyList<IPlayerCharacter> players)
    {
        if (!IsValidToUpdate())
        {
            PlayerList.Clear();
            return;
        }

        var isInSupportedTerritory = IsFrontlineTerritory();

        if (!ModuleConfig.ShowOutsideFrontline && !isInSupportedTerritory)
        {
            PlayerList.Clear();
            return;
        }

        RefreshPlayers(players, isInSupportedTerritory);
    }

    #endregion

    #region Hook 处理

    private static MapMarkerData* SetDataDetour(
        MapMarkerData* self,
        uint levelID,
        Utf8String* tooltipString,
        uint iconID,
        float x, float y, float z,
        float radius,
        ushort territoryTypeID,
        uint mapID,
        uint placeNameZoneID,
        uint placeNameID,
        ushort recommendedLevel,
        sbyte eventState)
    {
        if (!IsFrontlineTerritory())
        {
            return SetDataHook!.Original(self, levelID, tooltipString, iconID, x, y, z, radius,
                territoryTypeID, mapID, placeNameZoneID, placeNameID, recommendedLevel, eventState);
        }

        var result = SetDataHook!.Original(self, levelID, tooltipString, iconID, x, y, z, radius,
            territoryTypeID, mapID, placeNameZoneID, placeNameID, recommendedLevel, eventState);

        var position = new Vector3(x, y, z);

        if (IgnoredIconIDs.Contains(iconID))
            return result;

        if (MapMarkers.TryGetValue(position, out var existingMarker) && existingMarker.IconID != iconID)
        {
            if (IsTrackedMarker(existingMarker.IconID))
            {
                MapMarkers.Remove(position);
                if (!ControlPointIconIDs.Contains(iconID))
                    ControlPointStates.Remove(position);
            }
        }

        if (!IsTrackedMarker(iconID))
            return result;

        try
        {
            if (tooltipString is null || (void*)tooltipString->StringPtr == null)
                return result;

            var tooltipText = tooltipString->ToString();
            var suffix = ExtractSuffixForParsing(tooltipText);

            var displayText = string.Empty;

            if (CountdownIconIDs.Contains(iconID))
                displayText = ParseCountdownText(suffix);
            else if (HealthIconIDs.Contains(iconID))
                displayText = ParsePercentText(suffix);
            else if (ControlPointIconIDs.Contains(iconID))
            {
                displayText = ParsePercentText(suffix);
                UpdateControlPointStateFromHook(iconID, position, tooltipText);
            }

            MapMarkers[position] = new MapMarkerInfo(iconID, position, displayText);
        }
        catch
        {
            // ignored
        }

        return result;
    }

    #endregion

    #region 地图标记处理

    private static bool IsTrackedMarker(uint iconID) => AllTrackedIconIDs.Contains(iconID);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ExtractSuffixForParsing(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty :
        text.Length > CacheSuffixLength ? text[^CacheSuffixLength..] : text;

    private static string ParseCountdownText(string tooltipText)
    {
        var match = CountdownRegex().Match(tooltipText);
        if (!match.Success) return string.Empty;

        return int.TryParse(match.Groups[1].ValueSpan, out var minutes) &&
               int.TryParse(match.Groups[2].ValueSpan, out var seconds)
            ? minutes < 1 ? seconds.ToString() : $"{minutes}:{seconds:D2}"
            : string.Empty;
    }

    private static string ParsePercentText(string tooltipText)
    {
        var match = PercentRegex().Match(tooltipText);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, out var percent)
            ? $"{percent}%"
            : string.Empty;
    }

    private static void UpdateControlPointStateFromHook(uint iconID, Vector3 position, string tooltipText)
    {
        switch (GameState.TerritoryType)
        {
            case 431:
                UpdateSealRockControlPoint(iconID, position, tooltipText);
                break;
            case 888 or 1313:
                UpdateAzimSteppeControlPoint(iconID, position);
                break;
        }
    }

    private static void UpdateSealRockControlPoint(uint iconID, Vector3 position, string tooltipText)
    {
        var match = PercentRegex().Match(tooltipText);
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, out var percentInt))
            return;

        var lastDigit = percentInt % 10;
        var percent = lastDigit is 7 or 2 ? percentInt + 0.5 : percentInt;

        if (!SealRockControlPointScores.TryGetValue(iconID, out var initialScore))
            return;

        if (ControlPointStates.TryGetValue(position, out var state))
        {
            if (state.IconID != iconID)
                ControlPointStates.Remove(position);
            else
            {
                state.CurrentPercent = percent;
                return;
            }
        }

        ControlPointStates[position] = new ControlPointState(iconID, null, initialScore)
        {
            CurrentPercent = percent
        };
    }

    private static void UpdateAzimSteppeControlPoint(uint iconID, Vector3 position)
    {
        if (!AzimSteppeControlPointScores.TryGetValue(iconID, out var initialScore))
            return;

        var isNeutral = NeutralControlPointIconIDs.Contains(iconID);

        if (ControlPointStates.TryGetValue(position, out var state))
        {
            if (state.IconID != iconID)
                ControlPointStates.Remove(position);
            else
                return;
        }

        ControlPointStates[position] = new ControlPointState(
            iconID,
            isNeutral ? null : DateTime.Now,
            initialScore)
        {
            CurrentScore = initialScore
        };
    }

    private static void UpdateVisibleMarkerPositions()
    {
        VisibleMarkerPositions.Clear();

        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap is null)
                return;

            var eventMarkers = agentMap->EventMarkers;
            var count = eventMarkers.Count;

            for (var i = 0; i < count; i++)
            {
                ref var markerData = ref eventMarkers[i];
                VisibleMarkerPositions.Add(markerData.Position);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void UpdateControlPointScores()
    {
        if (GameState.TerritoryType is not (888 or 1313))
            return;

        var now = DateTime.Now;
        List<Vector3>? toRemove = null;

        foreach (var (position, state) in ControlPointStates)
        {
            if (!state.ControlStartTime.HasValue)
                continue;

            var elapsed = (now - state.ControlStartTime.Value).TotalSeconds;
            var intervals = (int)(elapsed / ControlPointDecreaseIntervalSeconds);

            var deductionPerInterval = (int)(state.InitialScore * ControlPointDecreaseFactor);
            var totalDeduction = intervals * deductionPerInterval;

            state.CurrentScore = Math.Max(0, state.InitialScore - totalDeduction);

            if (state.CurrentScore > 0)
                continue;

            if (!state.ReachedZeroTime.HasValue)
                state.ReachedZeroTime = now;
            else if ((now - state.ReachedZeroTime.Value).TotalSeconds >= ZeroScoreDisplayDuration)
            {
                toRemove ??= [];
                toRemove.Add(position);
            }
        }

        if (toRemove is not null)
        {
            foreach (var position in toRemove)
                ControlPointStates.Remove(position);
        }
    }

    #endregion

    #region 玩家雷达处理

    private static void RefreshCachedIconSizes()
    {
        var dotBaseSize = ModuleConfig.DotRadius * 40f;
        CachedDotIconSizes.Clear();
        foreach (var (iconType, (_, fixedScale)) in PlayerIcons)
            CachedDotIconSizes[iconType] = fixedScale * dotBaseSize;
    }

    private static void RefreshPlayers(IReadOnlyList<IPlayerCharacter> players, bool isInSupportedTerritory)
    {
        PlayerList.Clear();

        var localPlayerAddress = DService.ObjectTable.LocalPlayer?.Address ?? nint.Zero;

        foreach (var player in players)
        {
            if (player.Address == localPlayerAddress ||
                string.IsNullOrEmpty(player.Name.TextValue))
                continue;

            try
            {
                var character = (Character*)player.ToStruct();
                var jobID = character->CharacterData.ClassJob;

                if (isInSupportedTerritory)
                    ProcessPvPPlayer(player, character, jobID);
                else
                    ProcessNonPvPPlayer(player, jobID);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static void ProcessPvPPlayer(IPlayerCharacter player, Character* character, uint jobID)
    {
        if (ModuleConfig.HideFriendlyInPVP &&
            ((player.StatusFlags & StatusFlags.PartyMember) != 0 ||
             (player.StatusFlags & StatusFlags.AllianceMember) != 0))
            return;

        var dotIconType = GetPvPIconType(character->CharacterData.Battalion, player.IsDead);
        var showJobIcon = ModuleConfig.HighlightedJobs.Contains(jobID);

        PlayerList.Add(new PlayerRadarInfo(
            player.Address, false, "", KnownColor.White.ToVector4().ToUInt(),
            showJobIcon, jobID, true, dotIconType, true));
    }

    private static PlayerIconType GetPvPIconType(byte battalion, bool isDead) =>
        isDead ? PlayerIconType.PvPDead : battalion switch
        {
            0 => PlayerIconType.PvPMaelstrom,
            1 => PlayerIconType.PvPTwinAdder,
            2 => PlayerIconType.PvPImmortalFlames,
            _ => PlayerIconType.PvPDead
        };

    private static void ProcessNonPvPPlayer(IPlayerCharacter player, uint jobID)
    {
        var isFriend = (player.StatusFlags & StatusFlags.Friend) != 0;
        var isParty = (player.StatusFlags & StatusFlags.PartyMember) != 0;
        var isAlliance = (player.StatusFlags & StatusFlags.AllianceMember) != 0;

        bool showDot, showJobIcon, showName;
        PlayerIconType iconType;
        KnownColor nameColor;

        if (isFriend)
        {
            showDot = ModuleConfig.ShowFriendDots;
            showJobIcon = ModuleConfig.ShowFriendJobIcons;
            showName = ModuleConfig.ShowFriendNames;
            iconType = PlayerIconType.Friend;
            nameColor = KnownColor.Orange;
        }
        else if (isParty)
        {
            showDot = ModuleConfig.ShowPartyDots;
            showJobIcon = ModuleConfig.ShowPartyJobIcons;
            showName = ModuleConfig.ShowPartyNames;
            iconType = PlayerIconType.Party;
            nameColor = KnownColor.Cyan;
        }
        else if (isAlliance)
        {
            showDot = ModuleConfig.ShowAllianceDots;
            showJobIcon = ModuleConfig.ShowAllianceJobIcons;
            showName = ModuleConfig.ShowAllianceNames;
            iconType = PlayerIconType.Alliance;
            nameColor = KnownColor.Green;
        }
        else
        {
            showDot = ModuleConfig.ShowOtherDots;
            showJobIcon = ModuleConfig.ShowOtherJobIcons;
            showName = ModuleConfig.ShowOtherNames;
            iconType = PlayerIconType.Other;
            nameColor = KnownColor.White;
        }

        if (!showDot && !showJobIcon && !showName)
            return;

        PlayerList.Add(new PlayerRadarInfo(
            player.Address, showName, player.Name.TextValue, nameColor.ToVector4().ToUInt(),
            showJobIcon, jobID, showDot, iconType, false));
    }

    #endregion

    #region 绘制逻辑

    private static void DrawOverlay()
    {
        if (!IsValidToUpdate())
            return;

        if (GameState.TerritoryType == 0)
            return;

        var isInSupportedTerritory = IsFrontlineTerritory();
        var shouldDrawMarkers = isInSupportedTerritory;
        var shouldDrawRadar = ModuleConfig.ShowOutsideFrontline || isInSupportedTerritory;

        if (!shouldDrawMarkers && !shouldDrawRadar)
            return;

        RefreshMapOrigin();

        if (MapOrigin is not { } origin || origin == Vector2.Zero)
            return;

        var map = GameState.MapData;
        var drawList = ImGui.GetBackgroundDrawList();

        drawList.PushClipRect(MapPosSize[0], MapPosSize[1]);

        if (shouldDrawMarkers)
            DrawMapMarkers(drawList, origin, map);

        if (shouldDrawRadar)
            DrawPlayerRadar(drawList, origin, map, isInSupportedTerritory);

        drawList.PopClipRect();
    }

    private static void DrawMapMarkers(ImDrawListPtr drawList, Vector2 origin, LuminaMap map)
    {
        if (MapMarkers.Count == 0)
            return;

        var localPlayerPos = DService.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var localPlayerTexturePos = WorldToTexture(localPlayerPos, map);
        var scale = MapScale * GlobalUIScale;

        foreach (var marker in MapMarkers.Values)
        {
            if (!VisibleMarkerPositions.Contains(marker.Position))
                continue;

            var markerTexturePos = WorldToTexture(marker.Position, map);
            var textureOffset = (markerTexturePos - localPlayerTexturePos) * scale;
            var pos = origin + textureOffset;

            DrawMarker(drawList, pos, marker);
        }
    }

    private static void DrawMarker(ImDrawListPtr drawList, Vector2 pos, MapMarkerInfo marker)
    {
        var whiteColor = KnownColor.White.ToVector4().ToUInt();
        var posWithOffset = pos + new Vector2(0, ModuleConfig.TextOffsetY);

        if (!string.IsNullOrEmpty(marker.DisplayText))
        {
            var isCountdown = CountdownIconIDs.Contains(marker.IconID) && !marker.DisplayText.Contains('%');
            var isHealth = HealthIconIDs.Contains(marker.IconID) && marker.DisplayText.Contains('%');

            if ((isCountdown && ModuleConfig.ShowCountdown) || (isHealth && ModuleConfig.ShowHealthPercent))
            {
                var fontSize = ImGui.GetFontSize() * ModuleConfig.TextScale;
                DrawTextWithStroke(drawList, posWithOffset, marker.DisplayText, whiteColor, fontSize);
            }
        }

        if (ControlPointIconIDs.Contains(marker.IconID) && ModuleConfig.ShowControlPointScore)
        {
            if (ControlPointStates.TryGetValue(marker.Position, out var state))
            {
                var displayScore = state.DisplayScore;

                if (displayScore > 0 || (state.ControlStartTime.HasValue && displayScore == 0))
                {
                    var scoreText = ((int)Math.Round(displayScore)).ToString();
                    var fontSize = ImGui.GetFontSize() * ModuleConfig.TextScale;

                    DrawTextWithStroke(drawList, posWithOffset, scoreText, whiteColor, fontSize);
                }
            }
        }
    }

    private static void DrawPlayerRadar(ImDrawListPtr drawList, Vector2 origin, LuminaMap map, bool isInSupportedTerritory)
    {
        if (PlayerList.Count > 0)
        {
            var localPlayerPos = DService.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            var localPlayerTexturePos = WorldToTexture(localPlayerPos, map);
            var scale = MapScale * GlobalUIScale;

            foreach (var item in PlayerList)
            {
                if (DService.ObjectTable.CreateObjectReference(item.Address) is not IPlayerCharacter player || !player.IsValid())
                    continue;

                var worldPos = player.Position;
                var itemTexturePos = WorldToTexture(worldPos, map);
                var textureOffset = (itemTexturePos - localPlayerTexturePos) * scale;
                var pos = origin + textureOffset;

                DrawPlayerInfo(drawList, pos, item);
            }
        }

        if (isInSupportedTerritory && !ModuleConfig.HideLoadingRangeInPVP && DService.ObjectTable.LocalPlayer is not null)
        {
            var localPlayerPos = DService.ObjectTable.LocalPlayer.Position;
            var radiusWorldPos = new Vector3(localPlayerPos.X + PvPLoadingRadius, localPlayerPos.Y, localPlayerPos.Z);

            var centerTexturePos = WorldToTexture(localPlayerPos, map);
            var radiusTexturePos = WorldToTexture(radiusWorldPos, map);

            var assistRadius = Vector2.Distance(centerTexturePos, radiusTexturePos) * MapScale * GlobalUIScale;

            drawList.AddCircle(origin, assistRadius, KnownColor.Gray.ToVector4().ToUInt(), 0, 2f);
        }
    }

    private static void DrawPlayerInfo(ImDrawListPtr drawList, Vector2 pos, PlayerRadarInfo info)
    {
        if (info.ShowJobIcon)
        {
            var jobIconID = GetJobIconBaseID() + info.JobID;
            if (DService.Texture.TryGetFromGameIcon(new(jobIconID), out var jobIcon))
            {
                var iconSize = ModuleConfig.JobIconSize * ImGui.GetTextLineHeightWithSpacing();

                var iconPos = info.IsPvP
                    ? pos - new Vector2(iconSize / 2f)
                    : pos - new Vector2(iconSize / 2f, iconSize / 1.2f);

                drawList.AddImage(jobIcon.GetWrapOrEmpty().Handle, iconPos, iconPos + new Vector2(iconSize),
                    Vector2.Zero, Vector2.One, KnownColor.White.ToVector4().ToUInt());
            }
        }

        if (info.ShowDotIcon)
        {
            var dotIconID = PlayerIcons[info.DotIconType].IconID;
            if (DService.Texture.TryGetFromGameIcon(new(dotIconID), out var icon))
            {
                var iconSize = CachedDotIconSizes[info.DotIconType];
                var iconPos = pos - new Vector2(iconSize / 2f);
                drawList.AddImage(icon.GetWrapOrEmpty().Handle, iconPos, iconPos + new Vector2(iconSize),
                    Vector2.Zero, Vector2.One, KnownColor.White.ToVector4().ToUInt());
            }
        }

        if (info.ShowName && !string.IsNullOrWhiteSpace(info.Name))
            DrawText(drawList, pos, info.Name, info.NameColor, true);
    }

    private static void DrawTextWithStroke(
        ImDrawListPtr drawList, Vector2 pos, string text, uint color, float fontSize,
        bool centerAlignX = true, bool centerAlignY = true)
    {
        var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
        var textPos = pos;

        if (centerAlignX)
            textPos -= new Vector2(textSize.X / 2f, 0f);
        if (centerAlignY)
            textPos -= new Vector2(0f, textSize.Y / 2f);

        var strokeColor = KnownColor.Black.ToVector4().ToUInt();
        var font = ImGui.GetFont();

        drawList.AddText(font, fontSize, textPos + new Vector2(-1f, -1f), strokeColor, text);
        drawList.AddText(font, fontSize, textPos + new Vector2(-1f, 1f), strokeColor, text);
        drawList.AddText(font, fontSize, textPos + new Vector2(1f, -1f), strokeColor, text);
        drawList.AddText(font, fontSize, textPos + new Vector2(1f, 1f), strokeColor, text);
        drawList.AddText(font, fontSize, textPos, color, text);
    }

    private static void DrawText(ImDrawListPtr drawList, Vector2 pos, string text, uint col, bool stroke, bool centerAlignX = true)
    {
        if (centerAlignX)
            pos -= new Vector2(ImGui.CalcTextSize(text).X / 2f, 0f);

        if (stroke)
        {
            var strokeCol = KnownColor.Black.ToVector4().ToUInt();
            drawList.AddText(pos + new Vector2(-1f, -1f), strokeCol, text);
            drawList.AddText(pos + new Vector2(-1f, 1f), strokeCol, text);
            drawList.AddText(pos + new Vector2(1f, -1f), strokeCol, text);
            drawList.AddText(pos + new Vector2(1f, 1f), strokeCol, text);
        }

        drawList.AddText(pos, col, text);
    }

    private static void RefreshMapOrigin()
    {
        MapOrigin = null;

        var areaMapAddon = GetAddonByName("AreaMap");
        if (!IsAddonAndNodesReady(areaMapAddon) || areaMapAddon->UldManager.NodeListCount <= 4)
            return;

        GlobalUIScale = areaMapAddon->Scale;

        var componentNode = (AtkComponentNode*)areaMapAddon->UldManager.NodeList[3];
        if (componentNode is null || componentNode->Component->UldManager.NodeListCount < 233)
            return;

        var baseNode = componentNode->AtkResNode;

        if (TryFindMapMarkerNode(componentNode, out var innerComponentNode))
            CalculateMapOriginAndSize(areaMapAddon, baseNode, innerComponentNode);
    }

    private static bool TryFindMapMarkerNode(AtkComponentNode* componentNode, out AtkComponentNode* result)
    {
        result = null;

        for (var i = 6; i < componentNode->Component->UldManager.NodeListCount - 1; i++)
        {
            var node = componentNode->Component->UldManager.NodeList[i];
            if (node is null || !node->IsVisible())
                continue;

            var innerComponentNode = (AtkComponentNode*)node;
            var imageNode = (AtkImageNode*)innerComponentNode->Component->UldManager.NodeList[4];

            var foundIconID = TryGetIconID(imageNode);
            if (foundIconID == LocalPlayerIconID)
            {
                result = innerComponentNode;
                return true;
            }
        }

        return false;
    }

    private static uint? TryGetIconID(AtkImageNode* imageNode)
    {
        if (imageNode->PartsList is null || imageNode->PartId >= imageNode->PartsList->PartCount)
            return null;

        var part = imageNode->PartsList->Parts[imageNode->PartId];
        var uldAsset = part.UldAsset;

        if (uldAsset is null ||
            uldAsset->AtkTexture.TextureType != TextureType.Resource ||
            uldAsset->AtkTexture.Resource is null)
            return null;

        return uldAsset->AtkTexture.Resource->IconId;
    }

    private static void CalculateMapOriginAndSize(
        AtkUnitBase* areaMapAddon,
        AtkResNode baseNode,
        AtkComponentNode* innerComponentNode)
    {
        var innerNode = innerComponentNode->AtkResNode;
        var viewport = ImGui.GetMainViewport();
        var addonPos = new Vector2(areaMapAddon->X, areaMapAddon->Y);

        MapOrigin = viewport.Pos + addonPos +
                    ((new Vector2(baseNode.X, baseNode.Y) +
                      new Vector2(innerNode.X, innerNode.Y) +
                      new Vector2(innerNode.OriginX, innerNode.OriginY)) * GlobalUIScale);

        MapPosSize[0] = viewport.Pos + addonPos + (new Vector2(baseNode.X, baseNode.Y) * GlobalUIScale);
        MapPosSize[1] = viewport.Pos + addonPos + (new Vector2(baseNode.X, baseNode.Y) +
                        (new Vector2(baseNode.Width, baseNode.Height) * GlobalUIScale));
    }

    #endregion

    #region 小工具

    private static bool IsValidToUpdate() =>
        DService.ObjectTable.LocalPlayer is not null &&
        !DService.Condition[ConditionFlag.BetweenAreas];

    private static bool IsFrontlineTerritory() =>
        GameState.TerritoryIntendedUse == TerritoryIntendedUse.Frontline;

    private static uint GetJobIconBaseID() => JobIconBaseIDs[Math.Clamp(ModuleConfig.JobIconStyle, 1, 4) - 1];

    #endregion

    #region 数据结构

    private readonly record struct MapMarkerInfo(uint IconID, Vector3 Position, string DisplayText);

    private record ControlPointState(
        uint IconID,
        DateTime? ControlStartTime,
        int InitialScore)
    {
        public int?       CurrentScore      { get; set; }
        public double?    CurrentPercent    { get; set; }
        public DateTime?  ReachedZeroTime   { get; set; }

        public double DisplayScore =>
            CurrentPercent.HasValue
                ? InitialScore * CurrentPercent.Value / 100.0
                : CurrentScore ?? 0;
    }

    private readonly record struct PlayerRadarInfo(
        nint Address, bool ShowName, string Name, uint NameColor,
        bool ShowJobIcon, uint JobID, bool ShowDotIcon, PlayerIconType DotIconType, bool IsPvP);

    private enum PlayerIconType
    {
        Friend,
        Party,
        Alliance,
        Other,
        PvPDead,
        PvPMaelstrom,
        PvPTwinAdder,
        PvPImmortalFlames
    }

    #endregion

    #region 常量数据

    private const uint   LocalPlayerIconID                   = 60443;
    private const int    CacheSuffixLength                   = 5;
    private const int    ControlPointDecreaseIntervalSeconds = 3;
    private const double ControlPointDecreaseFactor          = 0.1;
    private const double ZeroScoreDisplayDuration            = 1.0;
    private const float  PvPLoadingRadius                    = 125f;

    private static readonly uint[] JobIconBaseIDs = [62000, 62100, 62225, 62800];

    private static readonly HashSet<uint> CountdownIconIDs =
    [
        60628, 60629, 60630,        // B
        60624, 60625, 60626,        // A
        60620, 60621, 60622,        // S
        60989, 60990,               // 大冰, 小冰
        63987, 63979,               // 截击指挥系统
        63986, 60992,               // 截击无人机
        63985, 60991                // 截击系统
    ];

    private static readonly HashSet<uint> HealthIconIDs =
    [
        60902, 60904,               // 大冰, 小冰
        60999, 60998                // 截击无人机, 截击系统
    ];

    private static readonly HashSet<uint> ControlPointIconIDs =
    [
        60585, 60586, 60587, 60588, // B
        60589, 60590, 60591, 60592, // A
        60593, 60594, 60595, 60596  // S
    ];

    private static readonly HashSet<uint> IgnoredIconIDs =
    [
        60484, 60485, 60486
    ];

    private static readonly HashSet<uint> NeutralControlPointIconIDs =
    [
        60585, 60589, 60593
    ];

    private static readonly HashSet<uint> AllTrackedIconIDs =
    [
        .. CountdownIconIDs,
        .. HealthIconIDs,
        .. ControlPointIconIDs
    ];

    private static readonly Dictionary<uint, int> SealRockControlPointScores = new()
    {
        [60585] = 80,  [60586] = 80,  [60587] = 80,  [60588] = 80,
        [60589] = 120, [60590] = 120, [60591] = 120, [60592] = 120,
        [60593] = 160, [60594] = 160, [60595] = 160, [60596] = 160
    };

    private static readonly Dictionary<uint, int> AzimSteppeControlPointScores = new()
    {
        [60585] = 50,  [60586] = 50,  [60587] = 50,  [60588] = 50,
        [60589] = 100, [60590] = 100, [60591] = 100, [60592] = 100,
        [60593] = 200, [60594] = 200, [60595] = 200, [60596] = 200
    };

    private static readonly Dictionary<PlayerIconType, (uint IconID, float FixedScale)> PlayerIcons = new()
    {
        [PlayerIconType.Friend]             = (60424, 1.8f),
        [PlayerIconType.Party]              = (60421, 1.0f),
        [PlayerIconType.Alliance]           = (60403, 1.0f),
        [PlayerIconType.Other]              = (60909, 1.2f),
        [PlayerIconType.PvPDead]            = (60909, 1.2f),
        [PlayerIconType.PvPMaelstrom]       = (60359, 1.0f),
        [PlayerIconType.PvPTwinAdder]       = (60360, 1.0f),
        [PlayerIconType.PvPImmortalFlames]  = (60361, 1.0f)
    };

    [GeneratedRegex(@"(\d+):(\d+)")]
    private static partial Regex CountdownRegex();

    [GeneratedRegex(@"(\d+)%")]
    private static partial Regex PercentRegex();

    #endregion

    private class Config : ModuleConfiguration
    {
        // 地图标记配置
        public float TextScale             = 1.5f;
        public int   TextOffsetY;
        public bool  ShowCountdown         = true;
        public bool  ShowHealthPercent     = true;
        public bool  ShowControlPointScore = true;

        // 玩家雷达配置
        public float            DotRadius               = 1.0f;
        public float            JobIconSize             = 1.4f;
        public int              JobIconStyle            = 1;
        public bool             HideLoadingRangeInPVP   = false;
        public bool             HideFriendlyInPVP       = true;
        public HashSet<uint>    HighlightedJobs         = [];

        // 非 PVP 设置
        public bool ShowOutsideFrontline = false;

        public bool ShowFriendDots       = false;
        public bool ShowFriendNames      = true;
        public bool ShowFriendJobIcons   = true;

        public bool ShowPartyDots        = false;
        public bool ShowPartyNames       = true;
        public bool ShowPartyJobIcons    = true;

        public bool ShowAllianceDots     = true;
        public bool ShowAllianceNames    = false;
        public bool ShowAllianceJobIcons = false;

        public bool ShowOtherDots        = true;
        public bool ShowOtherNames       = false;
        public bool ShowOtherJobIcons    = false;
    }
}
