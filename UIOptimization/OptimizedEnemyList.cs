using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedEnemyList : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("OptimizedEnemyListTitle"),
        Description     = GetLoc("OptimizedEnemyListDescription"),
        Category        = ModuleCategories.UIOptimization,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/OptimizedEnemyList-UI.png"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private static readonly CompSig                                AgentHudUpdateEnemyListSig = new("40 55 57 41 56 48 81 EC ?? ?? ?? ?? 4C 8B F1");
    private delegate        void                                   AgentHudUpdateEnemyListDelegate(AgentHUD* agent);
    private static          Hook<AgentHudUpdateEnemyListDelegate>? AgentHudUpdateEnemyListHook;

    private static Config ModuleConfig = null!;

    private static Dictionary<uint, int> HaterInfo = [];

    private static readonly
        List<(
            uint ComponentNodeID,
            TextNode TextNode,
            NineGridNode BackgroundNode,
            ProgressBarEnemyCastNode CastBarNode,
            IconTextNodesRow StatusNodes
            )>
        TextNodes = [];
    
    private static OverlayController? Controller;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Controller ??= new();

        AgentHudUpdateEnemyListHook ??= AgentHudUpdateEnemyListSig.GetHook<AgentHudUpdateEnemyListDelegate>(AgentHudUpdateEnemyListDetour);
        AgentHudUpdateEnemyListHook.Enable();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,            "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "_EnemyList", OnAddon);
    }
    
    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        ClearTextNodes();

        HaterInfo.Clear();

        Controller?.Dispose();
        Controller = null;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputFloat2($"{GetLoc("Offset")}###TextOffsetInput", ref ModuleConfig.TextOffset, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        ImGui.InputByte($"{GetLoc("FontSize")}###FontSize", ref ModuleConfig.FontSize);
        if (ImGui.IsItemDeactivatedAfterEdit())
            ModuleConfig.Save(this);

        ImGui.NewLine();

        ModuleConfig.TextColor = ImGuiComponents.ColorPickerWithPalette(0, "###TextColorInput", ModuleConfig.TextColor);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{GetLoc("Color")} ({GetLoc("Text")})");

        ModuleConfig.TextEdgeColor = ImGuiComponents.ColorPickerWithPalette(1, "###EdgeColorInput", ModuleConfig.TextEdgeColor);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{GetLoc("EdgeColor")} ({GetLoc("Text")})");
                
        ImGui.SetNextItemWidth(200f * GlobalFontScale);
        if (ImGui.SliderFloat($"{GetLoc("Alpha")} ({GetLoc("Background")})", ref ModuleConfig.BackgroundAlpha, 0, 1, "%.1f"))
            ModuleConfig.BackgroundAlpha = Math.Clamp(ModuleConfig.BackgroundAlpha, 0, 1);
                
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, $"{GetLoc("Save")}"))
            ModuleConfig.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Redo, $"{GetLoc("Reset")}"))
        {
            var newConfig = new Config();
            ModuleConfig.TextColor       = newConfig.TextColor;
            ModuleConfig.TextEdgeColor       = newConfig.TextEdgeColor;
            ModuleConfig.BackgroundAlpha = newConfig.BackgroundAlpha;

            ModuleConfig.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(GetLoc("OptimizedEnemyList-UseCustomGeneralInfo"), ref ModuleConfig.UseCustomizeText))
            ModuleConfig.Save(this);

        if (ModuleConfig.UseCustomizeText)
        {
            using (ImRaii.PushIndent())
            using (ImRaii.ItemWidth(300f * GlobalFontScale))
            {
                ImGui.InputText($"{GetLoc("General")}##CustomizeTextPatternInput", ref ModuleConfig.CustomTextPattern);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(this);
                
                ImGui.InputText($"{LuminaWrapper.GetAddonText(1032)}##CustomizeCastTextPatternInput", ref ModuleConfig.CustomCastTextPattern);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    ModuleConfig.Save(this);
            }
            
        }
    }

    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostRequestedUpdate:
                UpdateTextNodes();
                break;

            case AddonEvent.PostDraw:
                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Throttle("OptimizedEnemyList-OnAddonDraw", 10))
                    return;

                UpdateTextNodes();
                break;

            case AddonEvent.PreFinalize:
                ClearTextNodes();
                break;
        }
    }

    private static void AgentHudUpdateEnemyListDetour(AgentHUD* agent)
    {
        AgentHudUpdateEnemyListHook.Original(agent);
        UpdateHaterInfo();
    }

    private static void UpdateTextNodes()
    {
        if (!EnemyList->IsAddonAndNodesReady()) return;
        
        var enemyListArray = EnemyListNumberArray.Instance();
        if (enemyListArray == null) return;

        if (enemyListArray->EnemyCount == 0) return;
        
        var nodes = TextNodes;
        if (nodes is not { Count: > 0 })
        {
            CreateTextNodes();
            return;
        }

        var hudArray = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.Hud2);
        if (hudArray == null) return;

        var isTargetCasting = hudArray->IntArray[69] != -1;
        
        for (var i = 0; i < MathF.Min(enemyListArray->EnemyCount, nodes.Count); i++)
        {
            var info = enemyListArray->Enemies[i];

            var entityID = (uint)info.EntityId;
            if (entityID is 0 or 0xE0000000) continue;

            var textNode       = nodes[i].TextNode;
            var backgroundNode = nodes[i].BackgroundNode;
            var castBarNode    = nodes[i].CastBarNode;
            var statusNodes    = nodes[i].StatusNodes;

            var gameObj = CharacterManager.Instance()->LookupBattleCharaByEntityId(entityID);
            if (gameObj == null || !HaterInfo.TryGetValue(gameObj->EntityId, out var enmity))
            {
                textNode.String             = string.Empty;
                backgroundNode.IsVisible    = false;
                statusNodes.ShouldBeVisible = false;
                continue;
            }

            var componentNode = EnemyList->GetComponentNodeById(nodes[i].ComponentNodeID);
            if (componentNode == null)
            {
                CreateTextNodes();
                return;
            }

            var castTextNode = componentNode->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            var targetNameTextNode = componentNode->Component->UldManager.SearchNodeById(6)->GetAsAtkTextNode();
            if (targetNameTextNode == null) continue;

            var origCastBarNode         = componentNode->Component->UldManager.SearchNodeById(7);
            var origCastBarProgressNode = componentNode->Component->UldManager.SearchNodeById(8);
            if (origCastBarNode == null || origCastBarProgressNode == null) continue;

            statusNodes.Scale    = componentNode->GetScale()             - new Vector2(0.1f);
            statusNodes.Position = componentNode->GetNodeState().TopLeft - new Vector2(0, 1) * statusNodes.Scale;
            statusNodes.Alpha    = info.ActiveInList ? 1f : 0.5f;

            var counter = 0;

            foreach (var status in gameObj->StatusManager.Status)
            {
                if (counter == 5) break;

                if (status.StatusId == 0) continue;
                if ((uint)status.SourceObject != LocalPlayerState.EntityID) continue;

                var node = statusNodes[counter];
                node.IsVisible = true;
                node.Update(status);

                counter++;
            }

            if (counter < 5)
            {
                for (var d = counter; d < 5; d++)
                    statusNodes[d].IsVisible = false;
            }

            statusNodes.ShouldBeVisible = counter > 0;
            
            var isCasting = gameObj->IsCasting || isTargetCasting && (nint)gameObj == (TargetManager.Target?.Address ?? nint.Zero);
            if (isCasting)
            {
                origCastBarNode->SetAlpha(0);
                origCastBarProgressNode->SetAlpha(0);
                castTextNode->SetAlpha(0);

                var castBackgroundNode = componentNode->Component->UldManager.SearchNodeById(5);
                if (castBackgroundNode != null)
                    castBackgroundNode->SetAlpha(0);

                castBarNode.IsVisible          = true;
                castBarNode.ProgressNode.Width = 105 * (gameObj->CastInfo.CurrentCastTime / gameObj->CastInfo.TotalCastTime);

                if (gameObj->CastInfo.Interruptible)
                    castBarNode.AddColor = KnownColor.Red.ToVector4().ToVector3();
                else
                    castBarNode.AddColor = KnownColor.Yellow.ToVector4().ToVector3() / 255f;
            }
            else
            {
                castBarNode.IsVisible = false;
                castBarNode.Progress  = 0f;
            }
            
            textNode.TextColor        = ModuleConfig.TextColor;
            textNode.TextOutlineColor = ModuleConfig.TextEdgeColor;
            backgroundNode.Alpha      = ModuleConfig.BackgroundAlpha;

            textNode.FontSize = ModuleConfig.FontSize;

            var healthPercentage = (float)gameObj->Health / gameObj->MaxHealth * 100f;
            if (isCasting)
            {
                var castTimeLeft = MathF.Max(gameObj->CastInfo.TotalCastTime - gameObj->CastInfo.CurrentCastTime, 0f);

                textNode.String = $"{GetCastInfoText((ActionType)gameObj->CastInfo.ActionType, gameObj->CastInfo.ActionId, castTimeLeft, healthPercentage)}";
                backgroundNode.IsVisible = true;
            }
            else if (!gameObj->GetIsTargetable() && gameObj->Health == gameObj->MaxHealth)
            {
                textNode.String          = string.Empty;
                backgroundNode.IsVisible = false;
            }
            else
            {
                textNode.String          = GetGeneralInfoText((float)gameObj->Health / gameObj->MaxHealth * 100, enmity);
                backgroundNode.IsVisible = true;
            }

            textNode.Position = new
            (
                MathF.Max
                (
                    targetNameTextNode->X + targetNameTextNode->GetTextDrawSize().X + 5f,
                    castBarNode.X + 7f
                ) + ModuleConfig.TextOffset.X,
                4 + ModuleConfig.TextOffset.Y
            );

            if (!string.IsNullOrWhiteSpace(textNode.String.ToString()))
            {
                backgroundNode.Position = textNode.Position + new Vector2(-7f, -5f);
                backgroundNode.Size     = new(textNode.Width + 14f + textNode.FontSize - 10, (textNode.FontSize + 2) * 2);
            }
        }
    }

    private static void CreateTextNodes()
    {
        if (EnemyList == null) return;
        if (!TryFindButtonNodes(out var buttonNodesPtr)) return;

        ClearTextNodes();

        var counter = -1;
        foreach (var nodePtr in buttonNodesPtr)
        {
            var node = (AtkComponentNode*)nodePtr;

            var castTextNode = node->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            counter++;

            var textNode = new TextNode
            {
                String        = string.Empty,
                FontSize      = ModuleConfig.FontSize,
                IsVisible     = true,
                TextFlags     = TextFlags.Edge | TextFlags.Emboss | TextFlags.AutoAdjustNodeSize,
                AlignmentType = AlignmentType.TopLeft,
                Position      = new(100, 5),
                NodeFlags     = NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.AnchorTop | NodeFlags.AnchorLeft,
                LineSpacing   = 20
            };

            var backgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(124, 20),
                Offsets            = new(0, 0, 8, 8),
                IsVisible          = true,
                MultiplyColor      = new(100),
                Position           = new(75, 6),
                Alpha              = 0.6f,
                NodeFlags          = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.EmitsEvents | NodeFlags.Enabled
            };

            var castBarNode = new ProgressBarEnemyCastNode
            {
                IsVisible = true,
                Position  = new(85, 13.7f),
                Size      = new(120, 20)
            };

            castBarNode.ProgressNode.Height   -= 12f;
            castBarNode.ProgressNode.Position += new Vector2(7.7f, 6.5f);
            castBarNode.ProgressNode.AddColor =  new(1);

            backgroundNode.AttachNode(node);
            textNode.AttachNode(node);
            castBarNode.AttachNode(node);

            var statusNodes = new IconTextNodesRow(5, node->NodeId, counter);
            Controller.AddNode(statusNodes);

            TextNodes.Add(new(node->NodeId, textNode, backgroundNode, castBarNode, statusNodes));
        }
    }

    private static void ClearTextNodes()
    {
        foreach (var (_, textNode, backgroundNode, castBarNode, statusNodes) in TextNodes)
        {
            textNode?.Dispose();
            backgroundNode?.Dispose();
            castBarNode?.Dispose();

            foreach (var statusNode in statusNodes)
                statusNode?.Dispose();
            statusNodes?.Dispose();
        }

        TextNodes.Clear();
    }

    private static void UpdateHaterInfo()
    {
        var hater = UIState.Instance()->Hater;
        HaterInfo = hater.Haters
                         .ToArray()
                         .Take(hater.HaterCount)
                         .Where(x => x.EntityId != 0 && x.EntityId != 0xE0000000)
                         .DistinctBy(x => x.EntityId)
                         .ToDictionary(x => x.EntityId, x => x.Enmity);
    }

    private static string GetGeneralInfoText(float percentage, int enmity) =>
        ModuleConfig.UseCustomizeText
            ? string.Format(ModuleConfig.CustomTextPattern, percentage.ToString("F1"), enmity.ToString())
            : $"{LuminaWrapper.GetAddonText(232)}: {percentage:F1}% / {LuminaWrapper.GetAddonText(721)}: {enmity.ToString()}%";

    private static string GetCastInfoText(ActionType type, uint actionID, float remainingTime, float percentage)
    {
        var actionName = string.Empty;

        switch (type)
        {
            case ActionType.Action:
                actionName = LuminaWrapper.GetActionName(actionID);
                break;
        }

        if (string.IsNullOrEmpty(actionName))
            actionName = LuminaWrapper.GetAddonText(1032);

        var timeText = remainingTime != 0 ? remainingTime.ToString("F1") : "\ue07f\ue07b";
        if (ModuleConfig.UseCustomizeText)
        {
            return string.Format
            (
                ModuleConfig.CustomCastTextPattern,
                actionName,
                timeText,
                percentage.ToString("F1")
            );
        }

        return $"{actionName}: {timeText} / {LuminaWrapper.GetAddonText(232)}: {percentage:F1}%";
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
    
    private class Config : ModuleConfiguration
    {
        public byte    FontSize   = 10;
        public Vector2 TextOffset = Vector2.Zero;

        public bool   UseCustomizeText;
        public string CustomTextPattern     = @"HP: {0}% / Enmity: {1}%";
        public string CustomCastTextPattern = @"{0}: {1} / HP: {2}%";

        public Vector4 TextColor       = Vector4.One;
        public Vector4 TextEdgeColor   = new(0, 0.372549f, 1, 1);
        public float   BackgroundAlpha = 0.6f;

        public bool DisplayStatus = true;
    }

    private class IconTextNodesRow : OverlayNode, IEnumerable<IconTextNode>
    {
        public int  Count  { get; init; }
        public uint NodeID { get; init; }
        
        public int  Index  { get; init; }

        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => true;

        public bool ShouldBeVisible { get; set; }

        public List<IconTextNode> Nodes { get; init; } = [];

        public IconTextNode this[int index] => Nodes[index];

        public IconTextNodesRow(int count, uint nodeID, int index)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);
            ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

            Count  = count;
            NodeID = nodeID;
            Index  = index;

            for (var i = 0; i < count; i++)
            {
                var statusNode = new IconTextNode
                {
                    Size     = new(25, 41),
                    Position = new(-25 + (-25 + -2) * i, 0)
                };
                statusNode.AttachNode(this);
                Nodes.Add(statusNode);
            }

            Size = new(25 + (25 + 2) * count, 41);
        }

        public IEnumerator<IconTextNode> GetEnumerator() => Nodes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        protected override void OnUpdate()
        {
            IsVisible = ShouldBeVisible                   &&
                        !GameState.IsInPVPInstance        &&
                        EnemyList->IsAddonAndNodesReady() &&
                        Index < EnemyListNumberArray.Instance()->EnemyCount;
        }
    }

    private class IconTextNode : SimpleComponentNode
    {
        public readonly IconImageNode IconNode;
        public readonly TextNode      TextNode;
        
        public IconTextNode()
        {
            IconNode = new()
            {
                NodeId         = 3,
                Size           = new(24, 32),
                ImageNodeFlags = ImageNodeFlags.AutoFit
            };
            IconNode.TextureSize = new(24, 32);
            IconNode.AttachNode(this);

            TextNode = new()
            {
                NodeId           = 2,
                Size             = new(24, 18),
                Position         = new(0, 23),
                TextFlags        = TextFlags.Edge,
                AlignmentType    = AlignmentType.Center,
                FontType         = FontType.Axis,
                FontSize         = 12,
                TextColor        = new(0.788f, 1.000f, 0.894f, 1.000f),
                TextOutlineColor = new(0.039f, 0.373f, 0.141f, 1.000f)
            };
            TextNode.AttachNode(this);
        }

        public void Update(Status status)
        {
            if (!LuminaGetter.TryGetRow(status.StatusId, out Lumina.Excel.Sheets.Status row)) return;
            
            IconNode.IconId = row.Icon;
            TextNode.SetNumber((int)status.RemainingTime);

            TextTooltip = $"{row.Name}\n{row.Description}";
        }
    }
}
