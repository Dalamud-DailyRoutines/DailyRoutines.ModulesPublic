using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Data;
using System.Drawing;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using static DailyRoutines.Managers.CommandManager;

namespace DailyRoutines.ModulesPublic;

    public class PreventEntryIntoMapBoundaries : DailyModuleBase
    {
        public override ModuleInfo Info { get; } = new()
        {
            Title = GetLoc("PreventEntryIntoMapBoundariesTitle"),
            Description = GetLoc("PreventEntryIntoMapBoundariesDescription"),
            Category = ModuleCategories.Combat,
            Author = ["Nag0mi"]
        };
        
        private const string Command = "pdrfence";
        
        private static Config? ModuleConfig;
        protected override void Init()
        {
            ModuleConfig = LoadConfig<Config>() ?? new Config();
            FrameworkManager.Register(OnFrameworkUpdate);
            AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("PreventEntryIntoMapBoundaries-CommandHelp") });
        }

        protected override void ConfigUI()
        {
            using var configChild = ImRaii.Child(GetLoc("Settings-ModuleConfiguration"), Vector2.Zero, false);

            // 全局设置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-GlobalSettings"));

            // 添加当前区域按钮
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddCurrentZone")))
            {
                var zid = GameState.TerritoryType;

                if (zid == 0)
                    return;

                if (!ModuleConfig.ZoneIDs.Contains(zid))
                {
                    ModuleConfig.ZoneIDs.Add(zid);
                    ModuleConfig.ZoneLimitList[zid] = new ZoneLimit();
                    SaveConfig(ModuleConfig);
                    Chat(GetLoc("PreventEntryIntoMapBoundaries-ZoneAdded", zid));
                }
            }

            ImGui.NewLine();
            
            if (ImGuiOm.CheckboxColored(GetLoc("PreventEntryIntoMapBoundaries-ShowDebug"), ref ModuleConfig!.ShowDebugInfo))
                SaveConfig(ModuleConfig);
            
            if (ImGuiOm.CheckboxColored(GetLoc("PreventEntryIntoMapBoundaries-ShowVisualization"), ref ModuleConfig.ShowBoundaryVisualization))
                SaveConfig(ModuleConfig);
            
            // 死亡玩家数量配置
            if (ImGui.SliderInt(GetLoc("PreventEntryIntoMapBoundaries-DeathThreshold"), ref ModuleConfig.DisableOnDeathCount, 0, 8, "%d"))
                SaveConfig(ModuleConfig);
            
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DeathDescription"));

            // 配置管理
            ImGui.Text(GetLoc("ModuleConfig"));
            if (ImGui.Button(GetLoc("Export")))
                ExportToClipboard(ModuleConfig);

            if (ImGui.Button(GetLoc("Import")))
            {
                var imported = ImportFromClipboard<Config>();
                if (imported != null)
                {
                    ModuleConfig = imported;
                    SaveConfig(ModuleConfig);
                }
            }

            if (ImGui.Button(GetLoc("DailyModuleBase-Exported")))
                ExportCurrentZoneConfig();

            ImGui.NewLine();

            if (ModuleConfig.ZoneIDs.Count == 0)
            {
                ImGui.TextColored(KnownColor.Gray.Vector(), GetLoc("PreventEntryIntoMapBoundaries-NoZones"));
                return;
            }

            // 显示所有已添加的区域
            for (var i = 0; i < ModuleConfig.ZoneIDs.Count; i++)
            {
                var zid = ModuleConfig.ZoneIDs[i];
                if (!ModuleConfig.ZoneLimitList.TryGetValue(zid, out var zoneLimit))
                {
                    zoneLimit = new ZoneLimit();
                    ModuleConfig.ZoneLimitList[zid] = zoneLimit;
                    SaveConfig(ModuleConfig);
                }

                var isCurrentZone = GameState.TerritoryType == zid;
                var nodeColor = isCurrentZone ? KnownColor.Green.Vector() : KnownColor.White.Vector();
                var zoneName = GetZoneName(zid);
                var nodeLabel = $"{zid}: {zoneName}";

                using var nodeColorStyle = ImRaii.PushColor(ImGuiCol.Text, nodeColor);
                var isOpen = ImRaii.TreeNode($"{nodeLabel}###{zid}");


                if (isOpen)
                {
                    if (ImGui.Checkbox(GetLoc("Enable"), ref zoneLimit.Enabled))
                        SaveConfig(ModuleConfig);

                    ImGui.SameLine();
                    if (ImGui.Checkbox(GetLoc("Advance"), ref zoneLimit.IsAdvancedMode))
                        SaveConfig(ModuleConfig);

                    ImGui.SameLine();
                    if (isCurrentZone && ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-SetPosition")))
                    {
                        if (DService.ObjectTable.LocalPlayer != null)
                        {
                            zoneLimit.CenterPos = DService.ObjectTable.LocalPlayer.Position;
                            SaveConfig(ModuleConfig);
                        }
                    }

                    if (zoneLimit.IsAdvancedMode)
                        DrawAdvancedModeUI(zoneLimit, isCurrentZone);
                    else
                        DrawTraditionalModeUI(zoneLimit, zid, isCurrentZone);

                    using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.Red.Vector()))
                    {
                        if (ImGui.Button(GetLoc("Delete", zid)))
                        {
                            ModuleConfig.ZoneIDs.RemoveAt(i);
                            ModuleConfig.ZoneLimitList.Remove(zid);
                            SaveConfig(ModuleConfig);
                            ImGui.TreePop();
                            break;
                        }
                    }

                    ImGui.TreePop();
                }

                if (i < ModuleConfig.ZoneIDs.Count - 1)
                    ImGui.NewLine();
            }

            DrawRadarWindow();
        }

        private void DrawTraditionalModeUI(ZoneLimit zoneLimit, uint zid, bool isCurrentZone)
        {
            // 地图类型选择
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-MapType"));
            ImGui.SetNextItemWidth(150 * GlobalFontScale);
            var currentMapTypeIndex = zoneLimit.MapType == MapType.Circle ? 0 : 1;

            if (ImGui.Combo($"##MapType{zid}", ref currentMapTypeIndex,
                $"{GetLoc("PreventEntryIntoMapBoundaries-CircleType")}\0{GetLoc("PreventEntryIntoMapBoundaries-RectangleType")}\0", 2))
            {
                zoneLimit.MapType = currentMapTypeIndex == 0 ? MapType.Circle : MapType.Rectangle;
                SaveConfig(ModuleConfig!);
            }

            // 中心位置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
            ImGui.SetNextItemWidth(200 * GlobalFontScale);
            if (ImGui.InputFloat3($"##CenterPos{zid}", ref zoneLimit.CenterPos))
                SaveConfig(ModuleConfig!);

            // 半径
            ImGui.SetNextItemWidth(120 * GlobalFontScale);
            if (ImGui.InputFloat($"##Radius{zid}", ref zoneLimit.Radius, 1.0f, 10.0f, "%.2f"))
            {
                zoneLimit.Radius = Math.Max(1.0f, zoneLimit.Radius);
                SaveConfig(ModuleConfig!);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("Radius"));

            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ObjectTable.LocalPlayer != null)
            {
                var distance = LocalPlayerState.DistanceTo3D(zoneLimit.CenterPos);
                ImGui.TextColored(KnownColor.Gray.Vector(), GetLoc("PreventEntryIntoMapBoundaries-Distance", distance));
            }
        }

        private void DrawAdvancedModeUI(ZoneLimit zoneLimit, bool isCurrentZone)
        {
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DangerZoneManagement"));

            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddDangerZone")))
            {
                var newZone = new DangerZone(GetLoc("PreventEntryIntoMapBoundaries-DangerZoneName", zoneLimit.DangerZones.Count + 1));
                zoneLimit.DangerZones.Add(newZone);
                SaveConfig(ModuleConfig!);
            }

            for (var j = 0; j < zoneLimit.DangerZones.Count; j++)
            {
                var dangerZone = zoneLimit.DangerZones[j];

                if (ImRaii.TreeNode($"{dangerZone.Name}###{j}"))
                {
                    // 危险区域基本设置
                    if (ImGui.Checkbox($"{GetLoc("Enable")}##dz{j}", ref dangerZone.Enabled))
                        SaveConfig(ModuleConfig!);

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150 * GlobalFontScale);
                    if (ImGui.InputText($"##dzName{j}", ref dangerZone.Name, 50))
                        SaveConfig(ModuleConfig!);
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("Name"));

                    // 区域类型选择
                    ImGui.SetNextItemWidth(120 * GlobalFontScale);
                    var currentZoneTypeIndex = dangerZone.ZoneType switch
                    {
                        ZoneType.Circle => 0,
                        ZoneType.Annulus => 1,
                        ZoneType.Rectangle => 2,
                        ZoneType.RectangularSafeZone => 3,
                        ZoneType.Expression => 4,
                        _ => 0
                    };

                    if (ImGui.Combo($"##dzType{j}", ref currentZoneTypeIndex,
                        $"{GetLoc("PreventEntryIntoMapBoundaries-CircleType")}\0{GetLoc("PreventEntryIntoMapBoundaries-AnnulusType")}\0{GetLoc("PreventEntryIntoMapBoundaries-RectangleType")}\0{GetLoc("PreventEntryIntoMapBoundaries-RectangularSafeZoneType")}\0{GetLoc("PreventEntryIntoMapBoundaries-ExpressionType")}\0", 5))
                    {
                        dangerZone.ZoneType = currentZoneTypeIndex switch
                        {
                            0 => ZoneType.Circle,
                            1 => ZoneType.Annulus,
                            2 => ZoneType.Rectangle,
                            3 => ZoneType.RectangularSafeZone,
                            4 => ZoneType.Expression,
                            _ => ZoneType.Circle
                        };
                        SaveConfig(ModuleConfig!);
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("Type"));

                    // 根据类型显示不同的参数
                    switch (dangerZone.ZoneType)
                    {
                        case ZoneType.Expression:
                            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ExpressionDescription"));
                            ImGui.TextColored(KnownColor.Gray.Vector(), GetLoc("PreventEntryIntoMapBoundaries-ExpressionExample"));
                            if (ImGui.InputTextMultiline($"##expr{j}", ref dangerZone.MathExpression, 200, new Vector2(300, 60)))
                                SaveConfig(ModuleConfig!);
                            break;

                        case ZoneType.Circle:
                        case ZoneType.Annulus:
                            DrawCircularZoneUI(dangerZone, j);
                            break;

                        case ZoneType.Rectangle:
                            DrawRectangleZoneUI(dangerZone, j, GetLoc("PreventEntryIntoMapBoundaries-DangerArea"));
                            break;

                        case ZoneType.RectangularSafeZone:
                            ImGui.TextColored(KnownColor.Yellow.Vector(), GetLoc("PreventEntryIntoMapBoundaries-RectangularSafeZoneDescription"));
                            DrawRectangleZoneUI(dangerZone, j, GetLoc("PreventEntryIntoMapBoundaries-RectangularSafeArea"));
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
            ImGui.SetNextItemWidth(200 * GlobalFontScale);
            if (ImGui.InputFloat3($"##dzCenter{index}", ref dangerZone.CenterPos))
                SaveConfig(ModuleConfig!);

            // 外半径
            ImGui.SetNextItemWidth(100 * GlobalFontScale);
            if (ImGui.InputFloat($"##dzRadius{index}", ref dangerZone.Radius, 1.0f, 10.0f, "%.2f"))
            {
                dangerZone.Radius = Math.Max(1.0f, dangerZone.Radius);
                SaveConfig(ModuleConfig!);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-OuterRadius"));

            // 内半径（仅月环类型）
            if (dangerZone.ZoneType == ZoneType.Annulus)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100 * GlobalFontScale);
                if (ImGui.InputFloat($"##dzInnerRadius{index}", ref dangerZone.InnerRadius, 1.0f, 10.0f, "%.2f"))
                {
                    dangerZone.InnerRadius = Math.Max(0.0f, Math.Min(dangerZone.InnerRadius, dangerZone.Radius - 0.1f));
                    SaveConfig(ModuleConfig!);
                }
                ImGui.SameLine();
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-InnerRadius"));
            }
        }

        private void DrawRectangleZoneUI(DangerZone dangerZone, int index, string areaTypeKey)
        {
            // X范围
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-XRange"));
            ImGui.SetNextItemWidth(80 * GlobalFontScale);
            if (ImGui.InputFloat($"##minX{index}", ref dangerZone.MinX, 1.0f, 10.0f, "%.1f"))
                SaveConfig(ModuleConfig!);
            ImGui.SameLine();
            ImGui.Text("~");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80 * GlobalFontScale);
            if (ImGui.InputFloat($"##maxX{index}", ref dangerZone.MaxX, 1.0f, 10.0f, "%.1f"))
                SaveConfig(ModuleConfig!);

            // Z范围
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ZRange"));
            ImGui.SetNextItemWidth(80 * GlobalFontScale);
            if (ImGui.InputFloat($"##minZ{index}", ref dangerZone.MinZ, 1.0f, 10.0f, "%.1f"))
                SaveConfig(ModuleConfig!);
            ImGui.SameLine();
            ImGui.Text("~");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80 * GlobalFontScale);
            if (ImGui.InputFloat($"##maxZ{index}", ref dangerZone.MaxZ, 1.0f, 10.0f, "%.1f"))
                SaveConfig(ModuleConfig!);

            ImGui.TextColored(KnownColor.Gray.Vector(),
                $"{areaTypeKey}: X({dangerZone.MinX:F1}~{dangerZone.MaxX:F1}) Z({dangerZone.MinZ:F1}~{dangerZone.MaxZ:F1})");
        }

        private void DrawZoneColorAndDelete(DangerZone dangerZone, int index, ZoneLimit zoneLimit, bool isCurrentZone)
        {
            var color = new Vector4(
                ((dangerZone.Color >> 0) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 8) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 16) & 0xFF) / 255.0f,
                ((dangerZone.Color >> 24) & 0xFF) / 255.0f
            );

            ImGui.SetNextItemWidth(100 * GlobalFontScale);
            if (ImGui.ColorEdit4($"##dzColor{index}", ref color, ImGuiColorEditFlags.NoInputs))
            {
                dangerZone.Color =
                    ((uint)(color.W * 255) << 24) |
                    ((uint)(color.Z * 255) << 16) |
                    ((uint)(color.Y * 255) << 8) |
                    ((uint)(color.X * 255) << 0);
                SaveConfig(ModuleConfig!);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("Color"));

            // 调试信息
            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ObjectTable.LocalPlayer != null)
            {
                var playerPos = DService.ObjectTable.LocalPlayer.Position;
                var inDanger = IsInDangerZone(dangerZone, playerPos);
                ImGui.TextColored(
                    inDanger ? KnownColor.Red.Vector() : KnownColor.Green.Vector(),
                    GetLoc("PreventEntryIntoMapBoundaries-Status",
                        inDanger ? GetLoc("PreventEntryIntoMapBoundaries-Dangerous") : GetLoc("PreventEntryIntoMapBoundaries-Safe"))
                );
            }

            // 删除按钮
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.Red.Vector()))
            {
                if (ImGui.Button($"{GetLoc("Delete")}##dz{index}"))
                {
                    zoneLimit.DangerZones.RemoveAt(index);
                    SaveConfig(ModuleConfig);
                    ImGui.TreePop();
                }
            }
        }

        private static string GetZoneName(uint zid) => $"Zone {zid}"; // 简化区域名称显示

        private static bool IsInDangerZone(DangerZone zone, Vector3 position) =>
            zone.Enabled && zone.ZoneType switch
            {
                ZoneType.Circle => (position - zone.CenterPos).Length() <= zone.Radius,
                ZoneType.Annulus => (position - zone.CenterPos).Length() is var ringDistance &&
                                   (ringDistance < zone.InnerRadius || ringDistance > zone.Radius),
                ZoneType.Rectangle => position.X >= zone.MinX && position.X <= zone.MaxX && position.Z >= zone.MinZ && position.Z <= zone.MaxZ,
                ZoneType.RectangularSafeZone => position.X < zone.MinX || position.X > zone.MaxX || position.Z < zone.MinZ || position.Z > zone.MaxZ,
                ZoneType.Expression => EvaluateMathExpression(zone.MathExpression, position.X, position.Z),
                _ => false
            };

        private static Vector3 GetSafePositionFromDangerZone(DangerZone zone, Vector3 currentPos)
        {
            return zone.ZoneType switch
            {
                ZoneType.Circle => GetSafePositionFromCircle(zone, currentPos),
                ZoneType.Annulus => GetSafePositionFromAnnulus(zone, currentPos),
                ZoneType.Rectangle => GetSafePositionFromRectangle(zone, currentPos),
                ZoneType.RectangularSafeZone => GetSafePositionFromRectangularSafeZone(zone, currentPos),
                ZoneType.Expression => GetSafePositionFromExpression(zone, currentPos),
                _ => currentPos
            };
        }

        private static Vector3 GetSafePositionFromCircle(DangerZone zone, Vector3 currentPos)
        {
            var direction = currentPos - zone.CenterPos;
            direction = direction.Length() == 0 ? new Vector3(1.0f, 0, 0) : Vector3.Normalize(direction);
            return zone.CenterPos + (direction * (zone.Radius + 1.0f));
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
                return zone.CenterPos + (ringDirection * (zone.InnerRadius + 1.0f));
            else if (ringDistance > zone.Radius)
                return zone.CenterPos + (ringDirection * (zone.Radius - 1.0f));
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
            const float tolerance = 1e-6f;
            if (Math.Abs(minDist - distToLeft) < tolerance)
                squareNewX = zone.MinX - 1.0f;
            else if (Math.Abs(minDist - distToRight) < tolerance)
                squareNewX = zone.MaxX + 1.0f;
            else if (Math.Abs(minDist - distToTop) < tolerance)
                squareNewZ = zone.MinZ - 1.0f;
            else
                squareNewZ = zone.MaxZ + 1.0f;

            return new Vector3(squareNewX, currentPos.Y, squareNewZ);
        }

        private static Vector3 GetSafePositionFromRectangularSafeZone(DangerZone zone, Vector3 currentPos)
        {
            // 矩形内安全区：将玩家拉到矩形内的安全位置
            var newX = Math.Max(zone.MinX, Math.Min(zone.MaxX, currentPos.X));
            var newZ = Math.Max(zone.MinZ, Math.Min(zone.MaxZ, currentPos.Z));

            // 确保不在边界上，向内偏移一点
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
            dirToCenter = dirToCenter.Length() == 0 ? new Vector3(1.0f, 0, 0) : Vector3.Normalize(dirToCenter);
            return currentPos + (dirToCenter * 2.0f);
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
                if (double.TryParse(result.ToString(), out var numResult))
                    return numResult > 0;

                return false;
            }
            catch
            {
                return false;
            }
        }



        private void OnFrameworkUpdate(IFramework framework)
        {
            if (ModuleConfig == null || DService.ObjectTable.LocalPlayer == null)
                return;

            var zid = GameState.TerritoryType;

            if (!ModuleConfig.ZoneLimitList.TryGetValue(zid, out var zoneLimit))
                return;

            // 检查小队人数及死亡人数
            var deathCount = DService.PartyList.Count(p => p.CurrentHP <= 0);
            if (deathCount >= ModuleConfig.DisableOnDeathCount)
                return;

            var currentPos = DService.ObjectTable.LocalPlayer.Position;

            // 高级模式：检查多个危险区域
            if (zoneLimit is { IsAdvancedMode: true, DangerZones.Count: > 0 })
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                {
                    if (dangerZone.Enabled && IsInDangerZone(dangerZone, currentPos))
                    {
                        var safePos = GetSafePositionFromDangerZone(dangerZone, currentPos);
                        TeleportToSafePosition(safePos);
                        return;
                    }
                }
            }
            // 传统模式：单一边界检查
            else if (zoneLimit.Enabled)
                ProcessTraditionalMode(zoneLimit, currentPos);
        }

        private void ProcessTraditionalMode(ZoneLimit zoneLimit, Vector3 currentPos)
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
                return;

            if (isOutside)
                TeleportToSafePosition(newPos);
        }

        private void TeleportToSafePosition(Vector3 newPos)
        {
            MovementManager.TPPlayerAddress(newPos);
            if (ModuleConfig?.ShowDebugInfo == true)
                Chat(GetLoc("PreventEntryIntoMapBoundaries-DebugPosition",  newPos.X, newPos.Y, newPos.Z));
        }
         private void DrawRadarWindow()
        {
            if (ModuleConfig?.ShowBoundaryVisualization != true ||
                DService.ObjectTable.LocalPlayer == null)
                return;

            if (!ModuleConfig.ZoneLimitList.TryGetValue(GameState.TerritoryType, out var zoneLimit))
                return;

            var isOpen = true;
            if (!ImGui.Begin(GetLoc("PreventEntryIntoMapBoundaries-RadarWindow"), ref isOpen, ImGuiWindowFlags.NoResize))
            {
                ImGui.End();
                return;
            }

            ImGui.SetWindowSize(new Vector2(420, 600));

            const float radarRadius = 140f;
            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();
            var absoluteRadarCenter = new Vector2(windowPos.X + 210, windowPos.Y + 220);

            // 绘制雷达背景
            drawList.AddCircleFilled(absoluteRadarCenter, radarRadius, KnownColor.Gray.Vector().WithAlpha(0.2f).ToUint());
            drawList.AddCircle(absoluteRadarCenter, radarRadius, KnownColor.Green.Vector().ToUint(), 64, 2.0f);

            // 绘制雷达网格圈 (每圈代表一定距离)
            var gridDistance = ModuleConfig.RadarRange * 0.25f;
            var gridColor = KnownColor.Gray.Vector().WithAlpha(0.25f).ToUint();
            var labelColor = KnownColor.White.Vector().WithAlpha(0.5f).ToUint();

            for (var i = 1; i <= 4; i++)
            {
                var gridRadius = radarRadius * i * 0.25f;
                drawList.AddCircle(absoluteRadarCenter, gridRadius, gridColor, 32, 1.0f);

                if (i % 2 == 0 && gridRadius < radarRadius - 15)
                    drawList.AddText(new Vector2(absoluteRadarCenter.X + gridRadius + 5, absoluteRadarCenter.Y - 8), labelColor, $"{gridDistance * i:F0}m");
            }

            // 绘制十字线
            var crosshairColor = KnownColor.White.Vector().WithAlpha(0.25f).ToUint();
            drawList.AddLine(new Vector2(absoluteRadarCenter.X - radarRadius, absoluteRadarCenter.Y),
                           new Vector2(absoluteRadarCenter.X + radarRadius, absoluteRadarCenter.Y), crosshairColor, 1.0f);
            drawList.AddLine(new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y - radarRadius),
                           new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y + radarRadius), crosshairColor, 1.0f);

            var playerPos = DService.ObjectTable.LocalPlayer.Position;
            var radarCenterWorldPos = GetRadarCenterWorldPos();
            var playerRelativePos = playerPos - radarCenterWorldPos;
            var scale = radarRadius / ModuleConfig.RadarRange * ModuleConfig.RadarScale;
            var playerRadarPos = new Vector2(
                absoluteRadarCenter.X + playerRelativePos.X * scale,
                absoluteRadarCenter.Y + playerRelativePos.Z * scale);

            // 检查玩家是否在雷达显示范围内
            if (Vector2.Distance(playerRadarPos, absoluteRadarCenter) <= radarRadius)
            {
                drawList.AddCircleFilled(playerRadarPos, 4.0f, KnownColor.Red.Vector().ToUint());
                drawList.AddText(new Vector2(playerRadarPos.X + 8, playerRadarPos.Y - 8), KnownColor.Red.Vector().ToUint(), GetLoc("PreventEntryIntoMapBoundaries-Player"));
            }
            else
            {
                var directionToPlayer = Vector2.Normalize(playerRadarPos - absoluteRadarCenter);
                var edgePos = absoluteRadarCenter + directionToPlayer * (radarRadius - 10);
                drawList.AddCircleFilled(edgePos, 3.0f, KnownColor.Red.Vector().ToUint());
                drawList.AddText(new Vector2(edgePos.X + 6, edgePos.Y - 6), KnownColor.Red.Vector().ToUint(), GetLoc("PreventEntryIntoMapBoundaries-PlayerOutside"));
            }

            // 高级模式：绘制多个危险区域
            if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                    DrawDangerZoneOnRadar(drawList, absoluteRadarCenter, radarRadius, dangerZone);
            }
            else
                DrawTraditionalBoundaryOnRadar(drawList, absoluteRadarCenter, radarRadius, zoneLimit);

            // 绘制雷达中心标记
            drawList.AddCircle(absoluteRadarCenter, 3.0f, KnownColor.Blue.Vector().ToUint(), 16, 2.0f);
            drawList.AddText(new Vector2(absoluteRadarCenter.X + 8, absoluteRadarCenter.Y + 8), KnownColor.Blue.Vector().ToUint(), GetLoc("PreventEntryIntoMapBoundaries-Center"));

            // 显示实时信息
            ImGui.SetCursorPos(new Vector2(10, 310));
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-PlayerPosition", playerPos.X, playerPos.Z));
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-RadarCenter", radarCenterWorldPos.X, radarCenterWorldPos.Z));
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DisplayRange", ModuleConfig.RadarRange));
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-Scale", ModuleConfig.RadarRange / radarRadius));
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-RadarSettings"));

            var radarRange = ModuleConfig.RadarRange;
            if (ImGui.SliderFloat(GetLoc("PreventEntryIntoMapBoundaries-RadarRange"), ref radarRange, 10.0f, 200.0f, "%.1f"))
            {
                ModuleConfig.RadarRange = radarRange;
                SaveConfig(ModuleConfig);
            }

            var radarScale = ModuleConfig.RadarScale;
            if (ImGui.SliderFloat(GetLoc("PreventEntryIntoMapBoundaries-RadarScale"), ref radarScale, 0.5f, 5.0f, "%.1f"))
            {
                ModuleConfig.RadarScale = radarScale;
                SaveConfig(ModuleConfig);
            }

            ImGui.Spacing();

            // 显示当前危险状态
            var inDanger = false;
            if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                {
                    if (dangerZone.Enabled && IsInDangerZone(dangerZone, playerPos))
                    {
                        inDanger = true;
                        break;
                    }
                }
            }
            else if (zoneLimit.Enabled)
            {
                if (zoneLimit.MapType == MapType.Circle)
                    inDanger = (playerPos - zoneLimit.CenterPos).Length() >= zoneLimit.Radius - 0.3f;
                else if (zoneLimit.MapType == MapType.Rectangle)
                {
                    var halfSize = zoneLimit.Radius;
                    inDanger = Math.Abs(playerPos.X - zoneLimit.CenterPos.X) >= halfSize - 0.3f ||
                              Math.Abs(playerPos.Z - zoneLimit.CenterPos.Z) >= halfSize - 0.3f;
                }
            }

            ImGui.TextColored(inDanger ? KnownColor.Red.Vector() : KnownColor.Green.Vector(),
                             inDanger ? GetLoc("PreventEntryIntoMapBoundariesStatusDangerous") : GetLoc("PreventEntryIntoMapBoundariesStatusSafe"));

            ImGui.End();

            if (!isOpen)
            {
                ModuleConfig.ShowBoundaryVisualization = false;
                SaveConfig(ModuleConfig);
            }
        }

        private void DrawDangerZoneOnRadar(ImDrawListPtr drawList, Vector2 radarCenter, float radarRadius, DangerZone zone)
        {
            // 计算比例尺
            var scale = radarRadius / ModuleConfig.RadarRange;

            // 危险区域在雷达中的固定位置（相对于雷达中心）
            var relativeToRadarCenter = zone.CenterPos - GetRadarCenterWorldPos();
            var zoneRadarX = radarCenter.X + relativeToRadarCenter.X * scale * ModuleConfig.RadarScale;
            var zoneRadarY = radarCenter.Y + relativeToRadarCenter.Z * scale * ModuleConfig.RadarScale; // Z轴正向

            var color = zone.Enabled ? (zone.Color | 0x80000000) : KnownColor.Gray.Vector().WithAlpha(0.25f).ToUint();

            switch (zone.ZoneType)
            {
                case ZoneType.Circle:
                    var zoneRadiusOnRadar = zone.Radius * scale * ModuleConfig.RadarScale;
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), zoneRadiusOnRadar, color, 32, 2.0f);
                    // 绘制填充区域以便更好地可视化
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), zoneRadiusOnRadar, (uint)(color & 0x30FFFFFF));
                    break;

                case ZoneType.Annulus:
                    var outerRadiusOnRadar = zone.Radius * scale * ModuleConfig.RadarScale;
                    var innerRadiusOnRadar = zone.InnerRadius * scale * ModuleConfig.RadarScale;

                    // 绘制外圆边界（危险区域边界）
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), outerRadiusOnRadar, color, 32, 2.0f);
                    // 绘制内圆边界（危险区域边界）
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), innerRadiusOnRadar, color, 32, 2.0f);

                    // 填充内圆（危险区域）
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), innerRadiusOnRadar, (uint)(color & 0x40FFFFFF));

                    // 绘制安全圆环区域（绿色显示）
                    var safeDountColor = KnownColor.Green.Vector().WithAlpha(0.5f).ToUint();
                    for (float r = innerRadiusOnRadar + 2; r < outerRadiusOnRadar; r += 3.0f)
                        drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), r, safeDountColor, 16, 1.0f);

                    // 添加文字说明
                    if (innerRadiusOnRadar > 10) // 在内圆显示危险
                    {
                        var dangerTextPos = new Vector2(zoneRadarX, zoneRadarY - innerRadiusOnRadar / 2);
                        drawList.AddText(dangerTextPos, color, GetLoc("PreventEntryIntoMapBoundariesDangerous"));
                    }
                    var safeTextPos = new Vector2(zoneRadarX + (innerRadiusOnRadar + outerRadiusOnRadar) / 2, zoneRadarY - 5);
                    drawList.AddText(safeTextPos, KnownColor.Green.Vector().ToUint(), GetLoc("PreventEntryIntoMapBoundariesSafe"));
                    break;

                case ZoneType.Rectangle:
                    // 使用边界绘制方形
                    var radarCenterWorld = GetRadarCenterWorldPos();
                    var scaleRadar = scale * ModuleConfig.RadarScale;

                    drawList.AddRect(
                        new Vector2(
                            radarCenter.X + ((zone.MinX - radarCenterWorld.X) * scaleRadar),
                            radarCenter.Y + ((zone.MinZ - radarCenterWorld.Z) * scaleRadar)),
                        new Vector2(
                            radarCenter.X + ((zone.MaxX - radarCenterWorld.X) * scaleRadar),
                            radarCenter.Y + ((zone.MaxZ - radarCenterWorld.Z) * scaleRadar)),
                        color, 0.0f, ImDrawFlags.None, 2.0f);
                    // 绘制填充区域
                    drawList.AddRectFilled(
                        new Vector2(
                            radarCenter.X + ((zone.MinX - radarCenterWorld.X) * scaleRadar),
                            radarCenter.Y + ((zone.MinZ - radarCenterWorld.Z) * scaleRadar)),
                        new Vector2(
                            radarCenter.X + ((zone.MaxX - radarCenterWorld.X) * scaleRadar),
                            radarCenter.Y + ((zone.MaxZ - radarCenterWorld.Z) * scaleRadar)),
                        (color & 0x30FFFFFF));
                    break;

                case ZoneType.RectangularSafeZone:
                    // 使用MinX/MaxX和MinZ/MaxZ绘制矩形安全区
                    var radarCenterWorldSafe = GetRadarCenterWorldPos();

                    var sminXRelative = zone.MinX - radarCenterWorldSafe.X;
                    var smaxXRelative = zone.MaxX - radarCenterWorldSafe.X;
                    var sminZRelative = zone.MinZ - radarCenterWorldSafe.Z;
                    var smaxZRelative = zone.MaxZ - radarCenterWorldSafe.Z;

                    var safeTopLeft = new Vector2(
                        radarCenter.X + sminXRelative * scale * ModuleConfig.RadarScale,
                        radarCenter.Y + sminZRelative * scale * ModuleConfig.RadarScale
                    );
                    var safeBottomRight = new Vector2(
                        radarCenter.X + smaxXRelative * scale * ModuleConfig.RadarScale,
                        radarCenter.Y + smaxZRelative * scale * ModuleConfig.RadarScale
                    );

                    // 绘制安全区边界（绿色）
                    drawList.AddRect(safeTopLeft, safeBottomRight, KnownColor.Green.Vector().ToUint(), 0.0f, ImDrawFlags.None, 2.0f);
                    // 绘制半透明绿色填充
                    drawList.AddRectFilled(safeTopLeft, safeBottomRight, KnownColor.Green.Vector().WithAlpha(0.25f).ToUint());

                    // 添加文字说明
                    var safeCenterX = (safeTopLeft.X + safeBottomRight.X) / 2;
                    var safeCenterY = (safeTopLeft.Y + safeBottomRight.Y) / 2;
                    drawList.AddText(new Vector2(safeCenterX - 15, safeCenterY - 8), KnownColor.Green.Vector().ToUint(), GetLoc("PreventEntryIntoMapBoundariesSafeZone"));
                    break;

                case ZoneType.Expression:
                    // 对于复杂表达式，绘制一个更明显的标记
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), 6.0f, color);
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), 8.0f, color, 16, 2.0f);
                    break;
            }

            // 绘制区域中心点（除了方形类型）
            if (zone.ZoneType != ZoneType.Rectangle)
                drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), 2.0f, color);
            else
            {
                // 方形类型绘制边界标记
                var radarCenterWorld = GetRadarCenterWorldPos();

                var scaleRadar = scale * ModuleConfig.RadarScale;

                // 绘制对角点
                drawList.AddCircleFilled(new Vector2(
                    radarCenter.X + (zone.MinX - radarCenterWorld.X) * scaleRadar,
                    radarCenter.Y + (zone.MinZ - radarCenterWorld.Z) * scaleRadar), 3.0f, color);
                drawList.AddCircleFilled(new Vector2(
                    radarCenter.X + (zone.MaxX - radarCenterWorld.X) * scaleRadar,
                    radarCenter.Y + (zone.MaxZ - radarCenterWorld.Z) * scaleRadar), 3.0f, color);
            }

            // 绘制区域名称（智能位置避免重叠）
            if (!string.IsNullOrEmpty(zone.Name))
            {
                var textSize = ImGui.CalcTextSize(zone.Name);
                // 默认使用正下方位置
                var finalTextPos = new Vector2(zoneRadarX - textSize.X / 2, zoneRadarY + 20);

                // 添加文字背景提高可读性
                var bgColor = KnownColor.Gray.Vector().WithAlpha(0.5f).ToUint();
                drawList.AddRectFilled(
                    new Vector2(finalTextPos.X - 2, finalTextPos.Y - 2),
                    new Vector2(finalTextPos.X + textSize.X + 2, finalTextPos.Y + textSize.Y + 2),
                    bgColor);

                drawList.AddText(finalTextPos, color, zone.Name);
            }
        }

        private void DrawTraditionalBoundaryOnRadar(ImDrawListPtr drawList, Vector2 radarCenter, float radarRadius, ZoneLimit zoneLimit)
        {
            // 计算比例尺
            var scale = radarRadius / ModuleConfig.RadarRange;

            // 边界区域在雷达中的固定位置（相对于雷达中心）
            var relativeToRadarCenter = zoneLimit.CenterPos - GetRadarCenterWorldPos();
            var boundaryRadarX = radarCenter.X + relativeToRadarCenter.X * scale * ModuleConfig.RadarScale;
            var boundaryRadarY = radarCenter.Y + relativeToRadarCenter.Z * scale * ModuleConfig.RadarScale; // Z轴正向

            var color = zoneLimit.Enabled ? KnownColor.Red.Vector().ToUint() : KnownColor.Green.Vector().ToUint();
            var fillColor = zoneLimit.Enabled ? KnownColor.Red.Vector().WithAlpha(0.2f).ToUint() : KnownColor.Green.Vector().WithAlpha(0.2f).ToUint();

            if (zoneLimit.MapType == MapType.Circle)
            {
                var zoneRadiusOnRadar = zoneLimit.Radius * scale * ModuleConfig.RadarScale;
                drawList.AddCircle(new Vector2(boundaryRadarX, boundaryRadarY), zoneRadiusOnRadar, color, 32, 2.0f);
                drawList.AddCircleFilled(new Vector2(boundaryRadarX, boundaryRadarY), zoneRadiusOnRadar, (uint)fillColor);
            }
            else if (zoneLimit.MapType == MapType.Rectangle)
            {
                var halfSize = zoneLimit.Radius * scale * ModuleConfig.RadarScale;
                var topLeft = new Vector2(boundaryRadarX - halfSize, boundaryRadarY - halfSize);
                var bottomRight = new Vector2(boundaryRadarX + halfSize, boundaryRadarY + halfSize);
                drawList.AddRect(topLeft, bottomRight, color, 0.0f, ImDrawFlags.None, 2.0f);
                drawList.AddRectFilled(topLeft, bottomRight, (uint)fillColor);
            }

            // 绘制边界中心点
            drawList.AddCircleFilled(new Vector2(boundaryRadarX, boundaryRadarY), 2.0f, color);

            // 标注边界类型（智能位置）
            var boundaryText = string.Format(GetLoc("PreventEntryIntoMapBoundariesBoundary"), zoneLimit.MapType);
            var textSize = ImGui.CalcTextSize(boundaryText);
            var textOffset = 15;

            // 计算最佳文字位置，避免超出雷达边界
            var textPos = new Vector2(boundaryRadarX + textOffset, boundaryRadarY - textOffset);
            var distanceFromCenter = Vector2.Distance(textPos, radarCenter);

            if (distanceFromCenter + textSize.X > radarRadius - 10)
            {
                // 如果右上角超出边界，尝试左上角
                textPos = new Vector2(boundaryRadarX - textOffset - textSize.X, boundaryRadarY - textOffset);
                distanceFromCenter = Vector2.Distance(textPos, radarCenter);

                if (distanceFromCenter + textSize.X > radarRadius - 10)
                    textPos = new Vector2(boundaryRadarX - textSize.X / 2, boundaryRadarY + textOffset);
            }

            // 添加文字背景
            var bgColor = KnownColor.Green.Vector().WithAlpha(0.5f).ToUint();
            drawList.AddRectFilled(
                new Vector2(textPos.X - 2, textPos.Y - 2),
                new Vector2(textPos.X + textSize.X + 2, textPos.Y + textSize.Y + 2),
                bgColor);

            drawList.AddText(textPos, color, boundaryText);
        }

        private Vector3 GetRadarCenterWorldPos()
        {
            var zid = DService.ClientState.TerritoryType;
            if (ModuleConfig?.ZoneLimitList.TryGetValue(zid, out var zoneLimit) == true)
            {
                if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
                    return zoneLimit.DangerZones[0].CenterPos;
                else
                    return zoneLimit.CenterPos;
            }

            // 如果没有配置区域，使用玩家位置
            return DService.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        }
        

       
        private void OnCommand(string command, string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return;

            var action = parts[0].ToLower();
            var zid = GameState.TerritoryType;
            

            if (!ModuleConfig.ZoneLimitList.TryGetValue(zid, out var zoneLimit))
                return;

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
                    ChatError(GetLoc("Commands-SubCommandNotFound", action));
                    break;
            }
        }

        private void HandleAddCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("Commands-InvalidArgs"));
                return;
            }

            var shapeType = args[0].ToLower();
            var dangerZone = new DangerZone(GetLoc("PreventEntryIntoMapBoundaries-CommandZone", zoneLimit.DangerZones.Count + 1));

            switch (shapeType)
            {
                case "circle":
                    if (args.Length < 4)
                    {
                        ChatError(GetLoc("Commands-InvalidArgs"));
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
                        ChatError(GetLoc("Commands-InvalidArgs"));
                        return;
                    }
                    break;

                case "rect":
                    if (args.Length < 5)
                    {
                        ChatError(GetLoc("Commands-InvalidArgs"));
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
                        ChatError(GetLoc("Commands-InvalidArgs"));
                        return;
                    }
                    break;

                default:
                    ChatError(GetLoc("Commands-InvalidArgs", shapeType));
                    return;
            }

            zoneLimit.DangerZones.Add(dangerZone);
            zoneLimit.IsAdvancedMode = true;
            SaveConfig(ModuleConfig!);
        }

        private void HandleDeleteCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("Commands-InvalidArgs"));
                return;
            }

            if (int.TryParse(args[0], out var index) && index > 0 && index <= zoneLimit.DangerZones.Count)
            {
                var removedZone = zoneLimit.DangerZones[index - 1];
                zoneLimit.DangerZones.RemoveAt(index - 1);
                SaveConfig(ModuleConfig!);
                Chat(GetLoc("Deleted", removedZone.Name));
            }
           
        }

        private void HandleModifyCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 3)
            {
                ChatError(GetLoc("Commands-InvalidArgs"));
                return;
            }

            if (!int.TryParse(args[0], out var index) || index <= 0 || index > zoneLimit.DangerZones.Count)
            {
                ChatError(GetLoc("Commands-InvalidArgs", zoneLimit.DangerZones.Count));
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
                        SaveConfig(ModuleConfig!);
                        Chat(GetLoc("PreventEntryIntoMapBoundaries-ZoneToggled",
                            enabled ? GetLoc("Enabled") : GetLoc("Disable"),
                            dangerZone.Name));
                    }
                    break;

                case "name":
                    dangerZone.Name = value;
                    SaveConfig(ModuleConfig!);
                    Chat((GetLoc("PreventEntryIntoMapBoundaries-NameChanged", value)));
                    break;

                case "color":
                    if (uint.TryParse(value, out var color))
                    {
                        dangerZone.Color = color;
                        SaveConfig(ModuleConfig!);
                        Chat((GetLoc("PreventEntryIntoMapBoundaries-ColorChanged", color.ToString("X8"))));
                    }
                   
                    break;

                default:
                    ChatError((GetLoc("Commands-InvalidArgs", property)));
                    break;
            }
        }

        private static void ExportCurrentZoneConfig()
        {
            var zid = GameState.TerritoryType;
            if (!ModuleConfig.ZoneLimitList.TryGetValue(zid, out var zoneLimit))
                return;

            var exportConfig = new Config
            {
                ZoneIDs = [zid],
                ZoneLimitList = new Dictionary<uint, ZoneLimit> { { zid, zoneLimit } }
            };

            ExportToClipboard(exportConfig);
            
        }

        protected override void Uninit()
        {
            FrameworkManager.Unregister(OnFrameworkUpdate);
            RemoveSubCommand(Command);
        }
        
        private class Config : ModuleConfiguration
        {
            public List<uint> ZoneIDs { get; set; } = [];
            public Dictionary<uint, ZoneLimit> ZoneLimitList { get; set; } = [];
            public bool ShowDebugInfo ;
            public bool ShowBoundaryVisualization ;
            public int DisableOnDeathCount  = 2;
            public float RadarRange = 50.0f;
            public float RadarScale = 1.0f;
        }
        private enum ZoneType
        {
            Circle = 0,    // 圆形
            Annulus = 1,   // 月环
            Rectangle = 2, // 矩形
            RectangularSafeZone = 3,  // 矩形内安全区
            Expression = 4 // 数学表达式
        }

        private enum MapType
        {
            Circle = 0,   // 圆形
            Rectangle = 2 // 矩形
        }

        private class DangerZone(string name = "")
        {
            public bool Enabled = false;
            public string Name = name;
            public ZoneType ZoneType = ZoneType.Circle;
            public Vector3 CenterPos = new(100, 0, 100);
            public float Radius = 20f;
            public float InnerRadius = 10f;
            public float MinX = 80f;
            public float MaxX = 120f;
            public float MinZ = 80f;
            public float MaxZ = 120f;
            public string MathExpression = "(x-100)^2 + (z-100)^2 <= 400";
            public uint Color = KnownColor.Red.Vector().ToUint();
        }

        private class ZoneLimit
        {
            public bool Enabled = false;
            public MapType MapType = MapType.Circle;
            public Vector3 CenterPos = new(100, 0, 100);
            public float Radius = 20f;
            public List<DangerZone> DangerZones = new();
            public bool IsAdvancedMode = false;
        }
    }

