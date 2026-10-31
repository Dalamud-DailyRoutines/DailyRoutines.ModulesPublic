using System.Numerics;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Lumina;
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
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedEnemyList/preview-1.webp"
        ],
        ModulesPair = ["AutoDisplayHiddenCast"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private MemoryPatch enemyListIsCastInProgressPatch = null!;
    private MemoryPatch enemyListClearSpellIDPatch     = null!;
    private MemoryPatch enemyListDisplayCastPatch      = null!;

    private Config config = null!;

    private readonly List<EnemyListNode> nodes = [];

    private VerticalListNode? statusContainer;

    private bool isUpdating;

    protected override void Init()
    {
        enemyListIsCastInProgressPatch = new
        (
            "0F 84 ?? ?? ?? ?? 49 8B 04 24 49 8B CC FF 90 ?? ?? ?? ?? 48 85 C0",
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        );
        enemyListClearSpellIDPatch = new
        (
            "83 7E ?? ?? 0F 84 ?? ?? ?? ?? 8B B4 24",
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]
        );
        enemyListDisplayCastPatch = new
        (
            "74 ?? 49 8B 04 24 49 8B CC FF 90 ?? ?? ?? ?? 48 85 C0",
            [0x90, 0x90]
        );

        config = Config.Load(this) ?? new();

        enemyListIsCastInProgressPatch.Enable();
        enemyListClearSpellIDPatch.Enable();
        enemyListDisplayCastPatch.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreRequestedUpdate,  "_EnemyList", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "_EnemyList", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,            "_EnemyList", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize,         "_EnemyList", OnAddon);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        DisposeNodes();
    }

    private void DisposeNodes()
    {
        var enemyList = EnemyList;
        if (enemyList                                    != null &&
            statusContainer                              != null &&
            enemyList->AdditionalFocusableNodes[1].Value == statusContainer.ResNode)
            enemyList->AdditionalFocusableNodes[1] = null;

        statusContainer?.Dispose();
        statusContainer = null;

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
    }

    protected override void ConfigUI()
    {
        using var tabBar = ImRaii.TabBar("Config");
        if (!tabBar) return;

        using (var item = ImRaii.TabItem(Lang.Get("General")))
        {
            if (item)
            {
                if (ImGui.Checkbox(Lang.Get("OptimizedEnemyList-AlwaysUnlock"), ref config.AlwaysUnlock))
                    config.Save(this);
                ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedEnemyList-AlwaysUnlock-Help"));

                if (ImGui.Checkbox(Lang.Get("OptimizedEnemyList-SwapHealthStatus"), ref config.SwapHealthStatus))
                    config.Save(this);
                ImGuiOm.TextDisabledWrapped(Lang.Get("OptimizedEnemyList-SwapHealthStatus-Help"));
            }
        }

        using (var item = ImRaii.TabItem(Lang.Get("Cast")))
        {
            if (item)
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
        }

        // 体力
        using (var item = ImRaii.TabItem(LuminaWrapper.GetAddonText(11274)))
        {
            if (item)
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
        }

        // 仇恨
        using (var item = ImRaii.TabItem(LuminaWrapper.GetAddonText(721)))
        {
            if (item)
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
        }

        // 状态效果
        using (var item = ImRaii.TabItem(LuminaWrapper.GetAddonText(721)))
        {
            if (item)
            {
                ImGui.InputFloat2(Lang.Get("Offset"), ref config.StatusOffset, format: "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);

                ImGui.InputFloat(Lang.Get("Scale"), ref config.StatusScale, format: "%.1f");
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }
    }

    #region 事件

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
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

                break;

            case AddonEvent.PostRequestedUpdate:
            case AddonEvent.PostDraw:
                if (type == AddonEvent.PostDraw &&
                    !Throttler.Shared.Throttle("OptimizedEnemyList.UpdateEnemyList", 100))
                    return;

                if (isUpdating)
                    break;

                try
                {
                    isUpdating = true;
                    UpdateNodes();
                }
                finally
                {
                    isUpdating = false;
                }

                break;

            case AddonEvent.PreFinalize:
                DisposeNodes();
                break;
        }
    }

    #endregion

    private void UpdateNodes()
    {
        var enemyList = EnemyList;
        if (enemyList == null) return;

        var enemyListNumberArray = EnemyListNumberArray.Instance();
        if (enemyListNumberArray == null) return;

        if (nodes is not { Count: > 0 })
        {
            if (enemyListNumberArray->EnemyCount == 0) return;

            CreateNodes();
            if (nodes.Count == 0)
                return;
        }

        var firstComponent = enemyList->GetComponentNodeById(nodes[0].ComponentNodeID);
        var lastComponent  = enemyList->GetComponentNodeById(nodes[^1].ComponentNodeID);
        var lockNode       = firstComponent->Component->UldManager.SearchNodeById(14);
        var statusWidth    = 0f;
        var hasStatuses    = false;

        statusContainer.Scale       = new(config.StatusScale);
        statusContainer.ItemSpacing = ((lastComponent->Y - firstComponent->Y) / (nodes.Count - 1) / config.StatusScale) - 41;
        statusContainer.Alignment = config.SwapHealthStatus ?
                                        VerticalListAlignment.Right :
                                        VerticalListAlignment.Left;
        enemyList->AdditionalFocusableNodes[1] = statusContainer.ResNode;

        for (var i = 0; i < nodes.Count; i++)
        {
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

            if (i >= enemyListNumberArray->EnemyCount)
            {
                HideNodes();
                continue;
            }

            var info     = enemyListNumberArray->Enemies[i];
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

            var componentNode = enemyList->GetComponentNodeById(componentNodeID);

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

            if (lockImageNode == null)
            {
                HideNodes();
                continue;
            }

            nativeCastBarNode->SetAlpha(0);
            nativeCastBarProgressNode->SetAlpha(0);
            nativeCastNode->SetAlpha(0);
            nativeCastBackgroundNode->SetAlpha(0);

            #endregion

            #region 状态更新

            statusNodes.Alpha = componentNode->Color.A / 255f;

            var counter = 0;

            foreach (var status in gameObj->StatusManager.Status)
            {
                if (counter == STATUS_COUNT) break;

                if (status.StatusId           == 0) continue;
                if ((uint)status.SourceObject != LocalPlayerState.EntityID) continue;

                var node = (IconTextNode)statusNodes.Nodes[counter];
                node.IsVisible = !GameState.IsInPVPInstance;
                node.Update(status);

                counter++;
            }

            if (counter < STATUS_COUNT)
            {
                for (var d = counter; d < STATUS_COUNT; d++)
                    statusNodes.Nodes[d].IsVisible = false;
            }

            statusNodes.RecalculateLayout();
            statusNodes.IsVisible =  true;
            statusWidth           =  MathF.Max(statusWidth, statusNodes.Width);
            hasStatuses           |= counter > 0 && !GameState.IsInPVPInstance;

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

                healthNode.X = config.SwapHealthStatus ?
                                   componentNode->Width - lockImageNode->Width + StatusComponentOffset.X + config.HealthTextOffset.X :
                                   healthRightX                                - healthTextWidth         - HEALTH_TEXT_MARKER_PADDING - healthMarkerWidth;
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

                castBarNode.Progress        = gameObj->CastInfo.CurrentCastTime / gameObj->CastInfo.TotalCastTime;
                castBarNode.IsInterruptible = gameObj->CastInfo.Interruptible;

                var interruptibleColor = gameObj->CastInfo.Interruptible ?
                                             KnownColor.Red.ToVector4().ToVector3() :
                                             Vector3.Zero;

                // 可打断边缘发红光
                castBarNode.AddColor = interruptibleColor;
                castNode.AddColor    = interruptibleColor;

                var castText = GetCastInfoText
                (
                    (ActionType)gameObj->CastInfo.ActionType,
                    gameObj->CastInfo.ActionId,
                    leftCastTime
                );
                castNode.String = castText;

                var orig = IGameConfig.Instance().System.GetUInt("UiHighScale");
                var offset = orig switch
                {
                    0 => 3f,
                    1 => 0f,
                    2 => -3f,
                    3 => -6f
                };

                var castTextWidth = castNode.GetTextDrawSize(false).X + offset;

                castBackgroundNode.Position = CastBackgroundTextDefaultPosition with { X = castNode.Position.X - castTextWidth - 2f };
                castBackgroundNode.Width    = castTextWidth + (CAST_TEXT_BACKGROUND_PADDING * (config.TextSize / 10f));
            }

            #endregion

            #region 仇恨更新

            var haterCounter = 0;
            var enemity      = -1;

            foreach (var hater in UIState.Instance()->Hater.Haters)
            {
                haterCounter++;
                if (haterCounter   > UIState.Instance()->Hater.HaterCount) break;
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
                healthMarkerNode.IsVisible     = false;
                castBackgroundNode.IsVisible   = false;
                healthBackgroundNode.IsVisible = false;
                castBarNode.IsVisible          = false;
                enemityNode.IsVisible          = false;
                statusNodes.IsVisible          = i < enemyListNumberArray->EnemyCount;

                foreach (var statusNode in statusNodes.Nodes)
                    statusNode.IsVisible = false;
            }
        }

        statusContainer.Width = statusWidth;
        statusContainer.Position = new Vector2
        (
            firstComponent->X     +
            config.StatusOffset.X +
            (config.SwapHealthStatus ?
                 -statusWidth * config.StatusScale :
                 firstComponent->Width - lockNode->Width + StatusComponentOffset.X),
            firstComponent->Y + StatusComponentOffset.Y + config.StatusOffset.Y
        );
        statusContainer.RecalculateLayout();
        statusContainer.IsVisible = hasStatuses;
    }

    private void CreateNodes()
    {
        var enemyList = EnemyList;
        if (enemyList == null) return;
        if (!TryFindButtonNodes(out var buttonNodesPtr)) return;

        statusContainer = new VerticalListNode
        {
            FitContents = true,
            IsVisible   = false
        };
        statusContainer.AttachNode((AtkResNode*)buttonNodesPtr[0], NodePosition.BeforeTarget);
        enemyList->AdditionalFocusableNodes[1] = statusContainer.ResNode;

        foreach (var nodePtr in buttonNodesPtr)
        {
            var node = (AtkComponentNode*)nodePtr;

            var castTextNode = node->Component->UldManager.SearchNodeById(4)->GetAsAtkTextNode();
            if (castTextNode == null) continue;

            var castNode = new TextNode
            {
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                AlignmentType = AlignmentType.TopRight
            };

            var castBarNode = new ProgressBarEnemyCastNode
            {
                IsVisible = true,
                Position  = new(86, 16f),
                Size      = new(120, 20)
            };

            var castBackgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(124, 20),
                Offsets            = new(8),
                Alpha              = 1f
            };

            var healthNode = new TextNode
            {
                FontType      = FontType.Miedinger,
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft
            };

            var healthMarkerNode = new TextNode
            {
                String        = "%",
                TextFlags     = TextFlags.Edge,
                AlignmentType = AlignmentType.TopLeft
            };

            var healthBackgroundNode = new SimpleNineGridNode
            {
                TexturePath        = "ui/uld/EnemyList.tex",
                TextureCoordinates = new(96, 80),
                TextureSize        = new(24, 20),
                Size               = new(60, 24),
                Position           = new(-64, 6),
                Offsets            = new(8),
                Alpha              = 0.6f
            };

            var enemityNode = new TextNode
            {
                Position      = EnemityTextDefaultPosition,
                FontSize      = 8,
                TextFlags     = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                AlignmentType = AlignmentType.Center
            };
            AtkColors.Value.ApplyTo(enemityNode);

            castBackgroundNode.AttachNode(node);
            castNode.AttachNode(node);
            healthBackgroundNode.AttachNode(node);
            healthNode.AttachNode(node);
            healthMarkerNode.AttachNode(node);
            castBarNode.AttachNode(node);
            enemityNode.AttachNode(node);

            var statusNodes = new HorizontalListNode
            {
                Height            = 41,
                ItemSpacing       = 2,
                FitToContentWidth = true,
                IsVisible         = false
            };

            for (var i = 0; i < STATUS_COUNT; i++)
            {
                statusNodes.AddNode
                (
                    new IconTextNode
                    {
                        Size      = new(25, 41),
                        IsVisible = false
                    }
                );
            }

            statusContainer.AddNode(statusNodes);

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

    private static string GetCastInfoText
    (
        ActionType type,
        uint       actionID,
        float      remainingTime
    )
    {
        var actionName = type switch
        {
            ActionType.Action => LuminaWrapper.GetActionName(actionID),
            _                 => string.Empty
        };

        if (string.IsNullOrEmpty(actionName))
            actionName = LuminaWrapper.GetAddonText(16482);

        var timeText = remainingTime.ToString("F1");

        return $"{actionName}：{timeText}";
    }

    private static bool TryFindButtonNodes
    (
        out List<nint> nodes
    )
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
        public Vector4 TextEdgeColor = new(0.6156863f, 0.5137255f, 0.3568628f, 1.0f);
        public Vector2 TextOffset    = Vector2.Zero;

        // 体力值
        public byte    HealthTextSize      = 16;
        public Vector4 HealthTextColor     = Vector4.One;
        public Vector4 HealthTextEdgeColor = new(0.6156863f, 0.5137255f, 0.3568628f, 1.0f);
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
        public bool SwapHealthStatus;
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
        HorizontalListNode       statusNodes,
        TextNode                 enemityNode
    )
    {
        public uint                      ComponentNodeID      { get; set; } = componentNodeID;
        public TextNode?                 CastNode             { get; set; } = castNode;
        public TextNode?                 HeathNode            { get; set; } = heathNode;
        public TextNode?                 HealthMarkerNode     { get; set; } = healthMarkerNode;
        public NineGridNode?             CastBackgroundNode   { get; set; } = castBackgroundNode;
        public NineGridNode?             HealthBackgroundNode { get; set; } = healthBackgroundNode;
        public ProgressBarEnemyCastNode? CastBarNode          { get; set; } = castBarNode;
        public HorizontalListNode?       StatusNodes          { get; set; } = statusNodes;
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
            out HorizontalListNode?       statusNodes,
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

        public void Update
        (
            Status status
        )
        {
            if (!LuminaGetter.TryGetRow(status.StatusId, out Lumina.Excel.Sheets.Status row)) return;

            IconNode.IconId = row.Icon;
            TextNode.SetNumber((int)status.RemainingTime);

            using var utf8String = new Utf8String();
            _ = AgentHUD.Instance()->GetStatusTooltipText(&utf8String, status.StatusId, status.Param);
            
            TextTooltip = utf8String.AsReadOnlySeString();
        }
    }

    #region 常量

    private static readonly Vector2 CastTextDefaultPosition           = new(203, 8);
    private static readonly Vector2 CastBackgroundTextDefaultPosition = new(197, 4);
    private static readonly Vector2 HealthTextDefaultPosition         = new(-60, 8);
    private static readonly Vector2 EnemityTextDefaultPosition        = new(12, 21);
    private static readonly Vector2 StatusComponentOffset             = new(16, -1);

    private const float HEALTH_TEXT_MARKER_PADDING   = 1f;
    private const float CAST_TEXT_BACKGROUND_PADDING = 5f;
    private const int   STATUS_COUNT                 = 5;

    #endregion
}
