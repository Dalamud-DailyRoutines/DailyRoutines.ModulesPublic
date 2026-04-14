using System.Diagnostics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoNotifyCutsceneEnd : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyCutsceneEndTitle"),
        Description = Lang.Get("AutoNotifyCutsceneEndDescription"),
        Category    = ModuleCategory.Notice
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private Config config = null!;

    private bool       isDutyEnd;
    private Stopwatch? stopwatch;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        stopwatch  ??= new();
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      += OnDutyComplete;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;

        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().DutyState.DutyCompleted      -= OnDutyComplete;

        ClearResources();
        stopwatch = null;
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("SendChat"), ref config.SendChat))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendNotification"), ref config.SendNotification))
            config.Save(this);

        if (ImGui.Checkbox(Lang.Get("SendTTS"), ref config.SendTTS))
            config.Save(this);
    }

    private void OnZoneChanged(ushort zone)
    {
        ClearResources();

        if (GameState.ContentFinderCondition == 0 || GameState.IsInPVPArea) return;

        TaskHelper.Abort();
        TaskHelper.Enqueue
        (
            () =>
            {
                if (DService.Instance().Condition.IsBetweenAreas || LocalPlayerState.Object == null) return false;

                if (GroupManager.Instance()->MainGroup.MemberCount < 2)
                {
                    TaskHelper.Abort();
                    return true;
                }

                DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_PartyList", OnAddon);
                return true;
            },
            "检查是否需要开始监控"
        );
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag                             != ConditionFlag.InCombat ||
            GameState.ContentFinderCondition == 0                      ||
            GameState.IsInPVPArea                                      ||
            GroupManager.Instance()->MainGroup.MemberCount < 2)
            return;

        if (!value)
        {
            if (isDutyEnd) return;

            DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_PartyList", OnAddon);
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        // 不应该吧
        var agent = AgentHUD.Instance();
        if (agent == null) return;

        // 不在副本内 / PVP / 副本已经结束 / 少于两个真人玩家 → 结束检查
        if (GameState.ContentFinderCondition == 0 ||
            GameState.IsInPVPArea                 ||
            isDutyEnd                             ||
            GroupManager.Instance()->MainGroup.MemberCount < 2)
        {
            ClearResources();
            return;
        }

        // 本地玩家为空, 暂时不检查
        if (LocalPlayerState.Object == null) return;

        if (DService.Instance().Condition[ConditionFlag.InCombat])
        {
            // 进战时还在检查
            if (stopwatch.IsRunning)
                CheckStopwatchAndRelay();

            DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
            return;
        }

        // 计时器运行中
        if (stopwatch.IsRunning)
        {
            // 检查是否任一玩家仍在剧情状态
            if (IsAnyPartyMemberWatchingCutscene(agent))
                return;

            CheckStopwatchAndRelay();
        }
        else
        {
            // 居然无一人正在看剧情
            if (!IsAnyPartyMemberWatchingCutscene(agent))
                return;

            stopwatch.Restart();
        }
    }

    private void OnDutyComplete(object? sender, ushort zone) =>
        isDutyEnd = true;

    private void CheckStopwatchAndRelay()
    {
        if (!stopwatch.IsRunning || !Throttler.Shared.Throttle("AutoNotifyCutsceneEnd-Relay", 1_000)) return;

        var elapsedTime = stopwatch.Elapsed;
        stopwatch.Reset();

        // 小于四秒 → 不播报
        if (elapsedTime < TimeSpan.FromSeconds(4)) return;

        var message = $"{Lang.Get("AutoNotifyCutsceneEnd-NotificationMessage")}";
        if (config.SendChat)
            NotifyHelper.Instance().Chat($"{message} {Lang.Get("AutoNotifyCutsceneEnd-NotificationMessage-WaitSeconds", $"{elapsedTime.TotalSeconds:F0}")}");
        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo($"{message} {Lang.Get("AutoNotifyCutsceneEnd-NotificationMessage-WaitSeconds", $"{elapsedTime.TotalSeconds:F0}")}");
        if (config.SendTTS)
            NotifyHelper.Speak(message);
    }

    private static bool IsAnyPartyMemberWatchingCutscene(AgentHUD* agent)
    {
        if (agent == null) return false;

        var group = GroupManager.Instance()->MainGroup;
        if (group.MemberCount < 2) return false;

        foreach (var member in agent->PartyMembers)
        {
            if (member.EntityId  == 0 ||
                member.ContentId == 0 ||
                member.Object    == null)
                continue;

            if (!DService.Instance().DutyState.IsDutyStarted &&
                !member.Object->GetIsTargetable())
                return true;

            if (member.Object->OnlineStatus == 15)
                return true;
        }

        return false;
    }

    private void ClearResources()
    {
        TaskHelper?.Abort();
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        stopwatch?.Reset();
        isDutyEnd = false;
    }

    private class Config : ModuleConfig
    {
        public bool SendChat         = true;
        public bool SendNotification = true;
        public bool SendTTS          = true;
    }
}
