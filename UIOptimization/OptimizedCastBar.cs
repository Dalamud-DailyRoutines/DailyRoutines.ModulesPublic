using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Premade.Node.Simple;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedCastBar : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedCastBarTitle"),
        Description = Lang.Get("OptimizedCastBarDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private SimpleNineGridNode? slideMarkerZoneNode;
    private SimpleNineGridNode? slideMarkerLineNode;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_CastBar", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }
    
    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(1050));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(1050)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.CastingTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.CastingTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref config.CastingTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref config.CastingTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.CastingTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(14051));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(14051)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.InputByte(Lang.Get("Alpha"), ref config.IconAlpha);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref config.IconPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Scale"), ref config.IconScale);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(1051));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(1051)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.InterruptedTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.InterruptedTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref config.InterruptedTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref config.InterruptedTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.InterruptedTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(3626));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(3626)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.NameTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.NameTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref config.NameTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref config.NameTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.NameTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(701));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(701)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.CastTimeTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.CastTimeTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref config.CastTimeTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref config.CastTimeTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.CastTimeTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("OptimizedCastBar-SlideCastMarker"));
        ImGuiOm.HelpMarker(Lang.Get("OptimizedCastBar-SlideCastMarker-Help"));

        using (ImRaii.PushId("OptimizedCastBar-SlideCastMarker"))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            using (var combo = ImRaii.Combo
                   (
                       Lang.Get("Type"),
                       Lang.Get($"OptimizedCastBar-SlideCastHighlightType-{config.SlideCastHighlightType}")
                   ))
            {
                if (combo)
                {
                    foreach (var type in Enum.GetValues<SlideCastHighlightType>())
                    {
                        if (ImGui.Selectable(Lang.Get($"OptimizedCastBar-SlideCastHighlightType-{type}"), config.SlideCastHighlightType == type))
                        {
                            config.SlideCastHighlightType = type;
                            config.Save(this);
                        }
                    }
                }
            }

            if (config.SlideCastHighlightType == SlideCastHighlightType.None) return;

            if (config.SlideCastHighlightType == SlideCastHighlightType.Line)
            {
                ImGui.Spacing();

                ImGui.SliderInt(Lang.Get("Width"), ref config.SlideCastLineWidth, 1, 10);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                ImGui.SliderInt(Lang.Get("Height"), ref config.SlideCastLineHeight, 0, 20);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                ImGui.Spacing();
            }

            ImGui.SliderInt(Lang.Get("OptimizedCastBar-SlideCastOffsetTime"), ref config.SlideCastZoneAdjust, 0, 1000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("OptimizedCastBar-SlideCastMarkerNotReadyColor"), ref config.SlideCastNotReadyColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("OptimizedCastBar-SlideCastMarkerReadyColor"), ref config.SlideCastReadyColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }
    
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!ValidFlags.Contains(flag)) return;

        OnAddon(AddonEvent.PreFinalize, null);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                slideMarkerZoneNode?.Dispose();
                slideMarkerZoneNode = null;

                slideMarkerLineNode?.Dispose();
                slideMarkerLineNode = null;

                UpdateOriginalAddonNodes();
                return;
            case AddonEvent.PostDraw:
                if (CastBar == null) return;

                var addon = (AddonCastBar*)CastBar;

                var progressBarNode = (AtkNineGridNode*)CastBar->GetNodeById(11);
                if (progressBarNode == null) return;

                if (Throttler.Shared.Throttle("OptimizedCastBar-PostDraw-UpdateOriginal"))
                    UpdateOriginalAddonNodes();

                if (!Throttler.Shared.Throttle("OptimizedCastBar-PostDraw-UpdateSlideCast", 10)) return;

                var slidePerercentage = ((float)(addon->CastTime * 10) - config.SlideCastZoneAdjust) / (addon->CastTime * 10);
                var slidePosition     = 160                                                                * slidePerercentage;
                var slideColor = DService.Instance().Condition[ConditionFlag.Casting] || DService.Instance().Condition[ConditionFlag.OccupiedInEvent]
                                     ? config.SlideCastNotReadyColor
                                     : config.SlideCastReadyColor;

                switch (config.SlideCastHighlightType)
                {
                    case SlideCastHighlightType.Zone:
                        if (slideMarkerLineNode != null)
                            slideMarkerLineNode.IsVisible = false;

                        if (slideMarkerZoneNode == null)
                        {
                            slideMarkerZoneNode = new()
                            {
                                PartId             = 0,
                                TexturePath        = "ui/uld/parameter_gauge_hr1.tex",
                                TextureCoordinates = new(0, 0),
                                TextureSize        = new(160, 20),
                                Color              = progressBarNode->Color.RGBA.ToVector4(),
                                NodeFlags          = progressBarNode->NodeFlags,
                                Offsets            = new(12)
                            };

                            slideMarkerZoneNode.AttachNode(progressBarNode->ParentNode);
                        }

                        slideMarkerZoneNode.IsVisible = true;
                        slideMarkerZoneNode.Size      = new(168           - (int)slidePosition, 22);
                        slideMarkerZoneNode.Position  = new(slidePosition - 9, -1f);

                        slideMarkerZoneNode.AddColor      = slideColor.AsVector3();
                        slideMarkerZoneNode.MultiplyColor = slideColor.AsVector3();

                        break;
                    case SlideCastHighlightType.Line:
                        if (slideMarkerZoneNode != null)
                            slideMarkerZoneNode.IsVisible = false;

                        if (slideMarkerLineNode == null)
                        {
                            slideMarkerLineNode = new()
                            {
                                TexturePath        = "ui/uld/emjfacemask.tex",
                                TextureCoordinates = new(28, 28),
                                TextureSize        = new(8, 8),
                                NodeFlags          = NodeFlags.AnchorTop | NodeFlags.AnchorLeft
                            };

                            slideMarkerLineNode.AttachNode(progressBarNode->ParentNode);
                        }

                        slideMarkerLineNode.IsVisible = true;
                        slideMarkerLineNode.Size      = new(config.SlideCastLineWidth, 12 + config.SlideCastLineHeight * 2);
                        slideMarkerLineNode.Position  = new(slidePosition, 4                    - config.SlideCastLineHeight);
                        slideMarkerLineNode.Color     = slideColor;
                        break;
                }

                return;
        }
    }

    private void UpdateOriginalAddonNodes()
    {
        if (CastBar == null || config == null) return;

        var interruptedTextNode = CastBar->GetTextNodeById(2);

        if (interruptedTextNode != null)
        {
            interruptedTextNode->TextColor       = config.InterruptedTextColor.ToByteColor();
            interruptedTextNode->EdgeColor       = config.InterruptedTextEdgeColor.ToByteColor();
            interruptedTextNode->BackgroundColor = config.InterruptedTextBackgroundColor.ToByteColor();
            interruptedTextNode->FontSize        = config.InterruptedTextSize;
            interruptedTextNode->SetPositionFloat(config.InterruptedTextPosition.X, config.InterruptedTextPosition.Y);
        }

        var actionNameTextNode = CastBar->GetTextNodeById(4);

        if (actionNameTextNode != null)
        {
            actionNameTextNode->TextColor       = config.NameTextColor.ToByteColor();
            actionNameTextNode->EdgeColor       = config.NameTextEdgeColor.ToByteColor();
            actionNameTextNode->BackgroundColor = config.NameTextBackgroundColor.ToByteColor();
            actionNameTextNode->FontSize        = config.NameTextSize;
            actionNameTextNode->SetPositionFloat(config.NameTextPosition.X, config.NameTextPosition.Y);
        }

        var iconNode = (AtkComponentNode*)CastBar->GetNodeById(8);

        if (iconNode != null)
        {
            iconNode->SetAlpha(config.IconAlpha);
            iconNode->SetPositionFloat(config.IconPosition.X, config.IconPosition.Y);
            iconNode->SetScale(config.IconScale.X, config.IconScale.Y);
        }

        var castingTextNode = CastBar->GetTextNodeById(6);

        if (castingTextNode != null)
        {
            castingTextNode->TextColor       = config.CastingTextColor.ToByteColor();
            castingTextNode->EdgeColor       = config.CastingTextEdgeColor.ToByteColor();
            castingTextNode->BackgroundColor = config.CastingTextBackgroundColor.ToByteColor();
            castingTextNode->FontSize        = config.CastingTextSize;
            castingTextNode->SetPositionFloat(config.CastingTextPosition.X, config.CastingTextPosition.Y);
        }

        var castTimeTextNode = CastBar->GetTextNodeById(7);

        if (castTimeTextNode != null)
        {
            castTimeTextNode->TextColor       = config.CastTimeTextColor.ToByteColor();
            castTimeTextNode->EdgeColor       = config.CastTimeTextEdgeColor.ToByteColor();
            castTimeTextNode->BackgroundColor = config.CastTimeTextBackgroundColor.ToByteColor();
            castTimeTextNode->FontSize        = config.CastTimeTextSize;
            castTimeTextNode->SetPositionFloat(config.CastTimeTextPosition.X, config.CastTimeTextPosition.Y);
        }
    }

    private class Config : ModuleConfig
    {
        public Vector4 CastingTextBackgroundColor = new(0);

        // 发动中
        public Vector4 CastingTextColor            = new(1);
        public Vector4 CastingTextEdgeColor        = new(0.56f, 0.42f, 0.05f, 1);
        public Vector2 CastingTextPosition         = new(0, 0);
        public byte    CastingTextSize             = 12;
        public Vector4 CastTimeTextBackgroundColor = new(0);

        // 咏唱时间
        public Vector4 CastTimeTextColor     = new(1);
        public Vector4 CastTimeTextEdgeColor = new(0.56f, 0.42f, 0.05f, 1);
        public Vector2 CastTimeTextPosition  = new(130, 30);
        public byte    CastTimeTextSize      = 20;

        // 图标
        public byte    IconAlpha                      = 255;
        public Vector2 IconPosition                   = new(0, 3);
        public Vector2 IconScale                      = new(1);
        public Vector4 InterruptedTextBackgroundColor = new(0);

        // 中断
        public Vector4 InterruptedTextColor     = new(1);
        public Vector4 InterruptedTextEdgeColor = new(0.56f, 0.42f, 0.05f, 1);
        public Vector2 InterruptedTextPosition  = new(0, 11);
        public byte    InterruptedTextSize      = 18;
        public Vector4 NameTextBackgroundColor  = new(0);

        // 技能名
        public Vector4 NameTextColor     = new(1);
        public Vector4 NameTextEdgeColor = new(0.56f, 0.42f, 0.05f, 1);
        public Vector2 NameTextPosition  = new(48, 0);
        public byte    NameTextSize      = 12;

        public SlideCastHighlightType SlideCastHighlightType = SlideCastHighlightType.Zone;
        public int                    SlideCastLineHeight;

        public int SlideCastLineWidth = 3;

        public Vector4 SlideCastNotReadyColor = new(0.8f, 0.3f, 0.3f, 1);
        public Vector4 SlideCastReadyColor    = new(0.3f, 0.8f, 0.3f, 1);

        public int SlideCastZoneAdjust = 500;
    }

    private enum SlideCastHighlightType
    {
        None,
        Zone,
        Line
    }
    
    #region 常量

    private static readonly FrozenSet<ConditionFlag> ValidFlags =
    [
        ConditionFlag.BetweenAreas,
        ConditionFlag.Mounted
    ];

    #endregion
}
