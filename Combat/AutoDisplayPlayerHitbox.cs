using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Models;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

internal unsafe class AutoDisplayPlayerHitbox : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayPlayerHitboxTitle"),
        Description = Lang.Get("AutoDisplayPlayerHitboxDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Due"]
    };

    public override string ModuleName => nameof(AutoDisplayPlayerHitbox);

    private Config            config     = null!;
    private OverlayController controller = null!;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        controller = new();
        var frameState = new PlayerDotFrameState(config);
        controller.AddNode(frameState);
        controller.AddNode(new PlayerDotImageNode(config, frameState));

        for (var index = 0; index < PARTY_MEMBER_COUNT; index++)
            controller.AddNode(new PlayerDotImageNode(config.Party, frameState, index));

        for (var index = 0; index < ALLIANCE_MEMBER_COUNT; index++)
            controller.AddNode(new PlayerDotImageNode(config.Alliance, frameState, index, true));
    }

    protected override void Uninit()
    {
        controller?.Dispose();
        controller = null;
    }

    protected override void ConfigUI()
    {
        using var tabBar = ImRaii.TabBar("PlayerHitboxTabs");
        if (!tabBar)
            return;

        DrawSettings(Lang.Get("AutoDisplayPlayerHitbox-Self"),     config);
        DrawSettings(Lang.Get("AutoDisplayPlayerHitbox-Party"),    config.Party);
        DrawSettings(Lang.Get("AutoDisplayPlayerHitbox-Alliance"), config.Alliance);
    }

    private void DrawSettings(string label, DotConfig settings)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab)
            return;

        using var id = ImRaii.PushId(label);

        if (ImGui.Checkbox(Lang.Get("AutoDisplayPlayerHitbox-Enabled"), ref settings.Enabled))
            config.Save(this);
        
        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("OnlyInCombat"), ref settings.OnlyInCombat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyInDuty"), ref settings.OnlyInDuty))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("OnlyUnsheathed"), ref settings.OnlyUnsheathed))
            config.Save(this);

        ImGui.NewLine();

        using (ImRaii.ItemWidth(300f * GlobalUIScale))
        {
            if (ImGui.InputFloat(Lang.Get("Size"), ref settings.Size))
                settings.Size = MathF.Max(1, settings.Size);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.InputUInt(Lang.Get("Icon"), ref settings.IconID);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.SameLine();
            if (ImGui.Button($"{FontAwesomeIcon.Icons.ToIconString()}"))
                ChatManager.Instance().SendCommand("/xldata icon");
            ImGuiOm.TooltipHover($"{Lang.Get("IconBrowser")}\n（{Lang.Get("IconBrowser-Suggestion")}）");

            if (ImGui.InputFloat3(Lang.Get("Offset"), ref settings.Offset, 0.1f, 1f, "%.1f"))
                config.Save(this);
            
            ImGui.SetNextItemWidth(200f * GlobalUIScale);
            ImGui.ColorPicker4(Lang.Get("Color"), ref settings.Color);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);
        }
    }

    private sealed class PlayerDotFrameState
    (
        Config config
    ) : OverlayNode
    {
        public override OverlayLayer OverlayLayer      => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi  => false;
        public override bool         HideWithUiToggled => false;

        public bool               Ready;
        public bool               InCombat;
        public bool               InDuty;
        public bool               IsWeaponUnsheathed;
        public bool               IsAlliance;
        public byte               PartyMemberCount;
        public uint               LocalPlayerEntityID;
        public Vector3            LocalPlayerPosition;
        public float              LocalPlayerRotation;
        public uint               ProjectionRevision;
        public GameViewProjection Projection;

        protected override void OnUpdate()
        {
            IsVisible = false;
            Ready     = false;

            if (config is { Enabled: false, Party.Enabled: false, Alliance.Enabled: false })
                return;

            var player = Control.GetLocalPlayer();
            if (player == null)
                return;

            var condition = ICondition.Instance();
            if (condition[ConditionFlag.Occupied38])
                return;

            if (!GameViewHelper.TryGetProjection(out var projection))
                return;

            if (ProjectionRevision == 0 || Projection != projection)
            {
                Projection = projection;
                ProjectionRevision++;
            }

            InCombat            = condition[ConditionFlag.InCombat];
            InDuty              = condition[ConditionFlag.BoundByDuty];
            IsWeaponUnsheathed  = UIState.Instance()->WeaponState.IsUnsheathed;
            LocalPlayerEntityID = player->EntityId;
            LocalPlayerPosition = player->Position;
            LocalPlayerRotation = player->Rotation;

            PartyMemberCount = 0;
            IsAlliance       = false;

            if (config.Party.Enabled || config.Alliance.Enabled)
            {
                var group = GroupManager.Instance()->GetGroupWithCheck();

                if (group != null)
                {
                    PartyMemberCount = group->MemberCount;
                    IsAlliance       = group->IsAlliance;
                }
            }

            Ready = true;
        }
    }

    private class PlayerDotImageNode : OverlayNode
    {
        public override OverlayLayer OverlayLayer      => OverlayLayer.Foreground;
        public override bool         HideWithNativeUi  => false;
        public override bool         IgnoreGlobalScale => false;

        private readonly DotConfig           moduleConfig;
        private readonly PlayerDotFrameState frameState;
        private readonly int                 memberIndex;
        private readonly bool                isAlliance;

        private readonly IconImageNode imageNode;

        private Vector2 screenOffset;
        private Vector3 worldPosition;
        private uint    projectionRevision;
        private bool    onScreen;
        private float   appliedSize;
        private Vector4 appliedColor;

        public PlayerDotImageNode
        (
            DotConfig           config,
            PlayerDotFrameState inFrameState,
            int                 inMemberIndex = -1,
            bool                inIsAlliance  = false
        )
        {
            moduleConfig = config;
            frameState   = inFrameState;
            memberIndex  = inMemberIndex;
            isAlliance   = inIsAlliance;

            imageNode = new IconImageNode
            {
                IconId     = config.IconID,
                FitTexture = true,
                Color      = config.Color
            };
            imageNode.AttachNode(this);

            appliedColor = config.Color;
            appliedSize  = config.Size;
            Size         = new(appliedSize);
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();

            imageNode.Size = Size;
            screenOffset   = Size * 0.5f;
            Origin         = screenOffset;
        }

        protected override void OnUpdate()
        {
            IsVisible = false;

            if (!moduleConfig.Enabled || !frameState.Ready)
                return;

            if ((moduleConfig.OnlyInCombat && !frameState.InCombat) ||
                (moduleConfig.OnlyInDuty   && !frameState.InDuty))
                return;

            var position           = frameState.LocalPlayerPosition;
            var rotation           = frameState.LocalPlayerRotation;
            var isWeaponUnsheathed = frameState.IsWeaponUnsheathed;

            if (memberIndex >= 0)
            {
                if (isAlliance)
                {
                    if (!frameState.IsAlliance)
                        return;
                }
                else if (memberIndex >= frameState.PartyMemberCount)
                    return;

                var group = GroupManager.Instance()->GetGroupWithCheck();
                if (group == null)
                    return;

                var member = isAlliance ?
                                 group->GetAllianceMemberByIndex(memberIndex) :
                                 group->GetPartyMemberByIndex(memberIndex);
                if (member == null || member->EntityId is 0 or 0xE0000000 || member->EntityId == frameState.LocalPlayerEntityID)
                    return;

                if (isAlliance && group->IsEntityIdInParty(member->EntityId))
                    return;

                var player = (Character*)GameObjectManager.Instance()->Objects.GetObjectByEntityId(member->EntityId);
                if (player == null || !player->IsReadyToDraw())
                    return;

                position           = player->Position;
                rotation           = player->Rotation;
                isWeaponUnsheathed = player->IsWeaponDrawn;
            }

            if (moduleConfig.OnlyUnsheathed && !isWeaponUnsheathed)
                return;

            var offset = moduleConfig.Offset;

            if (offset.X != 0 || offset.Z != 0)
            {
                var (sin, cos) = MathF.SinCos(-rotation);
                offset         = new((cos * offset.X) - (sin * offset.Z), offset.Y, (sin * offset.X) + (cos * offset.Z));
            }

            position += offset;

            var sizeChanged = !appliedSize.Equals(moduleConfig.Size);

            if (sizeChanged)
            {
                appliedSize = moduleConfig.Size;
                Size        = new(appliedSize);
            }

            if (appliedColor != moduleConfig.Color)
            {
                appliedColor    = moduleConfig.Color;
                imageNode.Color = appliedColor;
            }

            imageNode.IconId = moduleConfig.IconID;

            if (sizeChanged || worldPosition != position || projectionRevision != frameState.ProjectionRevision)
            {
                worldPosition      = position;
                projectionRevision = frameState.ProjectionRevision;
                onScreen           = frameState.Projection.WorldToScreen(position, out var screenPos);
                if (onScreen)
                    Position = screenPos - screenOffset;
            }

            IsVisible = onScreen;
        }
    }

    private class Config : DotConfig
    {
        public DotConfig Party = new()
        {
            Enabled = false,
            IconID  = PARTY_ICON_ID
        };

        public DotConfig Alliance = new()
        {
            Enabled = false,
            IconID  = ALLIANCE_ICON_ID
        };
    }

    private class DotConfig : ModuleConfig
    {
        public bool    Enabled      = true;
        public float   Size         = 64f;
        public Vector4 Color        = new(1f, 1f, 1f, 1f);
        public uint    IconID       = SELF_ICON_ID;
        public Vector3 Offset       = Vector3.Zero;
        public bool    OnlyInCombat = true;
        public bool    OnlyInDuty   = true;
        public bool    OnlyUnsheathed;
    }

    #region 常量

    private const int  PARTY_MEMBER_COUNT    = 8;
    private const int  ALLIANCE_MEMBER_COUNT = 20;
    private const uint SELF_ICON_ID          = 60422;
    private const uint PARTY_ICON_ID         = 60403;
    private const uint ALLIANCE_ICON_ID      = 60424;

    #endregion
}
