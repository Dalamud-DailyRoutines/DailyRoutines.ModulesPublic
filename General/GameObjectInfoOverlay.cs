using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DailyRoutines.ModulesPublic;

public class GameObjectInfoOverlay : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("GameObjectInfoOverlayTitle"),
        Description = GetLoc("GameObjectInfoOverlayDescription"),
        Category    = ModuleCategories.General,
        Author      = ["JiaXX"]
    };

    private static Config ModuleConfig = null!;
    private static readonly Dictionary<uint, Vector2> OverlayPositions = new();
    private static readonly List<GameObjectInfo> CachedGameObjects = [];

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>();
        FrameworkManager.Register(OnUpdate);

        Overlay ??= new Overlay(this);
        Overlay.Flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                        ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings |
                        ImGuiWindowFlags.AlwaysAutoResize;

        Overlay.IsOpen = true;
    }

    protected override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        CachedGameObjects.Clear();
        OverlayPositions.Clear();

        if (Overlay != null)
            Overlay.IsOpen = false;
    }

    protected override void ConfigUI()
    {
        ImGui.Text(GetLoc("GameObjectInfoOverlay-Settings"));
        ImGui.Separator();

        // 基础设置
        if (ImGui.SliderFloat(GetLoc("GameObjectInfoOverlay-Opacity"), ref ModuleConfig.Opacity, 0.1f, 1.0f))
            SaveConfig(ModuleConfig);

        if (ImGui.SliderFloat(GetLoc("GameObjectInfoOverlay-Range"), ref ModuleConfig.Range, 5f, 100f))
            SaveConfig(ModuleConfig);

        if (ImGui.SliderFloat(GetLoc("GameObjectInfoOverlay-FontScale"), ref ModuleConfig.FontScale, 0.5f, 2.0f))
            SaveConfig(ModuleConfig);

        if (ImGui.SliderInt(GetLoc("GameObjectInfoOverlay-MaxObjects"), ref ModuleConfig.MaxObjects, 1, 100))
            SaveConfig(ModuleConfig);

        ImGui.Spacing();

        // 对象类型选择
        if (ImGui.CollapsingHeader(GetLoc("GameObjectInfoOverlay-ObjectTypes"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Columns(2, "ObjectTypes", false);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowPlayers"), ref ModuleConfig.ShowPlayers))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowBattleCharas"), ref ModuleConfig.ShowBattleCharas))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowEventNpcs"), ref ModuleConfig.ShowEventNpcs))
                SaveConfig(ModuleConfig);

            ImGui.NextColumn();

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowLocalPlayer"), ref ModuleConfig.ShowLocalPlayer))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowBattleNpcs"), ref ModuleConfig.ShowBattleNpcs))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowEventObjs"), ref ModuleConfig.ShowEventObjs))
                SaveConfig(ModuleConfig);

            ImGui.Columns();
        }

        ImGui.Spacing();

        // 显示选项
        if (ImGui.CollapsingHeader(GetLoc("GameObjectInfoOverlay-DisplayOptions"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Columns(2, "DisplayOptions", false);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowEntityId"), ref ModuleConfig.ShowEntityID))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowPosition"), ref ModuleConfig.ShowPosition))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowDistance"), ref ModuleConfig.ShowDistance))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowStatusList"), ref ModuleConfig.ShowStatusList))
                SaveConfig(ModuleConfig);
            
            ImGui.NextColumn();

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowDataId"), ref ModuleConfig.ShowDataID))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowRotation"), ref ModuleConfig.ShowRotation))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowCastInfo"), ref ModuleConfig.ShowCastInfo))
                SaveConfig(ModuleConfig);

            if (ImGui.Checkbox(GetLoc("GameObjectInfoOverlay-ShowHealth"), ref ModuleConfig.ShowHealth))
                SaveConfig(ModuleConfig);
            
            ImGui.Columns();
        }

        ImGui.Spacing();
    }

    protected override void OverlayUI()
    {
        if (!IsScreenReady() || BetweenAreas) return;

        if (CachedGameObjects.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();

        foreach (var objInfo in CachedGameObjects)
        {
            if (!OverlayPositions.TryGetValue(objInfo.EntityID, out var screenPos)) continue;

            DrawObjectInfoAt(drawList, objInfo, screenPos);
        }
    }

    private static void DrawObjectInfoAt(ImDrawListPtr drawList, GameObjectInfo objInfo, Vector2 position)
    {
        var lines = new List<(string text, Vector4 color)>
        {
            (objInfo.Name, Yellow),
            ($"{GetLoc("Type")}: {objInfo.ObjectKind}", White)
        };

        if (ModuleConfig.ShowEntityID)
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-EntityID")}: {objInfo.EntityID}", White));

        if (ModuleConfig.ShowDataID)
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-DataID")}: {objInfo.DataID}", White));

        if (ModuleConfig.ShowPosition)
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-Position")}: {objInfo.Position.X:F1}, {objInfo.Position.Y:F1}, {objInfo.Position.Z:F1}", White));

        if (ModuleConfig.ShowRotation)
        {
            var radians = objInfo.Rotation;
            var degrees = radians * 180.0 / Math.PI % 360.0;
            if (degrees < 0) 
                degrees += 360.0;

            lines.Add(($"{GetLoc("GameObjectInfoOverlay-Rotation")}: {objInfo.Rotation:F3} ({degrees:F2}°)", White));
        }

        // 显示距离
        if (ModuleConfig.ShowDistance && objInfo.Distance >= 0)
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-Distance")}: {objInfo.Distance:F1}m", White));


        if (ModuleConfig.ShowHealth && objInfo.CurrentHP > 0)
        {
            var healthPercentage = objInfo.MaxHP > 0 ? (float)objInfo.CurrentHP / objInfo.MaxHP : 0f;
            var healthColor = healthPercentage switch
            {
                > 0.7f => Green,
                > 0.3f => Yellow,
                _ => Red
            };
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-Health")}: {objInfo.CurrentHP:N0}/{objInfo.MaxHP:N0} ({healthPercentage:P0})", healthColor));
        }

        if (ModuleConfig.ShowCastInfo && objInfo.IsCasting)
        {
            lines.Add((GetLoc("GameObjectInfoOverlay-Casting"), Orange));

            var actionName = "";
            if (LuminaGetter.TryGetRow<Lumina.Excel.Sheets.Action>(objInfo.CastActionID, out var actionRow))
                actionName = actionRow.Name.ExtractText();

            var actionText = !string.IsNullOrEmpty(actionName)
                ? $"{GetLoc("GameObjectInfoOverlay-CastAction")}: {objInfo.CastActionID} ({actionName})"
                : $"{GetLoc("GameObjectInfoOverlay-CastAction")}: {objInfo.CastActionID}";

            lines.Add((actionText, Orange));

            if (objInfo.CastRotation.HasValue)
            {
                var castDegrees = objInfo.CastRotation.Value * 180.0 / Math.PI % 360.0;
                if (castDegrees < 0)
                    castDegrees += 360.0;
                lines.Add(($"{GetLoc("GameObjectInfoOverlay-CastRotation")}: {objInfo.CastRotation.Value:F3} ({castDegrees:F2}°)", Orange));
            }

            if (!string.IsNullOrEmpty(objInfo.CastTargetName))
                lines.Add(($"{GetLoc("GameObjectInfoOverlay-CastTarget")}: {objInfo.CastTargetName}", Orange));

            var castProgress = objInfo.TotalCastTime > 0 ? objInfo.CurrentCastTime / objInfo.TotalCastTime : 0f;
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-CastTime")}: {objInfo.CurrentCastTime:F1}s / {objInfo.TotalCastTime:F1}s ({castProgress:P0})", Orange));
        }

        if (ModuleConfig.ShowStatusList && objInfo.StatusEffects.Count > 0)
        {
            lines.Add(($"{GetLoc("GameObjectInfoOverlay-StatusEffects")} ({objInfo.StatusEffects.Count}):", Cyan));
            foreach (var status in objInfo.StatusEffects.Take(5)) // 只显示前5个状态以节省空间
            {
                var timeColor = status.RemainingTime < 5 ? Red : White;

                // 获取状态名称
                var statusName = "";
                if (LuminaGetter.TryGetRow<Lumina.Excel.Sheets.Status>(status.StatusID, out var statusRow))
                    statusName = statusRow.Name.ExtractText();

                var statusText = !string.IsNullOrEmpty(statusName)
                    ? $"  {status.StatusID} ({statusName}): {status.RemainingTime:F1}s"
                    : $"  {status.StatusID}: {status.RemainingTime:F1}s";

                if (status.Param > 0)
                    statusText += $" [{status.Param}]";

                lines.Add((statusText, timeColor));
            }
        }

        if (lines.Count == 0) return;
        
        var fontSize = 13f * ModuleConfig.FontScale;
        var lineHeight = fontSize + 2f;
        var maxWidth = fontSize * 30;
        var totalHeight = (lines.Count * lineHeight) + 8f;
        var bgMin = position + new Vector2(-4, -4);
        var bgMax = position + new Vector2(maxWidth + 4, totalHeight + 4);
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, ModuleConfig.Opacity * 0.85f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, ModuleConfig.Opacity * 0.7f));

        drawList.AddRectFilled(bgMin, bgMax, bgColor);
        drawList.AddRect(bgMin, bgMax, borderColor, 2f);

        // 绘制文本
        var currentPos = position;
        foreach (var (text, color) in lines)
        {
            var finalColor = color with { W = ModuleConfig.Opacity };
            var textColor = ImGui.ColorConvertFloat4ToU32(finalColor);
            
            // 获取默认字体并设置大小
            var font = ImGui.GetFont();
            var scaledSize = font.FontSize * ModuleConfig.FontScale;
            drawList.AddText(font, scaledSize, currentPos, textColor, text);
            
            currentPos.Y += lineHeight;
        }
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!IsScreenReady() || BetweenAreas) return;

        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        CachedGameObjects.Clear();
        OverlayPositions.Clear();

        foreach (var obj in DService.ObjectTable)
        {
            if (obj.EntityId == 0) continue;
            if (Vector3.Distance(localPlayer.Position, obj.Position) > ModuleConfig.Range) continue;
            if (!ShouldShowObject(obj)) continue;


            if (!DService.Gui.WorldToScreen(obj.Position, out var screenPos)) continue;
            var objInfo = CreateGameObjectInfo(obj);
            CachedGameObjects.Add(objInfo);
            OverlayPositions[obj.EntityId] = screenPos;
            if (CachedGameObjects.Count >= ModuleConfig.MaxObjects) break; // 限制显示对象数量以保证性能
        }
    }

    private static bool ShouldShowObject(IGameObject obj)
    {
        var localPlayer = DService.ObjectTable.LocalPlayer;

        return obj.ObjectKind switch
        {
            ObjectKind.Player when obj.Equals(localPlayer) => ModuleConfig.ShowLocalPlayer,
            ObjectKind.Player => ModuleConfig.ShowPlayers,
            ObjectKind.BattleNpc => ModuleConfig.ShowBattleNpcs,
            ObjectKind.EventNpc => ModuleConfig.ShowEventNpcs,
            ObjectKind.EventObj => ModuleConfig.ShowEventObjs,
            _ when obj is IBattleChara => ModuleConfig.ShowBattleCharas,
            _ => false
        };
    }

    private static GameObjectInfo CreateGameObjectInfo(IGameObject obj)
    {
        var localPlayer = DService.ObjectTable.LocalPlayer;
        var distance = localPlayer != null ? Vector3.Distance(localPlayer.Position, obj.Position) : -1f;

        var objInfo = new GameObjectInfo
        {
            EntityID = obj.EntityId,
            DataID = obj.DataId,
            Name = obj.Name.TextValue,
            ObjectKind = obj.ObjectKind,
            Position = obj.Position,
            Rotation = obj.Rotation,
            Distance = distance,
        };

        if (obj is IBattleChara battleChara)
        {
            objInfo.CurrentHP = battleChara.CurrentHp;
            objInfo.MaxHP = battleChara.MaxHp;
            objInfo.IsCasting = battleChara.IsCasting;
            objInfo.CastActionID = battleChara.CastActionId;
            objInfo.CurrentCastTime = battleChara.CurrentCastTime;
            objInfo.TotalCastTime = battleChara.TotalCastTime;

            if (battleChara.IsCasting)
            {
                unsafe
                {
                    var character = (Character*)obj.Address;
                    objInfo.CastRotation = character->CastRotation;
                }

                var castTargetID = battleChara.CastTargetObjectId;
                if (castTargetID != 0)
                {
                    var castTarget = DService.ObjectTable.SearchById(castTargetID);
                    objInfo.CastTargetName = castTarget?.Name.TextValue ?? $"ID:{castTargetID}";
                }
                else
                    objInfo.CastTargetName = GetLoc("GameObjectInfoOverlay-NoTarget");
            }

            objInfo.StatusEffects.Clear();
            foreach (var status in battleChara.StatusList)
            {
                if (status.StatusId == 0) continue;
                objInfo.StatusEffects.Add(new StatusInfo
                {
                    StatusID = status.StatusId,
                    RemainingTime = status.RemainingTime,
                    Param = status.Param
                });
            }
        }

        return objInfo;
    }

    private class Config : ModuleConfiguration
    {
        public float Opacity = 0.85f;
        public float Range = 30f;
        public float FontScale = 1.0f;
        public int MaxObjects = 10;

        public bool ShowPlayers = true;
        public bool ShowLocalPlayer;
        public bool ShowBattleCharas = true;
        public bool ShowBattleNpcs = true;
        public bool ShowEventNpcs;
        public bool ShowEventObjs;

        public bool ShowEntityID = true;
        public bool ShowDataID = true;
        public bool ShowPosition = true;
        public bool ShowRotation;
        public bool ShowDistance = true;
        public bool ShowCastInfo = true;
        public bool ShowStatusList = true;
        public bool ShowHealth = true;
    }

    private class GameObjectInfo
    {
        public uint EntityID { get; init; }
        public uint DataID { get; init; }
        public string Name { get; init; } = string.Empty;
        public ObjectKind ObjectKind { get; init; }
        public Vector3 Position { get; init; }
        public float Rotation { get; init; }
        public float Distance { get; init; }
        public uint CurrentHP { get; set; }
        public uint MaxHP { get; set; }
        public bool IsCasting { get; set; }
        public uint CastActionID { get; set; }
        public float CurrentCastTime { get; set; }
        public float TotalCastTime { get; set; }
        public string CastTargetName { get; set; } = string.Empty;
        public List<StatusInfo> StatusEffects { get; set; } = [];
        public float? CastRotation { get; set; }
    }

    private class StatusInfo
    {
        public uint StatusID { get; init; }
        public float RemainingTime { get; init; }
        public ushort Param { get; init; }
    }
}
