using System.Collections;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node.Simple;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedEnemyList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = Lang.Get("OptimizedEnemyListTitle"),
        Description     = Lang.Get("OptimizedEnemyListDescription"),
        Category        = ModuleCategory.UIOptimization,
        PreviewImageURL = ["https://gh.atmoomen.top/raw.githubusercontent.com/AtmoOmen/StaticAssets/main/DailyRoutines/image/OptimizedEnemyList-UI.png"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private delegate        void                                   AgentHudUpdateEnemyListDelegate(AgentHUD* agent);
    private static readonly CompSig                                AgentHudUpdateEnemyListSig = new("40 55 57 41 56 48 81 EC ?? ?? ?? ?? 4C 8B F1");
    // ReSharper disable once InconsistentNaming
    private                 Hook<AgentHudUpdateEnemyListDelegate>? AgentHudUpdateEnemyListHook;
    
    private Config             config = null!;
    private OverlayController? controller;

    private          Dictionary<uint, int> haterInfo = [];
    private readonly List<EnemyListNode>   nodes     = [];
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        controller ??= new();

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

        haterInfo.Clear();

        controller?.Dispose();
        controller = null;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputFloat2($"{Lang.Get("Offset")}###TextOffsetInput", ref config.TextOffset, format: "%.1f");

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        ImGui.InputByte($"{Lang.Get("FontSize")}###FontSize", ref config.FontSize);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        config.TextColor = ImGuiComponents.ColorPickerWithPalette(0, "###TextColorInput", config.TextColor);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Lang.Get("Color")} ({Lang.Get("Text")})");

        config.TextEdgeColor = ImGuiComponents.ColorPickerWithPalette(1, "###EdgeColorInput", config.TextEdgeColor);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{Lang.Get("EdgeColor")} ({Lang.Get("Text")})");

        ImGui.SetNextItemWidth(200f * GlobalUIScale);
        if (ImGui.SliderFloat($"{Lang.Get("Alpha")} ({Lang.Get("Background")})", ref config.BackgroundAlpha, 0, 1, "%.1f"))
            config.BackgroundAlpha = Math.Clamp(config.BackgroundAlpha, 0, 1);

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, $"{Lang.Get("Save")}"))
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Redo, $"{Lang.Get("Reset")}"))
        {
            var newConfig = new Config();
            config.TextColor       = newConfig.TextColor;
            config.TextEdgeColor   = newConfig.TextEdgeColor;
            config.BackgroundAlpha = newConfig.BackgroundAlpha;

            config.Save(this);
        }

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("OptimizedEnemyList-UseCustomGeneralInfo"), ref config.UseCustomizeText))
            config.Save(this);

        if (config.UseCustomizeText)
        {
            using (ImRaii.PushIndent())
            using (ImRaii.ItemWidth(300f * GlobalUIScale))
            {
                ImGui.InputText($"{Lang.Get("General")}##CustomizeTextPatternInput", ref config.CustomTextPattern);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                ImGui.InputText($"{LuminaWrapper.GetAddonText(1032)}##CustomizeCastTextPatternInput", ref config.CustomCastTextPattern);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }

        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostRequestedUpdate:
                UpdateTextNodes();
                break;

            case AddonEvent.PostDraw:
                if (!DService.Instance().Condition[ConditionFlag.InCombat] ||
                    !Throttler.Shared.Throttle("OptimizedEnemyList-OnAddonDraw", 10))
                    return;

                UpdateTextNodes();
                break;

            case AddonEvent.PreFinalize:
                DLog.Debug("调用清除");
                ClearTextNodes();
                break;
        }
    }

    private void AgentHudUpdateEnemyListDetour(AgentHUD* agent)
    {
        AgentHudUpdateEnemyListHook.Original(agent);
        UpdateHaterInfo();
    }

    private void UpdateTextNodes()
    {
        if (!EnemyList->IsAddonAndNodesReady()) return;

        var enemyListArray = EnemyListNumberArray.Instance();
        if (enemyListArray == null) return;

        if (enemyListArray->EnemyCount == 0) return;

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

            if (gameObj == null || !haterInfo.TryGetValue(gameObj->EntityId, out var enmity))
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

                if (status.StatusId           == 0) continue;
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

            textNode.TextColor        = config.TextColor;
            textNode.TextOutlineColor = config.TextEdgeColor;
            backgroundNode.Alpha      = config.BackgroundAlpha;

            textNode.FontSize = config.FontSize;

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
                    castBarNode.X         + 7f
                ) +
                config.TextOffset.X,
                4 + config.TextOffset.Y
            );

            if (!string.IsNullOrWhiteSpace(textNode.String.ToString()))
            {
                backgroundNode.Position = textNode.Position + new Vector2(-7f, -5f);
                backgroundNode.Size     = new(textNode.Width + 14f + textNode.FontSize - 10, (textNode.FontSize + 2) * 2);
            }
        }
    }

    private void CreateTextNodes()
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
                FontSize      = config.FontSize,
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
            controller.AddNode(statusNodes);

            nodes.Add(new(node->NodeId, textNode, backgroundNode, castBarNode, statusNodes));
        }
    }

    private void ClearTextNodes()
    {
        foreach (var (_, textNode, backgroundNode, castBarNode, statusNodes) in nodes)
        {
            textNode?.Dispose();
            backgroundNode?.Dispose();
            castBarNode?.Dispose();

            foreach (var statusNode in statusNodes)
                statusNode?.Dispose();
            statusNodes?.Dispose();
        }

        nodes.Clear();
    }

    private void UpdateHaterInfo()
    {
        var hater = UIState.Instance()->Hater;
        haterInfo = hater.Haters
                         .ToArray()
                         .Take(hater.HaterCount)
                         .Where(x => x.EntityId != 0 && x.EntityId != 0xE0000000)
                         .DistinctBy(x => x.EntityId)
                         .ToDictionary(x => x.EntityId, x => x.Enmity);
    }

    private string GetGeneralInfoText(float percentage, int enmity) =>
        config.UseCustomizeText
            ? string.Format(config.CustomTextPattern, percentage.ToString("F1"), enmity.ToString())
            : $"{LuminaWrapper.GetAddonText(232)}: {percentage:F1}% / {LuminaWrapper.GetAddonText(721)}: {enmity.ToString()}%";

    private string GetCastInfoText(ActionType type, uint actionID, float remainingTime, float percentage)
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

        if (config.UseCustomizeText)
        {
            return string.Format
            (
                config.CustomCastTextPattern,
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
    
    private class Config : ModuleConfig
    {
        public float  BackgroundAlpha       = 0.6f;
        public string CustomCastTextPattern = @"{0}: {1} / HP: {2}%";
        public string CustomTextPattern     = @"HP: {0}% / Enmity: {1}%";

        public bool DisplayStatus = true;
        public byte FontSize      = 10;

        public Vector4 TextColor     = Vector4.One;
        public Vector4 TextEdgeColor = new(0, 0.372549f, 1, 1);
        public Vector2 TextOffset    = Vector2.Zero;

        public bool UseCustomizeText;
    }

    private sealed class EnemyListNode
    (
        uint                     componentNodeID,
        TextNode                 textNode,
        NineGridNode             backgroundNode,
        ProgressBarEnemyCastNode castBarNode,
        IconTextNodesRow         statusNodes
    )
    {
        public uint                      ComponentNodeID { get; set; } = componentNodeID;
        public TextNode?                 TextNode        { get; set; } = textNode;
        public NineGridNode?             BackgroundNode  { get; set; } = backgroundNode;
        public ProgressBarEnemyCastNode? CastBarNode     { get; set; } = castBarNode;
        public IconTextNodesRow?         StatusNodes     { get; set; } = statusNodes;
        
        public void Deconstruct
        (
            out uint                      componentNodeID,
            out TextNode?                 textNode,
            out NineGridNode?             backgroundNode,
            out ProgressBarEnemyCastNode? castBarNode,
            out IconTextNodesRow?         statusNodes
        )
        {
            componentNodeID = ComponentNodeID;
            textNode        = TextNode;
            backgroundNode  = BackgroundNode;
            castBarNode     = CastBarNode;
            statusNodes     = StatusNodes;
        }
    }

    private class IconTextNodesRow : OverlayNode, IEnumerable<IconTextNode>
    {
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

        public int  Count  { get; init; }
        public uint NodeID { get; init; }

        public int Index { get; init; }

        public override OverlayLayer OverlayLayer     => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi => true;

        public bool ShouldBeVisible { get; set; }

        public List<IconTextNode> Nodes { get; init; } = [];

        public IconTextNode this[int index] => Nodes[index];

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
