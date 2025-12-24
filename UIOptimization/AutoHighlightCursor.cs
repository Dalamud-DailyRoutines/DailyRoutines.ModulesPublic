using System;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Classes.Timelines;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay;

namespace DailyRoutines.ModulesPublic;

public class AutoHighlightCursor : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoHighlightCursorTitle"),
        Description = GetLoc("AutoHighlightCursorDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

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
        if (ImGui.Checkbox($"{GetLoc("AutoHighlightCursor-PlayAnimation")}", ref ModuleConfig.PlayAnimation))
            ModuleConfig.Save(this);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightCursor-PlayAnimation-Help"));
        
        if (ImGui.Checkbox($"{GetLoc("AutoHighlightCursor-HideOnCameraMove")}", ref ModuleConfig.HideOnCameraMove))
            ModuleConfig.Save(this);
        ImGuiOm.HelpMarker(GetLoc("AutoHighlightCursor-HideOnCameraMove-Help"));
        
        ImGui.NewLine();

        using (ImRaii.ItemWidth(200f * GlobalFontScale))
        {
            ImGui.ColorPicker4(GetLoc("Color"), ref ModuleConfig.Color);
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
                ChatManager.SendMessage("/xldata icon");
            ImGuiOm.TooltipHover($"{GetLoc("IconBrowser")}\n({GetLoc("AutoHighlightCursor-IconBrowser-Help")})");
        }
        
        ImGui.NewLine();
        
        if (ImGui.Checkbox($"{GetLoc("OnlyInCombat")}", ref ModuleConfig.OnlyShowInCombat))
            ModuleConfig.Save(this);
        
        if (ImGui.Checkbox($"{GetLoc("OnlyInDuty")}", ref ModuleConfig.OnlyShowInDuty))
            ModuleConfig.Save(this);
    }

    private unsafe class CursorImageNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUI => false;
        
        private readonly IconImageNode imageNode;

        public CursorImageNode()
        {
            imageNode = new IconImageNode
            {
                IconId     = 60498,
                FitTexture = true,
            };
            imageNode.AttachNode(this);

            AddTimeline(new TimelineBuilder()
                        .BeginFrameSet(1, 120)
                        .AddLabel(1,   1, AtkTimelineJumpBehavior.Start,       0)
                        .AddLabel(60,  0, AtkTimelineJumpBehavior.LoopForever, 1)
                        .AddLabel(61,  2, AtkTimelineJumpBehavior.Start,       0)
                        .AddLabel(120, 0, AtkTimelineJumpBehavior.LoopForever, 2)
                        .EndFrameSet()
                        .Build());

            imageNode.AddTimeline(new TimelineBuilder()
                                  .BeginFrameSet(1, 60)
                                  .AddFrame(1,  scale: new Vector2(1.0f,  1.0f))
                                  .AddFrame(30, scale: new Vector2(0.75f, 0.75f))
                                  .AddFrame(60, scale: new Vector2(1.0f,  1.0f))
                                  .EndFrameSet()
                                  .BeginFrameSet(61, 120)
                                  .AddFrame(61, scale: new Vector2(1.0f, 1.0f))
                                  .EndFrameSet()
                                  .Build());
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size   = Size;
            imageNode.Origin = new Vector2(ModuleConfig.Size / 2.0f);
        }

        public override void Update()
        {
            base.Update();

            Size = new Vector2(ModuleConfig.Size);

            imageNode.Color  = ModuleConfig.Color;
            imageNode.IconId = ModuleConfig.IconID;

            Timeline?.PlayAnimation(ModuleConfig.PlayAnimation ? 1 : 2);

            ref var cursorData = ref UIInputData.Instance()->CursorInputs;
            Position = new Vector2(cursorData.PositionX, cursorData.PositionY) - imageNode.Size / 2.0f;

            var isLeftHeld  = (cursorData.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) != 0;
            var isRightHeld = (cursorData.MouseButtonHeldFlags & MouseButtonFlags.RBUTTON) != 0;

            if (ModuleConfig is { OnlyShowInCombat: true } or { OnlyShowInDuty: true })
            {
                var shouldShow = true;
                shouldShow &= !ModuleConfig.OnlyShowInCombat || DService.Condition[ConditionFlag.InCombat];
                shouldShow &= !ModuleConfig.OnlyShowInDuty || BoundByDuty;
                shouldShow &= !ModuleConfig.HideOnCameraMove || (!isLeftHeld && !isRightHeld);

                IsVisible = shouldShow;
            }
            else
                IsVisible = (!isLeftHeld && !isRightHeld) || !ModuleConfig.HideOnCameraMove;
        }
    }

    private class Config : ModuleConfiguration
    {
        public bool PlayAnimation    = true;
        public bool HideOnCameraMove = true;
        
        public Vector4 Color  = Vector4.One;
        public float   Size   = 96f;
        public uint    IconID = 60498;

        public bool OnlyShowInCombat = true;
        public bool OnlyShowInDuty;
    }
}
