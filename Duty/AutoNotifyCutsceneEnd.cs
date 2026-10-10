using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class AutoNotifyCutsceneEnd : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyCutsceneEndTitle"),
        Description = Lang.Get("AutoNotifyCutsceneEndDescription"),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };
    
    private long cutsceneBeginTick;
    private long cutsceneLastWatchingTick;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        ICondition.Instance().ConditionChange    += OnConditionChanged;

        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        ICondition.Instance().ConditionChange    -= OnConditionChanged;
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;

        ClearResources();
    }

    private void OnZoneChanged
    (
        uint zone
    )
    {
        ClearResources();

        if (GameState.ContentFinderCondition == 0 || GameState.IsInPVPArea) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue
        (
            () =>
            {
                if (ICondition.Instance().IsBetweenAreas || LocalPlayerState.Object == null) return false;

                if (GroupManager.Instance()->MainGroup.MemberCount < 2)
                {
                    TaskHelper.Abort();
                    return true;
                }

                IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "_PartyList", OnAddon);
                return true;
            },
            "检查是否需要开始监控"
        );
    }

    private void OnConditionChanged
    (
        ConditionFlag flag,
        bool          value
    )
    {
        if (flag                             != ConditionFlag.InCombat ||
            GameState.ContentFinderCondition == 0                      ||
            GameState.IsInPVPArea                                      ||
            GroupManager.Instance()->MainGroup.MemberCount < 2)
            return;

        if (!value)
        {
            if (GameState.IsDutyCompleted) 
                return;

            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRequestedUpdate, "_PartyList", OnAddon);
        }
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        // 不应该吧
        var agent = AgentHUD.Instance();
        if (agent == null) return;

        // 不在副本内 / PVP / 副本已经结束 / 少于两个真人玩家 → 结束检查
        if (GameState.ContentFinderCondition == 0 ||
            GameState.IsInPVPArea                 ||
            GameState.IsDutyCompleted             ||
            GroupManager.Instance()->MainGroup.MemberCount < 2)
        {
            ClearResources();
            return;
        }

        // 本地玩家为空, 暂时不检查
        if (LocalPlayerState.Object == null) return;

        if (ICondition.Instance()[ConditionFlag.InCombat])
        {
            // 进战时还在检查
            if (cutsceneBeginTick != 0)
                RelayCutsceneEnd();

            IAddonLifecycle.Instance().UnregisterListener(OnAddon);
            return;
        }

        var now = Environment.TickCount64;

        if (IsAnyPartyMemberWatchingCutscene(agent))
        {
            if (cutsceneBeginTick == 0)
                cutsceneBeginTick = now;

            cutsceneLastWatchingTick = now;
            return;
        }

        if (cutsceneBeginTick == 0) return;

        // 在线状态会瞬时抖动, 需要持续一段时间确认无人观看
        if (now - cutsceneLastWatchingTick < 1_000) return;

        RelayCutsceneEnd();
    }

    private void RelayCutsceneEnd()
    {
        var elapsedTime = TimeSpan.FromMilliseconds(cutsceneLastWatchingTick - cutsceneBeginTick);

        cutsceneBeginTick        = 0;
        cutsceneLastWatchingTick = 0;

        // 小于四秒 → 不播报
        if (elapsedTime < TimeSpan.FromSeconds(4)) 
            return;
        
        NotifyHelper.Instance().TrayInfo(Lang.Get("AutoNotifyCutsceneEnd-Notification"));
        NotifyHelper.Instance().Chat
        (
            Lang.Get
            (
                "AutoNotifyCutsceneEnd-Message",
                new Dictionary<string, object>
                {
                    ["seconds"] = (int)elapsedTime.TotalSeconds
                }
            ),
            false
        );
    }

    private static bool IsAnyPartyMemberWatchingCutscene
    (
        AgentHUD* agent
    )
    {
        if (agent == null) return false;

        ref var group = ref GroupManager.Instance()->MainGroup;
        if (group.MemberCount < 2) return false;

        // 0x10 为服务器同步的过场动画中标志
        for (var i = 0; i < group.MemberCount; i++)
        {
            if ((group.PartyMembers[i].Flags & 0x10) != 0)
                return true;
        }
        
        foreach (var member in agent->PartyMembers)
        {
            if (member.EntityId  == 0 ||
                member.ContentId == 0)
                continue;

            // 对象尚未创建, 说明该成员还没加载出来
            if (member.Object == null)
                return true;

            if (!GameState.IsDutyStarted && !member.Object->GetIsTargetable())
                return true;

            // 过场动画中
            if (member.Object->OnlineStatus == 15)
                return true;
        }

        return false;
    }

    private void ClearResources()
    {
        TaskHelper?.Abort();
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        cutsceneBeginTick        = 0;
        cutsceneLastWatchingTick = 0;
    }
}
