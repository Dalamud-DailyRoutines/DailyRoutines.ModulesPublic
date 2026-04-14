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
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayPlayerHitboxTitle"),
        Description = Lang.Get("AutoDisplayPlayerHitboxDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Due"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config             config = null!;
    private OverlayController? controller;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        controller ??= new();
        controller.CreateNode(() => new PlayerDotImageNode(config));
    }

    protected override void Uninit()
    {
        controller?.Dispose();
        controller = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyInCombat"), ref config.OnlyInCombat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyInDuty"), ref config.OnlyInDuty))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyUnsheathed"), ref config.OnlyUnsheathed))
            config.Save(this);

        ImGui.NewLine();

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.ColorPicker4(Lang.Get("Color"), ref config.Color);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        using (ImRaii.ItemWidth(300f * GlobalUIScale))
        {
            if (ImGui.InputFloat(Lang.Get("Size"), ref config.Size))
                config.Size = MathF.Max(1, config.Size);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputUInt(Lang.Get("Icon"), ref config.IconID);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Icons.ToIconString()}"))
                ChatManager.Instance().SendCommand("/xldata icon");
            ImGuiOm.TooltipHover($"{Lang.Get("IconBrowser")}\n({Lang.Get("IconBrowser-Suggestion")})");

            if (ImGui.InputFloat3(Lang.Get("Offset"), ref config.Offset, 0.1f, 1f, "%.1f"))
                config.Save(this);
        }
    }
    
    private static bool IsWeaponUnsheathed() =>
        UIState.Instance()->WeaponState.IsUnsheathed;

    private class PlayerDotImageNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => false;
        
        private readonly Config moduleConfig;
            
        private readonly IconImageNode imageNode;

        public PlayerDotImageNode(Config config)
        {
            moduleConfig = config;
            
            imageNode = new IconImageNode
            {
                IconId     = 60952,
                FitTexture = true
            };
            imageNode.AttachNode(this);
        }
        
        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size   = Size;
            imageNode.Origin = new Vector2(moduleConfig.Size / 2.0f);
        }

        protected override void OnUpdate()
        {
            Size = new(moduleConfig.Size);

            imageNode.Color  = moduleConfig.Color;
            imageNode.IconId = moduleConfig.IconID;

            Timeline?.PlayAnimation(1);

            if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            {
                IsVisible = false;
                return;
            }

            IsVisible = !DService.Instance().Condition[ConditionFlag.Occupied38]                                   &&
                        (!moduleConfig.OnlyInCombat   || DService.Instance().Condition[ConditionFlag.InCombat])    &&
                        (!moduleConfig.OnlyInDuty     || DService.Instance().Condition[ConditionFlag.BoundByDuty]) &&
                        (!moduleConfig.OnlyUnsheathed || IsWeaponUnsheathed());

            if (!IsVisible)
                return;

            var   offset = moduleConfig.Offset;
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
