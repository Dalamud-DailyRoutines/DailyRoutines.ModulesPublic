using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Data;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using static DailyRoutines.Managers.CommandManager;
using Dalamud.Interface;
using OmenTools;
using OmenTools.Helpers;
using static DailyRoutines.Helpers.NotifyHelper;

namespace DailyRoutines.ModulesPublic
{
    public unsafe class PreventEntryIntoMapBoundaries : DailyModuleBase
    {
        public override ModuleInfo Info { get; } = new()
        {
            Title = GetLoc("PreventEntryIntoMapBoundariesTitle"),
            Description = GetLoc("PreventEntryIntoMapBoundariesDescription"),
            Category = ModuleCategories.Combat,
            Author = ["Nag0mi"]
        };

        private const string Command = "pdrfence";
        private Config? ModuleConfig;

       

        protected override void Init()
        {
            ModuleConfig = LoadConfig<Config>() ?? new Config();
            FrameworkManager.Register(OnFrameworkUpdate);
            DService.ClientState.TerritoryChanged += OnTerritoryChanged;

            AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("PreventEntryIntoMapBoundaries-CommandHelp") });
        }

        protected override void ConfigUI()
        {
            using var configChild = ImRaii.Child(GetLoc("PreventEntryIntoMapBoundaries-ConfigTitle"), new Vector2(0, 0), false);

            // 全局设置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-GlobalSettings"));

            // 添加当前区域按钮
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddCurrentZone")))
            {
                var currentZoneId = DService.ClientState.TerritoryType;

                if (currentZoneId == 0)
                {
                    ChatError(GetLoc("PreventEntryIntoMapBoundaries-InvalidZone"));
                    return;
                }

                if (!ModuleConfig.ZoneIds.Contains(currentZoneId))
                {
                    ModuleConfig.ZoneIds.Add(currentZoneId);
                    ModuleConfig.ZoneLimitList[currentZoneId] = new ZoneLimit(currentZoneId);

                    SaveConfig(ModuleConfig);
                    Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneAdded"), currentZoneId));
                }
                else
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneExists"), currentZoneId));
            }

            ImGui.Separator();

            // 显示调试信息选项
            var showDebug = ModuleConfig.ShowDebugInfo;
            if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundaries-ShowDebug"), ref showDebug))
            {
                ModuleConfig.ShowDebugInfo = showDebug;
                SaveConfig(ModuleConfig);
            }

            var showVisualization = ModuleConfig.ShowBoundaryVisualization;
            if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundaries-ShowVisualization"), ref showVisualization))
            {
                ModuleConfig.ShowBoundaryVisualization = showVisualization;
                SaveConfig(ModuleConfig);
            }

            // 死亡玩家数量配置
            var deathCount = ModuleConfig.DisableOnDeathCount;
            if (ImGui.SliderInt(GetLoc("PreventEntryIntoMapBoundaries-DeathThreshold"), ref deathCount, 0, 8, "%d"))
            {
                ModuleConfig.DisableOnDeathCount = deathCount;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Notice"));
            ImGui.SameLine(0, 5 * ImGuiHelpers.GlobalScale);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DeathWarning"));
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DeathRecommendation"));

            // 配置管理
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ConfigManagement"));
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-ExportAll")))
                ExportToClipboard(ModuleConfig);
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-Import")))
            {
                var imported = ImportFromClipboard<Config>();
                if (imported != null)
                {
                    ModuleConfig = imported;
                    SaveConfig(ModuleConfig);
                    Chat(GetLoc("PreventEntryIntoMapBoundaries-ImportSuccess"));
                }
                else
                    ChatError(GetLoc("PreventEntryIntoMapBoundaries-ImportFailed"));
            }
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-ExportCurrent")))
                ExportCurrentZoneConfig();

            ImGui.Separator();

            if (ModuleConfig.ZoneIds.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundaries-NoZones"));
                return;
            }

            // 显示所有已添加的区域
            for (var i = 0; i < ModuleConfig.ZoneIds.Count; i++)
            {
                var zoneId = ModuleConfig.ZoneIds[i];
                if (!ModuleConfig.ZoneLimitList.TryGetValue(zoneId, out var zoneLimit))
                {
                    zoneLimit = new ZoneLimit(zoneId);
                    ModuleConfig.ZoneLimitList[zoneId] = zoneLimit;
                    SaveConfig(ModuleConfig);
                }

                var isCurrentZone = DService.ClientState.TerritoryType == zoneId;
                var nodeColor = isCurrentZone ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite;
                var zoneName = GetZoneName(zoneId);
                var nodeLabel = $"{zoneId}: {zoneName}";

                using var nodeColorStyle = ImRaii.PushColor(ImGuiCol.Text, nodeColor);
                var isOpen = ImGui.TreeNode($"{nodeLabel}###{zoneId}");

                if (isOpen)
                {
                    var enabled = zoneLimit.Enabled;
                    if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundaries-Enable"), ref enabled))
                    {
                        zoneLimit.Enabled = enabled;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    var advancedMode = zoneLimit.IsAdvancedMode;
                    if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundaries-AdvancedMode"), ref advancedMode))
                    {
                        zoneLimit.IsAdvancedMode = advancedMode;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    if (isCurrentZone && ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-SetPosition")))
                    {
                        if (DService.ClientState.LocalPlayer != null)
                        {
                            zoneLimit.CenterPos = DService.ClientState.LocalPlayer.Position;
                            SaveConfig(ModuleConfig);
                            Chat(GetLoc("PreventEntryIntoMapBoundaries-PositionUpdated"));
                        }
                    }

                    if (zoneLimit.IsAdvancedMode)
                        DrawAdvancedModeUI(zoneLimit, isCurrentZone);
                    else
                        DrawTraditionalModeUI(zoneLimit, zoneId, isCurrentZone);

                    using (var deleteButtonStyle = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
                    {
                        if (ImGui.Button(string.Format(GetLoc("PreventEntryIntoMapBoundaries-DeleteZone"), zoneId)))
                        {
                            ModuleConfig.ZoneIds.RemoveAt(i);
                            ModuleConfig.ZoneLimitList.Remove(zoneId);
                            SaveConfig(ModuleConfig);
                            ImGui.TreePop();
                            break;
                        }
                    }

                    ImGui.TreePop();
                }

                if (i < ModuleConfig.ZoneIds.Count - 1)
                    ImGui.Separator();
            }
        }

        private void DrawTraditionalModeUI(ZoneLimit zoneLimit, uint zoneId, bool isCurrentZone)
        {
            // 地图类型选择
            var mapTypes = new[] {
                GetLoc("PreventEntryIntoMapBoundaries-CircleType"),
                GetLoc("PreventEntryIntoMapBoundaries-RectangleType")
            };
            var mapTypeValues = new[] { MapType.Circle, MapType.Rectangle };
            var currentMapTypeIndex = Array.IndexOf(mapTypeValues, zoneLimit.MapType);
            if (currentMapTypeIndex == -1)
                currentMapTypeIndex = 0;

            if (ImGui.Combo(GetLoc("PreventEntryIntoMapBoundaries-MapType"), ref currentMapTypeIndex, mapTypes, mapTypes.Length))
            {
                zoneLimit.MapType = mapTypeValues[currentMapTypeIndex];
                SaveConfig(ModuleConfig);
            }

            // 中心位置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var centerPos = zoneLimit.CenterPos;
            if (ImGui.InputFloat3($"##CenterPos{zoneId}", ref centerPos))
            {
                zoneLimit.CenterPos = centerPos;
                SaveConfig(ModuleConfig);
            }

            // 半径
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            var radius = zoneLimit.Radius;
            if (ImGui.InputFloat($"##Radius{zoneId}", ref radius, 1.0f, 10.0f, "%.2f"))
            {
                zoneLimit.Radius = Math.Max(1.0f, radius);
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Radius"));

            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
            {
                var playerPos = DService.ClientState.LocalPlayer.Position;
                var distance = Vector3.Distance(playerPos, zoneLimit.CenterPos);
                ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(GetLoc("PreventEntryIntoMapBoundaries-Distance"), distance));
            }
        }

        private void DrawAdvancedModeUI(ZoneLimit zoneLimit, bool isCurrentZone)
        {
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DangerZoneManagement"));

            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddDangerZone")))
            {
                var newZone = new DangerZone(string.Format(GetLoc("PreventEntryIntoMapBoundaries-DangerZoneName"), zoneLimit.DangerZones.Count + 1));
                zoneLimit.DangerZones.Add(newZone);
                SaveConfig(ModuleConfig);
            }

            for (var j = 0; j < zoneLimit.DangerZones.Count; j++)
            {
                var dangerZone = zoneLimit.DangerZones[j];

                if (ImGui.TreeNode($"{dangerZone.Name}###{j}"))
                {
                    // 危险区域基本设置
                    var dzEnabled = dangerZone.Enabled;
                    if (ImGui.Checkbox($"{GetLoc("PreventEntryIntoMapBoundaries-Enable")}##dz{j}", ref dzEnabled))
                    {
                        dangerZone.Enabled = dzEnabled;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
                    var dzName = dangerZone.Name;
                    if (ImGui.InputText($"##dzName{j}", ref dzName, 50))
                    {
                        dangerZone.Name = dzName;
                        SaveConfig(ModuleConfig);
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Name"));

                    // 区域类型选择
                    ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
                    var zoneTypes = new[] {
                        GetLoc("PreventEntryIntoMapBoundaries-CircleType"),
                        GetLoc("PreventEntryIntoMapBoundaries-AnnulusType"),
                        GetLoc("PreventEntryIntoMapBoundaries-RectangleType"),
                        GetLoc("PreventEntryIntoMapBoundaries-SafeZoneType"),
                        GetLoc("PreventEntryIntoMapBoundaries-ExpressionType")
                    };
                    var zoneTypeValues = new[] { ZoneType.Circle, ZoneType.Annulus, ZoneType.Rectangle, ZoneType.SafeZone, ZoneType.Expression };
                    var currentZoneTypeIndex = Array.IndexOf(zoneTypeValues, dangerZone.ZoneType);
                    if (currentZoneTypeIndex == -1)
                        currentZoneTypeIndex = 0;

                    if (ImGui.Combo($"##dzType{j}", ref currentZoneTypeIndex, zoneTypes, zoneTypes.Length))
                    {
                        dangerZone.ZoneType = zoneTypeValues[currentZoneTypeIndex];
                        SaveConfig(ModuleConfig);
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Type"));

                    // 根据类型显示不同的参数
                    switch (dangerZone.ZoneType)
                    {
                        case ZoneType.Expression:
                            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ExpressionDescription"));
                            ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundaries-ExpressionExample"));
                            var mathExpr = dangerZone.MathExpression;
                            if (ImGui.InputTextMultiline($"##expr{j}", ref mathExpr, 200, new Vector2(300, 60)))
                            {
                                dangerZone.MathExpression = mathExpr;
                                SaveConfig(ModuleConfig);
                            }
                            break;

                        case ZoneType.Circle:
                        case ZoneType.Annulus:
                            DrawCircularZoneUI(dangerZone, j);
                            break;

                        case ZoneType.Rectangle:
                            DrawRectangleZoneUI(dangerZone, j, GetLoc("PreventEntryIntoMapBoundaries-DangerArea"));
                            break;

                        case ZoneType.SafeZone:
                            ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("PreventEntryIntoMapBoundaries-SafeZoneDescription"));
                            DrawRectangleZoneUI(dangerZone, j, GetLoc("PreventEntryIntoMapBoundaries-SafeArea"));
                            break;
                    }

                    // 颜色选择和删除按钮
                    DrawZoneColorAndDelete(dangerZone, j, zoneLimit, isCurrentZone);

                    ImGui.TreePop();
                }
            }
        }

        private void DrawCircularZoneUI(DangerZone dangerZone, int index)
        {
            // 中心位置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var dzCenterPos = dangerZone.CenterPos;
            if (ImGui.InputFloat3($"##dzCenter{index}", ref dzCenterPos))
            {
                dangerZone.CenterPos = dzCenterPos;
                SaveConfig(ModuleConfig);
            }

            // 外半径
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            var dzRadius = dangerZone.Radius;
            if (ImGui.InputFloat($"##dzRadius{index}", ref dzRadius, 1.0f, 10.0f, "%.2f"))
            {
                dangerZone.Radius = Math.Max(1.0f, dzRadius);
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-OuterRadius"));

            // 内半径（仅月环类型）
            if (dangerZone.ZoneType == ZoneType.Annulus)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
                var dzInnerRadius = dangerZone.InnerRadius;
                if (ImGui.InputFloat($"##dzInnerRadius{index}", ref dzInnerRadius, 1.0f, 10.0f, "%.2f"))
                {
                    dangerZone.InnerRadius = Math.Max(0.0f, Math.Min(dzInnerRadius, dangerZone.Radius - 0.1f));
                    SaveConfig(ModuleConfig);
                }
                ImGui.SameLine();
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-InnerRadius"));
            }
        }

        private void DrawRectangleZoneUI(DangerZone dangerZone, int index, string areaTypeKey)
        {
            // X范围
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-XRange"));
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            var minX = dangerZone.MinX;
            if (ImGui.InputFloat($"##minX{index}", ref minX, 1.0f, 10.0f, "%.1f"))
            {
                dangerZone.MinX = minX;
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text("~");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            var maxX = dangerZone.MaxX;
            if (ImGui.InputFloat($"##maxX{index}", ref maxX, 1.0f, 10.0f, "%.1f"))
            {
                dangerZone.MaxX = maxX;
                SaveConfig(ModuleConfig);
            }

            // Z范围
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ZRange"));
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            var minZ = dangerZone.MinZ;
            if (ImGui.InputFloat($"##minZ{index}", ref minZ, 1.0f, 10.0f, "%.1f"))
            {
                dangerZone.MinZ = minZ;
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text("~");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            var maxZ = dangerZone.MaxZ;
            if (ImGui.InputFloat($"##maxZ{index}", ref maxZ, 1.0f, 10.0f, "%.1f"))
            {
                dangerZone.MaxZ = maxZ;
                SaveConfig(ModuleConfig);
            }

            ImGui.TextColored(ImGuiColors.DalamudGrey,
                string.Format(areaTypeKey, dangerZone.MinX, dangerZone.MaxX, dangerZone.MinZ, dangerZone.MaxZ));
        }

        private void DrawZoneColorAndDelete(DangerZone dangerZone, int index, ZoneLimit zoneLimit, bool isCurrentZone)
        {
            var color = new Vector4(
                ((dangerZone.Color >> 0) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 8) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 16) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 24) & 0xFF) / 255.0f
            );

            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.ColorEdit4($"##dzColor{index}", ref color, ImGuiColorEditFlags.NoInputs))
            {
                dangerZone.Color =
                    ((uint)(color.W * 255) << 24) |
                    ((uint)(color.Z * 255) << 16) |
                    ((uint)(color.Y * 255) << 8) |
                    ((uint)(color.X * 255) << 0);
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Color"));

            // 调试信息
            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
            {
                var playerPos = DService.ClientState.LocalPlayer.Position;
                var inDanger = IsInDangerZone(dangerZone, playerPos);
                ImGui.TextColored(
                    inDanger ? ImGuiColors.DPSRed : ImGuiColors.HealerGreen,
                    string.Format(GetLoc("PreventEntryIntoMapBoundaries-Status"),
                        inDanger ? GetLoc("PreventEntryIntoMapBoundaries-Dangerous") : GetLoc("PreventEntryIntoMapBoundaries-Safe"))
                );
            }

            // 删除按钮
            ImGui.SameLine();
            using (var deleteButtonStyle = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
            {
                if (ImGui.Button($"{GetLoc("PreventEntryIntoMapBoundaries-Delete")}##dz{index}"))
                {
                    zoneLimit.DangerZones.RemoveAt(index);
                    SaveConfig(ModuleConfig);
                    ImGui.TreePop();
                    return;
                }
            }
        }

        private static string GetZoneName(uint zoneId)
        {
            return $"Zone {zoneId}"; // 简化区域名称显示
        }

        private static bool IsInDangerZone(DangerZone zone, Vector3 position)
        {
            if (!zone.Enabled)
                return false;

            var x = position.X;
            var z = position.Z;

            return zone.ZoneType switch
            {
                ZoneType.Circle => (position - zone.CenterPos).Length() <= zone.Radius,
                ZoneType.Annulus => (position - zone.CenterPos).Length() is var ringDistance &&
                                   (ringDistance < zone.InnerRadius || ringDistance > zone.Radius),
                ZoneType.Rectangle => x >= zone.MinX && x <= zone.MaxX && z >= zone.MinZ && z <= zone.MaxZ,
                ZoneType.SafeZone => x < zone.MinX || x > zone.MaxX || z < zone.MinZ || z > zone.MaxZ,
                ZoneType.Expression => EvaluateMathExpression(zone.MathExpression, x, z),
                _ => false
            };
        }

        private static Vector3 GetSafePositionFromDangerZone(DangerZone zone, Vector3 currentPos)
        {
            return zone.ZoneType switch
            {
                ZoneType.Circle => GetSafePositionFromCircle(zone, currentPos),
                ZoneType.Annulus => GetSafePositionFromAnnulus(zone, currentPos),
                ZoneType.Rectangle => GetSafePositionFromRectangle(zone, currentPos),
                ZoneType.SafeZone => GetSafePositionFromSafeZone(zone, currentPos),
                ZoneType.Expression => GetSafePositionFromExpression(zone, currentPos),
                _ => currentPos
            };
        }

        private static Vector3 GetSafePositionFromCircle(DangerZone zone, Vector3 currentPos)
        {
            var direction = currentPos - zone.CenterPos;
            if (direction.Length() == 0)
                direction = new Vector3(1.0f, 0, 0);
            else
                direction = Vector3.Normalize(direction);
            return zone.CenterPos + direction * (zone.Radius + 1.0f);
        }

        private static Vector3 GetSafePositionFromAnnulus(DangerZone zone, Vector3 currentPos)
        {
            var ringDirection = currentPos - zone.CenterPos;
            var ringDistance = ringDirection.Length();

            if (ringDistance == 0)
            {
                var safeRadius = (zone.InnerRadius + zone.Radius) / 2;
                return zone.CenterPos + new Vector3(safeRadius, 0, 0);
            }

            ringDirection = Vector3.Normalize(ringDirection);

            if (ringDistance < zone.InnerRadius)
                return zone.CenterPos + ringDirection * (zone.InnerRadius + 1.0f);
            else if (ringDistance > zone.Radius)
                return zone.CenterPos + ringDirection * (zone.Radius - 1.0f);
            else
                return currentPos;
        }

        private static Vector3 GetSafePositionFromRectangle(DangerZone zone, Vector3 currentPos)
        {
            var squareNewX = currentPos.X;
            var squareNewZ = currentPos.Z;

            var distToLeft = Math.Abs(currentPos.X - zone.MinX);
            var distToRight = Math.Abs(currentPos.X - zone.MaxX);
            var distToTop = Math.Abs(currentPos.Z - zone.MinZ);
            var distToBottom = Math.Abs(currentPos.Z - zone.MaxZ);

            var minDist = Math.Min(Math.Min(distToLeft, distToRight), Math.Min(distToTop, distToBottom));

            if (minDist == distToLeft)
                squareNewX = zone.MinX - 1.0f;
            else if (minDist == distToRight)
                squareNewX = zone.MaxX + 1.0f;
            else if (minDist == distToTop)
                squareNewZ = zone.MinZ - 1.0f;
            else
                squareNewZ = zone.MaxZ + 1.0f;

            return new Vector3(squareNewX, currentPos.Y, squareNewZ);
        }

        private static Vector3 GetSafePositionFromSafeZone(DangerZone zone, Vector3 currentPos)
        {
            var newX = Math.Max(zone.MinX, Math.Min(zone.MaxX, currentPos.X));
            var newZ = Math.Max(zone.MinZ, Math.Min(zone.MaxZ, currentPos.Z));

            if (Math.Abs(newX - zone.MinX) < 0.1f)
                newX = zone.MinX + 1.0f;
            if (Math.Abs(newX - zone.MaxX) < 0.1f)
                newX = zone.MaxX - 1.0f;
            if (Math.Abs(newZ - zone.MinZ) < 0.1f)
                newZ = zone.MinZ + 1.0f;
            if (Math.Abs(newZ - zone.MaxZ) < 0.1f) 
                newZ = zone.MaxZ - 1.0f;

            return new Vector3(newX, currentPos.Y, newZ);
        }

        private static Vector3 GetSafePositionFromExpression(DangerZone zone, Vector3 currentPos)
        {
            var dirToCenter = zone.CenterPos - currentPos;
            if (dirToCenter.Length() == 0)
                dirToCenter = new Vector3(1.0f, 0, 0);
            else
                dirToCenter = Vector3.Normalize(dirToCenter);
            return currentPos + dirToCenter * 2.0f;
        }

        private static bool EvaluateMathExpression(string expression, float x, float z)
        {
            try
            {
                var expr = expression.Replace("x", x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                                   .Replace("z", z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                                   .Replace("^", "**");

                var table = new DataTable();
                var result = table.Compute(expr, null);

                if (result == DBNull.Value)
                    return false;
                if (result is bool boolResult)
                    return boolResult;
                if (double.TryParse(result.ToString(), out double numResult))
                    return numResult > 0;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void OnTerritoryChanged(ushort territoryId)
        {
            if (ModuleConfig?.ShowDebugInfo == true)
                Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-TerritoryChanged"), territoryId));
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (ModuleConfig == null || DService.ClientState.LocalPlayer == null)
                return;

            var currentZoneId = DService.ClientState.TerritoryType;

            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
                return;

            // 检查小队人数及死亡人数
            var deathCount = DService.PartyList.Count(p => p.CurrentHP <= 0);
            if (deathCount >= ModuleConfig.DisableOnDeathCount)
                return;

            var currentPos = DService.ClientState.LocalPlayer.Position;

            // 高级模式：检查多个危险区域
            if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                {
                    if (dangerZone.Enabled && IsInDangerZone(dangerZone, currentPos))
                    {
                        var safePos = GetSafePositionFromDangerZone(dangerZone, currentPos);
                        TeleportToSafePosition(safePos, string.Format(GetLoc("PreventEntryIntoMapBoundaries-EscapeDanger"), dangerZone.Name));
                        return;
                    }
                }
            }
            // 传统模式：单一边界检查
            else if (zoneLimit.Enabled)
                ProcessTraditionalMode(zoneLimit, currentPos, currentZoneId);
        }

        private void ProcessTraditionalMode(ZoneLimit zoneLimit, Vector3 currentPos, uint currentZoneId)
        {
            var centerPos = zoneLimit.CenterPos;
            var radius = zoneLimit.Radius;
            var safeRadius = radius - 0.3f;

            var isOutside = false;
            var newPos = currentPos;

            if (zoneLimit.MapType == MapType.Circle)
            {
                var distance = (currentPos - centerPos).Length();
                if (distance < safeRadius)
                    return;

                var direction = Vector3.Normalize(currentPos - centerPos);
                newPos = centerPos + direction * (radius - 0.5f);
                isOutside = true;
            }
            else if (zoneLimit.MapType == MapType.Rectangle)
            {
                var halfSize = radius;
                var minX = centerPos.X - halfSize;
                var maxX = centerPos.X + halfSize;
                var minZ = centerPos.Z - halfSize;
                var maxZ = centerPos.Z + halfSize;

                var clampedX = currentPos.X;
                var clampedZ = currentPos.Z;

                if (currentPos.X < minX)
                {
                    clampedX = minX + 0.5f;
                    isOutside = true;
                }
                else if (currentPos.X > maxX)
                {
                    clampedX = maxX - 0.5f;
                    isOutside = true;
                }

                if (currentPos.Z < minZ)
                {
                    clampedZ = minZ + 0.5f;
                    isOutside = true;
                }
                else if (currentPos.Z > maxZ)
                {
                    clampedZ = maxZ - 0.5f;
                    isOutside = true;
                }

                if (isOutside)
                    newPos = new Vector3(clampedX, currentPos.Y, clampedZ);
            }
            else
            {
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-UnknownMapType"), zoneLimit.MapType, currentZoneId));
                return;
            }

            if (isOutside)
                TeleportToSafePosition(newPos, GetLoc("PreventEntryIntoMapBoundaries-PositionCorrected"));
        }

        private void TeleportToSafePosition(Vector3 newPos, string message)
        {
            {
                var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)DService.ClientState.LocalPlayer.Address;
                if (gameObject != null)
                {
                    gameObject->SetPosition(newPos.X, newPos.Y, newPos.Z);

                    if (ModuleConfig?.ShowDebugInfo == true)
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-DebugPosition"), message, newPos.X, newPos.Y, newPos.Z));
                }
                else
                    ChatError(GetLoc("PreventEntryIntoMapBoundaries-PlayerObjectNull"));
            }
        }

        private void OnCommand(string command, string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandUsage"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExamples"));
                return;
            }

            var action = parts[0].ToLower();
            var currentZoneId = DService.ClientState.TerritoryType;

            if (ModuleConfig == null)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundaries-ConfigNotInitialized"));
                return;
            }

            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
            {
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneNotConfigured"), currentZoneId));
                return;
            }

            switch (action)
            {
                case "add":
                    HandleAddCommand(parts.Skip(1).ToArray(), zoneLimit);
                    break;
                case "delete":
                    HandleDeleteCommand(parts.Skip(1).ToArray(), zoneLimit);
                    break;
                case "modify":
                    HandleModifyCommand(parts.Skip(1).ToArray(), zoneLimit);
                    break;
                default:
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-UnknownCommand"), action));
                    break;
            }
        }

        private void HandleAddCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundaries-AddCommandUsage"));
                return;
            }

            var shapeType = args[0].ToLower();
            var dangerZone = new DangerZone(string.Format(GetLoc("PreventEntryIntoMapBoundaries-CommandZone"), zoneLimit.DangerZones.Count + 1));

            switch (shapeType)
            {
                case "circle":
                    if (args.Length < 4)
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-CircleUsage"));
                        return;
                    }

                    if (float.TryParse(args[1], out var x) && float.TryParse(args[2], out var z) && float.TryParse(args[3], out var radius))
                    {
                        dangerZone.ZoneType = ZoneType.Circle;
                        dangerZone.CenterPos = new Vector3(x, 0, z);
                        dangerZone.Radius = radius;
                        dangerZone.Enabled = true;
                    }
                    else
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-InvalidNumericParams"));
                        return;
                    }
                    break;

                case "rect":
                    if (args.Length < 5)
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-RectUsage"));
                        return;
                    }

                    if (float.TryParse(args[1], out var minX) && float.TryParse(args[2], out var maxX) &&
                        float.TryParse(args[3], out var minZ) && float.TryParse(args[4], out var maxZ))
                    {
                        dangerZone.ZoneType = ZoneType.Rectangle;
                        dangerZone.MinX = minX;
                        dangerZone.MaxX = maxX;
                        dangerZone.MinZ = minZ;
                        dangerZone.MaxZ = maxZ;
                        dangerZone.Enabled = true;
                    }
                    else
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-InvalidNumericParams"));
                        return;
                    }
                    break;

                default:
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-UnknownShapeType"), shapeType));
                    return;
            }

            zoneLimit.DangerZones.Add(dangerZone);
            zoneLimit.IsAdvancedMode = true;
            SaveConfig(ModuleConfig);
            Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneAddedSuccess"), shapeType, dangerZone.Name));
        }

        private void HandleDeleteCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundaries-DeleteUsage"));
                return;
            }

            if (int.TryParse(args[0], out var index) && index > 0 && index <= zoneLimit.DangerZones.Count)
            {
                var removedZone = zoneLimit.DangerZones[index - 1];
                zoneLimit.DangerZones.RemoveAt(index - 1);
                SaveConfig(ModuleConfig);
                Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneDeleted"), removedZone.Name));
            }
            else
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-InvalidIndexDelete"), zoneLimit.DangerZones.Count));
        }

        private void HandleModifyCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 3)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundaries-ModifyUsage"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-ModifyProperties"));
                return;
            }

            if (!int.TryParse(args[0], out var index) || index <= 0 || index > zoneLimit.DangerZones.Count)
            {
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-InvalidIndexModify"), zoneLimit.DangerZones.Count));
                return;
            }

            var dangerZone = zoneLimit.DangerZones[index - 1];
            var property = args[1].ToLower();
            var value = args[2];

            switch (property)
            {
                case "enabled":
                    if (bool.TryParse(value, out var enabled))
                    {
                        dangerZone.Enabled = enabled;
                        SaveConfig(ModuleConfig);
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneToggled"),
                            enabled ? GetLoc("PreventEntryIntoMapBoundaries-Enabled") : GetLoc("PreventEntryIntoMapBoundaries-Disabled"),
                            dangerZone.Name));
                    }
                    else
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-EnabledValueError"));
                    break;

                case "name":
                    dangerZone.Name = value;
                    SaveConfig(ModuleConfig);
                    Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-NameChanged"), value));
                    break;

                case "color":
                    if (uint.TryParse(value, out var color))
                    {
                        dangerZone.Color = color;
                        SaveConfig(ModuleConfig);
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ColorChanged"), color.ToString("X8")));
                    }
                    else
                        ChatError(GetLoc("PreventEntryIntoMapBoundaries-InvalidColorValue"));
                    break;

                default:
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-UnknownProperty"), property));
                    break;
            }
        }

        private void ExportCurrentZoneConfig()
        {
            var currentZoneId = DService.ClientState.TerritoryType;
            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
            {
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-CurrentZoneNoConfig"), currentZoneId));
                return;
            }

            var exportConfig = new Config
            {
                ZoneIds = [currentZoneId],
                ZoneLimitList = new Dictionary<uint, ZoneLimit> { { currentZoneId, zoneLimit } }
            };

            ExportToClipboard(exportConfig);
            Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneConfigExported"), currentZoneId));
        }

        protected override void Uninit()
        {
            FrameworkManager.Unregister(OnFrameworkUpdate);
            DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
            RemoveSubCommand(Command);
        }

        

        private class Config : ModuleConfiguration
        {
            public List<uint> ZoneIds { get; set; } = [];
            public Dictionary<uint, ZoneLimit> ZoneLimitList { get; set; } = [];
            public bool ShowDebugInfo { get; set; } = false;
            public bool ShowBoundaryVisualization { get; set; } = false;
            public float RadarRange { get; set; } = 50.0f;
            public float RadarScale { get; set; } = 2.0f;
            public int DisableOnDeathCount { get; set; } = 2;
        }
        private enum ZoneType
        {
            Circle = 0,    // 圆形
            Annulus = 1,   // 月环
            Rectangle = 2, // 矩形
            SafeZone = 3,  // 矩形安全区
            Expression = 4 // 数学表达式
        }

        private enum MapType
        {
            Circle = 0,   // 圆形
            Rectangle = 2 // 矩形
        }

        private class DangerZone
        {
            public bool Enabled { get; set; }
            public string Name { get; set; }
            public ZoneType ZoneType { get; set; }
            public Vector3 CenterPos { get; set; }
            public float Radius { get; set; }
            public float InnerRadius { get; set; }
            public float MinX { get; set; }
            public float MaxX { get; set; }
            public float MinZ { get; set; }
            public float MaxZ { get; set; }
            public string MathExpression { get; set; }
            public uint Color { get; set; }

            public DangerZone(string name = "")
            {
                Enabled = false;
                Name = name;
                ZoneType = ZoneType.Circle;
                CenterPos = new Vector3(100, 0, 100);
                Radius = 20f;
                InnerRadius = 10f;
                MinX = 80f;
                MaxX = 120f;
                MinZ = 80f;
                MaxZ = 120f;
                MathExpression = "(x-100)^2 + (z-100)^2 <= 400";
                Color = ImGuiColors.DPSRed.ToUint();
            }
        }

        private class ZoneLimit
        {
            public bool Enabled { get; set; }
            public uint ZoneId { get; set; }
            public MapType MapType { get; set; }
            public Vector3 CenterPos { get; set; }
            public float Radius { get; set; }
            public List<DangerZone> DangerZones { get; set; }
            public bool IsAdvancedMode { get; set; }

            public ZoneLimit(uint id)
            {
                Enabled = false;
                ZoneId = id;
                MapType = MapType.Circle;
                CenterPos = new Vector3(100, 0, 100);
                Radius = 20f;
                DangerZones = new List<DangerZone>();
                IsAdvancedMode = false;
            }
            
        }

        
    }
}
