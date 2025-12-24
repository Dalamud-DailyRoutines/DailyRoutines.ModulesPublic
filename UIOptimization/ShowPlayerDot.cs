using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiToolKit.Classes;
using KamiToolKit.Classes.Timelines;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay;
using System;
using System.Drawing;
using System.Numerics;

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

    private static OverlayController? Controller;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Controller ??= new();
        Controller.CreateNode(() => new CursorImageNode());
    }

    protected override void Uninit()
    {
        Controller?.Dispose();
        Controller = null;
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

        using (ImRaii.ItemWidth(200f * GlobalFontScale))
        {
            ImGui.ColorPicker4(GetLoc("Color"), ref ModuleConfig.Colour);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            if (ImGui.InputFloat(GetLoc("Size"), ref ModuleConfig.Size))
                ModuleConfig.Size = MathF.Max(1, ModuleConfig.Size);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputUInt(GetLoc("Icon"), ref ModuleConfig.IconID);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Icons.ToIconString()}"))
                ChatHelper.SendMessage("/xldata icon");
            ImGuiOm.TooltipHover($"{GetLoc("IconBrowser")}\n({GetLoc("AutoHighlightCursor-IconBrowser-Help")})");
        }

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

    private unsafe class CursorImageNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;
        public override bool HideWithNativeUi => false;

        private readonly IconImageNode imageNode;

        public CursorImageNode()
        {
            imageNode = new IconImageNode
            {
                IconId = 60952,
                FitTexture = true,
            };
            imageNode.AttachNode(this);

            imageNode.AddTimeline(new TimelineBuilder()
                                 .BeginFrameSet(1, 120)
                                 .AddEmptyFrame(1)
                                 .EndFrameSet()
                                 .Build());
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size = Size;
            imageNode.Origin = new Vector2(ModuleConfig.Size / 2.0f);
        }

        public override void Update()
        {
            base.Update();

            Size = new Vector2(ModuleConfig.Size);

            imageNode.Color = ModuleConfig.Colour;
            imageNode.IconId = ModuleConfig.IconID;

            Timeline?.PlayAnimation(1);

            if (DService.ObjectTable.LocalPlayer is not { } localPlayer)
            {
                IsVisible = false;
                return;
            }

            IsVisible = ModuleConfig.Enabled &&
                        !DService.Condition[ConditionFlag.Occupied38] &&
                        (!ModuleConfig.Combat || DService.Condition[ConditionFlag.InCombat]) &&
                        (!ModuleConfig.Instance || DService.Condition[ConditionFlag.BoundByDuty]) &&
                        (!ModuleConfig.Unsheathed || IsWeaponUnsheathed());

            if (!IsVisible)
                return;

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

            Position = new Vector2(pos.X, pos.Y) - (imageNode.Size / 2.0f);

        }
    }

    private class Config : ModuleConfiguration
    {
        public bool Enabled = true;
        public bool Combat = false;
        public bool Instance = true;
        public bool Unsheathed = false;

        public Vector4 Colour = new(1f, 1f, 1f, 1f);
        public float Size = 96f;
        public uint IconID = 60952;

        public bool Offset = false;
        public bool RotateOffset = false;
        public float OffsetX = 0f;
        public float OffsetY = 0f;
        public bool Zedding = false;
        public float Zed = 0f;
    }
}
