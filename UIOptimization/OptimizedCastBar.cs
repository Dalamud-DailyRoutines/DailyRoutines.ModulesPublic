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
    private static readonly HashSet<ConditionFlag> ValidFlags = [ConditionFlag.BetweenAreas, ConditionFlag.Mounted];

    private static Config ModuleConfig = null!;

    private static SimpleNineGridNode? SlideMarkerZoneNode;
    private static SimpleNineGridNode? SlideMarkerLineNode;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedCastBarTitle"),
        Description = Lang.Get("OptimizedCastBarDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["Middo"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        ModuleConfig = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "_CastBar", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);

        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(1050));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(1050)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref ModuleConfig.CastingTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref ModuleConfig.CastingTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref ModuleConfig.CastingTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref ModuleConfig.CastingTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref ModuleConfig.CastingTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(14051));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(14051)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.InputByte(Lang.Get("Alpha"), ref ModuleConfig.IconAlpha);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref ModuleConfig.IconPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Scale"), ref ModuleConfig.IconScale);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(1051));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(1051)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref ModuleConfig.InterruptedTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref ModuleConfig.InterruptedTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref ModuleConfig.InterruptedTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref ModuleConfig.InterruptedTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref ModuleConfig.InterruptedTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(3626));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(3626)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref ModuleConfig.NameTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref ModuleConfig.NameTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref ModuleConfig.NameTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref ModuleConfig.NameTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref ModuleConfig.NameTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), LuminaWrapper.GetAddonText(701));

        using (ImRaii.PushId(LuminaWrapper.GetAddonText(701)))
        using (ImRaii.ItemWidth(250f * GlobalUIScale))
        using (ImRaii.PushIndent())
        {
            ImGui.ColorEdit4(Lang.Get("TextColor"), ref ModuleConfig.CastTimeTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref ModuleConfig.CastTimeTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("BackgroundColor"), ref ModuleConfig.CastTimeTextBackgroundColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputFloat2(Lang.Get("Position"), ref ModuleConfig.CastTimeTextPosition);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref ModuleConfig.CastTimeTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
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
                       Lang.Get($"OptimizedCastBar-SlideCastHighlightType-{ModuleConfig.SlideCastHighlightType}")
                   ))
            {
                if (combo)
                {
                    foreach (var type in Enum.GetValues<SlideCastHighlightType>())
                    {
                        if (ImGui.Selectable(Lang.Get($"OptimizedCastBar-SlideCastHighlightType-{type}"), ModuleConfig.SlideCastHighlightType == type))
                        {
                            ModuleConfig.SlideCastHighlightType = type;
                            ModuleConfig.Save(this);
                        }
                    }
                }
            }

            if (ModuleConfig.SlideCastHighlightType == SlideCastHighlightType.None) return;

            if (ModuleConfig.SlideCastHighlightType == SlideCastHighlightType.Line)
            {
                ImGui.Spacing();

                ImGui.SliderInt(Lang.Get("Width"), ref ModuleConfig.SlideCastLineWidth, 1, 10);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(this);

                ImGui.SliderInt(Lang.Get("Height"), ref ModuleConfig.SlideCastLineHeight, 0, 20);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(this);

                ImGui.Spacing();
            }

            ImGui.SliderInt(Lang.Get("OptimizedCastBar-SlideCastOffsetTime"), ref ModuleConfig.SlideCastZoneAdjust, 0, 1000);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("OptimizedCastBar-SlideCastMarkerNotReadyColor"), ref ModuleConfig.SlideCastNotReadyColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);

            ImGui.ColorEdit4(Lang.Get("OptimizedCastBar-SlideCastMarkerReadyColor"), ref ModuleConfig.SlideCastReadyColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                ModuleConfig.Save(this);
        }
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!ValidFlags.Contains(flag)) return;

        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                SlideMarkerZoneNode?.Dispose();
                SlideMarkerZoneNode = null;

                SlideMarkerLineNode?.Dispose();
                SlideMarkerLineNode = null;

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

                var slidePerercentage = ((float)(addon->CastTime * 10) - ModuleConfig.SlideCastZoneAdjust) / (addon->CastTime * 10);
                var slidePosition     = 160                                                                * slidePerercentage;
                var slideColor = DService.Instance().Condition[ConditionFlag.Casting] || DService.Instance().Condition[ConditionFlag.OccupiedInEvent]
                                     ? ModuleConfig.SlideCastNotReadyColor
                                     : ModuleConfig.SlideCastReadyColor;

                switch (ModuleConfig.SlideCastHighlightType)
                {
                    case SlideCastHighlightType.Zone:
                        if (SlideMarkerLineNode != null)
                            SlideMarkerLineNode.IsVisible = false;

                        if (SlideMarkerZoneNode == null)
                        {
                            SlideMarkerZoneNode = new()
                            {
                                PartId             = 0,
                                TexturePath        = "ui/uld/parameter_gauge_hr1.tex",
                                TextureCoordinates = new(0, 0),
                                TextureSize        = new(160, 20),
                                Color              = progressBarNode->Color.RGBA.ToVector4(),
                                NodeFlags          = progressBarNode->NodeFlags,
                                Offsets            = new(12)
                            };

                            SlideMarkerZoneNode.AttachNode(progressBarNode->ParentNode);
                        }

                        SlideMarkerZoneNode.IsVisible = true;
                        SlideMarkerZoneNode.Size      = new(168           - (int)slidePosition, 22);
                        SlideMarkerZoneNode.Position  = new(slidePosition - 9, -1f);

                        SlideMarkerZoneNode.AddColor      = slideColor.AsVector3();
                        SlideMarkerZoneNode.MultiplyColor = slideColor.AsVector3();

                        break;
                    case SlideCastHighlightType.Line:
                        if (SlideMarkerZoneNode != null)
                            SlideMarkerZoneNode.IsVisible = false;

                        if (SlideMarkerLineNode == null)
                        {
                            SlideMarkerLineNode = new()
                            {
                                TexturePath        = "ui/uld/emjfacemask.tex",
                                TextureCoordinates = new(28, 28),
                                TextureSize        = new(8, 8),
                                NodeFlags          = NodeFlags.AnchorTop | NodeFlags.AnchorLeft
                            };

                            SlideMarkerLineNode.AttachNode(progressBarNode->ParentNode);
                        }

                        SlideMarkerLineNode.IsVisible = true;
                        SlideMarkerLineNode.Size      = new(ModuleConfig.SlideCastLineWidth, 12 + ModuleConfig.SlideCastLineHeight * 2);
                        SlideMarkerLineNode.Position  = new(slidePosition, 4                    - ModuleConfig.SlideCastLineHeight);
                        SlideMarkerLineNode.Color     = slideColor;
                        break;
                }

                return;
        }
    }

    private static void UpdateOriginalAddonNodes()
    {
        if (CastBar == null || ModuleConfig == null) return;

        var interruptedTextNode = CastBar->GetTextNodeById(2);

        if (interruptedTextNode != null)
        {
            interruptedTextNode->TextColor       = ModuleConfig.InterruptedTextColor.ToByteColor();
            interruptedTextNode->EdgeColor       = ModuleConfig.InterruptedTextEdgeColor.ToByteColor();
            interruptedTextNode->BackgroundColor = ModuleConfig.InterruptedTextBackgroundColor.ToByteColor();
            interruptedTextNode->FontSize        = ModuleConfig.InterruptedTextSize;
            interruptedTextNode->SetPositionFloat(ModuleConfig.InterruptedTextPosition.X, ModuleConfig.InterruptedTextPosition.Y);
        }

        var actionNameTextNode = CastBar->GetTextNodeById(4);

        if (actionNameTextNode != null)
        {
            actionNameTextNode->TextColor       = ModuleConfig.NameTextColor.ToByteColor();
            actionNameTextNode->EdgeColor       = ModuleConfig.NameTextEdgeColor.ToByteColor();
            actionNameTextNode->BackgroundColor = ModuleConfig.NameTextBackgroundColor.ToByteColor();
            actionNameTextNode->FontSize        = ModuleConfig.NameTextSize;
            actionNameTextNode->SetPositionFloat(ModuleConfig.NameTextPosition.X, ModuleConfig.NameTextPosition.Y);
        }

        var iconNode = (AtkComponentNode*)CastBar->GetNodeById(8);

        if (iconNode != null)
        {
            iconNode->SetAlpha(ModuleConfig.IconAlpha);
            iconNode->SetPositionFloat(ModuleConfig.IconPosition.X, ModuleConfig.IconPosition.Y);
            iconNode->SetScale(ModuleConfig.IconScale.X, ModuleConfig.IconScale.Y);
        }

        var castingTextNode = CastBar->GetTextNodeById(6);

        if (castingTextNode != null)
        {
            castingTextNode->TextColor       = ModuleConfig.CastingTextColor.ToByteColor();
            castingTextNode->EdgeColor       = ModuleConfig.CastingTextEdgeColor.ToByteColor();
            castingTextNode->BackgroundColor = ModuleConfig.CastingTextBackgroundColor.ToByteColor();
            castingTextNode->FontSize        = ModuleConfig.CastingTextSize;
            castingTextNode->SetPositionFloat(ModuleConfig.CastingTextPosition.X, ModuleConfig.CastingTextPosition.Y);
        }

        var castTimeTextNode = CastBar->GetTextNodeById(7);

        if (castTimeTextNode != null)
        {
            castTimeTextNode->TextColor       = ModuleConfig.CastTimeTextColor.ToByteColor();
            castTimeTextNode->EdgeColor       = ModuleConfig.CastTimeTextEdgeColor.ToByteColor();
            castTimeTextNode->BackgroundColor = ModuleConfig.CastTimeTextBackgroundColor.ToByteColor();
            castTimeTextNode->FontSize        = ModuleConfig.CastTimeTextSize;
            castTimeTextNode->SetPositionFloat(ModuleConfig.CastTimeTextPosition.X, ModuleConfig.CastTimeTextPosition.Y);
        }
    }

    protected class Config : ModuleConfig
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

    protected enum SlideCastHighlightType
    {
        None,
        Zone,
        Line
    }
}
