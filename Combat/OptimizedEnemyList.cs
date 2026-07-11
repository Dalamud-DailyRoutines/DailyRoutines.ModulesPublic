using System.Collections;
using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.UiOverlay;
using OmenTools.Interop.Game;
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

    private MemoryPatch enemyListIsCastInProgressPatch = new
    (
        "0F 84 ?? ?? ?? ?? 49 8B 04 24 49 8B CC FF 90 ?? ?? ?? ?? 48 85 C0",
        [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
    );

    private MemoryPatch enemyListClearSpellIDPatch = new
    (
        "83 7E ?? ?? 0F 84 ?? ?? ?? ?? 8B B4 24",
        [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
    );
    
    private MemoryPatch enemyListDisplayCastPatch = new
    (
        "74 ?? 49 8B 04 24 49 8B CC FF 90 ?? ?? ?? ?? 48 85 C0",
        [0x90, 0x90]
    );
    
    private Config config = null!;

    private readonly List<EnemyListNode> nodes = [];
    private          OverlayController?  controller;

    private bool isUpdating;
    
    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        enemyListIsCastInProgressPatch.Enable();
        enemyListClearSpellIDPatch.Enable();
        enemyListDisplayCastPatch.Enable();
        
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,           "_EnemyList", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,        "_EnemyList", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        foreach (var (_, textNode, heathTextNode, healthMarkerNode, backgroundNode, healthBackgroundNode, castBarNode, _, enemityNode) in nodes)
        {
            textNode?.Dispose();
            heathTextNode?.Dispose();
            healthMarkerNode?.Dispose();
            backgroundNode?.Dispose();
            healthBackgroundNode?.Dispose();
            castBarNode?.Dispose();
            enemityNode?.Dispose();
        }

        nodes.Clear();

        controller?.Dispose();
        controller = null;
    }

    protected override void ConfigUI()
    {
        using (ImRaii.Heading1(Lang.Get("Cast")))
        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushId("CastText"))
        {
            ImGui.InputFloat2(Lang.Get("Offset"), ref config.TextOffset, format: "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.TextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.TextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            
            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.TextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
        
        ImGui.NewLine();
        
        using (ImRaii.Heading1(LuminaWrapper.GetAddonText(11274)))
        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushId("HealthText"))
        {
            ImGui.InputFloat2(Lang.Get("Offset"), ref config.HealthTextOffset, format: "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.HealthTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.HealthTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            
            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.HealthTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
        
        ImGui.NewLine();
        
        using (ImRaii.Heading1(LuminaWrapper.GetAddonText(721)))
        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushId("EnemityText"))
        {
            ImGui.InputFloat2(Lang.Get("Offset"), ref config.EnemityTextOffset, format: "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputByte(Lang.Get("FontSize"), ref config.EnemityTextSize);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.ColorEdit4(Lang.Get("TextColor"), ref config.EnemityTextColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            
            ImGui.ColorEdit4(Lang.Get("EdgeColor"), ref config.EnemityTextEdgeColor);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
        
        ImGui.NewLine();
        
        using (ImRaii.Heading1(Lang.Get("Status")))
        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (ImRaii.PushId("Status"))
        {
            ImGui.InputFloat2(Lang.Get("Offset"), ref config.StatusOffset, format: "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
            
            ImGui.InputFloat(Lang.Get("Scale"), ref config.StatusScale, format: "%.1f");
            if (ImGui.IsItemDeactivatedAfterEdit())
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

                if (isUpdating)
                    break;
                try
                {
                    UpdateNodes();
                }
                finally
                {
                    isUpdating = false;
                }
                break;
            
            case AddonEvent.PostDraw:
                if (!Throttler.Shared.Throttle("OptimizedEnemyList.UpdateEnemyList", 100))
                    return;
                
                if (isUpdating)
                    break;
                try
                {
                    UpdateNodes();
                }
                finally
                {
                    isUpdating = false;
                }
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
                out var healthBackgroundNode,
                out var castBarNode,
                out var statusNodes,
                out var enemityNode
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
            
            var lockImageNode = componentNode->Component->UldManager.SearchNodeById(14);
            if (lockImageNode == null) continue;
            
            nativeCastBarNode->SetAlpha(0);
            nativeCastBarProgressNode->SetAlpha(0);
            nativeCastNode->SetAlpha(0);
            nativeCastBackgroundNode->SetAlpha(0);

            #endregion
            
            #region 状态更新

            statusNodes.Scale = (componentNode->GetScale() - new Vector2(0.1f)) * config.StatusScale;
            statusNodes.Alpha = info.ActiveInList ? 1f : 0.5f;

            var nodeState = componentNode->GetNodeState();
            statusNodes.Position = nodeState.TopRight -
                                   lockImageNode->GetNodeState().Size.WithY(0) +
                                   (StatusComponentOffset * statusNodes.Scale) +
                                   config.StatusOffset;

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
                healthNode.IsVisible           = false;
                healthMarkerNode.IsVisible     = false;
                healthBackgroundNode.IsVisible = false;
            }
            else
            {
                healthNode.IsVisible           = true;
                healthMarkerNode.IsVisible     = true;
                healthBackgroundNode.IsVisible = true;

                healthNode.TextColor        = config.HealthTextColor;
                healthNode.TextOutlineColor = config.HealthTextEdgeColor;
                healthNode.FontSize         = config.HealthTextSize;

                healthMarkerNode.TextColor        = config.HealthTextColor;
                healthMarkerNode.TextOutlineColor = config.HealthTextEdgeColor;
                healthMarkerNode.FontSize         = (byte)Math.Max(0, config.HealthTextSize - 2);
                
                healthNode.String = $"{healthPercentage:F1}";

                var healthTextWidth     = healthNode.GetTextDrawSize(false).X;
                var healthTextHeight    = healthNode.GetTextDrawSize(false).Y;
                var healthBaseTextWidth = healthNode.GetTextDrawSize("99.9", false).X;
                var healthMarkerWidth   = healthMarkerNode.GetTextDrawSize(false).X;
                var healthRightX        = HealthTextDefaultPosition.X + config.HealthTextOffset.X + healthBaseTextWidth - 3f + healthMarkerWidth;

                healthNode.X = healthRightX                - healthTextWidth - HEALTH_TEXT_MARKER_PADDING - healthMarkerWidth;
                healthNode.Y = HealthTextDefaultPosition.Y + config.HealthTextOffset.Y;

                healthMarkerNode.X = healthNode.X                + healthTextWidth + HEALTH_TEXT_MARKER_PADDING;
                healthMarkerNode.Y = HealthTextDefaultPosition.Y + config.HealthTextOffset.Y;

                healthBackgroundNode.X      = healthNode.X     - 5f;
                healthBackgroundNode.Y      = 6f               + config.HealthTextOffset.Y;
                healthBackgroundNode.Width  = healthTextWidth  + HEALTH_TEXT_MARKER_PADDING + healthMarkerWidth + 11f;
                healthBackgroundNode.Height = healthTextHeight + 4f;
            }
            
            #endregion

            #region 咏唱
            
            castNode.TextColor        = config.TextColor;
            castNode.TextOutlineColor = config.TextEdgeColor;
            castNode.FontSize         = config.TextSize;
            castNode.Position         = CastTextDefaultPosition + config.TextOffset;

            // 当前不在咏唱
            var leftCastTime = MathF.Max(gameObj->CastInfo.TotalCastTime - gameObj->CastInfo.CurrentCastTime, 0f);
            if (leftCastTime <= 0 && gameObj->CastInfo.ActionId == 0)
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
                    leftCastTime
                );
                castNode.String = castText;
                
                // 因为等于 0 的时候算出来的宽度有很不太好看的的变化
                if (leftCastTime != 0)
                {
                    var castTextWidth = castNode.GetTextDrawSize(false).X + 1f;

                    castBackgroundNode.Position = CastBackgroundTextDefaultPosition with { X = castNode.Position.X - castTextWidth - 2f };
                    castBackgroundNode.Width    = castTextWidth + (CAST_TEXT_BACKGROUND_PADDING * (config.TextSize / 10f));
                }
            }

            #endregion

            #region 仇恨更新

            var haterCounter = 0;
            var enemity      = -1;
            foreach (var hater in UIState.Instance()->Hater.Haters)
            {
                haterCounter++;
                if (haterCounter > UIState.Instance()->Hater.HaterCount) break;
                if (hater.EntityId != gameObj->EntityId) continue;
            
                enemity = hater.Enmity;
                break;
            }

            var isEnemityValid = enemity is > 0 and not 100;

            if (isEnemityValid)
            {
                enemityNode.String           = $"{enemity}%";
                enemityNode.TextColor        = config.EnemityTextColor;
                enemityNode.TextOutlineColor = config.EnemityTextEdgeColor;
                enemityNode.FontSize         = config.EnemityTextSize;
                enemityNode.Position         = EnemityTextDefaultPosition + config.EnemityTextOffset;

                enemityNode.IsVisible = true;
            }
            else
                enemityNode.IsVisible = false;

            #endregion

            continue;

            void HideNodes()
            {
                castNode.IsVisible             = false;
                healthNode.IsVisible           = false;
                castBackgroundNode.IsVisible   = false;
                healthBackgroundNode.IsVisible = false;
                castBarNode.IsVisible          = false;
                statusNodes.ShouldBeVisible    = false;
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
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                AlignmentType = AlignmentType.TopRight,
            };
            
            var castBarNode = new ProgressBarEnemyCastNode
            {
                IsVisible = true,
                Position  = new(90, 13.7f),
                Size      = new(120, 20)
            };
            
            var castBackgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(124, 24),
                Offsets            = new(8),
                Alpha              = 1f,
            };

            castBarNode.ProgressNode.Height   -= 12f;
            castBarNode.ProgressNode.Position += new Vector2(7.7f, 6.5f);
            castBarNode.ProgressNode.AddColor =  new(1);
            
            var healthNode = new TextNode
            {
                FontType      = FontType.Miedinger,
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft,
            };

            var healthMarkerNode = new TextNode
            {
                String        = "%",
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft,
            };

            var healthBackgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList_hr1.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(60, 24),
                Position           = new(-64, 6),
                Offsets            = new(8),
                Alpha              = 0.6f,
            };
            
            var enemityNode = new TextNode
            {
                Position      = EnemityTextDefaultPosition,
                FontSize      = 8,
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                AlignmentType = AlignmentType.Center,
            };
            AtkColors.Value.ApplyTo(ref enemityNode);

            castBackgroundNode.AttachNode(node);
            castNode.AttachNode(node);
            healthBackgroundNode.AttachNode(node);
            healthNode.AttachNode(node);
            healthMarkerNode.AttachNode(node);
            castBarNode.AttachNode(node);
            enemityNode.AttachNode(node);

            var statusNodes = new IconTextNodesRow(5, node->NodeId, counter);
            controller.AddNode(statusNodes);

            nodes.Add
            (
                new
                (
                    node->NodeId,
                    castNode,
                    healthNode,
                    healthMarkerNode,
                    castBackgroundNode,
                    healthBackgroundNode,
                    castBarNode,
                    statusNodes,
                    enemityNode
                )
            );
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
        
        // 咏唱
        public byte    TextSize      = 10;
        public Vector4 TextColor     = Vector4.One;
        public Vector4 TextEdgeColor = new(0, 0.372549f, 1, 1);
        public Vector2 TextOffset    = Vector2.Zero;
        
        // 体力值
        public byte    HealthTextSize      = 16;
        public Vector4 HealthTextColor     = Vector4.One;
        public Vector4 HealthTextEdgeColor = new(0, 0.372549f, 1, 1);
        public Vector2 HealthTextOffset    = Vector2.Zero;
        
        // 仇恨值
        public byte    EnemityTextSize      = 8;
        public Vector4 EnemityTextColor     = AtkColors.Value.GetTextColor();
        public Vector4 EnemityTextEdgeColor = AtkColors.Value.GetEdgeColor();
        public Vector2 EnemityTextOffset    = Vector2.Zero;
        
        // 状态
        public float   StatusScale  = 1f;
        public Vector2 StatusOffset = Vector2.Zero;
        
        public bool AlwaysUnlock = true;
    }

    private sealed class EnemyListNode
    (
        uint                     componentNodeID,
        TextNode                 castNode,
        TextNode                 heathNode,
        TextNode                 healthMarkerNode,
        NineGridNode             castBackgroundNode,
        NineGridNode             healthBackgroundNode,
        ProgressBarEnemyCastNode castBarNode,
        IconTextNodesRow         statusNodes,
        TextNode enemityNode
    )
    {
        public uint                      ComponentNodeID      { get; set; } = componentNodeID;
        public TextNode?                 CastNode             { get; set; } = castNode;
        public TextNode?                 HeathNode            { get; set; } = heathNode;
        public TextNode?                 HealthMarkerNode     { get; set; } = healthMarkerNode;
        public NineGridNode?             CastBackgroundNode   { get; set; } = castBackgroundNode;
        public NineGridNode?             HealthBackgroundNode { get; set; } = healthBackgroundNode;
        public ProgressBarEnemyCastNode? CastBarNode          { get; set; } = castBarNode;
        public IconTextNodesRow?         StatusNodes          { get; set; } = statusNodes;
        public TextNode?                 EnemityNode          { get; set; } = enemityNode;

        public void Deconstruct
        (
            out uint                      componentNodeID,
            out TextNode?                 castNode,
            out TextNode?                 heathNode,
            out TextNode?                 healthMarkerNode,
            out NineGridNode?             castBackgroundNode,
            out NineGridNode?             healthBackgroundNode,
            out ProgressBarEnemyCastNode? castBarNode,
            out IconTextNodesRow?         statusNodes,
            out TextNode?                 enemityNode
        )
        {
            componentNodeID      = ComponentNodeID;
            castNode             = CastNode;
            heathNode            = HeathNode;
            healthMarkerNode     = HealthMarkerNode;
            castBackgroundNode   = CastBackgroundNode;
            healthBackgroundNode = HealthBackgroundNode;
            castBarNode          = CastBarNode;
            statusNodes          = StatusNodes;
            enemityNode          = EnemityNode;
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

        public override OverlayLayer OverlayLayer => OverlayLayer.Foreground;

        public override bool HideWithNativeUi => true;

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

    #region 常量

    private static readonly Vector2 CastTextDefaultPosition           = new(203, 6);
    private static readonly Vector2 CastBackgroundTextDefaultPosition = new(197, 2);
    private static readonly Vector2 HealthTextDefaultPosition         = new(-60, 8);
    private static readonly Vector2 EnemityTextDefaultPosition        = new(12, 21);
    private static readonly Vector2 StatusComponentOffset             = new(11, -1);
    
    private const float HEALTH_TEXT_MARKER_PADDING   = 1f;
    private const float CAST_TEXT_BACKGROUND_PADDING = 7f;

    #endregion
}
