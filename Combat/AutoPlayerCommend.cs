using DailyRoutines.Common.Info.Abstractions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using OmenTools.ImGuiOm.Widgets.Combos;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoPlayerCommend : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPlayerCommendTitle"),
        Description = Lang.Get("AutoPlayerCommendDescription"),
        Category    = ModuleCategory.Combat
    };
    
    private static uint MIPDisplayType
    {
        get => DService.Instance().GameConfig.UiConfig.GetUInt("MipDispType");
        set => DService.Instance().GameConfig.UiConfig.Set("MipDispType", value);
    }

    private          Config                       config = null!;
    private readonly AssignPlayerCommendationMenu menuItem;
    private readonly ContentSelectCombo           contentSelectCombo = new("Content");

    private ulong assignedContentID;

    public AutoPlayerCommend() =>
        menuItem = new(this);

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new() { TimeoutMS = 10_000 };

        contentSelectCombo.SelectedIDs = config.BlacklistContents;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().ContextMenu.OnMenuOpened     += OnMenuOpen;
        DService.Instance().DutyState.DutyCompleted      += OnDutyComplete;
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().ContextMenu.OnMenuOpened     -= OnMenuOpen;
        DService.Instance().DutyState.DutyCompleted      -= OnDutyComplete;

        assignedContentID = 0;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("AutoPlayerCommend-BlacklistContents")}");

        using (ImRaii.PushIndent())
        {
            ImGui.SetNextItemWidth(300f * GlobalUIScale);

            if (contentSelectCombo.DrawCheckbox())
            {
                config.BlacklistContents = contentSelectCombo.SelectedIDs.ToHashSet();
                config.Save(this);
            }
        }

        ImGui.NewLine();

        if (ImGui.Checkbox($"{Lang.Get("AutoPlayerCommend-BlockBlacklistPlayers")}", ref config.AutoIgnoreBlacklistPlayers))
            config.Save(this);
    }

    private void OnZoneChanged(ushort zone) =>
        assignedContentID = 0;

    private void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!menuItem.IsDisplay(args)) return;
        args.AddMenuItem(menuItem.Get());
    }

    private void OnDutyComplete(object? sender, ushort dutyZoneID)
    {
        if (TaskHelper.AbortByConflictKey(this)) return;
        if (config.BlacklistContents.Contains(GameState.ContentFinderCondition)) return;
        if (DService.Instance().PartyList.Length <= 1) return;

        var orig = MIPDisplayType;
        TaskHelper.Enqueue(() => MIPDisplayType = 0,    "设置最优队员推荐不显示列表");
        TaskHelper.Enqueue(OpenCommendWindow,           "打开最优队员推荐列表");
        TaskHelper.Enqueue(EnqueueCommendation,         "给予最优队员推荐");
        TaskHelper.Enqueue(() => MIPDisplayType = orig, "还原原始最优队友推荐设置");
    }

    private bool OpenCommendWindow()
    {
        var notification    = AddonHelper.GetByName("_Notification");
        var notificationMvp = AddonHelper.GetByName("_NotificationIcMvp");
        if (notification == null && notificationMvp == null) return true;

        if (assignedContentID == LocalPlayerState.ContentID)
            return true;

        notification->Callback(0, 11);
        return true;
    }

    private bool EnqueueCommendation()
    {
        if (!VoteMvp->IsAddonAndNodesReady()) return false;
        if (!AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsMvp)->IsAgentActive()) return false;

        if (assignedContentID == LocalPlayerState.ContentID)
            return true;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return false;

        var hudMembers = AgentHUD.Instance()->PartyMembers.ToArray();
        Dictionary<(string Name, uint HomeWorld, uint ClassJob, uint ClassJobCategory, byte RoleRaw, PlayerRole Role, ulong ContentID), int> partyMembers = [];

        foreach (var member in DService.Instance().PartyList)
        {
            if ((ulong)member.ContentId == LocalPlayerState.ContentID) continue;

            var index = Math.Clamp(hudMembers.IndexOf(x => x.ContentId == (ulong)member.ContentId) - 1, 0, 6);

            var rawRole = member.ClassJob.Value.Role;
            partyMembers[(member.Name.ToString(),
                             member.World.RowId,
                             member.ClassJob.RowId,
                             member.ClassJob.Value.ClassJobCategory.RowId,
                             rawRole,
                             GetCharacterJobRole(rawRole),
                             (ulong)member.ContentId)] = index;
        }

        if (partyMembers.Count == 0) return true;

        // 获取玩家自身职业和职能信息
        var selfRole     = GetCharacterJobRole(LocalPlayerState.ClassJobData.Role);
        var selfClassJob = LocalPlayerState.ClassJob;
        var selfCategory = LocalPlayerState.ClassJobData.ClassJobCategory.RowId;

        // 统计相同职业的数量
        var jobCounts = partyMembers
                        .GroupBy(x => x.Key.ClassJob)
                        .ToDictionary(g => g.Key, g => g.Count());

        // 优先级排序
        var playersToCommend = partyMembers
                               .Where
                               (x => !config.AutoIgnoreBlacklistPlayers ||
                                     InfoProxyBlacklist.Instance()->GetBlockResultType(x.Key.ContentID, 0) == InfoProxyBlacklist.BlockResultType.NotBlocked
                               )
                               // 优先已指定、职业相同或职能相同
                               .OrderByDescending
                               (x =>
                                   {
                                       if (assignedContentID != 0 &&
                                           x.Key.ContentID               == assignedContentID)
                                           return 3;

                                       if (selfClassJob == x.Key.ClassJob)
                                           return 2;

                                       // 同类型DPS (近战/远程) 有更高优先级
                                       if (selfRole is PlayerRole.MeleeDPS or PlayerRole.RangedDPS &&
                                           x.Key.Role is PlayerRole.MeleeDPS or PlayerRole.RangedDPS)
                                       {
                                           return selfRole     == x.Key.Role &&
                                                  selfCategory == x.Key.ClassJobCategory
                                                      ? 1
                                                      : 0;
                                       }

                                       // T / 奶
                                       if (LocalPlayerState.ClassJobData.Role == x.Key.RoleRaw)
                                           return 1;

                                       return 0;
                                   }
                               )
                               // 如果自身是DPS, 且队伍中有两个及以上相同的其他DPS职业，则降低它们的优先级
                               .ThenByDescending
                               (x =>
                                   {
                                       if (selfRole is PlayerRole.MeleeDPS or PlayerRole.RangedDPS   &&
                                           x.Key.Role is PlayerRole.MeleeDPS or PlayerRole.RangedDPS &&
                                           selfClassJob != x.Key.ClassJob                            &&
                                           jobCounts.TryGetValue(x.Key.ClassJob, out var count)      &&
                                           count >= 2)
                                           return 0;

                                       return 1;
                                   }
                               )
                               // 基于角色职能的优先级
                               .ThenByDescending
                               (x => selfRole switch
                                   {
                                       PlayerRole.Tank or PlayerRole.Healer
                                           => x.Key.Role
                                                  is PlayerRole.Tank or PlayerRole.Healer
                                                  ? 1
                                                  : 0,

                                       PlayerRole.MeleeDPS => x.Key.Role switch
                                       {
                                           PlayerRole.MeleeDPS  => 3,
                                           PlayerRole.RangedDPS => 2,
                                           PlayerRole.Healer    => 1,
                                           _                    => 0
                                       },

                                       PlayerRole.RangedDPS => x.Key.Role switch
                                       {
                                           PlayerRole.RangedDPS => 3,
                                           PlayerRole.MeleeDPS  => 2,
                                           PlayerRole.Healer    => 1,
                                           _                    => 0
                                       },
                                       _ => 0
                                   }
                               )
                               .Select
                               (x => new
                                   {
                                       x.Key.Name,
                                       x.Key.ClassJob,
                                       x.Key.HomeWorld
                                   }
                               )
                               .ToList();
        if (playersToCommend.Count == 0) return true;

        foreach (var memberInfo in playersToCommend)
        {
            if (!TryFindPlayerIndex(memberInfo.Name, memberInfo.ClassJob, out var playerIndex)) continue;
            if (!LuminaGetter.TryGetRow<ClassJob>(memberInfo.ClassJob, out var job)) continue;

            AgentId.ContentsMvp.SendEvent(0, 0, playerIndex);
            NotifyHelper.Instance().Chat
            (
                Lang.GetSe
                (
                    "AutoPlayerCommend-NoticeMessage",
                    new PlayerPayload(memberInfo.Name, memberInfo.HomeWorld),
                    job.ToBitmapFontIcon(),
                    job.Name.ToString()
                )
            );
            return true;
        }

        NotifyHelper.Instance().ChatError(Lang.Get("AutoPlayerCommend-ErrorWhenGiveCommendationMessage"));
        return true;

        bool TryFindPlayerIndex(string playerName, uint playerJob, out int playerIndex)
        {
            playerIndex = -1;

            var count = VoteMvp->AtkValues[1].UInt;

            for (var i = 0; i < count; i++)
            {
                var isEnabled = VoteMvp->AtkValues[16 + i].UInt == 1;
                if (!isEnabled) continue;

                var nameValue = VoteMvp->AtkValues[9 + i];
                if (nameValue.Type != ValueType.String || !nameValue.String.HasValue) continue;

                var name = nameValue.String.ToString();
                if (string.IsNullOrWhiteSpace(name) || name != playerName) continue;

                var classJob = VoteMvp->AtkValues[2 + i].UInt - 62100;
                if (classJob <= 0 || classJob != playerJob) continue;

                playerIndex = i;
                return true;
            }

            return false;
        }
    }

    private static PlayerRole GetCharacterJobRole(byte rawRole) =>
        rawRole switch
        {
            1 => PlayerRole.Tank,
            2 => PlayerRole.MeleeDPS,
            3 => PlayerRole.RangedDPS,
            4 => PlayerRole.Healer,
            _ => PlayerRole.None
        };
    
    private enum PlayerRole
    {
        Tank,
        Healer,
        MeleeDPS,
        RangedDPS,
        None
    }

    private class Config : ModuleConfig
    {
        public bool          AutoIgnoreBlacklistPlayers = true;
        public HashSet<uint> BlacklistContents          = [];
    }

    private class AssignPlayerCommendationMenu(AutoPlayerCommend module) : MenuItemBase
    {
        public override string Name       { get; protected set; } = Lang.Get("AutoPlayerCommend-AssignPlayerCommend");
        public override string Identifier { get; protected set; } = nameof(AutoPlayerCommend);

        public override bool IsDisplay(IMenuOpenedArgs args)
        {
            if (!DService.Instance().Condition[ConditionFlag.BoundByDuty]) return false;
            if (args.MenuType != ContextMenuType.Default    ||
                args.Target is not MenuTargetDefault target ||
                target.TargetCharacter == null && target.TargetContentId == 0) return false;

            return true;
        }

        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;
            if (target.TargetCharacter == null && target.TargetContentId == 0) return;

            var contentID   = target.TargetCharacter?.ContentId ?? target.TargetContentId;
            var playerName  = target.TargetCharacter != null ? target.TargetCharacter.Name : target.TargetName;
            var playerWorld = target.TargetCharacter?.HomeWorld ?? target.TargetHomeWorld;

            NotifyHelper.Instance().NotificationInfo
            (
                contentID == LocalPlayerState.ContentID
                    ? Lang.Get("AutoPlayerCommend-GiveNobodyCommendMessage")
                    : Lang.Get("AutoPlayerCommend-AssignPlayerCommendMessage", playerName, playerWorld.Value.Name.ToString())
            );

            module.assignedContentID = contentID;
        }
    }
}
