using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using OmenTools.Info.Game.Packets.Upstream;

namespace DailyRoutines.ModulesPublic;

public class AutoJoinKeyboundBrawler : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoJoinKeyboundBrawlerTitle"),
        Description = Lang.Get("AutoJoinKeyboundBrawlerDescription"),
        Category    = ModuleCategory.GoldSaucer,
        ModulesPrerequisite =
        [
            "AutoKeyboundBrawler",
            "AutoCommenceDuty"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        NeedAuth = true
    };

    public override string ModuleName => nameof(AutoJoinKeyboundBrawler);

    private Config config = null!;

    private bool isRunning;
    private long nextInteractionAt;

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();

        TaskHelper = new()
        {
            TimeoutMS       = 0,
            RetryIntervalMS = 500
        };

        IClientState.Instance().TerritoryChanged += OnTerritoryChanged;
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "KTGResult", OnResultSetup);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnTerritoryChanged;
        IAddonLifecycle.Instance().UnregisterListener(OnResultSetup);

        isRunning = false;
        TaskHelper.Abort();
    }

    protected override void ConfigUI()
    {
        ImGuiOm.ConflictKeyText();

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoJoinKeyboundBrawler-StopOnInvalidParty"), ref config.StopOnInvalidParty))
            config.Save(this);
        ImGuiOm.TextDisabledWrapped(Lang.Get("AutoJoinKeyboundBrawler-StopOnInvalidParty-Help"));

        ImGui.NewLine();

        using (ImRaii.Disabled(isRunning || GameState.TerritoryType != TERRITORY_ID))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
            {
                isRunning = true;
                OnTerritoryChanged(GameState.TerritoryType);
            }
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!isRunning))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
            {
                isRunning = false;
                TaskHelper.Abort();
            }
        }
    }

    private void OnTerritoryChanged
    (
        uint territory
    )
    {
        TaskHelper.Abort();
        nextInteractionAt = 0;

        if (isRunning &&
            territory == TERRITORY_ID)
            TaskHelper.Enqueue(RegisterForDuty);
    }

    private void OnResultSetup
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (isRunning)
            TaskHelper.Enqueue(FinishDuty);
    }

    private unsafe bool FinishDuty()
    {
        if (!isRunning)
            return true;

        var agent = AgentKTGResult.Instance();
        if (agent == null   ||
            !agent->IsShown ||
            !agent->HasAddon)
            return false;

        if (!agent->Finished)
            agent->Finish(false);

        return true;
    }

    private unsafe bool RegisterForDuty()
    {
        if (!isRunning ||
            GameState.TerritoryType != TERRITORY_ID)
            return true;

        if (TaskHelper.AbortByConflictKey(this))
        {
            isRunning = false;
            return true;
        }

        var condition = ICondition.Instance();
        if (!IClientState.Instance().IsLoggedIn   ||
            condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51])
            return false;

        var group = GroupManager.Instance()->GetGroup();

        if (config.StopOnInvalidParty &&
            (group->IsAlliance ||
             group->MemberCount is not (0 or 1 or 4)))
        {
            isRunning = false;
            TaskHelper.Abort();

            var notifyHelper = NotifyHelper.Instance();
            notifyHelper.ChatError(Lang.Get("AutoJoinKeyboundBrawler-Notice-Chat"));
            notifyHelper.TrayError
            (
                Lang.Get("AutoJoinKeyboundBrawler-Notice-TrayContent"),
                Lang.Get("AutoJoinKeyboundBrawler-Notice-TrayTitle")
            );
            return true;
        }

        if (condition[ConditionFlag.InDutyQueue] ||
            condition[ConditionFlag.WaitingForDutyFinder])
            return true;

        foreach (ref var member in group->PartyMembers[..group->MemberCount])
        {
            if (member.TerritoryType != TERRITORY_ID)
                return false;
        }

        var addon = (AddonKTGEnterDialogue*)KTGEnterDialogue;

        if (KTGEnterDialogue->IsAddonAndNodesReady())
        {
            addon->FireCallbackInt(0);
            nextInteractionAt = Environment.TickCount64 + REGISTRATION_RETRY_INTERVAL_MS;
            return false;
        }

        if (condition.IsOccupiedInEvent ||
            Environment.TickCount64 < nextInteractionAt)
            return false;

        new EventStartPackt(LocalPlayerState.EntityID, EVENT_ID).Send();
        nextInteractionAt = Environment.TickCount64 + REGISTRATION_RETRY_INTERVAL_MS;
        return false;
    }

    private class Config : ModuleConfig
    {
        public bool StopOnInvalidParty = true;
    }

    #region 常量

    private const uint TERRITORY_ID = 1360;

    private const uint EVENT_ID = 721882;
    
    private const int REGISTRATION_RETRY_INTERVAL_MS = 3000;

    #endregion
}
