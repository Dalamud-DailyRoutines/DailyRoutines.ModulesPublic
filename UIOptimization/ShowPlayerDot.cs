using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using System;
using System.Drawing;

namespace DailyRoutines.ModuleTemplate;

public unsafe class ShowPlayerDot : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("ShowPlayerDotTitle"),
        Description = GetLoc("ShowPlayerDotDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Due"]
    };

    private static bool IsWeaponUnsheathed() => UIState.Instance()->WeaponState.IsUnsheathed;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        WindowManager.Draw += DrawDot;
    }

    protected override void Uninit()
    {
        WindowManager.Draw -= DrawDot;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-EnableCond"));

        if (ImGui.Checkbox(GetLoc("Enable"), ref ModuleConfig.Enabled))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox(GetLoc("OnlyInCombat"), ref ModuleConfig.Combat))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox(GetLoc("OnlyInDuty"), ref ModuleConfig.Instance))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox(GetLoc("ShowPlayerDot-Unsheathed"), ref ModuleConfig.Unsheathed))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-Appearance"));

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("Color"));

        ImGui.SameLine(0, 5f * GlobalFontScale);
        ModuleConfig.Colour = ImGuiComponents.ColorPickerWithPalette(0, "###ColorInput", ModuleConfig.Colour);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, $"{GetLoc("Save")}"))
            SaveConfig(ModuleConfig);

        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Redo, $"{GetLoc("Reset")}"))
        {
            ModuleConfig.Colour = new Vector4(1f, 1f, 1f, 1f);
            SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputFloat(GetLoc("ShowPlayerDot-Thickness"), ref ModuleConfig.Thickness, 0.1f, 1f, "%.1f"))
        {
            ModuleConfig.Thickness = MathF.Max(0f, ModuleConfig.Thickness);
            SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.InputFloat(GetLoc("Radius"), ref ModuleConfig.Radius, 0.1f, 1f, "%.1f"))
        {
            ModuleConfig.Radius = MathF.Max(0f, ModuleConfig.Radius);
            SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        if (ImGui.SliderInt(GetLoc("ShowPlayerDot-Segments"), ref ModuleConfig.Segments, 4, 1000))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        if (ImGui.Checkbox(GetLoc("ShowPlayerDot-Filled"), ref ModuleConfig.Filled))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-Adjustments"));

        if (ImGui.Checkbox(GetLoc("ShowPlayerDot-ZAdjustment"), ref ModuleConfig.Zedding))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.Zedding)
        {
            ImGui.SameLine(0, 5f * GlobalFontScale);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            if (ImGui.InputFloat(GetLoc("ShowPlayerDot-ZValue"), ref ModuleConfig.Zed, 0.01f, 0.1f, "%.2f"))
                SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();
        if (ImGui.Checkbox(GetLoc("Offset"), ref ModuleConfig.Offset))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.Offset)
        {
            ImGui.SameLine(0, 5f * GlobalFontScale);
            if (ImGui.Checkbox(GetLoc("ShowPlayerDot-RotateWithPlayer"), ref ModuleConfig.RotateOffset))
                SaveConfig(ModuleConfig);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            if (ImGui.InputFloat(GetLoc("ShowPlayerDot-OffsetX"), ref ModuleConfig.OffsetX, 0.1f, 1f, "%.1f"))
                SaveConfig(ModuleConfig);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            if (ImGui.InputFloat(GetLoc("ShowPlayerDot-OffsetY"), ref ModuleConfig.OffsetY, 0.1f, 1f, "%.1f"))
                SaveConfig(ModuleConfig);
        }
    }

    private static void DrawDot()
    {
        if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return;
        if (!ModuleConfig.Enabled) return;
        if (DService.Condition[ConditionFlag.Occupied38]) return;
        if (ModuleConfig.Combat && !DService.Condition[ConditionFlag.InCombat]) return;
        if (ModuleConfig.Instance && !DService.Condition[ConditionFlag.BoundByDuty]) return;
        if (ModuleConfig.Unsheathed && !IsWeaponUnsheathed()) return;

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(0, 0)))
        {
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(0, 0));
            ImGui.Begin("Canvas",
                ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing);
            ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

            var xOff = 0f;
            var yOff = 0f;
            if (ModuleConfig.Offset)
            {
                xOff = ModuleConfig.OffsetX;
                yOff = ModuleConfig.OffsetY;
                if (ModuleConfig.RotateOffset)
                {
                    var angle = -localPlayer.Rotation;
                    var cosTheta = MathF.Cos(angle);
                    var sinTheta = MathF.Sin(angle);
                    var tempX = xOff;
                    xOff = (cosTheta * tempX) - (sinTheta * yOff);
                    yOff = (sinTheta * tempX) + (cosTheta * yOff);
                }
            }

            var zed = 0f;
            if (ModuleConfig.Zedding)
                zed = ModuleConfig.Zed;

            DService.Gui.WorldToScreen(new Vector3(localPlayer.Position.X + xOff, localPlayer.Position.Y + zed, localPlayer.Position.Z + yOff), out var pos);

            if (ModuleConfig.Filled)
                ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(pos.X, pos.Y), ModuleConfig.Radius, ImGui.GetColorU32(ModuleConfig.Colour), ModuleConfig.Segments);
            else
                ImGui.GetWindowDrawList().AddCircle(new Vector2(pos.X, pos.Y), ModuleConfig.Radius, ImGui.GetColorU32(ModuleConfig.Colour), ModuleConfig.Segments, ModuleConfig.Thickness);

        }
    }

    private class Config : ModuleConfiguration
    {
        public bool Enabled = true;
        public bool Combat = false;
        public bool Instance = true;
        public bool Unsheathed = false;

        public Vector4 Colour = new(1f, 1f, 1f, 1f);
        public float Thickness = 10f;
        public int Segments = 100;
        public float Radius = 2f;
        public bool Filled = true;

        public bool Offset = false;
        public bool RotateOffset = false;
        public float OffsetX = 0f;
        public float OffsetY = 0f;
        public bool Zedding = false;
        public float Zed = 0f;
    }
}
