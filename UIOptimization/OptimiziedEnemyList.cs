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
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimiziedEnemyList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("OptimiziedEnemyListTitle"),
        Description = GetLoc("OptimiziedEnemyListDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static Config ModuleConfig = null!;

    private static Dictionary<uint, int> HaterInfo = [];

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

        if (ImGui.Checkbox(GetLoc("OptimiziedEnemyList-UseCustomColor"), ref ModuleConfig.UseCustomizeTextColor))
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

        if (ImGui.Checkbox(GetLoc("OptimiziedEnemyList-UseCustomGeneralInfo"), ref ModuleConfig.UseCustomizeText))
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
        ImGui.TextColored(LightSkyBlue, GetLoc("OptimiziedEnemyList-CastInfoDisplayTargetBlacklist"));
        ImGuiOm.HelpMarker(GetLoc("OptimiziedEnemyList-CastInfoDisplayTargetBlacklistHelp"));
        
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
                UpdateTextNodes();
                break;
            case AddonEvent.PostDraw:
                if (!Throttler.Throttle("OptimiziedEnemyList-OnPostDraw")) break;
                UpdateTextNodes();
                break;
        }
    }

    private static void UpdateTextNodes()
    {
        if (!TryFindMadeTextNodes(out var nodes)) return;

        var numberArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.EnemyList);
        if (numberArray == null) return;

        if (Throttler.Throttle("OptimiziedEnemyList-UpdateHaterInfo"))
        {
            HaterInfo = UIState.Instance()->Hater.Haters.ToArray()
                                                .Where(x => x.EntityId != 0 && x.EntityId != 0xE0000000)
                                                .DistinctBy(x => x.EntityId)
                                                .ToDictionary(x => x.EntityId, x => x.Enmity);
        }

        var castWidth  = stackalloc ushort[1];
        var castHeight = stackalloc ushort[1];
        
        var infoWidth = stackalloc ushort[1];
        var infoHeight = stackalloc ushort[1];
        
        for (var i = 0; i < nodes.Count; i++)
        {
            var offset       = 8 + (i * 6);
            
            var gameObjectID = (ulong)numberArray->IntArray[offset];
            if (gameObjectID is 0 or 0xE0000000) continue;
            
            var textNode      = (AtkTextNode*)nodes[i].TextNodePtr;
            
            var gameObj = DService.ObjectTable.SearchById(gameObjectID);
            if (gameObj is not IBattleChara bc || !HaterInfo.TryGetValue(gameObj.EntityId, out var enmity))
            {
                textNode->SetText(string.Empty);
                continue;
            }
            
            var componentNode = (AtkComponentNode*)nodes[i].ComponentNodePtr;
            
            var castTextNode = componentNode->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            var targetNameTextNode = componentNode->Component->UldManager.SearchNodeById(6)->GetAsAtkTextNode();
            if (targetNameTextNode == null) continue;
            
            if (bc.IsCasting)
            {
                castTextNode->SetAlpha(0);

                var castBackgroundNode = componentNode->Component->UldManager.SearchNodeById(5);
                if (castBackgroundNode != null) 
                    castBackgroundNode->SetAlpha(0);
            }

            var targetName = SanitizeSeIcon(targetNameTextNode->NodeText.ExtractText());
            targetNameTextNode->GetTextDrawSize(castWidth, castHeight);
            
            textNode->TextColor = ModuleConfig.UseCustomizeTextColor
                                      ? ConvertVector4ToByteColor(ModuleConfig.TextColor)
                                      : castTextNode->TextColor;
            textNode->EdgeColor = ModuleConfig.UseCustomizeTextColor
                                      ? ConvertVector4ToByteColor(ModuleConfig.EdgeColor)
                                      : castTextNode->EdgeColor;
            textNode->BackgroundColor = ModuleConfig.UseCustomizeTextColor
                                            ? ConvertVector4ToByteColor(ModuleConfig.BackgroundColor)
                                            : castTextNode->BackgroundColor;
            
            textNode->FontSize = ModuleConfig.FontSize;
            
            if (bc.IsCasting && bc.CurrentCastTime != bc.TotalCastTime && !ModuleConfig.CastInfoTargetBlacklist.Contains(targetName))
                textNode->SetText($"{GetCastInfoText(bc.CastActionType, bc.CastActionId)}: {bc.TotalCastTime - bc.CurrentCastTime:F1}");
            else if (!bc.IsTargetable && bc.CurrentHp == bc.MaxHp)
                textNode->SetText(string.Empty);
            else
                textNode->SetText(GetGeneralInfoText((float)bc.CurrentHp / bc.MaxHp * 100, enmity));
            
            textNode->GetTextDrawSize(infoWidth, infoHeight);
            textNode->SetPositionFloat(Math.Max(90f, *castWidth + 28) + ModuleConfig.TextOffset.X, 4 + ModuleConfig.TextOffset.Y);
        }
    }
    
    private static void MakeTextNodesAndLink()
    {
        if (EnemyList == null) return;
        if (!TryFindButtonNodes(out var buttonNodesPtr) || TryFindMadeTextNodes(out _)) return;

        foreach (var nodePtr in buttonNodesPtr)
        {
            var node = (AtkComponentNode*)nodePtr;
            
            var castTextNode = node->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;
            
            var textNode = MakeTextNode(10001);
            
            textNode->NodeFlags   = NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.AnchorTop | NodeFlags.AnchorLeft;
            textNode->DrawFlags   = castTextNode->DrawFlags;
            textNode->Alpha_2     = 255;
            textNode->LineSpacing = 20;
            textNode->FontSize    = ModuleConfig.FontSize;
            textNode->TextFlags   = (byte)(TextFlags.AutoAdjustNodeSize | TextFlags.Edge | TextFlags.Bold | TextFlags.Emboss);
            
            textNode->SetPositionFloat(80, 5);
            textNode->SetAlignment(AlignmentType.TopLeft);
            
            LinkNodeAtEnd((AtkResNode*)textNode, node->Component);
        }
    }

    private static void FreeNodes()
    {
        if (EnemyList == null) return;
        if (!TryFindMadeTextNodes(out var nodes)) return;
        
        foreach (var node in nodes)
        {
            var textNode      = (AtkTextNode*)node.TextNodePtr;
            var componentNode = (AtkComponentNode*)node.ComponentNodePtr;
            UnlinkAndFreeTextNode(textNode, componentNode);
            
            var castTextNode = componentNode->Component->UldManager.SearchNodeById(4);
            if (castTextNode != null) 
                castTextNode->SetAlpha(255);
            
            var castBackgroundNode = componentNode->Component->UldManager.SearchNodeById(5);
            if (castBackgroundNode != null) 
                castBackgroundNode->SetAlpha(255);
        }
    }

    private static string GetGeneralInfoText(float percentage, int enmity) =>
        ModuleConfig.UseCustomizeText
            ? string.Format(ModuleConfig.CustomizeTextPattern, percentage.ToString("F1"), enmity)
            : $"{LuminaGetter.GetRow<Addon>(232)!.Value.Text.ExtractText()}: {percentage:F1}% / {LuminaGetter.GetRow<Addon>(721)!.Value.Text.ExtractText()}: {enmity}%";

    private static string GetCastInfoText(ActionType type, uint actionID)
    {
        var result = string.Empty;
        
        switch (type)
        {
            case ActionType.Action:
                if (!LuminaGetter.TryGetRow<Action>(actionID, out var row)) break;
                result = row.Name.ExtractText();
                break;
        }

        if (string.IsNullOrWhiteSpace(result))
            result = LuminaGetter.GetRow<Addon>(1032)!.Value.Text.ExtractText();
        
        return result;
    }
    
    /// <returns>键: Component Node, 值: TextNode</returns>
    private static bool TryFindMadeTextNodes(out List<(nint ComponentNodePtr, nint TextNodePtr)> nodes)
    {
        nodes = [];
        if (!TryFindButtonNodes(out var buttonNodesPtr)) return false;
        
        foreach (var ptr in buttonNodesPtr)
        {
            var buttonNode = (AtkComponentNode*)ptr;
            if (buttonNode == null) continue;

            for (var d = 0; d < buttonNode->Component->UldManager.NodeListCount; d++)
            {
                var nodeInternal = buttonNode->Component->UldManager.NodeList[d];
                if (nodeInternal->NodeId == 10001)
                    nodes.Add((ptr, (nint)nodeInternal));
            }
        }
        
        return nodes.Count > 0;
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
        FreeNodes();
        
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
