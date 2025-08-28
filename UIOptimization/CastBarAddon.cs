using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using System.Numerics;
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

    private static SimpleNineGridNode? slideMarker;

    private static SimpleNineGridNode? classicSlideMarker;

    private static Configs Config = null!;
    protected override void Init()
    {
        Config = LoadConfig<Configs>() ?? new();
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CastBar", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_CastBar", OnAddon);
    }
    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideCastingText"), ref Config.RemoveCastingText))
            SaveConfig(Config);
        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideIcon"), ref Config.RemoveIcon))
            SaveConfig(Config);
        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideInterruptedText"), ref Config.RemoveInterruptedText))
            SaveConfig(Config);
        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideCountdownText"), ref Config.RemoveCounter))
            SaveConfig(Config);
        if (Config.RemoveCastingText is true && Config.RemoveCounter is not true)
        {
            ImGui.SameLine();
            using (ImRaii.PushId("CounterPosition"))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 2f))
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.One))
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Left ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                    {
                        Config.AlignCounter = Alignment.Left;
                        SaveConfig(Config);
                    }
                }

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Center ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                    {
                        Config.AlignCounter = Alignment.Center;
                        SaveConfig(Config);
                    }
                }
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignCounter == Alignment.Right ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                    {
                        Config.AlignCounter = Alignment.Right;
                        SaveConfig(Config);
                    }
                }
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("CastBarAddonTitle-CountdownAlignmentPosition"));
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);
            if (ImGui.SliderFloat2(GetLoc("CastBarAddonTitle-HorizontalAndVerticalOffset") + "##offsetCounterPosition", ref Config.OffsetCounter, -100, 100, $"%.0f"))
                SaveConfig(Config);

            Config.OffsetCounter = Vector2.Clamp(Config.OffsetCounter, new Vector2(-100), new Vector2(100));
        }

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-HideName"), ref Config.RemoveName))
            SaveConfig(Config);

        if (Config.RemoveName is not true)
        {
            ImGui.SameLine();
            using (ImRaii.PushId("NamePosition"))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 2f))
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.One))
            using (ImRaii.PushFont(UiBuilder.IconFont))
            using (ImRaii.Group())
            {
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Left ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignLeft}"))
                    {
                        Config.AlignName = Alignment.Left;
                        SaveConfig(Config);
                    }
                }

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Center ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignCenter}"))
                    {
                        Config.AlignName = Alignment.Center;
                        SaveConfig(Config);
                    }
                }
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Border, Config.AlignName == Alignment.Right ? 0xFF00A5FFu : 0u))
                {
                    if (ImGui.Button($"{(char)FontAwesomeIcon.AlignRight}"))
                    {
                        Config.AlignName = Alignment.Right;
                        SaveConfig(Config);
                    }
                }
            }
            ImGui.SameLine();
            ImGui.Text(GetLoc("CastBarAddonTitle-NameAlignmentPosition"));
            ImGui.SetNextItemWidth(200 * ImGui.GetIO().FontGlobalScale);

            if (ImGui.SliderFloat2(GetLoc("CastBarAddonTitle-HorizontalAndVerticalOffset") + "##offsetNamePosition", ref Config.OffsetName, -100, 100, "%.0f"))
                SaveConfig(Config);
            Config.OffsetName = Vector2.Clamp(Config.OffsetName, new Vector2(-100), new Vector2(100));
        }

        if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-ShowSlideCastMarker"), ref Config.SlideCast))
            SaveConfig(Config);

        if (Config.SlideCast is true)
        {
            using (ImRaii.PushIndent())
            using (ImRaii.PushIndent())
            {
                if (ImGui.Checkbox(GetLoc("CastBarAddonTitle-ClassicMode"), ref Config.ClassicSlideCast))
                    SaveConfig(Config);

                if (Config.ClassicSlideCast is true)
                {
                    using (ImRaii.PushIndent())
                    using (ImRaii.PushIndent())
                    {
                        using (ImRaii.ItemWidth(100f * ImGui.GetIO().FontGlobalScale))
                        {
                            if (ImGui.SliderInt(GetLoc("CastBarAddonTitle-Width"), ref Config.ClassicSlideCastWidth, 1, 10))
                                SaveConfig(Config);
                        }

                        using (ImRaii.ItemWidth(100f * ImGui.GetIO().FontGlobalScale))
                        {
                            if (ImGui.SliderInt(GetLoc("CastBarAddonTitle-ExtraHeight"), ref Config.ClassicSlideCastOverHeight, 0, 20))
                                SaveConfig(Config);
                        }
                    }
                }

                if (ImGui.SliderInt(GetLoc("CastBarAddonTitle-SlideCastOffsetTime"), ref Config.SlideCastAdjust, 0, 1000))
                    SaveConfig(Config);

                if (ImGui.ColorEdit4(GetLoc("CastBarAddonTitle-SlideCastMarkerColor"), ref Config.SlideCastColor))
                    SaveConfig(Config);

                if (ImGui.ColorEdit4(GetLoc("CastBarAddonTitle-SlideCastReadyColor"), ref Config.SlideCastReadyColor))
                    SaveConfig(Config);
            }
        }

        ScaledDummy(5f);

        OnAddon(AddonEvent.PreFinalize, null);
    }
    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }
    protected void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (CastBar == null)
            return;
        var barNode = CastBar->GetNodeById(9);
        var icon = (AtkComponentNode*)CastBar->GetNodeById(8);
        var countdownText = CastBar->GetTextNodeById(7);
        var castingText = CastBar->GetTextNodeById(6);
        var skillNameText = CastBar->GetTextNodeById(4);
        var progressBar = (AtkNineGridNode*)CastBar->GetNodeById(11);
        var interruptedText = CastBar->GetTextNodeById(2);
        switch (type)
        {
            case AddonEvent.PreFinalize:
                if (slideMarker != null)
                {
                    Service.AddonController.DetachNode(slideMarker);
                    slideMarker = null;
                }
                if (classicSlideMarker != null)
                {
                    Service.AddonController.DetachNode(classicSlideMarker);
                    classicSlideMarker = null;
                }
                icon->AtkResNode.ToggleVisibility(true);
                countdownText->AtkResNode.ToggleVisibility(true);
                castingText->AtkResNode.ToggleVisibility(true);
                skillNameText->AtkResNode.ToggleVisibility(true);

                skillNameText->SetWidth(170);
                skillNameText->SetPositionFloat(barNode->X + 4, 0);

                countdownText->SetWidth(42);
                countdownText->SetPositionFloat(170, 30);
                interruptedText->AtkResNode.SetScale(1, 1);

                countdownText->AlignmentFontType = 0x25;
                skillNameText->AlignmentFontType = 0x03;

                return;
            case AddonEvent.PostDraw:
                if (Config.RemoveIcon)
                    icon->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveName)
                    skillNameText->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveCounter)
                    countdownText->AtkResNode.ToggleVisibility(false);
                if (Config.RemoveCastingText)
                    castingText->AtkResNode.ToggleVisibility(false);

                if (Config.RemoveCastingText is true && Config.RemoveCounter is not true)
                {
                    countdownText->AlignmentFontType = (byte)(0x20 | (byte)Config.AlignCounter);
                    countdownText->SetWidth((ushort)(barNode->Width - 8));
                    countdownText->SetPositionFloat(barNode->X + 4 + Config.OffsetCounter.X, 30 + Config.OffsetCounter.Y);
                }
                else
                {
                    countdownText->AlignmentFontType = 0x20 | (byte)Alignment.Right;
                    countdownText->SetWidth(42);
                    countdownText->SetXFloat(170);
                }

                if (Config.RemoveName is not true)
                {
                    skillNameText->AlignmentFontType = (byte)(0x00 | (byte)Config.AlignName);
                    skillNameText->SetPositionFloat(barNode->X + 4 + Config.OffsetName.X, Config.OffsetName.Y);
                    skillNameText->SetWidth((ushort)(barNode->Width - 8));
                }

                if (Config.RemoveInterruptedText is true)
                    interruptedText->AtkResNode.SetScale(0, 0);

                if (Config.SlideCast is true && Config.ClassicSlideCast is not true)
                {
                    if (classicSlideMarker != null)
                        classicSlideMarker.IsVisible = false;
                    if (slideMarker == null)
                    {
                        slideMarker = new SimpleNineGridNode
                        {
                            PartId = 0,
                            TexturePath = "ui/uld/bgparts_hr1.tex",
                            TextureCoordinates = new(32, 37),
                            TextureSize = new(28, 30),
                            IsVisible = false,
                            Color = progressBar->Color.RGBA.ToVector4(),
                            NodeFlags = progressBar->NodeFlags,
                        };

                        Service.AddonController.AttachNode(slideMarker, CastBar->GetNodeById(10));
                    }

                    if (slideMarker != null)
                    {
                        var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - Config.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                        var pos = 160 * slidePer;
                        slideMarker.IsVisible = true;
                        slideMarker.Size = new Vector2(168 - (int)pos, 15);
                        slideMarker.Position = new Vector2(pos - 11, 3);
                        var c = (slidePer * 100) >= ((AddonCastBar*)CastBar)->CastPercent ? Config.SlideCastColor : Config.SlideCastReadyColor;
                        slideMarker.AddColor = new Vector3(c.X, c.Y, c.Z);
                        slideMarker.MultiplyColor = new Vector3(c.X, c.Y, c.Z);
                        slideMarker.Alpha = c.W;
                        slideMarker.PartId = 0;
                    }

                }
                else if (Config.SlideCast is true && Config.ClassicSlideCast is true)
                {
                    if (slideMarker != null)
                        slideMarker.IsVisible = false;
                    if (classicSlideMarker == null)
                    {
                        if (progressBar == null) return;

                        classicSlideMarker = new SimpleNineGridNode()
                        {
                            TexturePath = "ui/uld/emjfacemask.tex",
                            TextureCoordinates = new(28, 28),
                            TextureSize = new(8, 8),
                            NodeFlags = NodeFlags.AnchorTop | NodeFlags.AnchorLeft,
                            IsVisible = true,
                            Width = 1,
                            Height = 12,
                            Position = new Vector2(100, 4),
                        };

                        Service.AddonController.AttachNode(classicSlideMarker, progressBar->ParentNode);
                    }

                    if (classicSlideMarker != null)
                    {
                        classicSlideMarker.IsVisible = true;

                        var slidePer = ((float)(((AddonCastBar*)CastBar)->CastTime * 10) - Config.SlideCastAdjust) / (((AddonCastBar*)CastBar)->CastTime * 10);
                        var pos = 160 * slidePer;

                        classicSlideMarker.Width = (ushort)Config.ClassicSlideCastWidth;
                        classicSlideMarker.Height = (ushort)(12 + Config.ClassicSlideCastOverHeight * 2);
                        classicSlideMarker.Position = new Vector2(pos, 4 - Config.ClassicSlideCastOverHeight);

                        var c = (slidePer * 100) >= ((AddonCastBar*)CastBar)->CastPercent ? Config.SlideCastColor : Config.SlideCastReadyColor;
                        classicSlideMarker.Color = new Vector4(c.X, c.Y, c.Z, c.W);
                    }
                }
                return;
        }
        return;
    }
    protected class Configs : ModuleConfiguration
    {
        public bool RemoveCastingText;
        public bool RemoveIcon = false;
        public bool RemoveCounter;
        public bool RemoveName;
        public bool RemoveInterruptedText;

        public bool SlideCast;
        public int SlideCastAdjust = 500;
        public Vector4 SlideCastColor = new(0.8F, 0.3F, 0.3F, 1);
        public Vector4 SlideCastReadyColor = new(0.3F, 0.8F, 0.3F, 1);
        public bool ClassicSlideCast;
        public int ClassicSlideCastWidth = 3;
        public int ClassicSlideCastOverHeight;

        public Alignment AlignName = Alignment.Left;
        public Alignment AlignCounter = Alignment.Right;

        public Vector2 OffsetName = new(0);
        public Vector2 OffsetCounter = new(0);
    }
}
