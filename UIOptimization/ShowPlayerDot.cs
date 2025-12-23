using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using System;
using System.Drawing;

namespace DailyRoutines.ModuleTemplate;

public unsafe class ShowPlayerDot : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        //Title       = GetLoc("ShowPlayerDotTitle"),
        //Description = GetLoc("ShowPlayerDotDescription"),
        Title = "显示玩家判定点",
        Description = "在屏幕中心显示玩家判定点位置。",
        Category = ModuleCategories.Combat,
        Author = ["Due"]
    };

    private static bool IsWeaponUnsheathed() => UIState.Instance()->WeaponState.IsUnsheathed;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.UIBuilder.Draw += DrawDot;
    }

    protected override void Uninit()
    {
        DService.UIBuilder.Draw -= DrawDot;
    }

    protected override void ConfigUI()
    {
        ImGui.NewLine();
        //ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-EnableCond"));
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), "启用条件：");

        if (ImGui.Checkbox(GetLoc("Enable"), ref ModuleConfig.Enabled))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox(GetLoc("OnlyInCombat"), ref ModuleConfig.Combat))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        if (ImGui.Checkbox(GetLoc("OnlyInDuty"), ref ModuleConfig.Instance))
            SaveConfig(ModuleConfig);

        ImGui.SameLine(0, 5f * GlobalFontScale);
        //if (ImGui.Checkbox(GetLoc("ShowPlayerDot-Unsheathed"), ref ModuleConfig.Unsheathed))
        if (ImGui.Checkbox("仅当掏出武器时", ref ModuleConfig.Unsheathed))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        //ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-Appearance"));
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), "外观：");

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
        //if (ImGui.InputFloat(GetLoc("ShowPlayerDot-Thickness"), ref ModuleConfig.Thickness, 0.1f, 1f, "%.1f"))
        if (ImGui.InputFloat("粗细", ref ModuleConfig.Thickness, 0.1f, 1f, "%.1f"))
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
        //if (ImGui.SliderInt(GetLoc("ShowPlayerDot-Segments"), ref ModuleConfig.Segments, 4, 1000))
        if (ImGui.SliderInt("分段数", ref ModuleConfig.Segments, 4, 1000))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        //if (ImGui.Checkbox(GetLoc("ShowPlayerDot-Filled"), ref ModuleConfig.Filled))
        if (ImGui.Checkbox("填充", ref ModuleConfig.Filled))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();
        //ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), GetLoc("ShowPlayerDot-Adjustments"));
        ImGui.TextColored(KnownColor.AliceBlue.ToVector4(), "调整");

        //if (ImGui.Checkbox(GetLoc("ShowPlayerDot-ZAdjustment"), ref ModuleConfig.Zedding))
        if (ImGui.Checkbox("高度调整", ref ModuleConfig.Zedding))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.Zedding)
        {
            ImGui.SameLine(0, 5f * GlobalFontScale);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            //if (ImGui.InputFloat(GetLoc("ShowPlayerDot-ZValue"), ref ModuleConfig.Zed, 0.01f, 0.1f, "%.01f"))
            if (ImGui.InputFloat("高度值", ref ModuleConfig.Zed, 0.01f, 0.1f, "%.2f"))
                SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();
        if (ImGui.Checkbox(GetLoc("Offset"), ref ModuleConfig.Offset))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.Offset)
        {
            ImGui.SameLine(0, 5f * GlobalFontScale);
            //if (ImGui.Checkbox(GetLoc("ShowPlayerDot-RotateWithPlayer"), ref ModuleConfig.RotateOffset))
            if (ImGui.Checkbox("跟随玩家旋转", ref ModuleConfig.RotateOffset))
                SaveConfig(ModuleConfig);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            //if (ImGui.InputFloat(GetLoc("ShowPlayerDot-OffsetX"), ref ModuleConfig.OffsetX, 0.1f, 1f, "%.1f"))
            if (ImGui.InputFloat("X轴偏移", ref ModuleConfig.OffsetX, 0.1f, 1f, "%.1f"))
                SaveConfig(ModuleConfig);
            ImGui.SetNextItemWidth(100f * GlobalFontScale);
            //if (ImGui.InputFloat(GetLoc("ShowPlayerDot-OffsetY"), ref ModuleConfig.OffsetY, 0.1f, 1f, "%.1f"))
            if (ImGui.InputFloat("Y轴偏移", ref ModuleConfig.OffsetY, 0.1f, 1f, "%.1f"))
                SaveConfig(ModuleConfig);
        }
    }

    private static void DrawDot()
    {
        if (DService.ObjectTable.LocalPlayer == null) return;
        if (!ModuleConfig.Enabled) return;
        if (DService.Condition[ConditionFlag.Occupied38]) return;
        if (ModuleConfig.Combat && !DService.Condition[ConditionFlag.InCombat]) return;
        if (ModuleConfig.Instance && !DService.Condition[ConditionFlag.BoundByDuty]) return;
        if (ModuleConfig.Unsheathed && !IsWeaponUnsheathed()) return;

        var actor = DService.ObjectTable.LocalPlayer;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
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
                var angle = -actor.Rotation;
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

        DService.Gui.WorldToScreen(new Vector3(actor.Position.X + xOff, actor.Position.Y + zed, actor.Position.Z + yOff), out var pos);

        if (ModuleConfig.Filled)
            ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(pos.X, pos.Y), ModuleConfig.Radius, ImGui.GetColorU32(ModuleConfig.Colour), ModuleConfig.Segments);
        else
            ImGui.GetWindowDrawList().AddCircle(new Vector2(pos.X, pos.Y), ModuleConfig.Radius, ImGui.GetColorU32(ModuleConfig.Colour), ModuleConfig.Segments, ModuleConfig.Thickness);

        ImGui.End();
        ImGui.PopStyleVar();
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
