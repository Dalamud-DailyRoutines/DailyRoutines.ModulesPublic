using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedEnemyList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimizedEnemyListTitle"),
        Description = GetLoc("OptimizedEnemyListDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static Config ModuleConfig = null!;

    private static Dictionary<uint, int> HaterInfo = [];
    
    private static readonly List<(nint ComponentPtr, TextNode TextNode, NineGridNode BackgroundNode)> TextNodes = [];

    private static string CastInfoTargetBlacklistInput = string.Empty;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        MakeTextNodesAndLink();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,          "_EnemyList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_EnemyList", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw,           "_EnemyList", OnAddon);
    }

    protected override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("Offset"));
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2("###TextOffsetInput", ref ModuleConfig.TextOffset, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.Save(this);
            UpdateTextNodes();
        }
        
        ImGui.Spacing();

        if (ImGui.Checkbox(GetLoc("OptimizedEnemyList-UseCustomColor"), ref ModuleConfig.UseCustomizeTextColor))
        {
            ModuleConfig.Save(this);
            UpdateTextNodes();
        }

        if (ModuleConfig.UseCustomizeTextColor)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(LightSkyBlue, $"{GetLoc("Color")}:");
            
            ImGui.SameLine();
            ModuleConfig.TextColor =
                ImGuiComponents.ColorPickerWithPalette(0, "###TextColorInput", ModuleConfig.TextColor);
            
            ImGui.SameLine();
            ImGui.TextDisabled("|");

            ImGui.SameLine();
            ImGui.TextColored(LightSkyBlue, $"{GetLoc("EdgeColor")}:");
            
            ImGui.SameLine();
            ModuleConfig.EdgeColor =
                ImGuiComponents.ColorPickerWithPalette(1, "###EdgeColorInput", ModuleConfig.EdgeColor);
            
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            
            ImGui.SameLine();
            ImGui.TextColored(LightSkyBlue, $"{GetLoc("BackgroundColor")}:");
            
            ImGui.SameLine();
            ModuleConfig.BackgroundColor =
                ImGuiComponents.ColorPickerWithPalette(2, "###BackgroundColorInput", ModuleConfig.BackgroundColor);
            
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            
            ImGui.SameLine();
            if (ImGui.Button($"{GetLoc("Save")}"))
            {
                ModuleConfig.Save(this);
                UpdateTextNodes();
            }
            
            ImGui.SameLine();
            if (ImGui.Button($"{GetLoc("Reset")}"))
            {
                ModuleConfig.TextColor       = new(1, 1, 1, 1);
                ModuleConfig.EdgeColor       = new(0.6157f, 0.5137f, 0.3569f, 1);
                ModuleConfig.BackgroundColor = new(0, 0, 0, 0);
                
                ModuleConfig.Save(this);
                UpdateTextNodes();
            }
        }
        
        ImGui.Spacing();

        if (ImGui.Checkbox(GetLoc("OptimizedEnemyList-UseCustomGeneralInfo"), ref ModuleConfig.UseCustomizeText))
        {
            ModuleConfig.Save(this);
            UpdateTextNodes();
        }

        if (ModuleConfig.UseCustomizeText)
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetTextLineHeightWithSpacing());
            ImGui.InputText("###CustomizeTextPatternInput", ref ModuleConfig.CustomizeTextPattern, 512);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                ModuleConfig.Save(this);
                UpdateTextNodes();
            }
        }
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("FontSize"));
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGuiOm.InputByte("###FontSize", ref ModuleConfig.FontSize);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            ModuleConfig.Save(this);
            UpdateTextNodes();
        }
        
        ImGui.Spacing();
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(LightSkyBlue, GetLoc("OptimizedEnemyList-CastInfoDisplayTargetBlacklist"));
        ImGuiOm.HelpMarker(GetLoc("OptimizedEnemyList-CastInfoDisplayTargetBlacklistHelp"));
        
        ImGui.SameLine();
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, GetLoc("Add")))
            ImGui.OpenPopup("CastInfoTargetBlacklistPopup");

        using (var popup = ImRaii.Popup("CastInfoTargetBlacklistPopup"))
        {
            if (popup)
            {
                ImGui.InputText("###CastInfoTargetBlacklistInput", ref CastInfoTargetBlacklistInput, 512);
                
                ImGui.SameLine();
                using (ImRaii.Disabled(string.IsNullOrWhiteSpace(CastInfoTargetBlacklistInput) || 
                                       ModuleConfig.CastInfoTargetBlacklist.Contains(CastInfoTargetBlacklistInput)))
                {
                    if (ImGui.Button(GetLoc("Confirm")))
                    {
                        ModuleConfig.CastInfoTargetBlacklist.Add(CastInfoTargetBlacklistInput);
                        ModuleConfig.Save(this);
                        
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        var counter = 1;
        foreach (var blacklist in ModuleConfig.CastInfoTargetBlacklist)
        {
            using (ImRaii.PushId($"CastInfoTargetBlacklist-{blacklist}"))
            {
                if (ImGui.Button(GetLoc("Delete")))
                {
                    ModuleConfig.CastInfoTargetBlacklist.Remove(blacklist);
                    ModuleConfig.Save(this);
                    
                    break;
                }
                
                ImGui.SameLine();
                ImGui.Text($"{counter}. {blacklist}");

                counter++;
            }
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                MakeTextNodesAndLink();
                break;
            case AddonEvent.PreRequestedUpdate:
                MakeTextNodesAndLink();
                UpdateTextNodes();
                break;
            case AddonEvent.PostDraw:
                if (!Throttler.Throttle("OptimizedEnemyList-OnPostDraw")) break;
                UpdateTextNodes();
                break;
        }
    }

    private static void UpdateTextNodes()
    {
        var nodes = TextNodes;
        
        var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.EnemyList);
        if (numberArray == null) return;

        if (Throttler.Throttle("OptimizedEnemyList-UpdateHaterInfo"))
        {
            HaterInfo = UIState.Instance()->Hater.Haters.ToArray()
                                                .Where(x => x.EntityId != 0 && x.EntityId != 0xE0000000)
                                                .DistinctBy(x => x.EntityId)
                                                .ToDictionary(x => x.EntityId, x => x.Enmity);
        }
        
        var castWidth  = stackalloc ushort[1];
        var castHeight = stackalloc ushort[1];

        for (var i = 0; i < nodes.Count; i++)
        {
            var offset = 8 + (i * 6);
            
            var gameObjectID = (ulong)numberArray->IntArray[offset];
            if (gameObjectID is 0 or 0xE0000000) continue;

            var textNode       = nodes[i].TextNode;
            var backgroundNode = nodes[i].BackgroundNode;
            
            var gameObj = DService.ObjectTable.SearchById(gameObjectID);
            if (gameObj is not IBattleChara bc || !HaterInfo.TryGetValue(gameObj.EntityId, out var enmity))
            {
                textNode.Text            = string.Empty;
                backgroundNode.IsVisible = false;
                continue;
            }
            
            var componentNode = (AtkComponentNode*)nodes[i].ComponentPtr;
            
            var castTextNode = componentNode->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            var targetNameTextNode = componentNode->Component->UldManager.SearchNodeById(6)->GetAsAtkTextNode();
            if (targetNameTextNode == null) continue;
            
            targetNameTextNode->GetTextDrawSize(castWidth, castHeight);
            
            if (bc.IsCasting)
            {
                castTextNode->SetAlpha(0);

                var castBackgroundNode = componentNode->Component->UldManager.SearchNodeById(5);
                if (castBackgroundNode != null) 
                    castBackgroundNode->SetAlpha(0);
            }

            var targetName = SanitizeSeIcon(targetNameTextNode->NodeText.ExtractText());
            
            textNode.TextColor = ModuleConfig.UseCustomizeTextColor
                                      ? ModuleConfig.TextColor
                                      : ConvertByteColorToVector4(castTextNode->TextColor);
            textNode.TextOutlineColor = ModuleConfig.UseCustomizeTextColor
                                     ? ModuleConfig.EdgeColor
                                     : ConvertByteColorToVector4(castTextNode->EdgeColor);
            textNode.BackgroundColor = ModuleConfig.UseCustomizeTextColor
                                            ? ModuleConfig.BackgroundColor
                                            : ConvertByteColorToVector4(castTextNode->BackgroundColor);
            
            textNode.FontSize = ModuleConfig.FontSize;
            
            if (bc.IsCasting && !ModuleConfig.CastInfoTargetBlacklist.Contains(targetName))
            {
                var castTimeLeft = MathF.Max(bc.TotalCastTime - bc.CurrentCastTime, 0f);
                
                textNode.Text            = $"{GetCastInfoText(bc.CastActionType, bc.CastActionId)}: " + (castTimeLeft != 0 ? $"{castTimeLeft:F1}" : "\ue07f\ue07b");
                backgroundNode.IsVisible = true;
            }            
            else if (!bc.IsTargetable && bc.CurrentHp == bc.MaxHp)
            {
                textNode.Text            = string.Empty;
                backgroundNode.IsVisible = false;
            }
            else
            {
                textNode.Text            = GetGeneralInfoText((float)bc.CurrentHp / bc.MaxHp * 100, enmity);
                backgroundNode.IsVisible = true;
            }
            
            textNode.Position = new(Math.Max(95, *castWidth + 28) + ModuleConfig.TextOffset.X, 4 + ModuleConfig.TextOffset.Y);

            var textSize = textNode.GetTextDrawSize(textNode.Text);
            
            backgroundNode.Position = new(Math.Max(68, *castWidth + 1) + (bc.IsCasting ? 12.5f : 0) + ModuleConfig.TextOffset.X, 6 + ModuleConfig.TextOffset.Y);
            backgroundNode.Size     = new(textSize.X                   + 47                     + (bc.IsCasting ? -18 : 0), textSize.Y * 0.7f);
        }
    }
    
    private static void MakeTextNodesAndLink()
    {
        if (EnemyList == null) return;
        if (!TryFindButtonNodes(out var buttonNodesPtr) || TextNodes.Count == buttonNodesPtr.Count) return;

        foreach (var nodePtr in buttonNodesPtr)
        {
            var node = (AtkComponentNode*)nodePtr;
            
            var castTextNode = node->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            var textNode = new TextNode
            {
                Text            = string.Empty,
                FontSize        = ModuleConfig.FontSize,
                IsVisible       = true,
                Size            = new(160f, 25f),
                AlignmentType   = AlignmentType.TopLeft,
                Position        = new(100, 5),
                TextFlags       = TextFlags.Edge    | TextFlags.Emboss,
                NodeFlags       = NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.AnchorTop | NodeFlags.AnchorLeft,
                LineSpacing     = 20,
            };

            // 需要把 STP 显示 UVWH 都除以 2
            var backgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/TextInputA.tex",
                TextureCoordinates = new(24, 0),
                TextureSize        = new(24, 24),
                Size               = new(160, 10),
                IsVisible          = true,
                Color              = Black,
                Position           = new(75, 6),
                Alpha              = 0.6f,

            };
            
            TextNodes.Add(new((nint)node, textNode, backgroundNode));
            
            Service.AddonController.AttachNode(backgroundNode, node);
            Service.AddonController.AttachNode(textNode, node);
        }
    }
    
    private static string GetGeneralInfoText(float percentage, int enmity) =>
        ModuleConfig.UseCustomizeText
            ? string.Format(ModuleConfig.CustomizeTextPattern, percentage.ToString("F1"), enmity)
            : $"{LuminaWrapper.GetAddonText(232)}: {percentage:F1}% / {LuminaWrapper.GetAddonText(721)}: {enmity}%";

    private static string GetCastInfoText(ActionType type, uint actionID)
    {
        var result = string.Empty;
        
        switch (type)
        {
            case ActionType.Action:
                result = LuminaWrapper.GetActionName(actionID);
                break;
        }

        if (string.IsNullOrWhiteSpace(result))
            result = LuminaWrapper.GetAddonText(1032);
        
        return result;
    }
    
    private static bool TryFindButtonNodes(out List<nint> nodes)
    {
        nodes = [];
        if (EnemyList == null) return false;
        
        for (var i = 4; i < EnemyList->UldManager.NodeListCount; i++)
        {
            var node = EnemyList->UldManager.NodeList[i];
            if (node == null || (ushort)node->Type != 1001) continue;
            
            var buttonNode = node->GetAsAtkComponentButton();
            if (buttonNode == null) continue;

            nodes.Add((nint)node);
        }

        nodes.Reverse();
        return nodes.Count > 0;
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);

        foreach (var (_, textNode, backgroundNode) in TextNodes)
        {
            Service.AddonController.DetachNode(textNode);
            Service.AddonController.DetachNode(backgroundNode);
        }
        TextNodes.Clear();
        
        HaterInfo.Clear();
    }

    private class Config : ModuleConfiguration
    {
        public Vector2 TextOffset = Vector2.Zero;
        
        public bool   UseCustomizeText;
        public string CustomizeTextPattern = @"HP: {0}% / Enmity: {1}%";

        public bool    UseCustomizeTextColor;
        public Vector4 TextColor = new(1, 1, 1, 1);
        public Vector4 EdgeColor = new(0.6157f, 0.5137f, 0.3569f, 1);
        public Vector4 BackgroundColor = new(0, 0, 0, 0);

        public byte FontSize = 10;

        public HashSet<string> CastInfoTargetBlacklist = [];
    }
}
