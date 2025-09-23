using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Data;
using System.Drawing;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Infos;
using static DailyRoutines.Infos.Widgets;
using DailyRoutines.Helpers;
using DailyRoutines.Windows;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using static DailyRoutines.Managers.CommandManager;
using static DailyRoutines.Helpers.NotifyHelper;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Infos;
using OmenTools.ImGuiOm;
using OmenTools.Helpers;
using static DailyRoutines.Helpers.NotifyHelper;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public unsafe class PreventEntryIntoMapBoundaries : DailyModuleBase
    {
        private static readonly DataTable ExpressionTable = new();
        private static readonly System.Globalization.CultureInfo InvariantCulture = System.Globalization.CultureInfo.InvariantCulture;
        private static readonly Dictionary<ZoneType, string> ZoneTypeDict = new()
        {
            { ZoneType.Circle, GetLoc("PreventEntryIntoMapBoundaries-CircleType") },
            { ZoneType.Annulus, GetLoc("PreventEntryIntoMapBoundaries-AnnulusType") },
            { ZoneType.Rectangle, GetLoc("PreventEntryIntoMapBoundaries-RectangleType") },
            { ZoneType.RectangularSafeZone, GetLoc("PreventEntryIntoMapBoundaries-RectangularSafeZoneType") },
            { ZoneType.Expression, GetLoc("PreventEntryIntoMapBoundaries-ExpressionType") }
        };
        private static readonly Dictionary<MapType, string> MapTypeDict = new()
        {
            { MapType.Circle, GetLoc("PreventEntryIntoMapBoundaries-CircleType") },
            { MapType.Rectangle, GetLoc("PreventEntryIntoMapBoundaries-RectangleType") }
        };
        // SetPosition Hook 相关定义
        private static readonly CompSig SetPositionSig = new("E8 ?? ?? ?? ?? 83 4B 70 01");
        private delegate void SetPositionDelegate(GameObject* gameObject, float x, float y, float z);
        private static Hook<SetPositionDelegate>? SetPositionHook;
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
            SetPositionHook ??= SetPositionSig.GetHook<SetPositionDelegate>(SetPositionDetour);
            SetPositionHook.Enable();
            AddSubCommand(Command, new(OnCommand) { HelpMessage = GetLoc("PreventEntryIntoMapBoundaries-CommandHelp") });
            DService.UiBuilder.Draw += OnDraw;
        }


        protected override void ConfigUI()
        {
            // 添加当前区域按钮
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-AddCurrentZone")))
            {
                if (!ModuleConfig.ZoneIDs.Contains(GameState.TerritoryType))
                {
                    ModuleConfig.ZoneIDs.Add(GameState.TerritoryType);
                    ModuleConfig.ZoneLimitList[GameState.TerritoryType] = new ZoneLimit();
                    SaveConfig(ModuleConfig);
                }
            }
            ImGui.NewLine();
            if (ImGuiOm.CheckboxColored(GetLoc("PreventEntryIntoMapBoundaries-ShowVisualization"), ref ModuleConfig.ShowBoundaryVisualization))
                SaveConfig(ModuleConfig);
            if (ModuleConfig.ShowBoundaryVisualization)
            {
                ImGui.SetNextItemWidth(120 * GlobalFontScale);
                if (ImGui.SliderFloat(GetLoc("PreventEntryIntoMapBoundaries-LineThickness"), ref ModuleConfig.LineThickness, 1.0f, 10.0f, "%.1f"))
                    SaveConfig(ModuleConfig);
            }
            
            if (ImGui.SliderInt(GetLoc("PreventEntryIntoMapBoundaries-DeathThreshold"), ref ModuleConfig.DisableOnDeathCount, 1,7, "%d"))
                SaveConfig(ModuleConfig);
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-DeathDescription"));
            if (ImGui.Button(GetLoc("Export")))
                ExportToClipboard(ModuleConfig);
            if (ImGui.Button(GetLoc("Import")))
            {
                if (ImportFromClipboard<Config>() is not null)
                {
                    ModuleConfig = ImportFromClipboard<Config>();
                    SaveConfig(ModuleConfig!);
                }
            }
            if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-ExportCurrentZone")))
                ExportCurrentZoneConfig();
            ImGui.NewLine();
            if (ModuleConfig.ZoneIDs.Count is 0)
                return;
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
                
                var nodeLabel = $"{zid}: {GetZoneName(zid)}";

                if (ImRaii.TreeNode($"{nodeLabel}###{zid}"))
                {
                    if (ImGui.Checkbox(GetLoc("Enable"), ref zoneLimit.Enabled))
                        SaveConfig(ModuleConfig);

                    ImGui.SameLine();
                    if (ImGui.Checkbox(GetLoc("Advance"), ref zoneLimit.IsAdvancedMode))
                        SaveConfig(ModuleConfig);

                    if (GameState.TerritoryType == zid)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button(GetLoc("PreventEntryIntoMapBoundaries-SetPosition")))
                        {
                            if (DService.ObjectTable.LocalPlayer is not null)
                            {
                                zoneLimit.CenterPos = DService.ObjectTable.LocalPlayer.Position;
                                SaveConfig(ModuleConfig);
                            }
                        }
                    }

                    if (zoneLimit.IsAdvancedMode)
                        DrawAdvancedModeUI(zoneLimit);
                    else
                        DrawTraditionalModeUI(zoneLimit, zid);

                    using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.Red.Vector()))
                    {
                        if (ImGui.Button(GetLoc("Delete", zid)))
                        {
                            ModuleConfig.ZoneIDs.RemoveAt(i);
                            ModuleConfig.ZoneLimitList.Remove(zid);
                            SaveConfig(ModuleConfig);
                            break;
                        }
                    }
                }
                if (i < ModuleConfig.ZoneIDs.Count - 1)
                    ImGui.NewLine();
            }

        }

        private void DrawTraditionalModeUI(ZoneLimit zoneLimit, uint zid)
        {
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-MapType"));
            ImGui.SetNextItemWidth(150 * GlobalFontScale);

            foreach (var (key, label) in MapTypeDict)
            {
                if (ImGui.RadioButton(label, zoneLimit.MapType == key))
                {
                    zoneLimit.MapType = key;
                    SaveConfig(ModuleConfig!);
                }
            }
            
            ImGui.Text(GetLoc("PreventEntryIntoMapBoundaries-CenterPosition"));
            ImGui.SetNextItemWidth(200 * GlobalFontScale);
            if (ImGui.InputFloat3($"##CenterPos{zid}", ref zoneLimit.CenterPos))
                SaveConfig(ModuleConfig!);
            
            ImGui.SetNextItemWidth(120 * GlobalFontScale);
            if (ImGui.InputFloat($"##Radius{zid}", ref zoneLimit.Radius, 1.0f, 10.0f, "%.2f"))
            {
                zoneLimit.Radius = Math.Max(1.0f, zoneLimit.Radius);
                SaveConfig(ModuleConfig!);
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("Radius"));

        }

        private void DrawAdvancedModeUI(ZoneLimit zoneLimit)
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
                    if (ImGui.Checkbox($"{GetLoc("Enable")}##dz{j}", ref dangerZone.Enabled))
                        SaveConfig(ModuleConfig!);

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150 * GlobalFontScale);
                    if (ImGui.InputText($"##dzName{j}", ref dangerZone.Name, 50))
                        SaveConfig(ModuleConfig!);
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("Name"));
                    
                    ImGui.SetNextItemWidth(120 * GlobalFontScale);
                    foreach (var (key, label) in ZoneTypeDict)
                    {
                        if (ImGui.RadioButton(label, dangerZone.ZoneType == key))
                        {
                            dangerZone.ZoneType = key;
                            SaveConfig(ModuleConfig!);
                        }
                    }
                    ImGui.SameLine();
                    ImGui.Text(GetLoc("Type"));
                    
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
                    DrawZoneColorAndDelete(dangerZone, j, zoneLimit);
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

        private void DrawZoneColorAndDelete(DangerZone dangerZone, int index, ZoneLimit zoneLimit)
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
            // 删除按钮
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, KnownColor.Red.Vector()))
            {
                if (ImGui.Button($"{GetLoc("Delete")}##dz{index}"))
                {
                    zoneLimit.DangerZones.RemoveAt(index);
                    SaveConfig(ModuleConfig!);
                }
            }
        }

        private static string GetZoneName(uint zid) => LuminaGetter.GetRow<TerritoryType>(zid).GetValueOrDefault().Map.Value.PlaceName.Value.Name.ToString();

        private static bool IsInDangerZone(DangerZone zone, Vector3 position) =>
            zone.Enabled && zone.ZoneType switch
            {
                ZoneType.Circle => (position - zone.CenterPos).Length() <= zone.Radius,
                ZoneType.Annulus => (position - zone.CenterPos).Length() is var ringDistance && (ringDistance < zone.InnerRadius || ringDistance > zone.Radius),
                ZoneType.Rectangle => position.X >= zone.MinX && position.X <= zone.MaxX && position.Z >= zone.MinZ && position.Z <= zone.MaxZ,
                ZoneType.RectangularSafeZone => position.X < zone.MinX || position.X > zone.MaxX || position.Z < zone.MinZ || position.Z > zone.MaxZ,
                ZoneType.Expression => EvaluateMathExpression(zone.MathExpression, position.X, position.Z),
                _ => false
            };

        private static Vector3 GetSafePositionFromDangerZone(DangerZone zone, Vector3 currentPos) =>
            zone.ZoneType switch
            {
                ZoneType.Circle => GetSafePositionFromCircle(zone, currentPos),
                ZoneType.Annulus => GetSafePositionFromAnnulus(zone, currentPos),
                ZoneType.Rectangle => GetSafePositionFromRectangle(zone, currentPos),
                ZoneType.RectangularSafeZone => GetSafePositionFromRectangularSafeZone(zone, currentPos),
                ZoneType.Expression => GetSafePositionFromExpression(zone, currentPos),
                _ => currentPos
            };


        private static Vector3 GetSafePositionFromCircle(DangerZone zone, Vector3 currentPos) =>
             zone.CenterPos + ((currentPos - zone.CenterPos).Length() == 0 ? new Vector3(1.0f, 0, 0) : Vector3.Normalize(currentPos - zone.CenterPos)) * (zone.Radius + 1.0f);

        private static Vector3 GetSafePositionFromAnnulus(DangerZone zone, Vector3 currentPos) =>
        (currentPos - zone.CenterPos).Length() == 0 ? zone.CenterPos + new Vector3((zone.InnerRadius + zone.Radius) / 2, 0, 0) : (Vector3.Normalize(currentPos - zone.CenterPos).Length() < zone.InnerRadius ? zone.CenterPos + Vector3.Normalize(currentPos - zone.CenterPos) * (zone.InnerRadius + 1.0f) : (Vector3.Normalize(currentPos - zone.CenterPos).Length() > zone.Radius ? zone.CenterPos + Vector3.Normalize(currentPos - zone.CenterPos) * (zone.Radius - 1.0f) : currentPos));

        private static Vector3 GetSafePositionFromRectangle(DangerZone zone, Vector3 currentPos) =>
    (Math.Abs(currentPos.X - zone.MinX) <= Math.Abs(currentPos.X - zone.MaxX) && Math.Abs(currentPos.X - zone.MinX) <= Math.Abs(currentPos.Z - zone.MinZ) && Math.Abs(currentPos.X - zone.MinX) <= Math.Abs(currentPos.Z - zone.MaxZ)) ? currentPos with { X = zone.MinX - 1.0f } : (Math.Abs(currentPos.X - zone.MaxX) <= Math.Abs(currentPos.Z - zone.MinZ) && Math.Abs(currentPos.X - zone.MaxX) <= Math.Abs(currentPos.Z - zone.MaxZ)) ? currentPos with { X = zone.MaxX + 1.0f } : (Math.Abs(currentPos.Z - zone.MinZ) <= Math.Abs(currentPos.Z - zone.MaxZ) ? currentPos with { Z = zone.MinZ - 1.0f } : currentPos with { Z = zone.MaxZ + 1.0f });

        private static Vector3 GetSafePositionFromRectangularSafeZone(DangerZone zone, Vector3 currentPos) =>
          new(currentPos.X < zone.MinX ? zone.MinX + 1.0f : currentPos.X > zone.MaxX ? zone.MaxX - 1.0f : Math.Clamp(currentPos.X, zone.MinX + 1.0f, zone.MaxX - 1.0f), currentPos.Y, currentPos.Z < zone.MinZ ? zone.MinZ + 1.0f : currentPos.Z > zone.MaxZ ? zone.MaxZ - 1.0f : Math.Clamp(currentPos.Z, zone.MinZ + 1.0f, zone.MaxZ - 1.0f));

        private static Vector3 GetSafePositionFromExpression(DangerZone zone, Vector3 currentPos) => 
            currentPos + ((zone.CenterPos - currentPos).LengthSquared() < 0.0001f ? Vector3.UnitX : Vector3.Normalize(zone.CenterPos - currentPos)) * 2.0f;
        private static Vector3 CheckAndCorrectTraditionalMode(ZoneLimit zoneLimit, Vector3 currentPos) =>
            zoneLimit.MapType switch
            {
                MapType.Circle => CheckCircleBoundary(zoneLimit, currentPos),
                MapType.Rectangle => CheckRectangleBoundary(zoneLimit, currentPos),
                _ => currentPos
            };

        private static Vector3 CheckCircleBoundary(ZoneLimit zoneLimit, Vector3 currentPos) =>
            (currentPos - zoneLimit.CenterPos) is var diff && diff.Length() is var distance && distance > zoneLimit.Radius-0.3f
                ? zoneLimit.CenterPos + (distance == 0 ? Vector3.UnitX : diff / distance) * (zoneLimit.Radius - 0.31f)
                : currentPos;

        private static Vector3 CheckRectangleBoundary(ZoneLimit zoneLimit, Vector3 currentPos) =>
            (zoneLimit.CenterPos.X - zoneLimit.Radius, zoneLimit.CenterPos.X + zoneLimit.Radius,
                zoneLimit.CenterPos.Z - zoneLimit.Radius, zoneLimit.CenterPos.Z + zoneLimit.Radius) is var (minX, maxX, minZ, maxZ) &&
            (Math.Max(minX + 0.5f, Math.Min(maxX - 0.5f, currentPos.X)),
                Math.Max(minZ + 0.5f, Math.Min(maxZ - 0.5f, currentPos.Z))) is var (clampedX, clampedZ)
                ? Math.Abs(clampedX - currentPos.X) > 0.001f || Math.Abs(clampedZ - currentPos.Z) > 0.001f
                      ? new Vector3(clampedX, currentPos.Y, clampedZ)
                      : currentPos
                : currentPos;

        
        private static bool EvaluateMathExpression(string expression, float x, float z)
        {
            var expr = expression.Replace("x", x.ToString("F2", InvariantCulture)).Replace("z", z.ToString("F2", InvariantCulture)).Replace("^", "**");
            var result = ExpressionTable.Compute(expr, null);
            return result switch
            {
                DBNull or null => false,
                bool boolResult => boolResult,
                IConvertible convertible => Convert.ToDouble(convertible) > 0,
                _ => false
            };
           
        }

        private static void SetPositionDetour(GameObject* gameObject, float x, float y, float z)
        {
            if ((nint)gameObject != DService.ObjectTable.LocalPlayer.Address ||
                !ModuleConfig.ZoneLimitList.TryGetValue(GameState.TerritoryType, out var zoneLimit) ||
                DService.PartyList.Count(p => p.CurrentHP <= 0) >= ModuleConfig.DisableOnDeathCount)
            {
                SetPositionHook!.Original(gameObject, x, y, z);
                return;
            }
            if (zoneLimit is { IsAdvancedMode: true, DangerZones.Count: > 0 })
            {
                foreach (var dangerZone in zoneLimit.DangerZones)
                {
                    if (dangerZone.Enabled && IsInDangerZone(dangerZone, new Vector3(x, y, z)))
                    {
                        var safePos = GetSafePositionFromDangerZone(dangerZone, new Vector3(x, y, z));
                        SetPositionHook!.Original(gameObject, safePos.X, safePos.Y, safePos.Z);
                        return;
                    }
                }
            }
            else if (zoneLimit.Enabled)
            {
                var correctedPos = CheckAndCorrectTraditionalMode(zoneLimit, new Vector3(x, y, z));
                SetPositionHook!.Original(gameObject, correctedPos.X, correctedPos.Y, correctedPos.Z);
                return;
            }
            SetPositionHook!.Original(gameObject, x, y, z);
        }
        
        private void OnDraw()
        {
            if (ModuleConfig?.ShowBoundaryVisualization != true ||
                !ModuleConfig.ZoneLimitList.TryGetValue(GameState.TerritoryType, out var zoneLimit))
                return;

            var drawList = ImGui.GetForegroundDrawList();
            
            if (zoneLimit is { IsAdvancedMode: true, DangerZones.Count: > 0 })
                zoneLimit.DangerZones.ForEach(zone => DrawDangerZoneInWorld(drawList, zone));
            else
                DrawTraditionalBoundaryInWorld(drawList, zoneLimit);
        }

        private void DrawDangerZoneInWorld(ImDrawListPtr drawList, DangerZone zone)
        {
            if (!zone.Enabled) return;
            
            switch (zone.ZoneType)
            {
                case ZoneType.Circle:
                    DrawCircleInWorld(drawList, zone.CenterPos, zone.Radius, zone.Color, ModuleConfig.LineThickness);
                    break;

                case ZoneType.Annulus:
                    DrawCircleInWorld(drawList, zone.CenterPos, zone.Radius, zone.Color, ModuleConfig.LineThickness);
                    DrawCircleInWorld(drawList, zone.CenterPos, zone.InnerRadius, zone.Color, ModuleConfig.LineThickness);
                    break;

                case ZoneType.Rectangle:
                    DrawRectangleInWorld(drawList, zone.MinX, zone.MaxX, zone.MinZ, zone.MaxZ, zone.Color, ModuleConfig.LineThickness);
                    break;

                case ZoneType.RectangularSafeZone:
                    DrawRectangleInWorld(drawList, zone.MinX, zone.MaxX, zone.MinZ, zone.MaxZ, KnownColor.Green.Vector().ToUint(), ModuleConfig.LineThickness);
                    break;

                case ZoneType.Expression:
                    if (DService.Gui.WorldToScreen(zone.CenterPos, out var screenPos))
                        drawList.AddCircleFilled(screenPos, 8.0f, zone.Color);
                    break;
            }
        }

        private void DrawTraditionalBoundaryInWorld(ImDrawListPtr drawList, ZoneLimit zoneLimit)
        {
            if (!zoneLimit.Enabled) return;
            
            switch (zoneLimit.MapType)
            {
                case MapType.Circle:
                    DrawCircleInWorld(drawList, zoneLimit.CenterPos, zoneLimit.Radius, KnownColor.Red.Vector().ToUint(), ModuleConfig.LineThickness);
                    break;
                case MapType.Rectangle:
                    var halfSize = zoneLimit.Radius;
                    DrawRectangleInWorld(drawList,
                        zoneLimit.CenterPos.X - halfSize, zoneLimit.CenterPos.X + halfSize,
                        zoneLimit.CenterPos.Z - halfSize, zoneLimit.CenterPos.Z + halfSize,
                        KnownColor.Red.Vector().ToUint(), ModuleConfig.LineThickness);
                    break;
            }
        }

        private void DrawCircleInWorld(ImDrawListPtr drawList, Vector3 center, float radius, uint color, float thickness)
        {
            for (var i = 0; i < 64; i++)
            {
                var angle1 = i * (float)(2 * Math.PI / 64);
                var angle2 = (i + 1) * (float)(2 * Math.PI / 64);

                // 内联计算坐标，减少临时变量
                DService.Gui.WorldToScreen(
                    new Vector3(center.X + radius * (float)Math.Cos(angle1), center.Y, center.Z + radius * (float)Math.Sin(angle1)),
                    out var screenPos1);
                DService.Gui.WorldToScreen(
                    new Vector3(center.X + radius * (float)Math.Cos(angle2), center.Y, center.Z + radius * (float)Math.Sin(angle2)),
                    out var screenPos2);

                drawList.AddLine(screenPos1, screenPos2, color, thickness);
            }
        }

        private void DrawRectangleInWorld(ImDrawListPtr drawList, float minX, float maxX, float minZ, float maxZ, uint color, float thickness)
        {
            var playerY = DService.ObjectTable.LocalPlayer?.Position.Y ?? 0;
            // 上边: 左上 -> 右上
            DService.Gui.WorldToScreen(new Vector3(minX, playerY, minZ), out var topLeft);
            DService.Gui.WorldToScreen(new Vector3(maxX, playerY, minZ), out var topRight);
            drawList.AddLine(topLeft, topRight, color, thickness);

            // 右边: 右上 -> 右下
            DService.Gui.WorldToScreen(new Vector3(maxX, playerY, maxZ), out var bottomRight);
            drawList.AddLine(topRight, bottomRight, color, thickness);

            // 下边: 右下 -> 左下
            DService.Gui.WorldToScreen(new Vector3(minX, playerY, maxZ), out var bottomLeft);
            drawList.AddLine(bottomRight, bottomLeft, color, thickness);

            // 左边: 左下 -> 左上
            drawList.AddLine(bottomLeft, topLeft, color, thickness);
        }
        
        private void OnCommand(string command, string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            var action = parts[0].ToLower();

            if (!ModuleConfig.ZoneLimitList.TryGetValue(GameState.TerritoryType, out var zoneLimit))
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
                            enabled ? GetLoc("Enabled") : GetLoc("Disable"), dangerZone.Name));
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
            if (!ModuleConfig.ZoneLimitList.TryGetValue(GameState.TerritoryType, out var zoneLimit))
                return;

            var exportConfig = new Config
            {
                ZoneIDs = [GameState.TerritoryType],
                ZoneLimitList = new Dictionary<uint, ZoneLimit> { { GameState.TerritoryType, zoneLimit } }
            };

            ExportToClipboard(exportConfig);
            
        }

        protected override void Uninit()
        {
            DService.UiBuilder.Draw -= OnDraw;
            RemoveSubCommand(Command);
        }
        
        private class Config : ModuleConfiguration
        {
            public List<uint> ZoneIDs { get; set; } = [];
            public Dictionary<uint, ZoneLimit> ZoneLimitList { get; set; } = [];
            public bool ShowBoundaryVisualization = false;
            public int DisableOnDeathCount  = 2;
            public float LineThickness = 3.0f;
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
            public List<DangerZone> DangerZones = [];
            public bool IsAdvancedMode = false;
        }
    }

