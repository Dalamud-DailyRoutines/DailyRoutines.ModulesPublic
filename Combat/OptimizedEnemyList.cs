using System.Collections;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node.Simple;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedEnemyList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedEnemyListTitle"),
        Description = Lang.Get("OptimizedEnemyListDescription"),
        Category    = ModuleCategory.Combat,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedEnemyList/preview-1.png"
        ],
        ModulesRecommend = ["AutoDisplayHiddenCast"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private readonly List<EnemyListNode> nodes = [];
    private          OverlayController?  controller;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,           "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,        "_EnemyList", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        foreach (var (_, textNode, heathTextNode, healthMarkerNode, backgroundNode, castBarNode, _) in nodes)
        {
            textNode?.Dispose();
            heathTextNode?.Dispose();
            healthMarkerNode?.Dispose();
            backgroundNode?.Dispose();
            castBarNode?.Dispose();
        }

        nodes.Clear();

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
        
        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Save, $"{Lang.Get("Save")}"))
            config.Save(this);

        ImGui.SameLine();

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Redo, $"{Lang.Get("Reset")}"))
        {
            var newConfig = new Config();
            config.TextColor       = newConfig.TextColor;
            config.TextEdgeColor   = newConfig.TextEdgeColor;

            config.Save(this);
        }
        
        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("OptimizedEnemyList-AlwaysUnlock"), ref config.AlwaysUnlock))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("OptimizedEnemyList-AlwaysUnlock-Help"));
    }

    #region 事件

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PreRequestedUpdate:
                if (config.AlwaysUnlock)
                {
                    var enemyListArray = EnemyListNumberArray.Instance();
                    for (var i = 0; i < enemyListArray->Enemies.Length; i++)
                        enemyListArray->Enemies[i].LockedInList = false;
                }

                UpdateNodes();
                break;
            
            case AddonEvent.PostDraw:
                if (!Throttler.Shared.Throttle("OptimizedEnemyList.UpdateEnemyList", 100))
                    return;
                
                UpdateNodes();
                break;

            case AddonEvent.PreFinalize:
                nodes.Clear();

                controller?.Dispose();
                controller = null;
                break;
        }
    }

    #endregion

    private void UpdateNodes()
    {
        var enemyListArray = EnemyListNumberArray.Instance();
        if (enemyListArray == null) return;

        if (enemyListArray->EnemyCount == 0) return;

        if (nodes is not { Count: > 0 })
        {
            CreateNodes();
            return;
        }

        for (var i = 0; i < MathF.Min(enemyListArray->EnemyCount, nodes.Count); i++)
        {
            var info = enemyListArray->Enemies[i];
            
            nodes[i].Deconstruct
            (
                out var componentNodeID,
                out var castNode,
                out var healthNode,
                out var healthMarkerNode,
                out var castBackgroundNode,
                out var castBarNode,
                out var statusNodes
            );

            var entityID = (uint)info.EntityId;
            if (entityID is 0 or 0xE0000000)
            {
                HideNodes();
                continue;
            }
            
            var gameObj = CharacterManager.Instance()->LookupBattleCharaByEntityId(entityID);
            if (gameObj == null)
            {
                HideNodes();
                continue;
            }

            #region 原生节点隐藏
            
            var componentNode = EnemyList->GetComponentNodeById(componentNodeID);
            if (componentNode == null)
            {
                HideNodes();
                continue;
            }

            var nativeCastNode = componentNode->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (nativeCastNode == null)
            {
                HideNodes();
                continue;
            }

            var nativeTargetNameNode = componentNode->Component->UldManager.SearchNodeById(6)->GetAsAtkTextNode();
            if (nativeTargetNameNode == null)
            {
                HideNodes();
                continue;
            }

            var nativeCastBarNode         = componentNode->Component->UldManager.SearchNodeById(7);
            var nativeCastBarProgressNode = componentNode->Component->UldManager.SearchNodeById(8);
            if (nativeCastBarNode == null || nativeCastBarProgressNode == null)
            {
                HideNodes();
                continue;
            }

            var nativeCastBackgroundNode = componentNode->Component->UldManager.SearchNodeById(5);
            if (nativeCastBackgroundNode == null)
            {
                HideNodes();
                continue;
            }
            
            nativeCastBarNode->SetAlpha(0);
            nativeCastBarProgressNode->SetAlpha(0);
            nativeCastNode->SetAlpha(0);
            nativeCastBackgroundNode->SetAlpha(0);

            #endregion
            
            #region 更新属性

            castNode.TextColor        = config.TextColor;
            castNode.TextOutlineColor = config.TextEdgeColor;
            castNode.FontSize         = config.FontSize;
            
            healthNode.TextColor        = config.TextColor;
            healthNode.TextOutlineColor = config.TextEdgeColor;
            
            healthMarkerNode.TextColor        = config.TextColor;
            healthMarkerNode.TextOutlineColor = config.TextEdgeColor;
            
            #endregion

            #region 状态效果更新

            statusNodes.Scale    = componentNode->GetScale()             - new Vector2(0.1f);
            statusNodes.Position = componentNode->GetNodeState().TopLeft + (new Vector2(216, -1) * statusNodes.Scale);
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

            #endregion

            #region 体力更新

            var healthPercentage = (float)gameObj->Health / gameObj->MaxHealth * 100;

            // 不可选中的敌人满血或空血
            if (!gameObj->GetIsTargetable() &&
                (gameObj->Health == gameObj->MaxHealth || gameObj->Health == 0))
            {
                healthNode.IsVisible       = false;
                healthMarkerNode.IsVisible = false;
            }
            else
            {
                healthNode.IsVisible       = true;
                healthMarkerNode.IsVisible = true;

                const float HEALTH_NODE_BASE_X    = -60f;
                const float HEALTH_MARKER_PADDING = 1f;

                healthNode.String = $"{healthPercentage:F1}";

                var healthTextWidth     = healthNode.GetTextDrawSize(false).X;
                var healthBaseTextWidth = healthNode.GetTextDrawSize("99.9", false).X;
                var healthMarkerWidth   = healthMarkerNode.GetTextDrawSize(false).X;
                var healthRightX        = HEALTH_NODE_BASE_X + healthBaseTextWidth + HEALTH_MARKER_PADDING + healthMarkerWidth;

                healthNode.X       = healthRightX - healthTextWidth - HEALTH_MARKER_PADDING - healthMarkerWidth;
                healthMarkerNode.X = healthNode.X + healthTextWidth + HEALTH_MARKER_PADDING;
            }
            
            #endregion

            #region 咏唱

            // 当前不在咏唱
            if (!gameObj->IsCasting)
            {
                castNode.IsVisible           = false;
                castBackgroundNode.IsVisible = false;
                castBarNode.IsVisible        = false;
            }
            else
            {
                castNode.IsVisible           = true;
                castBackgroundNode.IsVisible = true;
                castBarNode.IsVisible        = true;
                
                // 避免溢出所以手动进度控制
                castBarNode.ProgressNode.Width = 105 * (gameObj->CastInfo.CurrentCastTime / gameObj->CastInfo.TotalCastTime);
                
                // 可打断边缘发红光
                if (gameObj->CastInfo.Interruptible)
                    castBarNode.AddColor = KnownColor.Red.ToVector4().ToVector3();
                else
                    castBarNode.AddColor = KnownColor.Yellow.ToVector4().ToVector3() / 255f;

                var castText = GetCastInfoText
                (
                    (ActionType)gameObj->CastInfo.ActionType,
                    gameObj->CastInfo.ActionId,
                    MathF.Max(gameObj->CastInfo.TotalCastTime - gameObj->CastInfo.CurrentCastTime, 0f)
                );
                castNode.String = castText;

                var padding       = *(ushort*)((nint)EnemyList + 646);
                var castTextWidth = castNode.GetTextDrawSize(false).X + 2f;
                
                castBackgroundNode.X     = castNode.Position.X - castTextWidth - 1f;
                castBackgroundNode.Width = castTextWidth      + (padding / 2f);
            }

            #endregion

            continue;

            void HideNodes()
            {
                castNode.IsVisible           = false;
                healthNode.IsVisible         = false;
                castBackgroundNode.IsVisible = false;
                castBarNode.IsVisible        = false;
                statusNodes.ShouldBeVisible  = false;
            }
        }
    }

    private void CreateNodes()
    {
        if (EnemyList == null) return;
        if (!TryFindButtonNodes(out var buttonNodesPtr)) return;
        
        controller ??= new();

        var counter = -1;
        foreach (var nodePtr in buttonNodesPtr)
        {
            var node = (AtkComponentNode*)nodePtr;

            var castTextNode = node->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            counter++;

            var castNode = new TextNode
            {
                FontSize      = config.FontSize,
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                AlignmentType = AlignmentType.TopRight,
                Position      = new(198, 5),
            };
            
            var healthNode = new TextNode
            {
                FontSize      = 16,
                FontType      = FontType.Miedinger,
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft,
                Position      = new(-60, 8),
            };

            var healthMarkerNode = new TextNode
            {
                String        = "%",
                FontSize      = 14,
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft,
                Position      = new(-60, 8),
            };

            var backgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(124, 24),
                Position           = new(75, 2),
                Offsets            = new(8),
                Alpha              = 1f,
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
            castNode.AttachNode(node);
            healthNode.AttachNode(node);
            healthMarkerNode.AttachNode(node);
            castBarNode.AttachNode(node);

            var statusNodes = new IconTextNodesRow(5, node->NodeId, counter);
            controller.AddNode(statusNodes);

            nodes.Add(new(node->NodeId, castNode, healthNode, healthMarkerNode, backgroundNode, castBarNode, statusNodes));
        }
    }
    
    private static string GetCastInfoText(ActionType type, uint actionID, float remainingTime)
    {
        var actionName = string.Empty;

        switch (type)
        {
            case ActionType.Action:
                actionName = LuminaWrapper.GetActionName(actionID);
                break;
        }

        if (string.IsNullOrEmpty(actionName))
            actionName = $"{LuminaWrapper.GetAddonText(16482)}";

        var timeText = remainingTime != 0 ? remainingTime.ToString("F1") : "\ue07f\ue07b";

        return $"{actionName}: {timeText}";
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
        public bool DisplayStatus = true;
        public byte FontSize      = 10;

        public Vector4 TextColor     = Vector4.One;
        public Vector4 TextEdgeColor = new(0, 0.372549f, 1, 1);
        public Vector2 TextOffset    = Vector2.Zero;
        
        public bool AlwaysUnlock = true;
    }

    private sealed class EnemyListNode
    (
        uint                     componentNodeID,
        TextNode                 castNode,
        TextNode                 heathNode,
        TextNode                 healthMarkerNode,
        NineGridNode             castBackgroundNode,
        ProgressBarEnemyCastNode castBarNode,
        IconTextNodesRow         statusNodes
    )
    {
        public uint                      ComponentNodeID    { get; set; } = componentNodeID;
        public TextNode?                 CastNode           { get; set; } = castNode;
        public TextNode?                 HeathNode          { get; set; } = heathNode;
        public TextNode?                 HealthMarkerNode   { get; set; } = healthMarkerNode;
        public NineGridNode?             CastBackgroundNode { get; set; } = castBackgroundNode;
        public ProgressBarEnemyCastNode? CastBarNode        { get; set; } = castBarNode;
        public IconTextNodesRow?         StatusNodes        { get; set; } = statusNodes;

        public void Deconstruct
        (
            out uint                      componentNodeID,
            out TextNode?                 castNode,
            out TextNode?                 heathNode,
            out TextNode?                 healthMarkerNode,
            out NineGridNode?             castBackgroundNode,
            out ProgressBarEnemyCastNode? castBarNode,
            out IconTextNodesRow?         statusNodes
        )
        {
            componentNodeID    = ComponentNodeID;
            castNode           = CastNode;
            heathNode          = HeathNode;
            healthMarkerNode   = HealthMarkerNode;
            castBackgroundNode = CastBackgroundNode;
            castBarNode        = CastBarNode;
            statusNodes        = StatusNodes;
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
                    Position = new((25 + 2) * i, 0)
                };
                statusNode.AttachNode(this);
                Nodes.Add(statusNode);
            }

            Size = new(25 + ((25 + 2) * count), 41);
        }

        public int  Count  { get; init; }
        public uint NodeID { get; init; }

        public int Index { get; init; }

        public override OverlayLayer OverlayLayer
        {
            get => OverlayLayer.Foreground;
        }

        public override bool HideWithNativeUi
        {
            get => true;
        }

        public bool ShouldBeVisible { get; set; }

        public List<IconTextNode> Nodes { get; init; } = [];

        public IconTextNode this[int index] => Nodes[index];

        public IEnumerator<IconTextNode> GetEnumerator() => Nodes.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        protected override void OnUpdate() =>
            IsVisible = ShouldBeVisible                   &&
                        !GameState.IsInPVPInstance        &&
                        EnemyList->IsAddonAndNodesReady() &&
                        Index < EnemyListNumberArray.Instance()->EnemyCount;
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
