using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Action = Lumina.Excel.Sheets.Action;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace DailyRoutines.ModulesPublic;

public class AutoReplaceLocationAction : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoReplaceLocationActionTitle"),
        Description = Lang.Get("AutoReplaceLocationActionDescription"),
        Category    = ModuleCategory.Action
    };
    
    // 返回值为 GameObject*, 无对象则为 0
    private static readonly CompSig                              ParseActionCommandArgSig = new("E8 ?? ?? ?? ?? 33 ED 4C 8B F8");
    private delegate        nint                                 ParseActionCommandArgDelegate(nint a1, nint arg, bool a3, bool a4);
    private                 Hook<ParseActionCommandArgDelegate>? ParseActionCommandArgHook;

    private Config? config;
    
    private readonly ContentSelectCombo contentSelectCombo = new("Blakclist");

    private bool isNeedToReplace;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        contentSelectCombo.SelectedIDs = config.BlacklistContents;

        UseActionManager.Instance().RegPreUseActionLocation(OnPreUseActionLocation);
        ExecuteCommandManager.Instance().RegPreComplexLocation(OnPreExecuteCommandComplexLocation);

        ParseActionCommandArgHook ??= ParseActionCommandArgSig.GetHook<ParseActionCommandArgDelegate>(ParseActionCommandArgDetour);
        ParseActionCommandArgHook.Enable();
    }
    
    protected override void Uninit()
    {
        UseActionManager.Instance().Unreg(OnPreUseActionLocation);
        ExecuteCommandManager.Instance().Unreg(OnPreExecuteCommandComplexLocation);
    }

    protected override void ConfigUI()
    {
        if (config == null) return;

        DrawConfigCustom();

        ImGui.NewLine();

        DrawConfigBothers();

        ImGui.NewLine();

        DrawConfigActions();
    }

    private void DrawConfigBothers()
    {
        ImGui.TextColored(KnownColor.LightSteelBlue.ToVector4(), $"{Lang.Get("Settings")}");

        using var indent = ImRaii.PushIndent();

        // 通知发送
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        // 启用 <center> 命令参数
        if (ImGui.Checkbox(Lang.Get("AutoReplaceLocationAction-EnableCenterArg"), ref config.EnableCenterArgument))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoReplaceLocationAction-EnableCenterArgHelp"), 20f * GlobalUIScale);

        ImGui.Spacing();
        ImGui.Spacing();

        // 重定向距离
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoReplaceLocationAction-AdjustDistance")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);
            ImGui.InputFloat("###AdjustDistanceInput", ref config.AdjustDistance, 0, 0, "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            ImGuiOm.HelpMarker(Lang.Get("AutoReplaceLocationAction-AdjustDistanceHelp"));
        }

        ImGui.Spacing();

        // 黑名单副本
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoReplaceLocationAction-BlacklistContents")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (contentSelectCombo.DrawCheckbox())
            {
                config.BlacklistContents = contentSelectCombo.SelectedIDs.ToHashSet();
                config.Save(this);
            }
        }
    }

    private void DrawConfigActions()
    {
        ImGui.TextColored(KnownColor.LightSteelBlue.ToVector4(), $"{Lang.Get("Action")}");

        using var indent = ImRaii.PushIndent();

        // 技能启用情况
        foreach (var actionPair in config.EnabledActions)
        {
            if (!LuminaGetter.TryGetRow<Action>(actionPair.Key, out var action)) continue;
            var state = actionPair.Value;

            if (ImGui.Checkbox($"###{actionPair.Key}_{action.Name.ToString()}", ref state))
            {
                config.EnabledActions[actionPair.Key] = state;
                config.Save(this);
            }

            ImGui.SameLine();
            ImGuiOm.TextImage(action.Name.ToString(), ImageHelper.GetGameIcon(action.Icon).Handle, ScaledVector2(20f));
        }

        foreach (var actionPair in config.EnabledPetActions)
        {
            if (!LuminaGetter.TryGetRow<PetAction>(actionPair.Key, out var action)) continue;
            var state = actionPair.Value;

            if (ImGui.Checkbox($"###{actionPair.Key}_{action.Name.ToString()}", ref state))
            {
                config.EnabledPetActions[actionPair.Key] = state;
                config.Save(this);
            }

            ImGui.SameLine();
            ImGuiOm.TextImage(action.Name.ToString(), ImageHelper.GetGameIcon((uint)action.Icon).Handle, ScaledVector2(20f));
        }
    }

    private unsafe void DrawConfigCustom()
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        ImGui.TextColored(KnownColor.LightSteelBlue.ToVector4(), $"{Lang.Get("AutoReplaceLocationAction-CenterPointData")}");

        using var indent = ImRaii.PushIndent();

        var       isMapValid             = GameState.TerritoryTypeData is { RowId: > 0, ContentFinderCondition.RowId: > 0 };
        var       currentMapPlaceName    = isMapValid ? GameState.MapData.PlaceName.Value.Name.ToString() : string.Empty;
        var       currentMapPlaceNameSub = isMapValid ? GameState.MapData.PlaceNameSub.Value.Name.ToString() : string.Empty;
        using var disabled               = ImRaii.Disabled(!isMapValid);

        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("CurrentMap")}");

        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted($"{currentMapPlaceName}" + (string.IsNullOrEmpty(currentMapPlaceNameSub) ? string.Empty : $" / {currentMapPlaceNameSub}"));

            if (ImGui.Button($"{Lang.Get("OpenMap")}"))
            {
                agent->OpenMap(GameState.Map, GameState.TerritoryType, null, MapType.Teleport);
                MarkCenterPoint();
            }

            ImGui.SameLine();
            if (ImGui.Button($"{Lang.Get("AutoReplaceLocationAction-ClearMarks")}"))
                ClearCenterPoint();
        }

        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoReplaceLocationAction-CustomCenterPoint")}");

        using (ImRaii.PushIndent())
        {
            using (ImRaii.Disabled(agent->FlagMarkerCount == 0 || agent->FlagMapMarkers[0].MapId != GameState.Map))
            {
                if (ImGui.Button(Lang.Get("AutoReplaceLocationAction-AddFlagMarker")))
                {
                    config.CustomMarkers.TryAdd(GameState.Map, []);
                    config.CustomMarkers[GameState.Map].Add(new(agent->FlagMapMarkers[0].XFloat, agent->FlagMapMarkers[0].YFloat));
                    config.Save(this);

                    agent->FlagMarkerCount = 0;
                    MarkCenterPoint();
                }
            }

            var localPlayer = DService.Instance().ObjectTable.LocalPlayer;

            using (ImRaii.Disabled(localPlayer == null))
            {
                ImGui.SameLine();

                if (ImGui.Button(Lang.Get("AutoReplaceLocationAction-AddPlayerPosition")))
                {
                    config.CustomMarkers.TryAdd(GameState.Map, []);
                    config.CustomMarkers[GameState.Map].Add(localPlayer.Position.ToVector2());
                    config.Save(this);

                    MarkCenterPoint();
                }
            }

            ImGui.SameLine();

            if (ImGui.Button(Lang.Get("DeleteAll")))
            {
                config.CustomMarkers.TryAdd(GameState.Map, []);
                config.CustomMarkers[GameState.Map].Clear();
                config.Save(this);

                MarkCenterPoint();
            }
        }

        return;

        void MarkCenterPoint()
        {
            ClearCenterPoint();

            // 地图数据
            if (ZoneMapMarkers.TryGetValue(GameState.Map, out var markers))
            {
                markers.ForEach
                (x =>
                    {
                        var mapPosition = x.Value.ToVector3(0);
                        mapPosition.X += GameState.MapData.OffsetX;
                        mapPosition.Z += GameState.MapData.OffsetY;
                        agent->AddMapMarker(mapPosition, 60931);
                    }
                );
            }

            // 自动居中
            var mapAutoCenter = PositionHelper.MapToWorld(new(6.125f), GameState.MapData).ToVector3(0);
            mapAutoCenter.X += GameState.MapData.OffsetX;
            mapAutoCenter.Z += GameState.MapData.OffsetY;
            agent->AddMapMarker(mapAutoCenter, 60932);

            // 自定义
            if (config.CustomMarkers.TryGetValue(GameState.MapData.RowId, out var cMarkers))
            {
                cMarkers.ForEach
                (x =>
                    {
                        var mapPosition = x.ToVector3(0);
                        mapPosition.X += GameState.MapData.OffsetX;
                        mapPosition.Z += GameState.MapData.OffsetY;
                        agent->AddMapMarker(mapPosition, 60933);
                    }
                );
            }

            agent->OpenMap(GameState.Map, GameState.TerritoryType, null, MapType.Teleport);
        }

        void ClearCenterPoint()
        {
            agent->ResetMapMarkers();
            agent->ResetMiniMapMarkers();
        }
    }

    private void OnPreUseActionLocation
    (
        ref bool       isPrevented,
        ref ActionType type,
        ref uint       actionID,
        ref ulong      targetID,
        ref Vector3    location,
        ref uint       extraParam,
        ref byte       a7
    )
    {
        if (type != ActionType.Action) return;

        if (!config.EnabledActions.TryGetValue(actionID, out var isEnabled) || !isEnabled && !isNeedToReplace)
        {
            isNeedToReplace = false;
            return;
        }

        isNeedToReplace = false;

        if (config.BlacklistContents.Contains(GameState.ContentFinderCondition)) return;
        if (!ZoneMapMarkers.TryGetValue(GameState.Map, out var markers))
            markers = [];

        var modifiedLocation = location;

        if (HandleCustomLocation(ref modifiedLocation)       ||
            HandleMapLocation(markers, ref modifiedLocation) ||
            HandlePresetCenterLocation(ref modifiedLocation))
        {
            location = modifiedLocation;
            NotifyLocationRedirect(modifiedLocation);
        }
    }

    private void OnPreExecuteCommandComplexLocation
    (
        ref bool                      isPrevented,
        ref ExecuteCommandComplexFlag command,
        ref Vector3                   location,
        ref uint                      param1,
        ref uint                      param2,
        ref uint                      param3,
        ref uint                      param4
    )
    {
        if (command != ExecuteCommandComplexFlag.PetAction || param1 != 3) return;

        if (!config.EnabledPetActions.TryGetValue(3, out var isEnabled) || !isEnabled && !isNeedToReplace)
        {
            isNeedToReplace = false;
            return;
        }

        isNeedToReplace = false;

        if (config.BlacklistContents.Contains(GameState.ContentFinderCondition)) return;
        if (!ZoneMapMarkers.TryGetValue(GameState.Map, out var markers))
            markers = [];

        var modifiedLocation = location;

        if (HandleCustomLocation(ref modifiedLocation)       ||
            HandleMapLocation(markers, ref modifiedLocation) ||
            HandlePresetCenterLocation(ref modifiedLocation))
        {
            location    = modifiedLocation;
            isPrevented = true;

            ExecuteCommandManager.Instance().ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, modifiedLocation, 3);
            NotifyLocationRedirect(location);
        }
    }

    private unsafe nint ParseActionCommandArgDetour(nint a1, nint arg, bool a3, bool a4)
    {
        var original = ParseActionCommandArgHook.Original(a1, arg, a3, a4);
        if (!config.EnableCenterArgument ||
            config.BlacklistContents.Contains(GameState.ContentFinderCondition)) return original;

        var parsedArg = MemoryHelper.ReadSeStringNullTerminated(arg).TextValue;
        if (!parsedArg.Equals("<center>")) return original;

        isNeedToReplace = true;
        return (nint)Control.GetLocalPlayer();
    }

    // 自定义中心点场中
    private bool HandleCustomLocation(ref Vector3 sourceLocation)
    {
        if (!config.CustomMarkers.TryGetValue(GameState.Map, out var markers)) return false;

        var modifiedLocation = markers
                               .MinBy
                               (x => Vector2.DistanceSquared
                                (
                                    DService.Instance().ObjectTable.LocalPlayer.Position.ToVector2(),
                                    x
                                )
                               )
                               .ToPlayerHeight();

        return UpdateLocationIfClose(ref sourceLocation, modifiedLocation);
    }

    // 地图标记场中
    private bool HandleMapLocation(Dictionary<MapMarker, Vector2>? markers, ref Vector3 sourceLocation)
    {
        if (markers is not { Count: > 0 }) return false;

        var sourceCopy = sourceLocation;
        var modifiedLocation = markers.Values
                                      .Select(x => x.ToPlayerHeight() as Vector3?)
                                      .FirstOrDefault
                                      (x => x.HasValue &&
                                            Vector3.DistanceSquared(x.Value, sourceCopy) < 900
                                      );
        if (modifiedLocation == null) return false;

        return UpdateLocationIfClose(ref sourceLocation, (Vector3)modifiedLocation);
    }

    // 预设场中
    private unsafe bool HandlePresetCenterLocation(ref Vector3 sourceLocation)
    {
        if (!LuminaGetter.TryGetRow<ContentFinderCondition>
                (GameMain.Instance()->CurrentContentFinderConditionId, out var content) ||
            content.ContentType.RowId is not (4 or 5)                                   ||
            !LuminaGetter.TryGetRow<Map>(GameState.Map, out var map))
            return false;

        var modifiedLocation = PositionHelper.TextureToWorld(new(1024f), map).ToPlayerHeight();
        return UpdateLocationIfClose(ref sourceLocation, modifiedLocation);
    }

    private bool UpdateLocationIfClose(ref Vector3 sourceLocation, Vector3 candidateLocation)
    {
        if (Vector3.DistanceSquared(sourceLocation, candidateLocation) >
            config.AdjustDistance * config.AdjustDistance) return false;

        sourceLocation = candidateLocation;
        return true;
    }

    private void NotifyLocationRedirect(Vector3 location)
    {
        if (config.SendChat)
        {
            var mapPos = PositionHelper.WorldToMap(location.ToVector2(), GameState.MapData);
            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoReplaceLocationAction-RedirectMessage",
                    SeString.CreateMapLink(GameState.TerritoryType, GameState.Map, mapPos.X, mapPos.Y)
                )
            );
        }

        if (config.SendNotification)
        {
            NotifyHelper.Instance().NotificationSuccess
            (
                Lang.Get
                (
                    "AutoReplaceLocationAction-RedirectMessage",
                    $"[{location.X:F1}, {location.Y:F1}, {location.Z:F1}]"
                )
            );
        }
    }
    
    private class Config : ModuleConfig
    {
        public float         AdjustDistance    = 15;
        public HashSet<uint> BlacklistContents = [];

        public Dictionary<uint, List<Vector2>> CustomMarkers        = [];
        public bool                            EnableCenterArgument = true;

        public Dictionary<uint, bool> EnabledActions = new()
        {
            [7439]  = true, // 地星
            [25862] = true, // 礼仪之铃
            [3569]  = true, // 庇护所
            [188]   = true  // 野战治疗阵
        };

        public Dictionary<uint, bool> EnabledPetActions = new()
        {
            [3] = true // 移动
        };

        public bool SendChat         = true;
        public bool SendNotification = true;
    }
    
    #region 常量
    
    // MapID - Markers
    private static Dictionary<uint, Dictionary<MapMarker, Vector2>> ZoneMapMarkers
    {
        get
        {
            if (field != null) return field;

            field = [];

            foreach (var map in LuminaGetter.Get<Map>()
                                            .Where(x => x.TerritoryType is { RowId: > 0, Value.ContentFinderCondition.RowId: > 0 }))
            {
                foreach (var marker in map.GetMapMarkers())
                {
                    if (marker.Icon == 60442)
                    {
                        field.TryAdd(map.RowId, []);
                        field[map.RowId].TryAdd(marker, PositionHelper.TextureToWorld(marker.GetPosition(), map));
                    }
                }
            }

            return field;
        }
    }
    
    #endregion
}
