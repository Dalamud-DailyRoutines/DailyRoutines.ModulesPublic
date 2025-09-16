using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Data;
using DailyRoutines.Abstracts;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using static DailyRoutines.Managers.CommandManager;
using ImGuiNET;
using Dalamud.Interface;
using OmenTools;
using static DailyRoutines.Helpers.NotifyHelper;


namespace DailyRoutines.ModulesPublic
{
    public class DangerZone
    {
        public bool Enabled { get; set; }
        public string Name { get; set; }
        public string ZoneType { get; set; } // "圆", "月环", "方", "矩形安全区", "表达式"
        public Vector3 CenterPos { get; set; }
        public float Radius { get; set; }
        public float InnerRadius { get; set; } // 月环内半径
        public float MinX { get; set; } // 矩形安全区最小X
        public float MaxX { get; set; } // 矩形安全区最大X
        public float MinZ { get; set; } // 矩形安全区最小Z
        public float MaxZ { get; set; } // 矩形安全区最大Z
        public string MathExpression { get; set; } // 数学表达式，支持 x, z 变量
        public uint Color { get; set; } // RGBA color for debug visualization

        public DangerZone(string name = "危险区域")
        {
            Enabled = false; // 默认关闭
            Name = name;
            ZoneType = "圆";
            CenterPos = new Vector3(100, 0, 100); // 默认中心点
            Radius = 20f;
            InnerRadius = 10f;
            MinX = 80f;
            MaxX = 120f;
            MinZ = 80f;
            MaxZ = 120f;
            MathExpression = "(x-100)^2 + (z-100)^2 <= 400"; // 默认圆形表达式
            Color = 0xFF0000FF; // 红色
        }
    }

    public class ZoneLimit
    {
        public bool Enabled { get; set; }
        public uint ZoneId { get; set; }
        public string MapType { get; set; }
        public Vector3 CenterPos { get; set; }
        public float Radius { get; set; }
        public List<DangerZone> DangerZones { get; set; }
        public bool IsAdvancedMode { get; set; }

        public ZoneLimit(uint id)
        {
            Enabled = false;
            ZoneId = id;
            MapType = "圆"; // 默认地图类型
            CenterPos = new Vector3(100, 0, 100);
            Radius = 20f;
            DangerZones = new List<DangerZone>();
            IsAdvancedMode = false;
        }
    }

    public unsafe class PreventEntryIntoMapBoundaries : DailyModuleBase
    {
        public override ModuleInfo Info { get; } = new()
        {
            Title           = GetLoc("PreventEntryIntoMapBoundaries-Title"),
            Description     = GetLoc("PreventEntryIntoMapBoundaries-Description"),
            Category        = ModuleCategories.Combat,
            Author          = ["Nag0mi"]
        };

        private const string Command = "pdrfence";
        private Config? ModuleConfig;

        public class Config : ModuleConfiguration
        {
            public List<uint> ZoneIds { get; set; } = [];
            public Dictionary<uint, ZoneLimit> ZoneLimitList { get; set; } = [];
            public bool ShowDebugInfo { get; set; } = false;
            public bool ShowBoundaryVisualization { get; set; } = false;
            public float RadarRange { get; set; } = 50.0f; // 雷达显示范围（米）
            public float RadarScale { get; set; } = 2.0f; // 雷达缩放系数
            public int DisableOnDeathCount { get; set; } = 2; // 多少个死亡玩家时禁用防撞电网
        }

        protected override void Init()
        {
            ModuleConfig = LoadConfig<Config>() ?? new();
            DService.Framework.Update += OnFrameworkUpdate;
            DService.ClientState.TerritoryChanged += OnTerritoryChanged;

            AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("PreventEntryIntoMapBoundaries-CommandHelp") });
        }

        protected override void ConfigUI()
        {
            if (ModuleConfig == null) return;

            ImGui.BeginChild(GetLoc("PreventEntryIntoMapBoundaries-Config"), new Vector2(0, 0), false);

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
                    // 默认中心点为 100,0,100，不使用玩家位置

                    SaveConfig(ModuleConfig);
                    Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneAdded"), currentZoneId));
                }
                else
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneExists"), currentZoneId));
            }

            ImGui.Separator();

            // 显示调试信息选项
            var showDebug = ModuleConfig.ShowDebugInfo;
            if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundaries-ShowDebugInfo"), ref showDebug))
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
            if (ImGui.SliderInt(GetLoc("PreventEntryIntoMapBoundaries-DeathCountThreshold"), ref deathCount, 0, 8, "%d人"))
            {
                ModuleConfig.DisableOnDeathCount = deathCount;
                SaveConfig(ModuleConfig);
            }
            ImGuiComponents.HelpMarker(GetLoc("PreventEntryIntoMapBoundaries-DeathCountHelp"));

            // 配置导入导出
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ConfigManagement"));
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-ExportAll")))
                ExportAllConfigs();
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-Import")))
                ImportConfigs();
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-ExportCurrent")))
                ExportCurrentZoneConfig();

            ImGui.Separator();

            if (ModuleConfig.ZoneIds.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundaries-NoZones"));
                ImGui.EndChild();
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
                var nodeLabel = $"区域 {zoneId}: {zoneName}";

                ImGui.PushStyleColor(ImGuiCol.Text, nodeColor);
                var isOpen = ImGui.TreeNode($"{nodeLabel}###{zoneId}");
                ImGui.PopStyleColor();

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
                    if (isCurrentZone && ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-SetCurrentPos")))
                    {
                        if (DService.ClientState.LocalPlayer != null)
                        {
                            zoneLimit.CenterPos = DService.ClientState.LocalPlayer.Position;
                            SaveConfig(ModuleConfig);
                            Chat(GetLoc("PreventEntryIntoMapBoundaries-CenterUpdated"));
                        }
                    }

                    if (zoneLimit.IsAdvancedMode)
                        DrawAdvancedModeUI(zoneLimit, isCurrentZone);
                    else
                        DrawTraditionalModeUI(zoneLimit, zoneId, isCurrentZone);

                    ImGuiHelpers.ScaledDummy(5.0f);
                    ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DPSRed);
                    if (ImGui.Button(string.Format(GetLoc("PreventEntryIntoMapBoundaries-DeleteZone"), zoneId)))
                    {
                        ModuleConfig.ZoneIds.RemoveAt(i);
                        ModuleConfig.ZoneLimitList.Remove(zoneId);
                        SaveConfig(ModuleConfig);
                        ImGui.PopStyleColor();
                        ImGui.TreePop();
                        break;
                    }
                    ImGui.PopStyleColor();

                    ImGui.TreePop();
                }

                // 只在不是最后一个元素时添加分隔线
                if (i < ModuleConfig.ZoneIds.Count - 1)
                    ImGui.Separator();
                    
            }

            ImGui.EndChild();

            // 绘制雷达窗口（如果启用了边界可视化）
            if (ModuleConfig.ShowBoundaryVisualization)
                DrawRadarWindow();
        }

        private void DrawTraditionalModeUI(ZoneLimit zoneLimit, uint zoneId, bool isCurrentZone)
        {
            // 地图类型选择
            var mapTypes = new[] { GetLoc("PreventEntryIntoMapBoundaries-Circle"), GetLoc("PreventEntryIntoMapBoundaries-Rectangle") };
            var mapTypeValues = new[] { "圆", "方" };
            var currentMapTypeIndex = Array.IndexOf(mapTypeValues, zoneLimit.MapType);
            if (currentMapTypeIndex == -1)
                currentMapTypeIndex = 0;

            if (ImGui.Combo(GetLoc("PreventEntryIntoMapBoundaries-MapType"), ref currentMapTypeIndex, mapTypes, mapTypes.Length))
            {
                zoneLimit.MapType = mapTypeValues[currentMapTypeIndex];
                SaveConfig(ModuleConfig);
            }

            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
            var centerPos = zoneLimit.CenterPos;
            if (ImGui.InputFloat3($"##CenterPos{zoneId}", ref centerPos, "%.2f"))
            {
                zoneLimit.CenterPos = centerPos;
                SaveConfig(ModuleConfig);
            }

            var radius = zoneLimit.Radius;
            if (ImGui.InputFloat(GetLoc("PreventEntryIntoMapBoundaries-Radius"), ref radius, 1.0f, 10.0f, "%.2f"))
            {
                zoneLimit.Radius = Math.Max(1.0f, radius);
                SaveConfig(ModuleConfig);
            }

            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
            {
                var playerPos = DService.ClientState.LocalPlayer.Position;
                var distance = Vector3.Distance(playerPos, zoneLimit.CenterPos);
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"距离中心: {distance:F2}");
            }
        }

        private void DrawAdvancedModeUI(ZoneLimit zoneLimit, bool isCurrentZone)
        {
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DangerZoneManagement"));

            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddDangerZone")))
            {
                var newZone = new DangerZone(string.Format(GetLoc("PreventEntryIntoMapBoundaries-DangerZone"), zoneLimit.DangerZones.Count + 1));
                // 默认中心点为 100,0,100，不使用玩家位置

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

                    var dzName = dangerZone.Name;
                    if (ImGui.InputText($"{GetLoc("PreventEntryIntoMapBoundaries-Name")}##dz{j}", ref dzName, 50))
                    {
                        dangerZone.Name = dzName;
                        SaveConfig(ModuleConfig);
                    }

                    // 区域类型选择
                    var zoneTypes = new[] { GetLoc("PreventEntryIntoMapBoundaries-Circle"), GetLoc("PreventEntryIntoMapBoundaries-Annulus"), GetLoc("PreventEntryIntoMapBoundaries-Rectangle"), GetLoc("PreventEntryIntoMapBoundaries-SafeZone"), GetLoc("PreventEntryIntoMapBoundaries-MathExpression") };
                    var zoneTypeValues = new[] { "圆", "月环", "方", "矩形安全区", "表达式" };
                    var currentZoneTypeIndex = Array.IndexOf(zoneTypeValues, dangerZone.ZoneType);
                    if (currentZoneTypeIndex == -1)
                        currentZoneTypeIndex = 0;

                    if (ImGui.Combo($"{GetLoc("PreventEntryIntoMapBoundaries-Type")}##dz{j}", ref currentZoneTypeIndex, zoneTypes, zoneTypes.Length))
                    {
                        dangerZone.ZoneType = zoneTypeValues[currentZoneTypeIndex];
                        SaveConfig(ModuleConfig);
                    }

                    // 根据类型显示不同的参数
                    if (dangerZone.ZoneType == "表达式")
                    {
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-MathExpressionDesc"));
                        ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundaries-MathExample"));
                        var mathExpr = dangerZone.MathExpression;
                        if (ImGui.InputTextMultiline($"##expr{j}", ref mathExpr, 200, new Vector2(300, 60)))
                        {
                            dangerZone.MathExpression = mathExpr;
                            SaveConfig(ModuleConfig);
                        }
                    }

                    // 根据类型显示不同的参数
                    if (dangerZone.ZoneType == "圆" || dangerZone.ZoneType == "月环")
                    {
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
                        var dzCenterPos = dangerZone.CenterPos;
                        if (ImGui.InputFloat3($"##dzCenter{j}", ref dzCenterPos, "%.2f"))
                        {
                            dangerZone.CenterPos = dzCenterPos;
                            SaveConfig(ModuleConfig);
                        }

                        var dzRadius = dangerZone.Radius;
                        if (ImGui.InputFloat($"{GetLoc("PreventEntryIntoMapBoundaries-OuterRadius")}##dz{j}", ref dzRadius, 1.0f, 10.0f, "%.2f"))
                        {
                            dangerZone.Radius = Math.Max(1.0f, dzRadius);
                            SaveConfig(ModuleConfig);
                        }

                        if (dangerZone.ZoneType == "月环")
                        {
                            ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("PreventEntryIntoMapBoundaries-AnnulusDesc"));
                            var dzInnerRadius = dangerZone.InnerRadius;
                            if (ImGui.InputFloat($"{GetLoc("PreventEntryIntoMapBoundaries-InnerRadius")}##dz{j}", ref dzInnerRadius, 1.0f, 10.0f, "%.2f"))
                            {
                                dangerZone.InnerRadius = Math.Max(0.0f, Math.Min(dzInnerRadius, dangerZone.Radius - 0.1f));
                                SaveConfig(ModuleConfig);
                            }
                        }
                    }
                    else if (dangerZone.ZoneType == "方")
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("PreventEntryIntoMapBoundaries-SquareDangerDesc"));

                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-XRange"));
                        var squareMinX = dangerZone.MinX;
                        var squareMaxX = dangerZone.MaxX;
                        if (ImGui.InputFloat($"最小X##square{j}", ref squareMinX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinX = squareMinX;
                            SaveConfig(ModuleConfig);
                        }
                        if (ImGui.InputFloat($"最大X##square{j}", ref squareMaxX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxX = squareMaxX;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ZRange"));
                        var squareMinZ = dangerZone.MinZ;
                        var squareMaxZ = dangerZone.MaxZ;
                        if (ImGui.InputFloat($"最小Z##square{j}", ref squareMinZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinZ = squareMinZ;
                            SaveConfig(ModuleConfig);
                        }
                        if (ImGui.InputFloat($"最大Z##square{j}", ref squareMaxZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxZ = squareMaxZ;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(GetLoc("PreventEntryIntoMapBoundaries-CurrentDangerArea"), dangerZone.MinX, dangerZone.MaxX, dangerZone.MinZ, dangerZone.MaxZ));
                    }
                    else if (dangerZone.ZoneType == "矩形安全区")
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("PreventEntryIntoMapBoundaries-SafeZoneDesc"));

                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-XRange"));
                        var minX = dangerZone.MinX;
                        var maxX = dangerZone.MaxX;
                        if (ImGui.InputFloat($"最小X##dz{j}", ref minX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinX = minX;
                            SaveConfig(ModuleConfig);
                        }
                        if (ImGui.InputFloat($"最大X##dz{j}", ref maxX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxX = maxX;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-ZRange"));
                        var minZ = dangerZone.MinZ;
                        var maxZ = dangerZone.MaxZ;
                        if (ImGui.InputFloat($"最小Z##dz{j}", ref minZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinZ = minZ;
                            SaveConfig(ModuleConfig);
                        }
                        if (ImGui.InputFloat($"最大Z##dz{j}", ref maxZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxZ = maxZ;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(GetLoc("PreventEntryIntoMapBoundaries-CurrentSafeArea"), dangerZone.MinX, dangerZone.MaxX, dangerZone.MinZ, dangerZone.MaxZ));
                    }
                    else if (dangerZone.ZoneType != "表达式")
                    {
                        // 其他类型的后备处理
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
                        var dzCenterPos = dangerZone.CenterPos;
                        if (ImGui.InputFloat3($"##dzCenter{j}", ref dzCenterPos, "%.2f"))
                        {
                            dangerZone.CenterPos = dzCenterPos;
                            SaveConfig(ModuleConfig);
                        }
                    }

                    // 颜色选择
                    var color = new Vector4(
                        ((dangerZone.Color >> 0) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 8) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 16) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 24) & 0xFF) / 255.0f
                    );

                    if (ImGui.ColorEdit4($"{GetLoc("PreventEntryIntoMapBoundaries-Color")}##dz{j}", ref color))
                    {
                        dangerZone.Color =
                            ((uint)(color.W * 255) << 24) |
                            ((uint)(color.Z * 255) << 16) |
                            ((uint)(color.Y * 255) << 8) |
                            ((uint)(color.X * 255) << 0);
                        SaveConfig(ModuleConfig);
                    }

                    // 调试信息
                    if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
                    {
                        var playerPos = DService.ClientState.LocalPlayer.Position;
                        var inDanger = IsInDangerZone(dangerZone, playerPos);
                        ImGui.TextColored(
                            inDanger ? ImGuiColors.DPSRed : ImGuiColors.HealerGreen,
                            string.Format(GetLoc("PreventEntryIntoMapBoundaries-CurrentStatus"), inDanger ? GetLoc("PreventEntryIntoMapBoundaries-Dangerous") : GetLoc("PreventEntryIntoMapBoundaries-Safe"))
                        );
                    }

                    // 删除危险区域按钮
                    ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DPSRed);
                    if (ImGui.Button($"{GetLoc("PreventEntryIntoMapBoundaries-Delete")}##dz{j}"))
                    {
                        zoneLimit.DangerZones.RemoveAt(j);
                        SaveConfig(ModuleConfig);
                        ImGui.PopStyleColor();
                        ImGui.TreePop();
                        break;
                    }
                    ImGui.PopStyleColor();

                    ImGui.TreePop();
                }
            }
        }

        private static string GetZoneName(uint zoneId)
        {
            return $"Zone {zoneId}"; // 简化区域名称显示
        }

        private static bool EvaluateMathExpression(string expression, float x, float z)
        {
            try
            {
                // 替换变量
                var expr = expression.Replace("x", x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                                   .Replace("z", z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
                                   .Replace("^", "**"); // 支持幂运算

                // 简单的表达式求值
                var table = new DataTable();
                var result = table.Compute(expr, null);

                if (result == DBNull.Value) return false;

                // 如果结果是布尔值，直接返回
                if (result is bool boolResult) return boolResult;

                // 如果结果是数值，检查是否 > 0 或 == 1
                if (double.TryParse(result.ToString(), out double numResult))
                    return numResult > 0;

                return false;
            }
            catch
            {
                return false; // 表达式错误时默认为安全
            }
        }

        private static bool IsInDangerZone(DangerZone zone, Vector3 position)
        {
            if (!zone.Enabled) return false;

            var x = position.X;
            var z = position.Z;

            switch (zone.ZoneType)
            {
                case "圆":
                    var distance = (position - zone.CenterPos).Length();
                    return distance <= zone.Radius;

                case "月环":
                    var ringDistance = (position - zone.CenterPos).Length();
                    // 月环：圆环内为安全区，内圆和外圆为危险区
                    return ringDistance < zone.InnerRadius || ringDistance > zone.Radius;

                case "方":
                    // 方形危险区：使用边界条件
                    return x >= zone.MinX && x <= zone.MaxX && z >= zone.MinZ && z <= zone.MaxZ;

                case "矩形安全区":
                    // 矩形安全区：矩形内为安全，矩形外为危险
                    return x < zone.MinX || x > zone.MaxX || z < zone.MinZ || z > zone.MaxZ;

                case "表达式":
                    return EvaluateMathExpression(zone.MathExpression, x, z);

                default:
                    return false;
            }
        }

        private static Vector3 GetSafePositionFromDangerZone(DangerZone zone, Vector3 currentPos)
        {
            switch (zone.ZoneType)
            {
                case "圆":
                    var direction = currentPos - zone.CenterPos;
                    if (direction.Length() == 0)
                        direction = new Vector3(1.0f, 0, 0);
                    else
                        direction = Vector3.Normalize(direction);
                    return zone.CenterPos + direction * (zone.Radius + 1.0f);

                case "月环":
                    // 月环的安全位置处理（圆环内为安全，内外圆为危险）
                    var ringDirection = currentPos - zone.CenterPos;
                    var ringDistance = ringDirection.Length();

                    if (ringDistance == 0)
                    {
                        // 如果在中心，移动到安全圆环区域
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

                case "方":
                    // 方形危险区：使用边界条件定义
                    var squareNewX = currentPos.X;
                    var squareNewZ = currentPos.Z;

                    // 找到最近的边界并向外移动
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

                case "矩形安全区":
                    // 矩形安全区：移动到最近的安全区域（矩形内）
                    var safeCenterX = (zone.MinX + zone.MaxX) / 2;
                    var safeCenterZ = (zone.MinZ + zone.MaxZ) / 2;

                    // 找到移动到安全区域的最短路径
                    var newX = Math.Max(zone.MinX, Math.Min(zone.MaxX, currentPos.X));
                    var newZ = Math.Max(zone.MinZ, Math.Min(zone.MaxZ, currentPos.Z));

                    // 如果已经在边界上，向内移动一点
                    if (Math.Abs(newX - zone.MinX) < 0.1f) newX = zone.MinX + 1.0f;
                    if (Math.Abs(newX - zone.MaxX) < 0.1f) newX = zone.MaxX - 1.0f;
                    if (Math.Abs(newZ - zone.MinZ) < 0.1f) newZ = zone.MinZ + 1.0f;
                    if (Math.Abs(newZ - zone.MaxZ) < 0.1f) newZ = zone.MaxZ - 1.0f;

                    return new Vector3(newX, currentPos.Y, newZ);

                case "表达式":
                    // 对于复杂表达式，简单地向中心点移动
                    var dirToCenter = zone.CenterPos - currentPos;
                    if (dirToCenter.Length() == 0)
                        dirToCenter = new Vector3(1.0f, 0, 0);
                    else
                        dirToCenter = Vector3.Normalize(dirToCenter);
                    return currentPos + dirToCenter * 2.0f;

                default:
                    return currentPos;
            }
        }

        private void OnTerritoryChanged(ushort territoryId)
        {
            if (ModuleConfig?.ShowDebugInfo == true)
                Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-SwitchedToZone"), territoryId));
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (ModuleConfig == null || DService.ClientState.LocalPlayer == null)
                return;

            var currentZoneId = DService.ClientState.TerritoryType;

            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
                return;

            if (!zoneLimit.Enabled)
                return;

            // 检查小队人数及死亡人数
            if (DService.PartyList.Count(p => p.CurrentHP <= 0) >= ModuleConfig.DisableOnDeathCount)
                return;

            var currentPos = DService.ClientState.LocalPlayer.Position;

            // 高级模式：检查多个危险区域
            if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                {
                    if (IsInDangerZone(dangerZone, currentPos))
                    {
                        var safePos = GetSafePositionFromDangerZone(dangerZone, currentPos);
                        TeleportToSafePosition(safePos, string.Format(GetLoc("PreventEntryIntoMapBoundaries-EscapeDanger"), dangerZone.Name));
                        return; // 只处理第一个碰撞的危险区域
                    }
                }
            }
            // 传统模式：单一边界检查
            else
            {
                var centerPos = zoneLimit.CenterPos;
                var radius = zoneLimit.Radius;
                var safeRadius = radius - 0.3f; // 安全范围半径

                var isOutside = false;
                var newPos = currentPos; // 初始化新位置为当前位置

                if (zoneLimit.MapType == "圆")
                {
                    // 计算玩家与中心点的距离
                    var distance = (currentPos - centerPos).Length();

                    if (distance < safeRadius)
                        return; // 玩家在安全范围内，无需调整

                    // 计算玩家应该被拉回的位置
                    var direction = Vector3.Normalize(currentPos - centerPos);
                    newPos = centerPos + direction * (radius - 0.5f); // 留出0.5f的缓冲
                    isOutside = true;
                }
                else if (zoneLimit.MapType == "方")
                {
                    // 计算正方形的边界
                    var halfSize = radius; // 将半径解释为半边长

                    var minX = centerPos.X - halfSize;
                    var maxX = centerPos.X + halfSize;
                    var minZ = centerPos.Z - halfSize;
                    var maxZ = centerPos.Z + halfSize;

                    var clampedX = currentPos.X;
                    var clampedZ = currentPos.Z;

                    // 检查并截断X坐标
                    if (currentPos.X < minX)
                    {
                        clampedX = minX + 0.5f; // 留出0.5f的缓冲
                        isOutside = true;
                    }
                    else if (currentPos.X > maxX)
                    {
                        clampedX = maxX - 0.5f;
                        isOutside = true;
                    }

                    // 检查并截断Z坐标
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

                    // 如果有任何坐标被截断，则更新新位置
                    if (isOutside)
                        newPos = new Vector3(clampedX, currentPos.Y, clampedZ);
                }
                else
                {
                    ChatError($"未知的 MapType: {zoneLimit.MapType} for Zone ID {currentZoneId}");
                    return;
                }

                if (isOutside)
                    TeleportToSafePosition(newPos, GetLoc("PreventEntryIntoMapBoundaries-PositionCorrected"));
            }
        }

        private void TeleportToSafePosition(Vector3 newPos, string message)
        {
            unsafe
            {
                var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)DService.ClientState.LocalPlayer.Address;
                if (gameObject != null)
                {
                    gameObject->SetPosition(newPos.X, newPos.Y, newPos.Z);

                    if (ModuleConfig?.ShowDebugInfo == true)
                        Chat($"{message}: {newPos:F2}");
                }
                else
                    ChatError("Player GameObject is null.");
            }
        }


        private void DrawRadarWindow()
        {
            if (ModuleConfig?.ShowBoundaryVisualization != true ||
                DService.ClientState.LocalPlayer == null)
                return;

            var currentZoneId = DService.ClientState.TerritoryType;
            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit) || !zoneLimit.Enabled)
                return;

            var isOpen = true;
            if (ImGui.Begin("防撞电网雷达", ref isOpen, ImGuiWindowFlags.NoResize))
            {
                var windowSize = new Vector2(380, 480); // 增加窗口大小以防止重叠
                ImGui.SetWindowSize(windowSize);

                var radarSize = 260f; // 略微减小雷达区域给文字留出空间
                var radarRadius = radarSize / 2; // 雷达半径
                var radarCenter = new Vector2(windowSize.X / 2, radarSize / 2 + 40); // 雷达中心位置，留出更多顶部空间

                var drawList = ImGui.GetWindowDrawList();
                var windowPos = ImGui.GetWindowPos();
                var absoluteRadarCenter = new Vector2(windowPos.X + radarCenter.X, windowPos.Y + radarCenter.Y);

                // 雷达设置控制
                ImGui.Text("雷达设置:");
                var radarRange = ModuleConfig.RadarRange;
                if (ImGui.SliderFloat("显示范围(米)", ref radarRange, 10.0f, 200.0f, "%.1f"))
                {
                    ModuleConfig.RadarRange = radarRange;
                    SaveConfig(ModuleConfig);
                }

                var radarScale = ModuleConfig.RadarScale;
                if (ImGui.SliderFloat("缩放系数", ref radarScale, 0.5f, 5.0f, "%.1f"))
                {
                    ModuleConfig.RadarScale = radarScale;
                    SaveConfig(ModuleConfig);
                }

                ImGui.Separator();

                // 绘制雷达背景
                drawList.AddCircleFilled(absoluteRadarCenter, radarRadius, 0x30000000); // 半透明黑色背景
                drawList.AddCircle(absoluteRadarCenter, radarRadius, 0xFF00FF00, 64, 2.0f); // 绿色边框

                // 绘制雷达网格圈 (每圈代表一定距离)
                var gridCount = 4;
                var gridDistance = ModuleConfig.RadarRange / gridCount; // 每格代表的实际距离

                for (var i = 1; i <= gridCount; i++)
                {
                    var gridRadius = radarRadius * i / gridCount;
                    drawList.AddCircle(absoluteRadarCenter, gridRadius, 0x40FFFFFF, 32, 1.0f);

                    // 在每个网格圈上标注距离（只显示偶数圈防止重叠）
                    if (i % 2 == 0 && gridRadius < radarRadius - 15) // 防止标签超出雷达边界
                    {
                        var labelPos = new Vector2(absoluteRadarCenter.X + gridRadius + 5, absoluteRadarCenter.Y - 8);
                        drawList.AddText(labelPos, 0x80FFFFFF, $"{gridDistance * i:F0}m");
                    }
                }

                // 绘制十字线
                drawList.AddLine(
                    new Vector2(absoluteRadarCenter.X - radarRadius, absoluteRadarCenter.Y),
                    new Vector2(absoluteRadarCenter.X + radarRadius, absoluteRadarCenter.Y),
                    0x40FFFFFF, 1.0f);
                drawList.AddLine(
                    new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y - radarRadius),
                    new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y + radarRadius),
                    0x40FFFFFF, 1.0f);

                var playerPos = DService.ClientState.LocalPlayer.Position;
                var radarCenterWorldPos = GetRadarCenterWorldPos();

                // 计算玩家在雷达上的位置（相对于雷达中心）
                var playerRelativePos = playerPos - radarCenterWorldPos;
                var scale = radarRadius / ModuleConfig.RadarRange;
                var playerRadarX = absoluteRadarCenter.X + playerRelativePos.X * scale * ModuleConfig.RadarScale;
                var playerRadarY = absoluteRadarCenter.Y + playerRelativePos.Z * scale * ModuleConfig.RadarScale; // Z轴正向

                // 检查玩家是否在雷达显示范围内
                var playerDistanceFromRadarCenter = Vector2.Distance(new Vector2(playerRadarX, playerRadarY), absoluteRadarCenter);
                if (playerDistanceFromRadarCenter <= radarRadius)
                {
                    // 绘制玩家位置点
                    drawList.AddCircleFilled(new Vector2(playerRadarX, playerRadarY), 4.0f, 0xFF0000FF); // 红色玩家点
                    drawList.AddText(new Vector2(playerRadarX + 8, playerRadarY - 8), 0xFF0000FF, "玩家");
                }
                else
                {
                    // 玩家在雷达范围外，在边缘显示箭头指示方向
                    var directionToPlayer = Vector2.Normalize(new Vector2(playerRadarX, playerRadarY) - absoluteRadarCenter);
                    var edgePos = absoluteRadarCenter + directionToPlayer * (radarRadius - 10);
                    drawList.AddCircleFilled(edgePos, 3.0f, 0xFF0000FF);
                    drawList.AddText(new Vector2(edgePos.X + 6, edgePos.Y - 6), 0xFF0000FF, "玩家(外)");
                }

                // 高级模式：绘制多个危险区域
                if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
                {
                    foreach (var dangerZone in zoneLimit.DangerZones)
                    {
                        if (!dangerZone.Enabled) continue;
                        DrawDangerZoneOnRadar(drawList, absoluteRadarCenter, radarRadius, dangerZone, playerPos);
                    }
                }
                // 传统模式：绘制单一边界
                else
                    DrawTraditionalBoundaryOnRadar(drawList, absoluteRadarCenter, radarRadius, zoneLimit, playerPos);

                // 绘制雷达中心标记
                drawList.AddCircle(absoluteRadarCenter, 3.0f, 0xFF00FFFF, 16, 2.0f); // 青色圆圈
                drawList.AddText(new Vector2(absoluteRadarCenter.X + 8, absoluteRadarCenter.Y + 8), 0xFF00FFFF, "中心");

                // 显示实时信息（放在雷达下方）
                ImGui.SetCursorPos(new Vector2(10, radarCenter.Y + radarRadius + 20));
                ImGui.Text($"玩家位置: ({playerPos.X:F1}, {playerPos.Z:F1})");
                ImGui.Text($"雷达中心: ({radarCenterWorldPos.X:F1}, {radarCenterWorldPos.Z:F1})");
                ImGui.Text($"显示范围: {ModuleConfig.RadarRange:F0}米");
                ImGui.Text($"比例: 1像素 = {ModuleConfig.RadarRange / radarRadius:F2}米");

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
                else
                {
                    // 传统模式危险检测
                    if (zoneLimit.MapType == "圆")
                    {
                        var distance = (playerPos - zoneLimit.CenterPos).Length();
                        inDanger = distance >= zoneLimit.Radius - 0.3f;
                    }
                    else if (zoneLimit.MapType == "方")
                    {
                        var halfSize = zoneLimit.Radius;
                        inDanger = Math.Abs(playerPos.X - zoneLimit.CenterPos.X) >= halfSize - 0.3f ||
                                  Math.Abs(playerPos.Z - zoneLimit.CenterPos.Z) >= halfSize - 0.3f;
                    }
                }

                ImGui.TextColored(inDanger ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1),
                                 inDanger ? GetLoc("PreventEntryIntoMapBoundaries-StatusDangerous") : GetLoc("PreventEntryIntoMapBoundaries-StatusSafe"));
            }
            ImGui.End();

            if (!isOpen)
            {
                ModuleConfig.ShowBoundaryVisualization = false;
                SaveConfig(ModuleConfig);
            }
        }

        private void DrawDangerZoneOnRadar(ImDrawListPtr drawList, Vector2 radarCenter, float radarRadius, DangerZone zone, Vector3 playerPos)
        {
            // 计算比例尺
            var scale = radarRadius / ModuleConfig.RadarRange;

            // 危险区域在雷达中的固定位置（相对于雷达中心）
            var relativeToRadarCenter = zone.CenterPos - GetRadarCenterWorldPos();
            var zoneRadarX = radarCenter.X + relativeToRadarCenter.X * scale * ModuleConfig.RadarScale;
            var zoneRadarY = radarCenter.Y + relativeToRadarCenter.Z * scale * ModuleConfig.RadarScale; // Z轴正向

            var color = zone.Color | 0x80000000; // 添加透明度

            switch (zone.ZoneType)
            {
                case "圆":
                    var zoneRadiusOnRadar = zone.Radius * scale * ModuleConfig.RadarScale;
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), zoneRadiusOnRadar, color, 32, 2.0f);
                    // 绘制填充区域以便更好地可视化
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), zoneRadiusOnRadar, (uint)(color & 0x30FFFFFF));
                    break;

                case "月环":
                    var outerRadiusOnRadar = zone.Radius * scale * ModuleConfig.RadarScale;
                    var innerRadiusOnRadar = zone.InnerRadius * scale * ModuleConfig.RadarScale;

                    // 绘制外圆边界（危险区域边界）
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), outerRadiusOnRadar, color, 32, 2.0f);
                    // 绘制内圆边界（危险区域边界）
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), innerRadiusOnRadar, color, 32, 2.0f);

                    // 填充内圆（危险区域）
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), innerRadiusOnRadar, (uint)(color & 0x40FFFFFF));

                    // 绘制安全圆环区域（绿色显示）
                    var DsafeColor = 0x8000FF00; // 半透明绿色
                    for (float r = innerRadiusOnRadar + 2; r < outerRadiusOnRadar; r += 3.0f)
                        drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), r, DsafeColor, 16, 1.0f);

                    // 添加文字说明
                    if (innerRadiusOnRadar > 10) // 在内圆显示危险
                    {
                        var dangerTextPos = new Vector2(zoneRadarX, zoneRadarY - innerRadiusOnRadar / 2);
                        drawList.AddText(dangerTextPos, color, GetLoc("PreventEntryIntoMapBoundaries-Dangerous"));
                    }
                    var safeTextPos = new Vector2(zoneRadarX + (innerRadiusOnRadar + outerRadiusOnRadar) / 2, zoneRadarY - 5);
                    drawList.AddText(safeTextPos, 0xFF00FF00, GetLoc("PreventEntryIntoMapBoundaries-Safe"));
                    break;

                case "方":
                    // 使用边界绘制方形
                    var radarCenterWorld = GetRadarCenterWorldPos();

                    var SminXRelative = zone.MinX - radarCenterWorld.X;
                    var SmaxXRelative = zone.MaxX - radarCenterWorld.X;
                    var SminZRelative = zone.MinZ - radarCenterWorld.Z;
                    var SmaxZRelative = zone.MaxZ - radarCenterWorld.Z;

                    var topLeft = new Vector2(
                        radarCenter.X + SminXRelative * scale * ModuleConfig.RadarScale,
                        radarCenter.Y + SminZRelative * scale * ModuleConfig.RadarScale
                    );
                    var bottomRight = new Vector2(
                        radarCenter.X + SmaxXRelative * scale * ModuleConfig.RadarScale,
                        radarCenter.Y + SmaxZRelative * scale * ModuleConfig.RadarScale
                    );

                    drawList.AddRect(topLeft, bottomRight, color, 0.0f, ImDrawFlags.None, 2.0f);
                    // 绘制填充区域
                    drawList.AddRectFilled(topLeft, bottomRight, (uint)(color & 0x30FFFFFF));
                    break;

                case "矩形安全区":
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
                    var safeColor = 0xFF00FF00; // 绿色
                    drawList.AddRect(safeTopLeft, safeBottomRight, safeColor, 0.0f, ImDrawFlags.None, 2.0f);
                    // 绘制半透明绿色填充
                    drawList.AddRectFilled(safeTopLeft, safeBottomRight, 0x4000FF00);

                    // 添加文字说明
                    var safeCenterX = (safeTopLeft.X + safeBottomRight.X) / 2;
                    var safeCenterY = (safeTopLeft.Y + safeBottomRight.Y) / 2;
                    drawList.AddText(new Vector2(safeCenterX - 15, safeCenterY - 8), safeColor, GetLoc("PreventEntryIntoMapBoundaries-SafeZone"));
                    break;

                case "表达式":
                    // 对于复杂表达式，绘制一个更明显的标记
                    drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), 6.0f, color);
                    drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), 8.0f, color, 16, 2.0f);
                    break;
            }

            // 绘制区域中心点（除了方形类型）
            if (zone.ZoneType != "方")
                drawList.AddCircleFilled(new Vector2(zoneRadarX, zoneRadarY), 2.0f, color);
            else
            {
                // 方形类型绘制边界标记
                var radarCenterWorld = GetRadarCenterWorldPos();

                var minXRelative = zone.MinX - radarCenterWorld.X;
                var maxXRelative = zone.MaxX - radarCenterWorld.X;
                var minZRelative = zone.MinZ - radarCenterWorld.Z;
                var maxZRelative = zone.MaxZ - radarCenterWorld.Z;

                var corner1Radar = new Vector2(
                    radarCenter.X + minXRelative * scale * ModuleConfig.RadarScale,
                    radarCenter.Y + minZRelative * scale * ModuleConfig.RadarScale
                );
                var corner2Radar = new Vector2(
                    radarCenter.X + maxXRelative * scale * ModuleConfig.RadarScale,
                    radarCenter.Y + maxZRelative * scale * ModuleConfig.RadarScale
                );

                // 绘制对角点
                drawList.AddCircleFilled(corner1Radar, 3.0f, color);
                drawList.AddCircleFilled(corner2Radar, 3.0f, color);
            }

            // 绘制区域名称（防止与雷达元素重叠）
            if (!string.IsNullOrEmpty(zone.Name))
            {
                // 检查文字位置是否在雷达范围内，如果在则调整位置
                var textOffset = 15;
                var textPos = new Vector2(zoneRadarX + textOffset, zoneRadarY - textOffset);

                // 确保文字不超出雷达边界
                var distanceFromCenter = Vector2.Distance(textPos, radarCenter);
                if (distanceFromCenter > radarRadius - 30) // 预留文字空间
                    textPos = new Vector2(zoneRadarX - textOffset - 30, zoneRadarY + textOffset);

                drawList.AddText(textPos, color, zone.Name);
            }
        }

        private void DrawTraditionalBoundaryOnRadar(ImDrawListPtr drawList, Vector2 radarCenter, float radarRadius, ZoneLimit zoneLimit, Vector3 playerPos)
        {
            // 计算比例尺
            var scale = radarRadius / ModuleConfig.RadarRange;

            // 边界区域在雷达中的固定位置（相对于雷达中心）
            var relativeToRadarCenter = zoneLimit.CenterPos - GetRadarCenterWorldPos();
            var boundaryRadarX = radarCenter.X + relativeToRadarCenter.X * scale * ModuleConfig.RadarScale;
            var boundaryRadarY = radarCenter.Y + relativeToRadarCenter.Z * scale * ModuleConfig.RadarScale; // Z轴正向

            var color = 0xFF0000FF; // 红色
            var fillColor = 0x300000FF; // 半透明红色填充

            if (zoneLimit.MapType == "圆")
            {
                var zoneRadiusOnRadar = zoneLimit.Radius * scale * ModuleConfig.RadarScale;
                drawList.AddCircle(new Vector2(boundaryRadarX, boundaryRadarY), zoneRadiusOnRadar, color, 32, 2.0f);
                drawList.AddCircleFilled(new Vector2(boundaryRadarX, boundaryRadarY), zoneRadiusOnRadar, (uint)fillColor);
            }
            else if (zoneLimit.MapType == "方")
            {
                var halfSize = zoneLimit.Radius * scale * ModuleConfig.RadarScale;
                var topLeft = new Vector2(boundaryRadarX - halfSize, boundaryRadarY - halfSize);
                var bottomRight = new Vector2(boundaryRadarX + halfSize, boundaryRadarY + halfSize);
                drawList.AddRect(topLeft, bottomRight, color, 0.0f, ImDrawFlags.None, 2.0f);
                drawList.AddRectFilled(topLeft, bottomRight, (uint)fillColor);
            }

            // 绘制边界中心点
            drawList.AddCircleFilled(new Vector2(boundaryRadarX, boundaryRadarY), 2.0f, color);

            // 标注边界类型
            var textPos = new Vector2(boundaryRadarX + 10, boundaryRadarY - 10);
            drawList.AddText(textPos, color, string.Format(GetLoc("PreventEntryIntoMapBoundaries-Boundary"), zoneLimit.MapType));
        }

        private Vector3 GetRadarCenterWorldPos()
        {
            var currentZoneId = DService.ClientState.TerritoryType;
            if (ModuleConfig?.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit) == true)
            {
                if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
                    return zoneLimit.DangerZones[0].CenterPos;
                else
                    return zoneLimit.CenterPos;
            }

            // 如果没有配置区域，使用玩家位置
            return DService.ClientState.LocalPlayer?.Position ?? Vector3.Zero;
        }

        private string SerializeZoneLimitList(Dictionary<uint, ZoneLimit> zoneLimitList)
        {
            var lines = new List<string>();
            foreach (var kvp in zoneLimitList)
            {
                var zoneId = kvp.Key;
                var zone = kvp.Value;

                lines.Add($"Zone:{zoneId}|Enabled:{zone.Enabled}|MapType:{zone.MapType}|IsAdvanced:{zone.IsAdvancedMode}");
                lines.Add($"Center:{zone.CenterPos.X},{zone.CenterPos.Y},{zone.CenterPos.Z}|Radius:{zone.Radius}");

                foreach (var dz in zone.DangerZones)
                {
                    lines.Add($"DangerZone:{dz.Name}|Enabled:{dz.Enabled}|Type:{dz.ZoneType}|Color:{dz.Color}");
                    lines.Add($"DZCenter:{dz.CenterPos.X},{dz.CenterPos.Y},{dz.CenterPos.Z}|Radius:{dz.Radius}|InnerRadius:{dz.InnerRadius}");
                    lines.Add($"DZBounds:MinX={dz.MinX},MaxX={dz.MaxX},MinZ={dz.MinZ},MaxZ={dz.MaxZ}|Expr:{dz.MathExpression}");
                }
                lines.Add("---");
            }
            return string.Join("\n", lines);
        }

        private Dictionary<uint, ZoneLimit> DeserializeZoneLimitList(string data)
        {
            var result = new Dictionary<uint, ZoneLimit>();
            var lines = data.Split('\n');

            ZoneLimit currentZone = null;
            uint currentZoneId = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line == "---")
                {
                    if (currentZone != null)
                    {
                        result[currentZoneId] = currentZone;
                        currentZone = null;
                    }
                    continue;
                }

                if (line.StartsWith("Zone:"))
                {
                    var parts = line.Split('|');
                    currentZoneId = uint.Parse(parts[0].Split(':')[1]);
                    currentZone = new ZoneLimit(currentZoneId);

                    foreach (var part in parts.Skip(1))
                    {
                        var keyValue = part.Split(':');
                        if (keyValue.Length != 2) continue;

                        switch (keyValue[0])
                        {
                            case "Enabled":
                                currentZone.Enabled = bool.Parse(keyValue[1]);
                                break;
                            case "MapType":
                                currentZone.MapType = keyValue[1];
                                break;
                            case "IsAdvanced":
                                currentZone.IsAdvancedMode = bool.Parse(keyValue[1]);
                                break;
                        }
                    }
                }
                else if (line.StartsWith("Center:") && currentZone != null)
                {
                    var parts = line.Split('|');
                    var centerParts = parts[0].Split(':')[1].Split(',');
                    currentZone.CenterPos = new Vector3(float.Parse(centerParts[0]), float.Parse(centerParts[1]), float.Parse(centerParts[2]));

                    if (parts.Length > 1 && parts[1].StartsWith("Radius:"))
                        currentZone.Radius = float.Parse(parts[1].Split(':')[1]);
                }
                else if (line.StartsWith("DangerZone:") && currentZone != null)
                {
                    var parts = line.Split('|');
                    var dz = new DangerZone(parts[0].Split(':')[1]);

                    foreach (var part in parts.Skip(1))
                    {
                        var keyValue = part.Split(':');
                        if (keyValue.Length != 2) continue;

                        switch (keyValue[0])
                        {
                            case "Enabled":
                                dz.Enabled = bool.Parse(keyValue[1]);
                                break;
                            case "Type":
                                dz.ZoneType = keyValue[1];
                                break;
                            case "Color":
                                dz.Color = uint.Parse(keyValue[1]);
                                break;
                        }
                    }
                    currentZone.DangerZones.Add(dz);
                }
                else if (line.StartsWith("DZCenter:") && currentZone != null && currentZone.DangerZones.Count > 0)
                {
                    var dz = currentZone.DangerZones.Last();
                    var parts = line.Split('|');
                    var centerParts = parts[0].Split(':')[1].Split(',');
                    dz.CenterPos = new Vector3(float.Parse(centerParts[0]), float.Parse(centerParts[1]), float.Parse(centerParts[2]));

                    foreach (var part in parts.Skip(1))
                    {
                        var keyValue = part.Split(':');
                        if (keyValue.Length != 2) continue;

                        switch (keyValue[0])
                        {
                            case "Radius":
                                dz.Radius = float.Parse(keyValue[1]);
                                break;
                            case "InnerRadius":
                                dz.InnerRadius = float.Parse(keyValue[1]);
                                break;
                        }
                    }
                }
                else if (line.StartsWith("DZBounds:") && currentZone != null && currentZone.DangerZones.Count > 0)
                {
                    var dz = currentZone.DangerZones.Last();
                    var parts = line.Split('|');

                    if (parts[0].Contains("MinX="))
                    {
                        var boundsStr = parts[0].Split(':')[1];
                        var boundsParts = boundsStr.Split(',');

                        foreach (var boundPart in boundsParts)
                        {
                            var eq = boundPart.Split('=');
                            if (eq.Length != 2) continue;

                            switch (eq[0])
                            {
                                case "MinX":
                                    dz.MinX = float.Parse(eq[1]);
                                    break;
                                case "MaxX":
                                    dz.MaxX = float.Parse(eq[1]);
                                    break;
                                case "MinZ":
                                    dz.MinZ = float.Parse(eq[1]);
                                    break;
                                case "MaxZ":
                                    dz.MaxZ = float.Parse(eq[1]);
                                    break;
                            }
                        }
                    }

                    if (parts.Length > 1 && parts[1].StartsWith("Expr:"))
                        dz.MathExpression = parts[1].Split(':')[1];
                }
            }

            if (currentZone != null)
                result[currentZoneId] = currentZone;

            return result;
        }

        private void ExportAllConfigs()
        {
            try
            {
                if (ModuleConfig == null)
                {
                    ChatError("没有配置可导出");
                    return;
                }

                var configData = SerializeZoneLimitList(ModuleConfig.ZoneLimitList);
                var encodedString = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(configData));

                ImGui.SetClipboardText(encodedString);
                Chat(GetLoc("PreventEntryIntoMapBoundaries-ConfigExported"));
            }
            catch (Exception ex)
            {
                ChatError($"导出失败: {ex.Message}");
            }
        }

        private void ExportCurrentZoneConfig()
        {
            try
            {
                if (ModuleConfig == null)
                {
                    ChatError("没有配置可导出");
                    return;
                }

                var currentZoneId = DService.ClientState.TerritoryType;
                if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
                {
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundaries-CurrentZoneNoConfig"), currentZoneId));
                    return;
                }

                var exportData = new Dictionary<uint, ZoneLimit> { { currentZoneId, zoneLimit } };
                var configData = SerializeZoneLimitList(exportData);
                var encodedString = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(configData));

                ImGui.SetClipboardText(encodedString);
                Chat($"已导出区域 {currentZoneId} 配置到剪贴板");
            }
            catch (Exception ex)
            {
                ChatError($"导出失败: {ex.Message}");
            }
        }

        private void ImportConfigs()
        {
            try
            {
                if (ModuleConfig == null)
                {
                    ChatError(GetLoc("PreventEntryIntoMapBoundaries-ConfigNotInitialized"));
                    return;
                }

                var clipboardText = ImGui.GetClipboardText();
                if (string.IsNullOrWhiteSpace(clipboardText))
                {
                    ChatError("剪贴板为空，请先复制配置字符串");
                    return;
                }

                string configString;
                try
                {
                    var decodedBytes = Convert.FromBase64String(clipboardText);
                    configString = System.Text.Encoding.UTF8.GetString(decodedBytes);
                }
                catch
                {
                    ChatError("无效的base64配置字符串");
                    return;
                }

                var importedData = DeserializeZoneLimitList(configString);
                if (importedData == null)
                {
                    ChatError("解析配置失败");
                    return;
                }

                var importCount = 0;
                foreach (var kvp in importedData)
                {
                    var zoneId = kvp.Key;
                    var zoneLimit = kvp.Value;

                    if (!ModuleConfig.ZoneIds.Contains(zoneId))
                        ModuleConfig.ZoneIds.Add(zoneId);

                    ModuleConfig.ZoneLimitList[zoneId] = zoneLimit;
                    importCount++;
                }

                SaveConfig(ModuleConfig);
                Chat($"成功导入 {importCount} 个区域配置");
            }
            catch (Exception ex)
            {
                ChatError($"导入失败: {ex.Message}");
            }
        }

        private void OnCommand(string command, string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandUsage"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExamples"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExample1"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExample2"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExample3"));
                Chat(GetLoc("PreventEntryIntoMapBoundaries-CommandExample4"));
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
                        dangerZone.ZoneType = "圆";
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
                        dangerZone.ZoneType = "方";
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
            Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneAdded"), shapeType, dangerZone.Name));
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
                ChatError($"无效的索引，当前有 {zoneLimit.DangerZones.Count} 个危险区域");
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
                ChatError($"无效的索引，当前有 {zoneLimit.DangerZones.Count} 个危险区域");
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
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundaries-ZoneToggled"), enabled ? GetLoc("PreventEntryIntoMapBoundaries-Enabled") : GetLoc("PreventEntryIntoMapBoundaries-Disabled"), dangerZone.Name));
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

        protected override void Uninit()
        {
            DService.Framework.Update -= OnFrameworkUpdate;
            DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
            RemoveSubCommand(Command);
        }
    }
}
