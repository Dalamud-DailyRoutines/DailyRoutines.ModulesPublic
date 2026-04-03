using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayPlayerHitbox : ModuleBase
{
    private static Config ModuleConfig = null!;

    private static OverlayController? Controller;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayPlayerHitboxTitle"),
        Description = Lang.Get("AutoDisplayPlayerHitboxDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Due"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static bool IsWeaponUnsheathed() =>
        UIState.Instance()->WeaponState.IsUnsheathed;

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        Controller ??= new();
        Controller.CreateNode(() => new PlayerDotImageNode());
    }

    protected override void Uninit()
    {
        Controller?.Dispose();
        Controller = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyInCombat"), ref ModuleConfig.OnlyInCombat))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyInDuty"), ref ModuleConfig.OnlyInDuty))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyUnsheathed"), ref ModuleConfig.OnlyUnsheathed))
            ModuleConfig.Save(this);

        ImGui.NewLine();

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.ColorPicker4(Lang.Get("Color"), ref ModuleConfig.Color);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.NewLine();

        using (ImRaii.ItemWidth(300f * GlobalUIScale))
        {
            if (ImGui.InputFloat(Lang.Get("Size"), ref ModuleConfig.Size))
                ModuleConfig.Size = MathF.Max(1, ModuleConfig.Size);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputUInt(Lang.Get("Icon"), ref ModuleConfig.IconID);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Icons.ToIconString()}"))
                ChatManager.Instance().SendCommand("/xldata icon");
            ImGuiOm.TooltipHover($"{Lang.Get("IconBrowser")}\n({Lang.Get("IconBrowser-Suggestion")})");

            if (ImGui.InputFloat3(Lang.Get("Offset"), ref ModuleConfig.Offset, 0.1f, 1f, "%.1f"))
                ModuleConfig.Save(this);
        }
    }

    private class PlayerDotImageNode : OverlayNode
    {
        private readonly IconImageNode imageNode;

        public PlayerDotImageNode()
        {
            imageNode = new IconImageNode
            {
                IconId     = 60952,
                FitTexture = true
            };
            imageNode.AttachNode(this);
        }

        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => false;

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size   = Size;
            imageNode.Origin = new Vector2(ModuleConfig.Size / 2.0f);
        }

        protected override void OnUpdate()
        {
            Size = new Vector2(ModuleConfig.Size);

            imageNode.Color  = ModuleConfig.Color;
            imageNode.IconId = ModuleConfig.IconID;

            Timeline?.PlayAnimation(1);

            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            {
                IsVisible = false;
                return;
            }

            IsVisible = !DService.Instance().Condition[ConditionFlag.Occupied38]                                   &&
                        (!ModuleConfig.OnlyInCombat   || DService.Instance().Condition[ConditionFlag.InCombat])    &&
                        (!ModuleConfig.OnlyInDuty     || DService.Instance().Condition[ConditionFlag.BoundByDuty]) &&
                        (!ModuleConfig.OnlyUnsheathed || IsWeaponUnsheathed());

            if (!IsVisible)
                return;

            var   offset = ModuleConfig.Offset;
            var   angle  = -localPlayer.Rotation;
            float cos    = MathF.Cos(angle), sin = MathF.Sin(angle);

            var rotatedOffset = new Vector3(cos * offset.X - sin * offset.Z, offset.Y, sin * offset.X + cos * offset.Z);
            DService.Instance().GameGUI.WorldToScreen(localPlayer.Position + rotatedOffset, out var screenPos);

            Position = screenPos - imageNode.Size / 2f;
        }
    }

    private class Config : ModuleConfig
    {
        public Vector4 Color        = new(1f, 1f, 1f, 1f);
        public uint    IconID       = 60422;
        public Vector3 Offset       = Vector3.Zero;
        public bool    OnlyInCombat = true;
        public bool    OnlyInDuty   = true;
        public bool    OnlyUnsheathed;
        public float   Size = 96f;
    }
}
