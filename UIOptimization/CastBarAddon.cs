using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using static FFXIVClientStructs.FFXIV.Client.UI.ListPanel;

namespace DailyRoutines.ModulesPublic;

public unsafe class CastBarAddon : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("CastBarAddonTitle"),
        Description = GetLoc("CastBarAddonDescription"),
        Category = ModuleCategories.UIOptimization,
        Author = ["Middo"]
    };

    private static SimpleNineGridNode? SlideMarkerNode;
    private static SimpleNineGridNode? ClassicSlideMarkerNode;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CastBar", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideCastingText"), ref ModuleConfig.RemoveCastingText))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideIcon"), ref ModuleConfig.RemoveIcon))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideInterruptedText"), ref ModuleConfig.RemoveInterruptedText))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideCountdownText"), ref ModuleConfig.RemoveCounter))
            SaveConfig(ModuleConfig);

        if (ModuleConfig is { RemoveCastingText: true, RemoveCounter: false })
        {
            ImGui.SameLine(0, 8f * GlobalFontScale);
            using (ImRaii.PushId("CounterPosition"))
            using (ImRaii.Group())
            {
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                {
                    ModuleConfig.AlignCounter = Alignment.Left;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                {
                    ModuleConfig.AlignCounter = Alignment.Center;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                {
                    ModuleConfig.AlignCounter = Alignment.Right;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGui.SameLine();
            ImGui.Text(GetLoc("CastBarAddonTitle-CountdownAlignmentPosition"));

            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            if (ImGui.SliderFloat2($"{GetLoc("CastBarAddonTitle-HorizontalAndVerticalOffset")}##offsetCounterPosition", ref ModuleConfig.OffsetCounter, -100, 100, "%.0f"))
                ModuleConfig.OffsetCounter = Vector2.Clamp(ModuleConfig.OffsetCounter, new(-100), new(100));
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideName"), ref ModuleConfig.RemoveName))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.RemoveName == false)
        {
            ImGui.SameLine(0, 8f * GlobalFontScale);
            using (ImRaii.PushId("NamePosition"))
            using (ImRaii.Group())
            {
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                {
                    ModuleConfig.AlignName = Alignment.Left;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                {
                    ModuleConfig.AlignName = Alignment.Center;
                    SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                {
                    ModuleConfig.AlignName = Alignment.Right;
                    SaveConfig(ModuleConfig);
                }
            }

            ImGui.SameLine();
            ImGui.Text(GetLoc("CastBarAddonTitle-NameAlignmentPosition"));

            ImGui.SetNextItemWidth(200f * GlobalFontScale);
            if (ImGui.SliderFloat2($"{GetLoc("CastBarAddonTitle-HorizontalAndVerticalOffset")}##offsetNamePosition", ref ModuleConfig.OffsetName, -100, 100, "%.0f"))
                ModuleConfig.OffsetName = Vector2.Clamp(ModuleConfig.OffsetName, new(-100), new(100));
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
        }

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-ShowSlideCastMarker"), ref ModuleConfig.SlideCast))
            SaveConfig(ModuleConfig);

        if (ModuleConfig.SlideCast)
        {
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-ClassicMode"), ref ModuleConfig.ClassicSlideCast))
                    SaveConfig(ModuleConfig);

                if (ModuleConfig.ClassicSlideCast)
                {
                    using (ImRaii.PushIndent())
                    {
                        ImGui.SetNextItemWidth(100f * GlobalFontScale);
                        ImGui.SliderInt(GetLoc("CastBarAddonTitle-Width"), ref ModuleConfig.ClassicSlideCastWidth, 1, 10);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            SaveConfig(ModuleConfig);

                        ImGui.SetNextItemWidth(100f * GlobalFontScale);
                        ImGui.SliderInt(GetLoc("CastBarAddonTitle-ExtraHeight"), ref ModuleConfig.ClassicSlideCastOverHeight, 0, 20);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            SaveConfig(ModuleConfig);
                    }
                }

                ImGui.SliderInt(GetLoc("CastBarAddonTitle-SlideCastOffsetTime"), ref ModuleConfig.SlideCastAdjust, 0, 1000);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SaveConfig(ModuleConfig);

                ImGui.ColorEdit4(GetLoc("CastBarAddonTitle-SlideCastMarkerColor"), ref ModuleConfig.SlideCastColor);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SaveConfig(ModuleConfig);

                ImGui.ColorEdit4(GetLoc("CastBarAddonTitle-SlideCastReadyColor"), ref ModuleConfig.SlideCastReadyColor);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    SaveConfig(ModuleConfig);
            }
        }
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreFinalize:
                Service.AddonController.DetachNode(SlideMarkerNode);
                SlideMarkerNode = null;

                Service.AddonController.DetachNode(ClassicSlideMarkerNode);
                ClassicSlideMarkerNode = null;
                return;
            case AddonEvent.PostDraw:
                if (CastBar == null) return;

                var barNode = CastBar->GetNodeById(9);
                if (barNode == null) return;

                var iconNode = (AtkComponentNode*)CastBar->GetNodeById(8);
                if (iconNode == null) return;

                var countdownTextNode = CastBar->GetTextNodeById(7);
                if (countdownTextNode == null) return;

                var castingTextNode = CastBar->GetTextNodeById(6);
                if (castingTextNode == null) return;

                var actionNameTextNode = CastBar->GetTextNodeById(4);
                if (actionNameTextNode == null) return;

                var progressBarNode = (AtkNineGridNode*)CastBar->GetNodeById(11);
                if (progressBarNode == null) return;

                var interruptedTextNode = CastBar->GetTextNodeById(2);
                if (interruptedTextNode == null) return;

                if (ModuleConfig.RemoveIcon)
                    iconNode->AtkResNode.ToggleVisibility(false);

                if (ModuleConfig.RemoveName)
                    actionNameTextNode->AtkResNode.ToggleVisibility(false);

                if (ModuleConfig.RemoveCounter)
                    countdownTextNode->AtkResNode.ToggleVisibility(false);

                if (ModuleConfig.RemoveCastingText)
                    castingTextNode->AtkResNode.ToggleVisibility(false);

                if (ModuleConfig is { RemoveCastingText: true, RemoveCounter: false })
                {
                    countdownTextNode->AlignmentFontType = (byte)(0x20 | (byte)ModuleConfig.AlignCounter);
                    countdownTextNode->SetWidth((ushort)(barNode->Width - 8));
                    countdownTextNode->SetPositionFloat(barNode->X + 4 + ModuleConfig.OffsetCounter.X, 30 + ModuleConfig.OffsetCounter.Y);
                }
                else
                {
                    countdownTextNode->AlignmentFontType = 0x20 | (byte)Alignment.Right;
                    countdownTextNode->SetWidth(42);
                    countdownTextNode->SetXFloat(170);
                }

                if (ModuleConfig.RemoveName == false)
                {
                    actionNameTextNode->AlignmentFontType = (byte)(0x00 | (byte)ModuleConfig.AlignName);
                    actionNameTextNode->SetPositionFloat(barNode->X + 4 + ModuleConfig.OffsetName.X, ModuleConfig.OffsetName.Y);
                    actionNameTextNode->SetWidth((ushort)(barNode->Width - 8));
                }

                if (ModuleConfig.RemoveInterruptedText)
                    interruptedTextNode->AtkResNode.SetScale(0, 0);

                switch (ModuleConfig)
                {
                    case { SlideCast: true, ClassicSlideCast: false }:
                        {
                            if (ClassicSlideMarkerNode != null)
                                ClassicSlideMarkerNode.IsVisible = false;

                            if (SlideMarkerNode == null)
                            {
                                SlideMarkerNode = new SimpleNineGridNode
                                {
                                    PartId = 0,
                                    TexturePath = "ui/uld/bgparts_hr1.tex",
                                    TextureCoordinates = new(32, 37),
                                    TextureSize = new(28, 30),
                                    IsVisible = false,
                                    Color = progressBarNode->Color.RGBA.ToVector4(),
                                    NodeFlags = progressBarNode->NodeFlags
                                };

                                Service.AddonController.AttachNode(SlideMarkerNode, CastBar->GetNodeById(10));
                            }

                            if (SlideMarkerNode != null)
                            {
                                var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - ModuleConfig.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                                var pos = 160 * slidePer;
                                SlideMarkerNode.IsVisible = true;
                                SlideMarkerNode.Size = new(168 - (int)pos, 15);
                                SlideMarkerNode.Position = new(pos - 11, 3);
                                var c = slidePer * 100 >= ((AddonCastBar*)CastBar)->CastPercent ? ModuleConfig.SlideCastColor : ModuleConfig.SlideCastReadyColor;
                                SlideMarkerNode.AddColor = new(c.X, c.Y, c.Z);
                                SlideMarkerNode.MultiplyColor = new(c.X, c.Y, c.Z);
                                SlideMarkerNode.Alpha = c.W;
                                SlideMarkerNode.PartId = 0;
                            }

                            break;
                        }
                    case { SlideCast: true, ClassicSlideCast: true }:
                        {
                            if (SlideMarkerNode != null)
                                SlideMarkerNode.IsVisible = false;

                            if (ClassicSlideMarkerNode == null)
                            {
                                if (progressBarNode == null) return;

                                ClassicSlideMarkerNode = new SimpleNineGridNode
                                {
                                    TexturePath = "ui/uld/emjfacemask.tex",
                                    TextureCoordinates = new(28, 28),
                                    TextureSize = new(8, 8),
                                    NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft,
                                    IsVisible = true,
                                    Width = 1,
                                    Height = 12,
                                    Position = new Vector2(100, 4)
                                };

                                Service.AddonController.AttachNode(ClassicSlideMarkerNode, progressBarNode->ParentNode);
                            }

                            if (ClassicSlideMarkerNode != null)
                            {
                                ClassicSlideMarkerNode.IsVisible = true;

                                var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - ModuleConfig.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                                var pos = 160 * slidePer;

                                ClassicSlideMarkerNode.Width = (ushort)ModuleConfig.ClassicSlideCastWidth;
                                ClassicSlideMarkerNode.Height = (ushort)(12 + (ModuleConfig.ClassicSlideCastOverHeight * 2));
                                ClassicSlideMarkerNode.Position = new Vector2(pos, 4 - ModuleConfig.ClassicSlideCastOverHeight);

                                var c = slidePer * 100 >= ((AddonCastBar*)CastBar)->CastPercent ? ModuleConfig.SlideCastColor : ModuleConfig.SlideCastReadyColor;
                                ClassicSlideMarkerNode.Color = new(c.X, c.Y, c.Z, c.W);
                            }

                            break;
                        }
                }

                return;
        }
    }

    protected class Config : ModuleConfiguration
    {
        public bool RemoveCastingText;
        public bool RemoveIcon;
        public bool RemoveCounter;
        public bool RemoveName;
        public bool RemoveInterruptedText;

        public bool SlideCast;
        public int SlideCastAdjust = 500;
        public Vector4 SlideCastColor = new(0.8f, 0.3f, 0.3f, 1);
        public Vector4 SlideCastReadyColor = new(0.3f, 0.8f, 0.3f, 1);
        public bool ClassicSlideCast;
        public int ClassicSlideCastWidth = 3;
        public int ClassicSlideCastOverHeight;

        public Alignment AlignName = Alignment.Left;
        public Alignment AlignCounter = Alignment.Right;

        public Vector2 OffsetName = Vector2.Zero;
        public Vector2 OffsetCounter = Vector2.Zero;
    }
}
