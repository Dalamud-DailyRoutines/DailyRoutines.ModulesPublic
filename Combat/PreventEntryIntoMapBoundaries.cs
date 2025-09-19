using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Data;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using static DailyRoutines.Managers.CommandManager;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using OmenTools;
using OmenTools.Helpers;
using static DailyRoutines.Helpers.NotifyHelper;


namespace DailyRoutines.ModulesPublic.old
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
            Color = ImGuiColors.DPSRed.ToUint();
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
            Title           = GetLoc("PreventEntryIntoMapBoundaries"),
            Description     = GetLoc("PreventEntryIntoMapBoundaries"),
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
            FrameworkManager.Register(OnFrameworkUpdate);
            DService.ClientState.TerritoryChanged += OnTerritoryChanged;

            AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("PreventEntryIntoMapBoundariesCommandHelp") });
        }

        protected override void ConfigUI()
        {
           

            using var configChild = ImRaii.Child(GetLoc("PreventEntryIntoMapBoundariesConfigTitle"), new Vector2(0, 0), false);

            // 全局设置
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesGlobalSettingsTitle"));

            // 添加当前区域按钮
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesAddCurrentZoneButton")))
            {
                var currentZoneId = DService.ClientState.TerritoryType;

                if (currentZoneId == 0)
                {
                    ChatError(GetLoc("PreventEntryIntoMapBoundariesInvalidZone"));
                    return;
                }

                if (!ModuleConfig.ZoneIds.Contains(currentZoneId))
                {
                    ModuleConfig.ZoneIds.Add(currentZoneId);
                    ModuleConfig.ZoneLimitList[currentZoneId] = new ZoneLimit(currentZoneId);
                    // 默认中心点为 100,0,100，不使用玩家位置

                    SaveConfig(ModuleConfig);
                    Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneAddedSuccess"), currentZoneId));
                }
                else
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneAlreadyExists"), currentZoneId));
            }

            ImGui.Separator();

            // 显示调试信息选项
            var showDebug = ModuleConfig.ShowDebugInfo;
            if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundariesShowDebugInfo"), ref showDebug))
            {
                ModuleConfig.ShowDebugInfo = showDebug;
                SaveConfig(ModuleConfig);
            }


            var showVisualization = ModuleConfig.ShowBoundaryVisualization;
            if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundariesShowVisualization"), ref showVisualization))
            {
                ModuleConfig.ShowBoundaryVisualization = showVisualization;
                SaveConfig(ModuleConfig);
            }

            // 死亡玩家数量配置
            var deathCount = ModuleConfig.DisableOnDeathCount;
            if (ImGui.SliderInt(GetLoc("PreventEntryIntoMapBoundariesDeathCountThreshold"), ref deathCount, 0, 8, "%d"))
            {
                ModuleConfig.DisableOnDeathCount = deathCount;
                SaveConfig(ModuleConfig);
            }

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesNoticeLabel"));
            ImGui.SameLine(0, 5 * ImGuiHelpers.GlobalScale);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesDeathCountWarning"));
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesDeathCountRecommendation"));

            // 配置导入导出
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesConfigManagement"));
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesExportAll")))
                ExportAllConfigs();
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesImport")))
                ImportConfigs();
            ImGui.SameLine();
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesExportCurrent")))
                ExportCurrentZoneConfig();

            ImGui.Separator();

            if (ModuleConfig.ZoneIds.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundariesNoZones"));
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
                    if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundariesEnable"), ref enabled))
                    {
                        zoneLimit.Enabled = enabled;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    var advancedMode = zoneLimit.IsAdvancedMode;
                    if (ImGui.Checkbox(GetLoc("PreventEntryIntoMapBoundariesAdvancedMode"), ref advancedMode))
                    {
                        zoneLimit.IsAdvancedMode = advancedMode;
                        SaveConfig(ModuleConfig);
                    }

                    ImGui.SameLine();
                    if (isCurrentZone && ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesSetCurrentPosition")))
                    {
                        if (DService.ClientState.LocalPlayer != null)
                        {
                            zoneLimit.CenterPos = DService.ClientState.LocalPlayer.Position;
                            SaveConfig(ModuleConfig);
                            Chat(GetLoc("PreventEntryIntoMapBoundariesCenterPositionUpdated"));
                        }
                    }

                    if (zoneLimit.IsAdvancedMode)
                        DrawAdvancedModeUI(zoneLimit, isCurrentZone);
                    else
                        DrawTraditionalModeUI(zoneLimit, zoneId, isCurrentZone);

                    using (var deleteButtonStyle = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
                    {
                        if (ImGui.Button(string.Format(GetLoc("PreventEntryIntoMapBoundariesDeleteZone"), zoneId)))
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

                // 只在不是最后一个元素时添加分隔线
                if (i < ModuleConfig.ZoneIds.Count - 1)
                    ImGui.Separator();
                    
            }

            // 绘制雷达窗口（如果启用了边界可视化）
            if (ModuleConfig.ShowBoundaryVisualization)
                DrawRadarWindow();
        }

        private void DrawTraditionalModeUI(ZoneLimit zoneLimit, uint zoneId, bool isCurrentZone)
        {
            // 地图类型选择
            var mapTypes = new[] { GetLoc("PreventEntryIntoMapBoundariesCircle"), GetLoc("PreventEntryIntoMapBoundariesRectangle") };
            var mapTypeValues = new[] { "圆", "方" };
            var currentMapTypeIndex = Array.IndexOf(mapTypeValues, zoneLimit.MapType);
            if (currentMapTypeIndex == -1)
                currentMapTypeIndex = 0;

            if (ImGui.Combo(GetLoc("PreventEntryIntoMapBoundariesMapType"), ref currentMapTypeIndex, mapTypes, mapTypes.Length))
            {
                zoneLimit.MapType = mapTypeValues[currentMapTypeIndex];
                SaveConfig(ModuleConfig);
            }

            // 中心位置 - 紧凑布局
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesCenterPosition"));
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var centerPos = zoneLimit.CenterPos;
            if (ImGui.InputFloat3($"##CenterPos{zoneId}", ref centerPos))
            {
                zoneLimit.CenterPos = centerPos;
                SaveConfig(ModuleConfig);
            }

            // 半径 - 并排布局
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            var radius = zoneLimit.Radius;
            if (ImGui.InputFloat($"##Radius{zoneId}", ref radius, 1.0f, 10.0f, "%.2f"))
            {
                zoneLimit.Radius = Math.Max(1.0f, radius);
                SaveConfig(ModuleConfig);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesRadius"));

            if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
            {
                var playerPos = DService.ClientState.LocalPlayer.Position;
                var distance = Vector3.Distance(playerPos, zoneLimit.CenterPos);
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"Distance: {distance:F2}");
            }
        }

        private void DrawAdvancedModeUI(ZoneLimit zoneLimit, bool isCurrentZone)
        {
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesDangerZoneManagement"));

            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundariesAddDangerZone")))
            {
                var newZone = new DangerZone(string.Format(GetLoc("PreventEntryIntoMapBoundariesDangerZone"), zoneLimit.DangerZones.Count + 1));
                // 默认中心点为 100,0,100，不使用玩家位置

                zoneLimit.DangerZones.Add(newZone);
                SaveConfig(ModuleConfig);
            }

            for (var j = 0; j < zoneLimit.DangerZones.Count; j++)
            {
                var dangerZone = zoneLimit.DangerZones[j];

                if (ImGui.TreeNode($"{dangerZone.Name}###{j}"))
                {
                    // 危险区域基本设置（紧凑布局）
                    var dzEnabled = dangerZone.Enabled;
                    if (ImGui.Checkbox($"{GetLoc("PreventEntryIntoMapBoundariesEnable")}##dz{j}", ref dzEnabled))
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
                    ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesName"));

                    // 区域类型选择（紧凑布局）
                    ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
                    var zoneTypes = new[] { GetLoc("PreventEntryIntoMapBoundariesCircle"), GetLoc("PreventEntryIntoMapBoundariesAnnulus"), GetLoc("PreventEntryIntoMapBoundariesRectangle"), GetLoc("PreventEntryIntoMapBoundariesSafeZone"), GetLoc("PreventEntryIntoMapBoundariesMathExpression") };
                    var zoneTypeValues = new[] { "圆", "月环", "方", "矩形安全区", "表达式" };//不会被显示，显示的是上面的
                    var currentZoneTypeIndex = Array.IndexOf(zoneTypeValues, dangerZone.ZoneType);
                    if (currentZoneTypeIndex == -1)
                        currentZoneTypeIndex = 0;

                    if (ImGui.Combo($"##dzType{j}", ref currentZoneTypeIndex, zoneTypes, zoneTypes.Length))
                    {
                        dangerZone.ZoneType = zoneTypeValues[currentZoneTypeIndex];
                        SaveConfig(ModuleConfig);
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesType"));

                    // 根据类型显示不同的参数
                    if (dangerZone.ZoneType == "表达式")
                    {
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesMathExpressionDesc"));
                        ImGui.TextColored(ImGuiColors.DalamudGrey, GetLoc("PreventEntryIntoMapBoundariesMathExample"));
                        var mathExpr = dangerZone.MathExpression;
                        if (ImGui.InputTextMultiline($"##expr{j}", ref mathExpr, 200, new Vector2(300, 60)))
                        {
                            dangerZone.MathExpression = mathExpr;
                            SaveConfig(ModuleConfig);
                        }
                    }

                    // 根据类型显示不同的参数（紧凑布局）
                    if (dangerZone.ZoneType == "圆" || dangerZone.ZoneType == "月环")
                    {
                        // 中心位置 - 使用更紧凑的布局
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesCenterPosition"));
                        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                        var dzCenterPos = dangerZone.CenterPos;
                        if (ImGui.InputFloat3($"##dzCenter{j}", ref dzCenterPos))
                        {
                            dangerZone.CenterPos = dzCenterPos;
                            SaveConfig(ModuleConfig);
                        }

                        // 半径 - 并排布局
                        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
                        var dzRadius = dangerZone.Radius;
                        if (ImGui.InputFloat($"##dzRadius{j}", ref dzRadius, 1.0f, 10.0f, "%.2f"))
                        {
                            dangerZone.Radius = Math.Max(1.0f, dzRadius);
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.SameLine();
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesOuterRadius"));

                        if (dangerZone.ZoneType == "月环")
                        {
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
                            var dzInnerRadius = dangerZone.InnerRadius;
                            if (ImGui.InputFloat($"##dzInnerRadius{j}", ref dzInnerRadius, 1.0f, 10.0f, "%.2f"))
                            {
                                dangerZone.InnerRadius = Math.Max(0.0f, Math.Min(dzInnerRadius, dangerZone.Radius - 0.1f));
                                SaveConfig(ModuleConfig);
                            }
                            ImGui.SameLine();
                            ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesInnerRadius"));
                        }
                    }
                    else if (dangerZone.ZoneType == "方")
                    {
                        // X范围 - 并排布局
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesXRange"));
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var squareMinX = dangerZone.MinX;
                        if (ImGui.InputFloat($"##squareMinX{j}", ref squareMinX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinX = squareMinX;
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.SameLine();
                        ImGui.Text("~");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var squareMaxX = dangerZone.MaxX;
                        if (ImGui.InputFloat($"##squareMaxX{j}", ref squareMaxX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxX = squareMaxX;
                            SaveConfig(ModuleConfig);
                        }

                        // Z范围 - 并排布局
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesZRange"));
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var squareMinZ = dangerZone.MinZ;
                        if (ImGui.InputFloat($"##squareMinZ{j}", ref squareMinZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinZ = squareMinZ;
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.SameLine();
                        ImGui.Text("~");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var squareMaxZ = dangerZone.MaxZ;
                        if (ImGui.InputFloat($"##squareMaxZ{j}", ref squareMaxZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxZ = squareMaxZ;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(GetLoc("PreventEntryIntoMapBoundariesCurrentDangerArea"), dangerZone.MinX, dangerZone.MaxX, dangerZone.MinZ, dangerZone.MaxZ));
                    }
                    else if (dangerZone.ZoneType == "矩形安全区")
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("PreventEntryIntoMapBoundariesSafeZoneDesc"));

                        // X范围 - 并排布局
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesXRange"));
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var minX = dangerZone.MinX;
                        if (ImGui.InputFloat($"##safeMinX{j}", ref minX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinX = minX;
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.SameLine();
                        ImGui.Text("~");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var maxX = dangerZone.MaxX;
                        if (ImGui.InputFloat($"##safeMaxX{j}", ref maxX, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxX = maxX;
                            SaveConfig(ModuleConfig);
                        }

                        // Z范围 - 并排布局
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesZRange"));
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var minZ = dangerZone.MinZ;
                        if (ImGui.InputFloat($"##safeMinZ{j}", ref minZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MinZ = minZ;
                            SaveConfig(ModuleConfig);
                        }
                        ImGui.SameLine();
                        ImGui.Text("~");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                        var maxZ = dangerZone.MaxZ;
                        if (ImGui.InputFloat($"##safeMaxZ{j}", ref maxZ, 1.0f, 10.0f, "%.1f"))
                        {
                            dangerZone.MaxZ = maxZ;
                            SaveConfig(ModuleConfig);
                        }

                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(GetLoc("PreventEntryIntoMapBoundariesCurrentSafeArea"), dangerZone.MinX, dangerZone.MaxX, dangerZone.MinZ, dangerZone.MaxZ));
                    }
                    else if (dangerZone.ZoneType != "表达式")
                    {
                        // 其他类型的后备处理
                        ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesCenterPosition"));
                        var dzCenterPos = dangerZone.CenterPos;
                        if (ImGui.InputFloat3($"##dzCenter{j}", ref dzCenterPos))
                        {
                            dangerZone.CenterPos = dzCenterPos;
                            SaveConfig(ModuleConfig);
                        }
                    }

                    // 颜色选择和删除按钮 - 并排布局
                    var color = new Vector4(
                        ((dangerZone.Color >> 0) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 8) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 16) & 0xFF) / 255.0f,
                        ((dangerZone.Color >> 24) & 0xFF) / 255.0f
                    );

                    ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
                    if (ImGui.ColorEdit4($"##dzColor{j}", ref color, ImGuiColorEditFlags.NoInputs))
                    {
                        dangerZone.Color =
                            ((uint)(color.W * 255) << 24) |
                            ((uint)(color.Z * 255) << 16) |
                            ((uint)(color.Y * 255) << 8) |
                            ((uint)(color.X * 255) << 0);
                        SaveConfig(ModuleConfig);
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("PreventEntryIntoMapBoundariesColor"));

                    // 调试信息
                    if (ModuleConfig.ShowDebugInfo && isCurrentZone && DService.ClientState.LocalPlayer != null)
                    {
                        var playerPos = DService.ClientState.LocalPlayer.Position;
                        var inDanger = IsInDangerZone(dangerZone, playerPos);
                        ImGui.TextColored(
                            inDanger ? ImGuiColors.DPSRed : ImGuiColors.HealerGreen,
                            string.Format(GetLoc("PreventEntryIntoMapBoundariesCurrentStatus"), inDanger ? GetLoc("PreventEntryIntoMapBoundariesDangerous") : GetLoc("PreventEntryIntoMapBoundariesSafe"))
                        );
                    }

                    // 删除按钮 - 放在颜色选择器同一行
                    ImGui.SameLine();
                    using (var deleteButtonStyle = ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
                    {
                        if (ImGui.Button($"{GetLoc("PreventEntryIntoMapBoundariesDelete")}##dz{j}"))
                        {
                            zoneLimit.DangerZones.RemoveAt(j);
                            SaveConfig(ModuleConfig);
                            ImGui.TreePop();
                            break;
                        }
                    }

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
                Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesSwitchedToZone"), territoryId));
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
                    // 只有启用的危险区域才进行传送检测
                    if (dangerZone.Enabled && IsInDangerZone(dangerZone, currentPos))
                    {
                        var safePos = GetSafePositionFromDangerZone(dangerZone, currentPos);
                        TeleportToSafePosition(safePos, string.Format(GetLoc("PreventEntryIntoMapBoundariesEscapeDanger"), dangerZone.Name));
                        return; // 只处理第一个碰撞的危险区域
                    }
                }
            }
            // 传统模式：单一边界检查
            else if (zoneLimit.Enabled)
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
                    TeleportToSafePosition(newPos, GetLoc("PreventEntryIntoMapBoundariesPositionCorrected"));
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
            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
                return;

            var isOpen = true;
            if (ImGui.Begin("防撞电网雷达", ref isOpen, ImGuiWindowFlags.NoResize))
            {
                var windowSize = new Vector2(420, 600); // 进一步增加窗口大小
                ImGui.SetWindowSize(windowSize);

                var radarSize = 280f; // 增大雷达区域
                var radarRadius = radarSize / 2; // 雷达半径
                var radarCenter = new Vector2(windowSize.X / 2, radarSize / 2 + 80); // 雷达中心位置，留出足够的顶部空间

                var drawList = ImGui.GetWindowDrawList();
                var windowPos = ImGui.GetWindowPos();
                var absoluteRadarCenter = new Vector2(windowPos.X + radarCenter.X, windowPos.Y + radarCenter.Y);

                


                // 绘制雷达背景
                drawList.AddCircleFilled(absoluteRadarCenter, radarRadius, ImGuiColors.DalamudGrey3.WithAlpha(0.2f).ToUint());
                drawList.AddCircle(absoluteRadarCenter, radarRadius, ImGuiColors.HealerGreen.ToUint(), 64, 2.0f);

                // 绘制雷达网格圈 (每圈代表一定距离)
                var gridCount = 4;
                var gridDistance = ModuleConfig.RadarRange / gridCount; // 每格代表的实际距离

                for (var i = 1; i <= gridCount; i++)
                {
                    var gridRadius = radarRadius * i / gridCount;
                    drawList.AddCircle(absoluteRadarCenter, gridRadius, ImGuiColors.DalamudWhite.WithAlpha(0.25f).ToUint(), 32, 1.0f);

                    // 在每个网格圈上标注距离（只显示偶数圈防止重叠）
                    if (i % 2 == 0 && gridRadius < radarRadius - 15) // 防止标签超出雷达边界
                    {
                        var labelPos = new Vector2(absoluteRadarCenter.X + gridRadius + 5, absoluteRadarCenter.Y - 8);
                        drawList.AddText(labelPos, ImGuiColors.DalamudWhite.WithAlpha(0.5f).ToUint(), $"{gridDistance * i:F0}m");
                    }
                }

                // 绘制十字线
                drawList.AddLine(
                    new Vector2(absoluteRadarCenter.X - radarRadius, absoluteRadarCenter.Y),
                    new Vector2(absoluteRadarCenter.X + radarRadius, absoluteRadarCenter.Y),
                    ImGuiColors.DalamudWhite.WithAlpha(0.25f).ToUint(), 1.0f);
                drawList.AddLine(
                    new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y - radarRadius),
                    new Vector2(absoluteRadarCenter.X, absoluteRadarCenter.Y + radarRadius),
                    ImGuiColors.DalamudWhite.WithAlpha(0.25f).ToUint(), 1.0f);

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
                    drawList.AddCircleFilled(new Vector2(playerRadarX, playerRadarY), 4.0f, ImGuiColors.DPSRed.ToUint());
                    drawList.AddText(new Vector2(playerRadarX + 8, playerRadarY - 8), ImGuiColors.DPSRed.ToUint(), "玩家");
                }
                else
                {
                    // 玩家在雷达范围外，在边缘显示箭头指示方向
                    var directionToPlayer = Vector2.Normalize(new Vector2(playerRadarX, playerRadarY) - absoluteRadarCenter);
                    var edgePos = absoluteRadarCenter + directionToPlayer * (radarRadius - 10);
                    drawList.AddCircleFilled(edgePos, 3.0f, ImGuiColors.DPSRed.ToUint());
                    drawList.AddText(new Vector2(edgePos.X + 6, edgePos.Y - 6), ImGuiColors.DPSRed.ToUint(), "玩家(外)");
                }

                // 高级模式：绘制多个危险区域
                if (zoneLimit.IsAdvancedMode && zoneLimit.DangerZones.Count > 0)
                {
                    foreach (var dangerZone in zoneLimit.DangerZones)
                    {
                        // 无论是否启用都绘制，用不同颜色表示状态
                        DrawDangerZoneOnRadar(drawList, absoluteRadarCenter, radarRadius, dangerZone, playerPos);
                    }
                }
                // 传统模式：绘制单一边界
                else
                    DrawTraditionalBoundaryOnRadar(drawList, absoluteRadarCenter, radarRadius, zoneLimit, playerPos);

                // 绘制雷达中心标记
                drawList.AddCircle(absoluteRadarCenter, 3.0f, ImGuiColors.ParsedBlue.ToUint(), 16, 2.0f);
                drawList.AddText(new Vector2(absoluteRadarCenter.X + 8, absoluteRadarCenter.Y + 8), ImGuiColors.ParsedBlue.ToUint(), "中心");

                // 显示实时信息（放在雷达下方，确保有足够间距）
                ImGui.SetCursorPos(new Vector2(10, radarCenter.Y + radarRadius + 30));
                ImGui.Text($"玩家位置: ({playerPos.X:F1}, {playerPos.Z:F1})");
                ImGui.Text($"雷达中心: ({radarCenterWorldPos.X:F1}, {radarCenterWorldPos.Z:F1})");
                ImGui.Text($"显示范围: {ModuleConfig.RadarRange:F0}米");
                ImGui.Text($"比例: 1像素 = {ModuleConfig.RadarRange / radarRadius:F2}米");
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

                ImGui.Spacing(); // 添加间距

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

                ImGui.TextColored(inDanger ? ImGuiColors.DPSRed : ImGuiColors.HealerGreen,
                                 inDanger ? GetLoc("PreventEntryIntoMapBoundariesStatusDangerous") : GetLoc("PreventEntryIntoMapBoundariesStatusSafe"));
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

            var color = zone.Enabled ? (zone.Color | 0x80000000) : ImGuiColors.DalamudGrey.WithAlpha(0.25f).ToUint();

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
                    var DsafeColor = ImGuiColors.HealerGreen.WithAlpha(0.5f).ToUint();
                    for (float r = innerRadiusOnRadar + 2; r < outerRadiusOnRadar; r += 3.0f)
                        drawList.AddCircle(new Vector2(zoneRadarX, zoneRadarY), r, DsafeColor, 16, 1.0f);

                    // 添加文字说明
                    if (innerRadiusOnRadar > 10) // 在内圆显示危险
                    {
                        var dangerTextPos = new Vector2(zoneRadarX, zoneRadarY - innerRadiusOnRadar / 2);
                        drawList.AddText(dangerTextPos, color, GetLoc("PreventEntryIntoMapBoundariesDangerous"));
                    }
                    var safeTextPos = new Vector2(zoneRadarX + (innerRadiusOnRadar + outerRadiusOnRadar) / 2, zoneRadarY - 5);
                    drawList.AddText(safeTextPos, ImGuiColors.HealerGreen.ToUint(), GetLoc("PreventEntryIntoMapBoundariesSafe"));
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
                    var safeColor = ImGuiColors.HealerGreen.ToUint();
                    drawList.AddRect(safeTopLeft, safeBottomRight, safeColor, 0.0f, ImDrawFlags.None, 2.0f);
                    // 绘制半透明绿色填充
                    drawList.AddRectFilled(safeTopLeft, safeBottomRight, ImGuiColors.HealerGreen.WithAlpha(0.25f).ToUint());

                    // 添加文字说明
                    var safeCenterX = (safeTopLeft.X + safeBottomRight.X) / 2;
                    var safeCenterY = (safeTopLeft.Y + safeBottomRight.Y) / 2;
                    drawList.AddText(new Vector2(safeCenterX - 15, safeCenterY - 8), safeColor, GetLoc("PreventEntryIntoMapBoundariesSafeZone"));
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

            // 绘制区域名称（智能位置避免重叠）
            if (!string.IsNullOrEmpty(zone.Name))
            {
                // 计算多个可能的文字位置
                var textOffset = 20;
                var textSize = ImGui.CalcTextSize(zone.Name);

                var candidatePositions = new[]
                {
                    new Vector2(zoneRadarX + textOffset, zoneRadarY - textOffset),           // 右上
                    new Vector2(zoneRadarX - textOffset - textSize.X, zoneRadarY - textOffset), // 左上
                    new Vector2(zoneRadarX + textOffset, zoneRadarY + textOffset),           // 右下
                    new Vector2(zoneRadarX - textOffset - textSize.X, zoneRadarY + textOffset), // 左下
                    new Vector2(zoneRadarX - textSize.X / 2, zoneRadarY - textOffset - textSize.Y), // 正上方
                    new Vector2(zoneRadarX - textSize.X / 2, zoneRadarY + textOffset)        // 正下方
                };

                // 选择第一个不会超出雷达边界的位置
                Vector2 finalTextPos = candidatePositions[0];
                foreach (var pos in candidatePositions)
                {
                    // 检查是否在雷达圆形区域内且有足够边距
                    var distanceFromCenter = Vector2.Distance(pos, radarCenter);
                    var maxDistance = Vector2.Distance(new Vector2(pos.X + textSize.X, pos.Y + textSize.Y), radarCenter);

                    if (distanceFromCenter > 25 && maxDistance < radarRadius - 15) // 确保文字不与中心重叠，且不超出边界
                    {
                        finalTextPos = pos;
                        break;
                    }
                }

                // 添加文字背景提高可读性
                var bgColor = ImGuiColors.DalamudGrey3.WithAlpha(0.5f).ToUint();
                drawList.AddRectFilled(
                    new Vector2(finalTextPos.X - 2, finalTextPos.Y - 2),
                    new Vector2(finalTextPos.X + textSize.X + 2, finalTextPos.Y + textSize.Y + 2),
                    bgColor);

                drawList.AddText(finalTextPos, color, zone.Name);
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

            var color = zoneLimit.Enabled ? ImGuiColors.DPSRed.ToUint() : ImGuiColors.DalamudGrey.ToUint();
            var fillColor = zoneLimit.Enabled ? ImGuiColors.DPSRed.WithAlpha(0.2f).ToUint() : ImGuiColors.DalamudGrey.WithAlpha(0.2f).ToUint();

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
                {
                    // 如果还是超出，放在下方
                    textPos = new Vector2(boundaryRadarX - textSize.X / 2, boundaryRadarY + textOffset);
                }
            }

            // 添加文字背景
            var bgColor = ImGuiColors.DalamudGrey3.WithAlpha(0.5f).ToUint();
            drawList.AddRectFilled(
                new Vector2(textPos.X - 2, textPos.Y - 2),
                new Vector2(textPos.X + textSize.X + 2, textPos.Y + textSize.Y + 2),
                bgColor);

            drawList.AddText(textPos, color, boundaryText);
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
                Chat(GetLoc("PreventEntryIntoMapBoundariesConfigExported"));
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
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesCurrentZoneNoConfig"), currentZoneId));
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
                    ChatError(GetLoc("PreventEntryIntoMapBoundariesConfigNotInitialized"));
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
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandUsage"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandExamples"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandExample1"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandExample2"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandExample3"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesCommandExample4"));
                return;
            }

            var action = parts[0].ToLower();
            var currentZoneId = DService.ClientState.TerritoryType;

            if (ModuleConfig == null)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundariesConfigNotInitialized"));
                return;
            }

            if (!ModuleConfig.ZoneLimitList.TryGetValue(currentZoneId, out var zoneLimit))
            {
                ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneNotConfigured"), currentZoneId));
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
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesUnknownCommand"), action));
                    break;
            }
        }

        private void HandleAddCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundariesAddCommandUsage"));
                return;
            }

            var shapeType = args[0].ToLower();
            var dangerZone = new DangerZone(string.Format(GetLoc("PreventEntryIntoMapBoundariesCommandZone"), zoneLimit.DangerZones.Count + 1));

            switch (shapeType)
            {
                case "circle":
                    if (args.Length < 4)
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesCircleUsage"));
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
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesInvalidNumericParams"));
                        return;
                    }
                    break;

                case "rect":
                    if (args.Length < 5)
                    {
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesRectUsage"));
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
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesInvalidNumericParams"));
                        return;
                    }
                    break;

                default:
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesUnknownShapeType"), shapeType));
                    return;
            }

            zoneLimit.DangerZones.Add(dangerZone);
            zoneLimit.IsAdvancedMode = true;
            SaveConfig(ModuleConfig);
            Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneAdded"), shapeType, dangerZone.Name));
        }

        private void HandleDeleteCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 1)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundariesDeleteUsage"));
                return;
            }

            if (int.TryParse(args[0], out var index) && index > 0 && index <= zoneLimit.DangerZones.Count)
            {
                var removedZone = zoneLimit.DangerZones[index - 1];
                zoneLimit.DangerZones.RemoveAt(index - 1);
                SaveConfig(ModuleConfig);
                Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneDeleted"), removedZone.Name));
            }
            else
                ChatError($"无效的索引，当前有 {zoneLimit.DangerZones.Count} 个危险区域");
        }

        private void HandleModifyCommand(string[] args, ZoneLimit zoneLimit)
        {
            if (args.Length < 3)
            {
                ChatError(GetLoc("PreventEntryIntoMapBoundariesModifyUsage"));
                Chat(GetLoc("PreventEntryIntoMapBoundariesModifyProperties"));
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
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesZoneToggled"), enabled ? GetLoc("PreventEntryIntoMapBoundariesEnabled") : GetLoc("PreventEntryIntoMapBoundariesDisabled"), dangerZone.Name));
                    }
                    else
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesEnabledValueError"));
                    break;

                case "name":
                    dangerZone.Name = value;
                    SaveConfig(ModuleConfig);
                    Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesNameChanged"), value));
                    break;

                case "color":
                    if (uint.TryParse(value, out var color))
                    {
                        dangerZone.Color = color;
                        SaveConfig(ModuleConfig);
                        Chat(string.Format(GetLoc("PreventEntryIntoMapBoundariesColorChanged"), color.ToString("X8")));
                    }
                    else
                        ChatError(GetLoc("PreventEntryIntoMapBoundariesInvalidColorValue"));
                    break;

                default:
                    ChatError(string.Format(GetLoc("PreventEntryIntoMapBoundariesUnknownProperty"), property));
                    break;
            }
        }

        protected override void Uninit()
        {
            FrameworkManager.Unregister(OnFrameworkUpdate);
            DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
            RemoveSubCommand(Command);
        }
    }
}
