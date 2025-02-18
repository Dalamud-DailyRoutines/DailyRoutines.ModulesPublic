using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class DisplayTargetHP : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("DisplayTargetHPTitle"),
        Description = GetLoc("DisplayTargetHPDescription"),
        Category = ModuleCategories.Combat,
    };

    private const  int    TargetHPNodeID = 0x915240 + 1;
    private static Config ModuleConfig   = null!;

    private static int NumberPreview = 5555555;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        FrameworkManager.Register(true, OnUpdate);
    }

    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-DisplayFormat")}:");
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(400f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###DisplayFormatCombo",
                                        $"{DisplayFormatLoc.GetValueOrDefault(ModuleConfig.DisplayFormat, GetLoc("DisplayTargetHP-UnknownDisplayFormat"))} ({FormatNumber((uint)NumberPreview, ModuleConfig.DisplayFormat)})", ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"{GetLoc("DisplayTargetHP-NumberPreview")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputInt("###PreviewNumberInput", ref NumberPreview, 0, 0))
                    NumberPreview = (int)Math.Clamp(NumberPreview, 0, uint.MaxValue);

                ImGui.Separator();
                ImGui.Spacing();

                foreach (var displayFormat in Enum.GetValues<DisplayFormat>())
                {
                    if (ImGui.Selectable(
                            $"{DisplayFormatLoc.GetValueOrDefault(displayFormat, GetLoc("DisplayTargetHP-UnknownDisplayFormat"))} ({FormatNumber((uint)NumberPreview, displayFormat)})##FormatSelect",
                            ModuleConfig.DisplayFormat == displayFormat))
                    {
                        ModuleConfig.DisplayFormat = displayFormat;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }
            
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-DisplayStringFormat")}:");
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(400f * GlobalFontScale);
        ImGui.InputText("###DisplayStringFormatInput", ref ModuleConfig.DisplayFormatString, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("DisplayTargetHP-DisplayStringFormatHelp"));

        ScaledDummy(5f);
        
        ImGui.TextColored(ImGuiColors.TankBlue, LuminaCache.GetRow<Addon>(1030)!.Value.Text.ExtractText());
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-AlignLeft")}:");
        
        ImGui.SameLine();
        if (ImGui.Checkbox("###TargetAlignLeft", ref ModuleConfig.AlignLeft))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-PosOffset")}:");
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputFloat2("###TargetPosition", ref ModuleConfig.Position, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-CustomColor")}:");
        
        ImGui.SameLine();
        if (ImGui.ColorButton("###TargetCustomColorButton", ModuleConfig.CustomColor))
            ImGui.OpenPopup("TargetCustomColorPopup");
        
        ImGuiOm.TooltipHover(GetLoc("DisplayTargetHP-ZeroAlphaHelp"));

        if (ImGui.BeginPopup("TargetCustomColorPopup"))
        {
            ImGui.ColorPicker4("###TargetCustomColor", ref ModuleConfig.CustomColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
            
            ImGui.EndPopup();
        }
        
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("FontScale")}:");
        
        ImGui.SameLine();
        var targetFontSize = (int)ModuleConfig.FontSize;
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        if (ImGui.SliderInt("###TargetFontSize", ref targetFontSize, 1, 32))
            ModuleConfig.FontSize = (byte)targetFontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-HideAutoAttackIcon")}:");

        ImGui.SameLine();
        if (ImGui.Checkbox("###TargetHideAutoAttackIcon", ref ModuleConfig.HideAutoAttack))
            SaveConfig(ModuleConfig);
        
        ScaledDummy(5f);
        
        ImGui.TextColored(ImGuiColors.TankBlue, LuminaCache.GetRow<Addon>(1110)!.Value.Text.ExtractText());
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-AlignLeft")}:");
        
        ImGui.SameLine();
        if (ImGui.Checkbox("###FocusAlignLeft", ref ModuleConfig.FocusAlignLeft))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-PosOffset")}:");
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        ImGui.InputFloat2("###FocusPosition", ref ModuleConfig.FocusPosition, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("DisplayTargetHP-CustomColor")}:");
        
        ImGui.SameLine();
        if (ImGui.ColorButton("###FocusCustomColorButton", ModuleConfig.FocusCustomColor))
            ImGui.OpenPopup("FocusCustomColorPopup");
        
        ImGuiOm.TooltipHover(GetLoc("DisplayTargetHP-ZeroAlphaHelp"));

        if (ImGui.BeginPopup("FocusCustomColorPopup"))
        {
            ImGui.ColorPicker4("###FocusCustomColor", ref ModuleConfig.FocusCustomColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                SaveConfig(ModuleConfig);
            
            ImGui.EndPopup();
        }
        
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("FontScale")}:");
        
        ImGui.SameLine();
        var focusFontSize = (int)ModuleConfig.FocusFontSize;
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        if (ImGui.SliderInt("###FocusFontSize", ref focusFontSize, 1, 32))
            ModuleConfig.FocusFontSize = (byte)focusFontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
    }

    private static void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("DisplayTargetHP", 100)) return;
        Update();
    }

    private static void Update(bool reset = false)
    {
        var target = DService.Targets.SoftTarget ?? DService.Targets.Target;
        if (target != null || reset)
        {
            if (IsAddonAndNodesReady(TargetInfo) || reset)
                UpdateMainTarget(TargetInfo, target, reset);

            if (IsAddonAndNodesReady(TargetInfoMainTarget) || reset)
                UpdateMainTargetSplit(TargetInfoMainTarget, target, reset);
        }

        if (DService.Targets.FocusTarget != null || reset)
        {
            if (IsAddonAndNodesReady(FocusTargetInfo) || reset)
                UpdateFocusTarget(FocusTargetInfo, DService.Targets.FocusTarget, reset);
        }
    }

    private static void UpdateMainTarget(AtkUnitBase* unitBase, IGameObject target, bool reset = false)
    {
        if (unitBase == null || unitBase->UldManager.NodeList == null ||
            unitBase->UldManager.NodeListCount < 40) return;

        var gauge    = (AtkComponentNode*)unitBase->UldManager.NodeList[36];
        var textNode = (AtkTextNode*)unitBase->UldManager.NodeList[39];
        SetSize(unitBase->UldManager.NodeList[37], reset || !ModuleConfig.HideAutoAttack ? 44 : 0,
                reset || !ModuleConfig.HideAutoAttack ? 20 : 0);
        UpdateGaugeBar(gauge, textNode, target, ModuleConfig.Position,
                       ModuleConfig.CustomColor, ModuleConfig.FontSize, ModuleConfig.AlignLeft, reset);
    }

    private static void UpdateFocusTarget(AtkUnitBase* unitBase, IGameObject target, bool reset = false)
    {
        if (unitBase                           == null || unitBase->UldManager.NodeList == null ||
            unitBase->UldManager.NodeListCount < 11) return;
        var gauge    = (AtkComponentNode*)unitBase->UldManager.NodeList[2];
        var textNode = (AtkTextNode*)unitBase->UldManager.NodeList[10];
        UpdateGaugeBar(gauge,                         textNode, target, ModuleConfig.FocusPosition,
                       ModuleConfig.FocusCustomColor, ModuleConfig.FocusFontSize,
                       ModuleConfig.FocusAlignLeft,   reset);
    }

    private static void UpdateMainTargetSplit(AtkUnitBase* unitBase, IGameObject target, bool reset = false)
    {
        if (unitBase == null || unitBase->UldManager.NodeList == null || unitBase->UldManager.NodeListCount < 9) return;
        var gauge    = (AtkComponentNode*)unitBase->UldManager.NodeList[5];
        var textNode = (AtkTextNode*)unitBase->UldManager.NodeList[8];
        SetSize(unitBase->UldManager.NodeList[6], reset || !ModuleConfig.HideAutoAttack ? 44 : 0,
                reset                                   || !ModuleConfig.HideAutoAttack ? 20 : 0);
        UpdateGaugeBar(gauge, textNode, target, ModuleConfig.Position,
                       ModuleConfig.CustomColor,
                       ModuleConfig.FontSize, ModuleConfig.AlignLeft, reset);
    }

    private static void UpdateGaugeBar(
        AtkComponentNode* gauge,       AtkTextNode* cloneTextNode, IGameObject target,    Vector2 positionOffset,
        Vector4?          customColor, byte         fontSize,      bool        alignLeft, bool    reset)
    {
        if (gauge == null || (ushort)gauge->AtkResNode.Type < 1000) return;
        
        if (customColor is { W: 0 }) customColor = null;
        
        AtkTextNode* textNode = null;

        for (var i = 5; i < gauge->Component->UldManager.NodeListCount; i++)
        {
            var node = gauge->Component->UldManager.NodeList[i];
            if (node->Type == NodeType.Text && node->NodeId == TargetHPNodeID)
            {
                textNode = (AtkTextNode*)node;
                break;
            }
        }

        if (textNode == null && reset) return; // Nothing to clean

        if (textNode == null)
        {
            textNode                    = CloneNode(cloneTextNode);
            textNode->AtkResNode.NodeId = TargetHPNodeID;
            var newStrPtr = Alloc(512);
            textNode->NodeText.StringPtr = (byte*)newStrPtr;
            textNode->NodeText.BufSize   = 512;
            textNode->SetText("");
            ExpandNodeList(gauge, 1);
            gauge->Component->UldManager.NodeList[gauge->Component->UldManager.NodeListCount++] = (AtkResNode*)textNode;

            var nextNode                                       = gauge->Component->UldManager.RootNode;
            while (nextNode->PrevSiblingNode != null) nextNode = nextNode->PrevSiblingNode;

            textNode->AtkResNode.ParentNode      = (AtkResNode*)gauge;
            textNode->AtkResNode.ChildNode       = null;
            textNode->AtkResNode.PrevSiblingNode = null;
            textNode->AtkResNode.NextSiblingNode = nextNode;
            nextNode->PrevSiblingNode            = (AtkResNode*)textNode;
        }

        if (reset)
        {
            textNode->AtkResNode.ToggleVisibility(false);
            return;
        }

        textNode->AlignmentFontType = (byte)(alignLeft ? AlignmentType.BottomLeft : AlignmentType.BottomRight);

        SetPosition(textNode, positionOffset.X, positionOffset.Y);
        SetSize(textNode, gauge->AtkResNode.Width - 5, gauge->AtkResNode.Height);
        textNode->AtkResNode.ToggleVisibility(true);
        if (!customColor.HasValue)
            textNode->TextColor = cloneTextNode->TextColor;
        else
        {
            textNode->TextColor.A = (byte)(customColor.Value.W * 255);
            textNode->TextColor.R = (byte)(customColor.Value.X * 255);
            textNode->TextColor.G = (byte)(customColor.Value.Y * 255);
            textNode->TextColor.B = (byte)(customColor.Value.Z * 255);
        }

        textNode->EdgeColor = cloneTextNode->EdgeColor;
        textNode->FontSize  = fontSize;

        if (target is ICharacter chara)
            textNode->SetText(string.Format(ModuleConfig.DisplayFormatString, FormatNumber(chara.MaxHp),
                                            FormatNumber(chara.CurrentHp)));
        else
            textNode->SetText("");
    }

    private static string FormatNumber(uint num, DisplayFormat? displayFormat = null)
    {
        displayFormat ??= ModuleConfig.DisplayFormat;
        var currentLang = LanguageManager.CurrentLanguage;
        switch (displayFormat)
        {
            case DisplayFormat.FullNumber:
                return num.ToString();
            case DisplayFormat.FullNumberSeparators:
                return num.ToString("N0");
            case DisplayFormat.ChineseFull:
                return FormatNumberByChineseNotation((int)num, currentLang)->ToString();
            case DisplayFormat.ChineseZeroPrecision:
            case DisplayFormat.ChineseOnePrecision:
            case DisplayFormat.ChineseTwoPrecision:
                var (divisor, unit) = num switch
                {
                    >= 1_0000_0000 => (1_0000_0000f, currentLang is ("ChineseTraditional" or "Japanese") ? "億" : "亿"),
                    >= 1_0000      => (1_0000f, currentLang == "ChineseTraditional" ? "萬" : "万"),
                    _              => (1f, string.Empty)
                };

                var value = num / divisor;
                var fStrChinese = displayFormat switch
                {
                    DisplayFormat.ChineseOnePrecision  => "F1",
                    DisplayFormat.ChineseTwoPrecision  => "F2",
                    DisplayFormat.ChineseZeroPrecision => "F0"
                };

                var formattedValue = value.ToString(fStrChinese);
                formattedValue = formattedValue.TrimEnd('0').TrimEnd('.');

                return $"{formattedValue}{unit}";
            case DisplayFormat.ZeroPrecision:
            case DisplayFormat.OnePrecision:
            case DisplayFormat.TwoPrecision:
                var fStrEnglish = displayFormat switch {
                    DisplayFormat.OnePrecision  => "F1",
                    DisplayFormat.TwoPrecision  => "F2",
                    DisplayFormat.ZeroPrecision => "F0"
                };

                return num switch {
                    >= 1000000 => $"{(num / 1000000f).ToString(fStrEnglish)}M",
                    >= 1000    => $"{(num / 1000f).ToString(fStrEnglish)}K",
                    _          => $"{num}"
                };
            default:
                return num.ToString("N0");
        }
    }

    public override void Uninit()
    {
        if (Initialized)
        {
            FrameworkManager.Unregister(OnUpdate);
            Update(true);
        }
    }

    private class Config : ModuleConfiguration
    {
        public DisplayFormat DisplayFormat = DisplayFormat.ChineseOnePrecision;
        public string DisplayFormatString = "{0} / {1}";
        
        public Vector2 Position      = new(0);
        public Vector4 CustomColor = new(1);
        public byte    FontSize    = 14;
        public bool    HideAutoAttack;
        public bool    AlignLeft;
        
        public Vector2 FocusPosition    = new(0);
        public Vector4 FocusCustomColor = new(1);
        public byte    FocusFontSize    = 14;
        public bool    FocusAlignLeft;
    }
    
    public enum DisplayFormat
    {
        FullNumber,
        FullNumberSeparators,
        ChineseFull,
        ChineseZeroPrecision,
        ChineseOnePrecision,
        ChineseTwoPrecision,
        ZeroPrecision,
        OnePrecision,
        TwoPrecision
    }

    private static readonly Dictionary<DisplayFormat, string> DisplayFormatLoc = new()
    {
        { DisplayFormat.FullNumber, GetLoc("DisplayTargetHP-FullNumber") },
        { DisplayFormat.FullNumberSeparators, GetLoc("DisplayTargetHP-FullNumberSeparators") },
        { DisplayFormat.ChineseFull, GetLoc("DisplayTargetHP-ChineseFull") },
        { DisplayFormat.ChineseZeroPrecision, GetLoc("DisplayTargetHP-ChineseZeroPrecision") },
        { DisplayFormat.ChineseOnePrecision, GetLoc("DisplayTargetHP-ChineseOnePrecision") },
        { DisplayFormat.ChineseTwoPrecision, GetLoc("DisplayTargetHP-ChineseTwoPrecision") },
        { DisplayFormat.ZeroPrecision, GetLoc("DisplayTargetHP-ZeroPrecision") },
        { DisplayFormat.OnePrecision, GetLoc("DisplayTargetHP-OnePrecision") },
        { DisplayFormat.TwoPrecision, GetLoc("DisplayTargetHP-TwoPrecision") },
    };
}
