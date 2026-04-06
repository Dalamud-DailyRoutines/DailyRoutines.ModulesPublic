using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Timelines;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoHighlightCursor : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoHighlightCursorTitle"),
        Description = Lang.Get("AutoHighlightCursorDescription"),
        Category    = ModuleCategory.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config             config = null!;
    private OverlayController? controller;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        controller ??= new();
        controller.AddNode(new CursorImageNode(config));
    }

    protected override void Uninit()
    {
        controller?.Dispose();
        controller = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox($"{Lang.Get("AutoHighlightCursor-PlayAnimation")}", ref config.PlayAnimation))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoHighlightCursor-PlayAnimation-Help"));

        if (ImGui.Checkbox($"{Lang.Get("AutoHighlightCursor-HideOnCameraMove")}", ref config.HideOnCameraMove))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoHighlightCursor-HideOnCameraMove-Help"));

        ImGui.NewLine();

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        {
            ImGui.ColorPicker4(Lang.Get("Color"), ref config.Color);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            if (ImGui.InputFloat(Lang.Get("Size"), ref config.Size))
                config.Size = MathF.Max(1, config.Size);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputUInt(Lang.Get("Icon"), ref config.IconID);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Icons.ToIconString()}"))
                ChatManager.Instance().SendMessage("/xldata icon");
            ImGuiOm.TooltipHover($"{Lang.Get("IconBrowser")}\n({Lang.Get("IconBrowser-Suggestion")})");
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{Lang.Get("OnlyInCombat")}", ref config.OnlyShowInCombat))
            config.Save(this);

        if (ImGui.Checkbox($"{Lang.Get("OnlyInDuty")}", ref config.OnlyShowInDuty))
            config.Save(this);
    }
    
    private class Config : ModuleConfig
    {
        public Vector4 Color            = Vector4.One;
        public bool    HideOnCameraMove = true;
        public uint    IconID           = 60498;

        public bool  OnlyShowInCombat = true;
        public bool  OnlyShowInDuty;
        public bool  PlayAnimation = true;
        public float Size          = 96f;
    }

    private unsafe class CursorImageNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => true;

        private readonly Config moduleConfig;
        
        private readonly IconImageNode imageNode;
        
        public CursorImageNode(Config config)
        {
            moduleConfig = config;
            
            imageNode = new IconImageNode
            {
                IconId     = 60498,
                FitTexture = true
            };
            imageNode.AttachNode(this);

            AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(1, 120)
                    .AddLabel(1,   1, AtkTimelineJumpBehavior.Start,       0)
                    .AddLabel(60,  0, AtkTimelineJumpBehavior.LoopForever, 1)
                    .AddLabel(61,  2, AtkTimelineJumpBehavior.Start,       0)
                    .AddLabel(120, 0, AtkTimelineJumpBehavior.LoopForever, 2)
                    .EndFrameSet()
                    .Build()
            );

            imageNode.AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(1, 60)
                    .AddFrame(1,  scale: new Vector2(1.0f,  1.0f))
                    .AddFrame(30, scale: new Vector2(0.75f, 0.75f))
                    .AddFrame(60, scale: new Vector2(1.0f,  1.0f))
                    .EndFrameSet()
                    .BeginFrameSet(61, 120)
                    .AddFrame(61, scale: new Vector2(1.0f, 1.0f))
                    .EndFrameSet()
                    .Build()
            );
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size   = Size;
            imageNode.Origin = new Vector2(moduleConfig.Size / 2.0f);
        }

        protected override void OnUpdate()
        {
            Size = new Vector2(moduleConfig.Size);

            imageNode.Color  = moduleConfig.Color;
            imageNode.IconId = moduleConfig.IconID;

            Timeline?.PlayAnimation(moduleConfig.PlayAnimation ? 1 : 2);

            ref var cursorData = ref UIInputData.Instance()->CursorInputs;
            Position = new Vector2(cursorData.PositionX, cursorData.PositionY) - imageNode.Size / 2.0f;

            var isLeftHeld  = (cursorData.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) != 0;
            var isRightHeld = (cursorData.MouseButtonHeldFlags & MouseButtonFlags.RBUTTON) != 0;

            if (moduleConfig is { OnlyShowInCombat: true } or { OnlyShowInDuty: true })
            {
                var shouldShow = true;
                shouldShow &= !moduleConfig.OnlyShowInCombat || DService.Instance().Condition[ConditionFlag.InCombat];
                shouldShow &= !moduleConfig.OnlyShowInDuty   || DService.Instance().Condition.IsBoundByDuty;
                shouldShow &= !moduleConfig.HideOnCameraMove || !isLeftHeld && !isRightHeld;

                IsVisible = shouldShow;
            }
            else
                IsVisible = !isLeftHeld && !isRightHeld || !moduleConfig.HideOnCameraMove;
        }
    }
}
