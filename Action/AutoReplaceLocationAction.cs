using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace DailyRoutines.Modules;

public class AutoReplaceLocationAction : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoReplaceLocationActionTitle"),
        Description = GetLoc("AutoReplaceLocationActionDescription"),
        Category = ModuleCategories.Action,
    };

    // 返回值为 GameObject*, 无对象则为 0
    private static readonly CompSig ParseActionCommandArgSig = new("E8 ?? ?? ?? ?? 45 33 E4 4C 8B F8 48 85 C0 74 ?? 48 8B 00");
    private delegate nint ParseActionCommandArgDelegate(nint a1, nint arg, bool a3, bool a4);
    private static Hook<ParseActionCommandArgDelegate>? ParseActionCommandArgHook;

    private static Config? ModuleConfig;

    // MapID - Markers
    private static readonly Dictionary<uint, Dictionary<MapMarker, Vector2>> ZoneMapMarkers = [];

    private static string ContentSearchInput = string.Empty;

    static AutoReplaceLocationAction()
    {
        LuminaCache.Get<Map>()
                   .Where(x => x.TerritoryType.RowId > 0 && x.TerritoryType.Value.ContentFinderCondition.RowId > 0)
                   .ForEach(map =>
                   {
                       GetMapMarkers(map.RowId)
                           .ForEach(marker =>
                           {
                               if (marker.Icon == 60442)
                               {
                                   ZoneMapMarkers.TryAdd(map.RowId, []);
                                   ZoneMapMarkers[map.RowId].TryAdd(marker, TextureToWorld(marker.GetPosition(), map));
                               }
                           });
                   });
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        UseActionManager.Register(OnPreUseActionLocation);
        ExecuteCommandManager.Register(OnPreExecuteCommandComplexLocation);

        ParseActionCommandArgHook ??=
            ParseActionCommandArgSig.GetHook<ParseActionCommandArgDelegate>(ParseActionCommandArgDetour);
        ParseActionCommandArgHook.Enable();
    }

    public override void ConfigUI()
    {
        if (ModuleConfig == null) return;

        DrawConfigCustom();

        ImGui.Spacing();
        DrawConfigBothers();

        ImGui.Spacing();
        DrawConfigActions();
    }

    private void DrawConfigBothers()
    {
        ImGui.TextColored(LightSteelBlue1, $"{GetLoc("Settings")}");
        using var indent = ImRaii.PushIndent();

        // 通知发送
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendMessage))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        // 启用 <center> 命令参数
        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("AutoReplaceLocationAction-EnableCenterArg"), ref ModuleConfig.EnableCenterArgument))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoReplaceLocationAction-EnableCenterArgHelp"), 20f * GlobalFontScale);

        // 重定向距离
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoReplaceLocationAction-AdjustDistance")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat("###AdjustDistanceInput", ref ModuleConfig.AdjustDistance, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoReplaceLocationAction-AdjustDistanceHelp"));

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        // 黑名单副本
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoReplaceLocationAction-BlacklistContents")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        if (ContentSelectCombo(ref ModuleConfig.BlacklistContent, ref ContentSearchInput))
            SaveConfig(ModuleConfig);
    }

    private void DrawConfigActions()
    {
        ImGui.TextColored(LightSteelBlue1, $"{GetLoc("Action")}");
        using var indent = ImRaii.PushIndent();

        // 技能启用情况
        foreach (var actionPair in ModuleConfig.EnabledActions)
        {
            if (!LuminaCache.TryGetRow<Action>(actionPair.Key, out var action)) continue;
            var state = actionPair.Value;

            if (ImGui.Checkbox($"###{actionPair.Key}_{action.Name.ExtractText()}", ref state))
            {
                ModuleConfig.EnabledActions[actionPair.Key] = state;
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            ImGuiOm.TextImage(action.Name.ExtractText(), ImageHelper.GetIcon(action.Icon).ImGuiHandle, ScaledVector2(20f));
        }

        foreach (var actionPair in ModuleConfig.EnabledPetActions)
        {
            if (!LuminaCache.TryGetRow<PetAction>(actionPair.Key, out var action)) continue;
            var state = actionPair.Value;

            if (ImGui.Checkbox($"###{actionPair.Key}_{action.Name.ExtractText()}", ref state))
            {
                ModuleConfig.EnabledPetActions[actionPair.Key] = state;
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
            ImGuiOm.TextImage(action.Name.ExtractText(), ImageHelper.GetIcon((uint)action.Icon).ImGuiHandle, ScaledVector2(20f));
        }
    }

    private unsafe void DrawConfigCustom()
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        ImGui.TextColored(LightSteelBlue1, $"{GetLoc("AutoReplaceLocationAction-CenterPointData")}");
        using var indent = ImRaii.PushIndent();

        var isMapValid = LuminaCache.TryGetRow<Map>(DService.ClientState.MapId, out var currentMapData) && currentMapData.TerritoryType.RowId > 0 &&
                         currentMapData.TerritoryType.Value.ContentFinderCondition.RowId > 0;
        var currentMapPlaceName = isMapValid ? currentMapData.PlaceName.Value.Name.ExtractText() : "";
        var currentMapPlaceNameSub = isMapValid ? currentMapData.PlaceNameSub.Value.Name.ExtractText() : "";
        using var disabled = ImRaii.Disabled(!isMapValid);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("CurrentMap")}:");

        ImGui.SameLine();
        ImGui.Text($"{currentMapPlaceName} / {currentMapPlaceNameSub}");

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("OpenMap")}"))
        {
            agent->OpenMap(currentMapData.RowId, currentMapData.TerritoryType.RowId, null, MapType.Teleport);
            MarkCenterPoint();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{GetLoc("AutoReplaceLocationAction-ClearMarks")}"))
            ClearCenterPoint();

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("AutoReplaceLocationAction-CustomCenterPoint")}:");

        using (ImRaii.Disabled(agent->IsFlagMarkerSet != 1 || agent->FlagMapMarker.MapId != DService.ClientState.MapId))
        {
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("AutoReplaceLocationAction-AddFlagMarker")))
            {
                ModuleConfig.CustomMarkers.TryAdd(DService.ClientState.MapId, []);
                ModuleConfig.CustomMarkers[DService.ClientState.MapId].Add(new(agent->FlagMapMarker.XFloat, agent->FlagMapMarker.YFloat));
                SaveConfig(ModuleConfig);

                agent->IsFlagMarkerSet = 0;
                MarkCenterPoint();
            }
        }

        var localPlayer = DService.ClientState.LocalPlayer;
        using (ImRaii.Disabled(localPlayer == null))
        {
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("AutoReplaceLocationAction-AddPlayerPosition")))
            {
                ModuleConfig.CustomMarkers.TryAdd(DService.ClientState.MapId, []);
                ModuleConfig.CustomMarkers[DService.ClientState.MapId].Add(localPlayer.Position.ToVector2());
                SaveConfig(ModuleConfig);

                MarkCenterPoint();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(GetLoc("DeleteAll")))
        {
            ModuleConfig.CustomMarkers.TryAdd(DService.ClientState.MapId, []);
            ModuleConfig.CustomMarkers[DService.ClientState.MapId].Clear();
            SaveConfig(ModuleConfig);

            MarkCenterPoint();
        }

        void MarkCenterPoint()
        {
            ClearCenterPoint();

            // 地图数据
            if (ZoneMapMarkers.TryGetValue(currentMapData.RowId, out var markers))
            {
                markers.ForEach(x =>
                {
                    var mapPosition = x.Value.ToVector3(0);
                    mapPosition.X += currentMapData.OffsetX;
                    mapPosition.Z += currentMapData.OffsetY;
                    agent->AddMapMarker(mapPosition, 60931);
                });
            }

            // 自动居中
            var mapAutoCenter = MapToWorld(new Vector2(6.125f), currentMapData).ToVector3(0);
            mapAutoCenter.X += currentMapData.OffsetX;
            mapAutoCenter.Z += currentMapData.OffsetY;
            agent->AddMapMarker(mapAutoCenter, 60932);

            // 自定义
            if (ModuleConfig.CustomMarkers.TryGetValue(currentMapData.RowId, out var cMarkers))
            {
                cMarkers.ForEach(x =>
                {
                    var mapPosition = x.ToVector3(0);
                    mapPosition.X += currentMapData.OffsetX;
                    mapPosition.Z += currentMapData.OffsetY;
                    agent->AddMapMarker(mapPosition, 60933);
                });
            }

            agent->OpenMap(currentMapData.RowId, currentMapData.TerritoryType.RowId, null, MapType.Teleport);
        }

        void ClearCenterPoint()
        {
            agent->ResetMapMarkers();
            agent->ResetMiniMapMarkers();
        }
    }

    private static void OnPreUseActionLocation(
        ref bool isPrevented, ref ActionType type, ref uint actionID,
        ref ulong targetID, ref Vector3 location, ref uint extraParam)
    {
        if (type != ActionType.Action) return;
        if (!ModuleConfig.EnabledActions.TryGetValue(actionID, out var isEnabled) || !isEnabled) return;

        if (ModuleConfig.BlacklistContent.Contains(DService.ClientState.TerritoryType)) return;
        if (!ZoneMapMarkers.TryGetValue(DService.ClientState.MapId, out var markers)) markers = [];

        var modifiedLocation = location;
        if (HandleCustomLocation(ref modifiedLocation) ||
            HandleMapLocation(markers, ref modifiedLocation) ||
            HandlePresetCenterLocation(ref modifiedLocation))
        {
            isPrevented = true;

            UseActionManager.UseActionLocation(type, actionID, targetID, modifiedLocation, extraParam);
            NotifyLocationRedirect(modifiedLocation);
        }
    }

    private static void OnPreExecuteCommandComplexLocation(
        ref bool isPrevented, ref ExecuteCommandComplexFlag command, ref Vector3 location, ref int param1,
        ref int param2, ref int param3, ref int param4)
    {
        if (command != ExecuteCommandComplexFlag.PetAction || param1 != 3) return;
        if (!ModuleConfig.EnabledPetActions.TryGetValue(3, out var isEnabled) || !isEnabled) return;

        if (ModuleConfig.BlacklistContent.Contains(DService.ClientState.TerritoryType)) return;
        if (!ZoneMapMarkers.TryGetValue(DService.ClientState.MapId, out var markers)) markers = [];

        var modifiedLocation = location;
        if (HandleCustomLocation(ref modifiedLocation) ||
            HandleMapLocation(markers, ref modifiedLocation) ||
            HandlePresetCenterLocation(ref modifiedLocation))
        {
            location = modifiedLocation;
            isPrevented = true;

            ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, modifiedLocation, 3);
            NotifyLocationRedirect(location);
        }
    }

    private static unsafe nint ParseActionCommandArgDetour(nint a1, nint arg, bool a3, bool a4)
    {
        var original = ParseActionCommandArgHook.Original(a1, arg, a3, a4);
        if (!ModuleConfig.EnableCenterArgument ||
            ModuleConfig.BlacklistContent.Contains(DService.ClientState.TerritoryType)) return original;

        var parsedArg = MemoryHelper.ReadSeStringNullTerminated(arg).TextValue;
        if (!parsedArg.Equals("<center>")) return original;

        return (nint)Control.GetLocalPlayer();
    }

    // 自定义中心点场中
    private static bool HandleCustomLocation(ref Vector3 sourceLocation)
    {
        if (!ModuleConfig.CustomMarkers.TryGetValue(DService.ClientState.MapId, out var markers)) return false;

        var modifiedLocation = markers
                               .MinBy(x => Vector2.DistanceSquared(
                                            DService.ClientState.LocalPlayer.Position.ToVector2(), x))
                               .ToVector3();

        return UpdateLocationIfClose(ref sourceLocation, modifiedLocation);
    }

    // 地图标记场中
    private static bool HandleMapLocation(Dictionary<MapMarker, Vector2>? markers, ref Vector3 sourceLocation)
    {
        if (markers is not { Count: > 0 }) return false;

        var sourceCopy = sourceLocation;
        var modifiedLocation = markers.Values
                                      .Select(x => x.ToVector3() as Vector3?)
                                      .FirstOrDefault(x => x.HasValue && 
                                                           Vector3.DistanceSquared(x.Value, sourceCopy) < 900);
        if (modifiedLocation == null) return false;

        return UpdateLocationIfClose(ref sourceLocation, (Vector3)modifiedLocation);
    }

    // 预设场中
    private static unsafe bool HandlePresetCenterLocation(ref Vector3 sourceLocation)
    {
        if (!LuminaCache.TryGetRow<ContentFinderCondition>
                (GameMain.Instance()->CurrentContentFinderConditionId, out var content) ||
            content.ContentType.RowId is not (4 or 5)                                     ||
            !LuminaCache.TryGetRow<Map>(DService.ClientState.MapId, out var map))
            return false;
        
        var modifiedLocation = TextureToWorld(new(1024f), map).ToVector3();
        return UpdateLocationIfClose(ref sourceLocation, modifiedLocation);
    }

    private static bool UpdateLocationIfClose(ref Vector3 sourceLocation, Vector3 candidateLocation)
    {
        if (Vector3.DistanceSquared(sourceLocation, candidateLocation) >
            ModuleConfig.AdjustDistance * ModuleConfig.AdjustDistance) return false;

        sourceLocation = candidateLocation;
        return true;
    }

    private static void NotifyLocationRedirect(Vector3 location)
    {
        var message = GetLoc("AutoReplaceLocationAction-RedirectMessage", $"{location:F1}");

        if (ModuleConfig.SendMessage)
            Chat(message);
        if (ModuleConfig.SendNotification)
            NotificationSuccess(message);
    }

    public override void Uninit()
    {
        UseActionManager.Unregister(OnPreUseActionLocation);
        ExecuteCommandManager.Unregister(OnPreExecuteCommandComplexLocation);

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public Dictionary<uint, bool> EnabledActions = new()
        {
            { 7439, true },  // 地星
            { 25862, true }, // 礼仪之铃
            { 3569, true },  // 庇护所
            { 188, true },   // 野战治疗阵
        };

        public Dictionary<uint, bool> EnabledPetActions = new()
        {
            { 3, true }, // 移动
        };

        public Dictionary<uint, List<Vector2>> CustomMarkers = [];

        public bool SendMessage = true;
        public bool SendNotification = true;

        public float AdjustDistance = 15;
        public HashSet<uint> BlacklistContent = [];
        public bool EnableCenterArgument = true;
    }
}
