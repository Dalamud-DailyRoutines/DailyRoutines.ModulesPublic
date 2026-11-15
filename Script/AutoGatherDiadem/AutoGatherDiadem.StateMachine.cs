using System.Numerics;
using DailyRoutines.Manager;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;
using Action = Lumina.Excel.Sheets.Action;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using AgentHwdGathererInspection = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentHwdGathererInspection;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using HWDManager = FFXIVClientStructs.Expansion.FFXIV.Client.Game.HWDManager;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    private void OnUpdate_StateMachine
    (
        IFramework framework
    )
    {
        if (machine.CurrentPhase == Phase.Idle ||
            !GameState.IsLoggedIn              ||
            !UIModule.IsScreenReady()          ||
            LocalPlayerState.Object == null)
            return;

        var now = Environment.TickCount64;

        if (now < machine.ResumeTick)
            return;

        if (GameState.TerritoryType == ZONE_DIADEM &&
            isOnAutoGather)
        {
            if (!JobInfos.ContainsKey(LocalPlayerState.ClassJob) ||
                !vnavmeshIPC.IsPluginEnabled())
            {
                StopAutomation();
                return;
            }

            if (Gathering->IsAddonAndNodesReady())
            {
                if (machine.CurrentPhase != Phase.Gathering &&
                    machine.CurrentPhase != Phase.Moving)
                {
                    machine.SetPhase(Phase.Gathering);
                    machine.GatherActionsPrepared = false;
                }

                if (machine.CurrentPhase == Phase.Gathering)
                    HandleGathering();
                return;
            }

            if (ICondition.Instance()[ConditionFlag.Gathering])
                return;

            if (machine.CurrentPhase is not (Phase.Leaving or Phase.Registering) &&
                Inventories.Player.IsFull())
            {
                var message = Lang.Get("AutoGatherDiadem-Notification-InventoryFull");
                NotifyHelper.Instance().Chat(message);
                NotifyHelper.Instance().TrayWarning(message, Lang.Get("AutoGatherDiadem-Notification-InventoryFull-Title"));
                BeginLeave(false, false);
                return;
            }

            if (machine.CurrentPhase is not (Phase.Leaving or Phase.Registering) &&
                !HasAugerEnergy())
            {
                var director = EventFramework.Instance()->GetContentDirector();

                if (director                  != null &&
                    director->ContentTimeLeft <= (180 * 60) - (config.RoundMinute * 60))
                {
                    NotifyHelper.Instance().Chat
                    (
                        Lang.Get
                        (
                            "AutoGatherDiadem-Notification-RoundEnd",
                            new Dictionary<string, object>
                            {
                                ["minutes"] = config.RoundMinute
                            }
                        )
                    );
                    BeginLeave(true, true);
                    return;
                }
            }
        }

        if (now - machine.PhaseStartedTick >= 180_000 &&
            machine.CurrentPhase is not (Phase.Searching or Phase.Registering or Phase.Leaving or Phase.Moving))
        {
            StopActivePathfind();
            if (GameState.TerritoryType == ZONE_DIADEM)
                BeginLeave(false, isOnAutoGather);
            else
                StopAutomation();
            return;
        }

        switch (machine.CurrentPhase)
        {
            case Phase.Registering:
                HandleRegistering();
                break;

            case Phase.Leaving:
                HandleLeaving();
                break;

            case Phase.Inspecting:
            case Phase.Purchasing:
            case Phase.Discarding:
                HandleExchange();
                break;

            case Phase.Equipping:
                HandleEquipping();
                break;

            case Phase.Auger:
                if (!ICondition.Instance().IsCasting)
                    machine.SetPhase(Phase.Searching);
                break;

            case Phase.Windmire:
                if (!ICondition.Instance()[ConditionFlag.Jumping61])
                    machine.SetPhase(Phase.Searching);
                break;

            case Phase.Moving:
                HandleMoving();
                break;

            case Phase.Interacting:
                HandleInteracting();
                break;

            case Phase.Gathering:
                machine.SetPhase(Phase.Searching);
                break;

            case Phase.Searching:
                HandleSearching();
                break;
        }
    }

    private void HandleRegistering()
    {
        if (!isOnAutoGather)
        {
            machine.SetPhase(Phase.Idle);
            return;
        }

        if (GameState.TerritoryType == ZONE_DIADEM)
        {
            machine.SetPhase(Phase.Searching);
            return;
        }

        if (!ICondition.Instance().IsOccupiedInEvent &&
            Throttler.Shared.Throttle("AutoGatherDiadem.Enter", 3000))
            ChatManager.Instance().SendMessage("/pdrfe Diadem");
    }

    private void BeginLeave
    (
        bool inspect,
        bool resume
    )
    {
        StopActivePathfind();

        machine.InspectAfterLeaving = inspect;
        machine.ResumeAfterExchange = resume;

        machine.SetPhase(Phase.Leaving);

        isOnAutoGather = resume;
    }

    private void HandleLeaving()
    {
        if (GameState.TerritoryType == ZONE_DIADEM)
        {
            if (!ICondition.Instance().IsOccupiedInEvent &&
                Throttler.Shared.Throttle("AutoGatherDiadem.Leave", 3000))
                DutyCommand.Leave(DutyCommand.LeaveDutyKind.Inactive);
            return;
        }

        if (GameState.TerritoryType != ZONE_FIRMAMENT ||
            ICondition.Instance().IsOccupiedInEvent)
            return;

        if (machine.InspectAfterLeaving)
            BeginExchange(machine.ResumeAfterExchange);
        else
            machine.SetPhase
            (
                machine.ResumeAfterExchange ?
                    Phase.Registering :
                    Phase.Idle
            );
    }

    private void HandleSearching()
    {
        if (GameState.TerritoryType != ZONE_DIADEM)
        {
            machine.SetPhase(Phase.Registering);
            return;
        }

        if (!vnavmeshIPC.IsNavReady                 ||
            ICondition.Instance().IsOccupiedInEvent ||
            ICondition.Instance().IsCasting         ||
            MovementManager.Instance().IsManagerBusy)
            return;

        Overlay.IsOpen = true;

        var jobInfo = JobInfos[LocalPlayerState.ClassJob];

        if (!LocalPlayerState.HasStatus(jobInfo.BaseDetectStatus, out _) &&
            !TryFindNearestGatherPoint(out _, out _))
        {
            if (UseActionManager.Instance().UseAction(ActionType.Action, jobInfo.BaseDetectAction))
                machine.ResumeTick = Environment.TickCount64 + 1000;
            return;
        }

        if (!ICondition.Instance().IsOnMount &&
            machine.AugerAttempts < 3        &&
            HasAugerEnergy()                 &&
            TryFindNearestMonster(out var monster))
        {
            TargetManager.Target = monster;
            machine.AugerAttempts++;

            if (UseActionManager.Instance().UseAction(ActionType.Action, 19700, monster.EntityID))
            {
                machine.SetPhase(Phase.Auger);
                machine.ResumeTick = Environment.TickCount64 + 3000;
            }

            return;
        }

        machine.AugerAttempts = 0;

        if (TryGetAnyCordialToUse(out var cordial))
        {
            AgentInventoryContext.Instance()->UseItem(cordial.Value.GetItemId(), cordial.Value.GetInventoryType(), cordial.Value.GetSlot());
            machine.ResumeTick = Environment.TickCount64 + 1000;
            return;
        }

        if (config.Mode == GatheringMode.绿材)
        {
            HandleSpecialWeather();
            return;
        }

        if (UpdateGatheringJob())
            return;

        if (TryFindNearestGatherPoint(out var position, out _))
        {
            BeginMove(position, false);
            return;
        }

        if (TryGetMiniGatheringMarker(out var marker, out _))
        {
            BeginMove(marker, true);
            return;
        }

        if (!LocalPlayerState.HasStatus(jobInfo.DetectStatus, out _)                               &&
            ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, jobInfo.DetectAction) &&
            UseActionManager.Instance().UseAction(ActionType.Action, jobInfo.DetectAction))
            machine.ResumeTick = Environment.TickCount64 + 1000;
    }

    private void HandleSpecialWeather()
    {
        var weather   = WeatherManager.Instance()->GetCurrentWeather();
        var nearSpawn = LocalPlayerState.DistanceTo2DSquared(ZoneDefaultPosition.ToVector2()) < 2500;

        if (config.BlacklistWeathers.Contains(weather) ||
            !SpecialWeathers.TryGetValue(weather, out var job))
        {
            if (!nearSpawn)
                BeginLeave(false, true);
            else
                machine.ResumeTick = Environment.TickCount64 + 10_000;
            return;
        }

        if (LocalPlayerState.ClassJob != job)
        {
            TrySwitchJob(job);
            return;
        }

        if (TryFindNearestGatherPoint(out var position, out _))
        {
            BeginMove(position, false);
            return;
        }

        if (TryGetMiniGatheringMarker(out var marker, out var type))
        {
            if (type == GatheringNodeType.普通)
                BeginLeave(false, true);
            else
                BeginMove(marker, true);
            return;
        }

        var action = JobInfos[job].DetectPremiumAction;

        if (!nearSpawn                                                               &&
            ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, action) &&
            UseActionManager.Instance().UseAction(ActionType.Action, action))
            machine.ResumeTick = Environment.TickCount64 + 1000;
    }

    private bool UpdateGatheringJob()
    {
        var job = LocalPlayerState.ClassJob;

        var otherJob = job == 16 ?
                           17U :
                           16U;

        if (config.Mode == GatheringMode.练级)
        {
            if (config.LevelThresholds.Count == 0)
            {
                StopAutomation();
                return true;
            }

            var level      = LocalPlayerState.GetClassJobLevel(job);
            var otherLevel = LocalPlayerState.GetClassJobLevel(otherJob);
            var max        = config.LevelThresholds.Max();

            if (level      >= max &&
                otherLevel >= max)
            {
                BeginLeave(false, false);
                return true;
            }

            var threshold      = config.LevelThresholds.Where(value => level      >= value).DefaultIfEmpty(0U).Max();
            var otherThreshold = config.LevelThresholds.Where(value => otherLevel >= value).DefaultIfEmpty(0U).Max();

            return threshold > otherThreshold &&
                   TrySwitchJob(otherJob);
        }

        if (config.IgnoreSkillPoints)
            return false;

        if (!AchievementManager.Instance().TryGetAchievement(JobInfos[job].Achievement500K,      out var current) ||
            !AchievementManager.Instance().TryGetAchievement(JobInfos[otherJob].Achievement500K, out var other))
            return true;

        if (current.IsFinished &&
            other.IsFinished)
        {
            BeginLeave(false, false);
            return true;
        }

        return current.Current >= 400000                      &&
               (other.Current < 400000 || current.IsFinished) &&
               TrySwitchJob(otherJob);
    }

    private bool TrySwitchJob
    (
        uint job
    )
    {
        if (LocalPlayerState.GetClassJobLevel(job) < 10 ||
            !LocalPlayerState.TryFindClassJobGearset(job, out var gearset))
            return false;

        StopActivePathfind();

        if (Throttler.Shared.Throttle("AutoGatherDiadem.SwitchJob", 1500))
            LocalPlayerState.SwitchGearset(gearset);

        machine.ResumeTick = Environment.TickCount64 + 1500;
        return true;
    }

    private void HandleEquipping()
    {
        var instance = RecommendEquipModule.Instance();

        if (!machine.EquipmentRequested)
        {
            instance->SetupForClassJob((byte)LocalPlayerState.ClassJob);
            machine.EquipmentRequested = true;
            machine.ResumeTick         = Environment.TickCount64 + 100;
            return;
        }

        if (instance->IsUpdating)
            return;

        instance->EquipRecommendedGear();
        machine.EquipmentRequested = false;
        machine.SetPhase(Phase.Searching);
    }

    private void HandleGathering()
    {
        var now   = Environment.TickCount64;
        var state = ReadGatheringState();

        if (ICondition.Instance()[ConditionFlag.ExecutingGatheringAction])
            return;

        switch (state.State)
        {
            case GatheringState.等待加载:
            case GatheringState.采集完毕:
                return;
            case GatheringState.无法采集:
                Gathering->Close(true);
                BeginMove(RedirectPosition, false, true);
                return;
        }

        if (!machine.GatherActionsPrepared)
        {
            PrepareGatherActions(state.Count);
            machine.GatherActionsPrepared = true;
        }

        if (machine.GatherActions.TryPeek(out var action))
        {
            if (UseActionManager.Instance().UseActionLocation(ActionType.Action, action))
            {
                machine.GatherActions.Dequeue();
                machine.ResumeTick = now + 700;
            }

            return;
        }

        if (!state.QuickGatheringEnabled)
        {
            Gathering->Callback(130, true);
            return;
        }

        if (Throttler.Shared.Throttle("AutoGatherDiadem.Gather"))
        {
            Gathering->AtkValues->SetUInt(0);
            Gathering->CallbackNoUpdate(state.Index);
        }
    }

    private void PrepareGatherActions
    (
        uint count
    )
    {
        machine.GatherActions.Clear();

        var player = Control.GetLocalPlayer();

        if (player == null)
            return;

        var info = JobInfos[LocalPlayerState.ClassJob];
        var gp   = (int)player->GatheringPoints;

        if (player->MaxGatheringPoints >= 850)
        {
            if (gp >= 850)
            {
                foreach (var action in info.ActionsAbove850)
                {
                    if (ActionManager.IsActionUnlocked(action))
                        machine.GatherActions.Enqueue(action);
                }

                return;
            }

            if (TryGetAnyCordialToUse(out _)                     ||
                (config.UseCordial && IsAnyCordialOffCooldown()) ||
                gp + (6 * count) + 50 < player->MaxGatheringPoints)
                return;

            foreach (var action in info.ActionsSingle)
            {
                if (!ActionManager.IsActionUnlocked(action)          ||
                    !LuminaGetter.TryGetRow(action, out Action data) ||
                    data.PrimaryCostValue > gp)
                    continue;

                machine.GatherActions.Enqueue(action);
                break;
            }

            return;
        }

        foreach (var action in info.ActionsAbove850.Concat(info.ActionsSingle))
        {
            if (!ActionManager.IsActionUnlocked(action)          ||
                !LuminaGetter.TryGetRow(action, out Action data) ||
                data.PrimaryCostValue > gp)
                continue;

            gp -= data.PrimaryCostValue;
            machine.GatherActions.Enqueue(action);
        }
    }

    private void BeginExchange
    (
        bool resume
    )
    {
        StopActivePathfind();

        machine.ResumeAfterExchange     = resume;
        machine.ExchangeStage           = ExchangeStep.Opening;
        machine.InspectionCategory      = 0;
        machine.InspectionNeedsPurchase = false;

        machine.SetPhase(Phase.Inspecting);
    }

    private void HandleExchange()
    {
        if (GameState.TerritoryType != ZONE_FIRMAMENT)
            return;

        switch (machine.CurrentPhase)
        {
            case Phase.Discarding:
                if (IsAutoDiscardModuleBusy ?? false)
                    return;

                machine.ExchangeStage = ExchangeStep.Opening;
                machine.SetPhase(Phase.Purchasing);
                return;

            case Phase.Purchasing:
                HandlePurchasing();
                return;
        }

        var agent   = AgentHwdGathererInspection.Instance();
        var manager = HWDManager.Instance();

        if (agent   == null ||
            manager == null)
            return;

        switch (machine.ExchangeStage)
        {
            case ExchangeStep.Opening:
                if (agent->IsAgentActive())
                {
                    if (agent->Data != null &&
                        agent->Data->IsDataLoaded)
                        machine.ExchangeStage = ExchangeStep.Processing;

                    return;
                }

                if (!ICondition.Instance().IsOccupiedInEvent &&
                    Throttler.Shared.Throttle("AutoGatherDiadem.OpenInspection", 1000))
                    new EventStartPackt(LocalPlayerState.EntityID, 721556).Send();
                break;

            case ExchangeStep.Processing:
                if (manager->IsInspectionRequestPending ||
                    agent->Data == null                 ||
                    !agent->Data->IsDataLoaded)
                    return;

                if (agent->Data->ResetTimestamp > Framework.GetServerTime())
                {
                    machine.ResumeTick = Environment.TickCount64 + 1000;
                    return;
                }

                var category = (byte)machine.InspectionCategory;

                if (agent->Data->SelectedCategoryIndex != category ||
                    agent->Data->SelectedCategory      != category)
                {
                    agent->SelectCategory(category);
                    machine.ResumeTick = Environment.TickCount64 + 100;
                    return;
                }

                if (agent->Data->SlotCount         == 0 &&
                    agent->CountSubmittableItems() == 0)
                {
                    machine.InspectionCategory++;

                    if (machine.InspectionCategory >= INSPECTION_CATEGORIES)
                    {
                        machine.InspectionNeedsPurchase = false;
                        machine.ExchangeStage           = ExchangeStep.Closing;
                    }

                    return;
                }

                agent->AutoFillSlots();

                if (agent->Data->SlotCount == 0)
                {
                    machine.ResumeTick = Environment.TickCount64 + 100;
                    return;
                }

                agent->SubmitSelectedItems();

                if (manager->IsInspectionRequestPending)
                    machine.ExchangeStage = ExchangeStep.AwaitingInspection;
                else
                    machine.ResumeTick = Environment.TickCount64 + 100;
                break;

            case ExchangeStep.AwaitingInspection:
                if (manager->IsInspectionRequestPending)
                    return;

                machine.ExchangeStage = ExchangeStep.Processing;
                break;

            case ExchangeStep.Closing:
                if (agent->IsAgentActive())
                {
                    agent->Hide();
                    return;
                }

                if (ICondition.Instance().IsOccupiedInEvent)
                    return;

                if (machine.InspectionNeedsPurchase)
                {
                    machine.ExchangeStage = ExchangeStep.Opening;
                    machine.SetPhase(Phase.Purchasing);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(config.AutoDiscardGroupName) &&
                    ModuleManager.Instance().IsModuleEnabled("AutoDiscard") == true)
                {
                    ChatManager.Instance().SendMessage($"/pdrdiscard {config.AutoDiscardGroupName}");
                    machine.SetPhase(Phase.Discarding);
                    machine.ResumeTick = Environment.TickCount64 + 500;
                    return;
                }

                machine.SetPhase(Phase.Purchasing);
                machine.ExchangeStage = ExchangeStep.Opening;
                break;
        }
    }

    private void HandlePurchasing()
    {
        var item = config.ExchangeItem;

        if (item == null)
        {
            if (machine.InspectionNeedsPurchase)
                StopAutomation();
            else
                FinishExchange();
            return;
        }

        switch (machine.ExchangeStage)
        {
            case ExchangeStep.Opening:
                if (HWDGathereInspect != null ||
                    ICondition.Instance().IsOccupiedInEvent)
                    return;

                if (Throttler.Shared.Throttle("AutoGatherDiadem.OpenShop", 1000))
                {
                    new EventStartPackt(LocalPlayerState.EntityID, 721546).Send();
                    machine.ExchangeStage = ExchangeStep.Selecting;
                }

                break;

            case ExchangeStep.Selecting:
                if (ShopExchangeCurrency->IsAddonAndNodesReady())
                    machine.ExchangeStage = ExchangeStep.Processing;
                else if (Throttler.Shared.Throttle("AutoGatherDiadem.SelectShop"))
                    AddonSelectStringEvent.Select(item.GetShopIndex());
                break;

            case ExchangeStep.Processing:
                if (SelectYesno->IsAddonAndNodesReady())
                {
                    if (Throttler.Shared.Throttle("AutoGatherDiadem.ConfirmPurchase"))
                        AddonSelectYesnoEvent.ClickYes();
                    return;
                }

                if (!ShopExchangeCurrency->IsAddonAndNodesReady())
                    return;

                if (item.GetAvailablePurchaseCount() == 0)
                    machine.ExchangeStage = ExchangeStep.Closing;
                else if (Throttler.Shared.Throttle("AutoGatherDiadem.Purchase", 1000))
                    item.Purchase();
                break;

            case ExchangeStep.Closing:
                if (ShopExchangeCurrency != null)
                {
                    ShopExchangeCurrency->Close(true);
                    return;
                }

                if (machine.InspectionNeedsPurchase)
                {
                    if (CurrencyManager.Instance()->GetItemCount(SKYBUILDERS_SCRIP_ITEM_ID) >= machine.InspectionScripsBeforePurchase)
                    {
                        StopAutomation();
                        return;
                    }

                    machine.SetPhase(Phase.Inspecting);
                    machine.ExchangeStage = ExchangeStep.Opening;
                }
                else
                    FinishExchange();

                break;
        }
    }

    private void FinishExchange() =>
        machine.SetPhase
        (
            machine.ResumeAfterExchange ?
                Phase.Registering :
                Phase.Idle
        );

    private void StopAutomation()
    {
        StopActivePathfind();
        machine.Reset();

        isOnAutoGather = false;
        lastLevel      = 0;
    }

    private static bool HasAugerEnergy() =>
        HWDAetherGauge->IsAddonAndNodesReady() &&
        HWDAetherGauge->AtkValues[1].Int >= 200;

    private sealed class StateMachineContext
    {
        public Phase CurrentPhase     { get; private set; }
        public long  PhaseStartedTick { get; private set; }
        public long  ResumeTick       { get; set; }

        public int AugerAttempts { get; set; }

        public bool        GatherActionsPrepared { get; set; }
        public Queue<uint> GatherActions         { get; } = new();

        public bool EquipmentRequested { get; set; }

        public bool InspectAfterLeaving { get; set; }
        public bool ResumeAfterExchange { get; set; }

        public ExchangeStep ExchangeStage                  { get; set; }
        public int          InspectionCategory             { get; set; }
        public bool         InspectionNeedsPurchase        { get; set; }
        public uint         InspectionScripsBeforePurchase { get; set; }

        public Vector3 Destination { get; set; }
        public bool    IsMarker    { get; set; }
        public bool    IsRedirect  { get; set; }
        public bool    IsWindmire  { get; set; }

        public void SetPhase
        (
            Phase phase
        )
        {
            CurrentPhase     = phase;
            PhaseStartedTick = Environment.TickCount64;
            ResumeTick       = 0;
        }

        public void Reset()
        {
            SetPhase(Phase.Idle);

            AugerAttempts = 0;

            GatherActionsPrepared = false;
            GatherActions.Clear();

            EquipmentRequested = false;

            InspectAfterLeaving = false;
            ResumeAfterExchange = false;

            ExchangeStage                  = default;
            InspectionCategory             = 0;
            InspectionNeedsPurchase        = false;
            InspectionScripsBeforePurchase = 0;

            Destination = default;
            IsMarker    = false;
            IsRedirect  = false;
            IsWindmire  = false;
        }
    }

    #region 常量

    private const int  INSPECTION_CATEGORIES     = 3;
    private const uint SKYBUILDERS_SCRIP_ITEM_ID = 28063;

    #endregion
}
