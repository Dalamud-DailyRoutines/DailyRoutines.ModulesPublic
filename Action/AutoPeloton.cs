using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DailyRoutines.ModulesPublic;

public class AutoPeloton : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoPelotonTitle"),
        Description = GetLoc("AutoPelotonDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["yamiYori"]
    };
    
    // 诗人 机工 舞者
    private readonly HashSet<uint> ValidClassJobs = [23, 31, 38];

    private const uint PelotoningActionID = 7557;

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        TaskHelper   ??= new() { TimeLimitMS = 30_000 };
        ModuleConfig =   LoadConfig<Config>() ?? new();

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.ClientState.LevelChanged     += OnLevelChanged;
        DService.ClientState.ClassJobChanged  += OnClassJobChanged;

        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyInDuty"), ref ModuleConfig.OnlyInDuty))
        {
            SaveConfig(ModuleConfig);
            
            TaskHelper.Abort();
            TaskHelper.Enqueue(OneTimeConditionCheck);
        }

        if (ImGui.Checkbox(GetLoc("AutoPeloton-DisableInWalk"), ref ModuleConfig.DisableInWalk))
            SaveConfig(ModuleConfig);
    }

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced    -= OnDutyRecommenced;
        DService.Condition.ConditionChange    -= OnConditionChanged;
        DService.ClientState.LevelChanged     -= OnLevelChanged;
        DService.ClientState.ClassJobChanged  -= OnClassJobChanged;
    }

    // 重新挑战
    private void OnDutyRecommenced(object? sender, ushort e)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    // 地图变更
    private unsafe void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();

        if (ModuleConfig.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    // 等级变更
    private void OnLevelChanged(uint classJobID, uint level)
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    // 职业变更
    private void OnClassJobChanged(uint classJobID)
    {
        TaskHelper.Abort();

        if (!ValidClassJobs.Contains(classJobID)) return;

        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    // 战斗状态
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;
        
        TaskHelper.Abort();
        if (!value) 
            TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private bool? OneTimeConditionCheck()
    {
        if (ModuleConfig.OnlyInDuty && GameState.ContentFinderCondition == 0) return true;
        if (GameState.IsInPVPArea) return true;
        if (DService.Condition[ConditionFlag.InCombat]) return true;

        TaskHelper.Enqueue(MainProcess);
        return true;
    }

    private bool Cycle(int delayMs = 0)
    {
        if (delayMs > 0) 
            TaskHelper.DelayNext(delayMs);
        
        TaskHelper.Enqueue(MainProcess);
        return true;
    }

    private unsafe bool? MainProcess()
    {
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent || DService.ObjectTable.LocalPlayer is not { } localPlayer)
            return Cycle(1_000);
        if (!ValidClassJobs.Contains(localPlayer.ClassJob.RowId))
            return true;
        if (!IsActionUnlocked(PelotoningActionID))
            return true;
        if (ModuleConfig.DisableInWalk && Control.Instance()->IsWalking)
            return Cycle(1_000);

        TaskHelper.Enqueue(UsePeloton, "UsePeloton", 5_000, true, 1);
        return Cycle(1_000);
    }

    private unsafe bool? UsePeloton()
    {
        if (DService.ObjectTable.LocalPlayer is not { } localPlayer) return false;
        var actionManager = ActionManager.Instance();
        var statusManager = localPlayer.ToStruct()->StatusManager;

        // PeletonNotReady
        if (actionManager->GetActionStatus(ActionType.Action, PelotoningActionID) != 0) return true;
        // AlreadyHasPeletonBuff
        if (statusManager.HasStatus(1199) || statusManager.HasStatus(50)) return true;
        // NotMoving
        if (!LocalPlayerState.IsMoving) return true;

        TaskHelper.Enqueue(() => UseActionManager.UseAction(ActionType.Action, PelotoningActionID),
                           $"UseAction_{PelotoningActionID}", 5_000, true, 1);
        return true;
    }
    
    private class Config : ModuleConfiguration
    {
        public bool OnlyInDuty    = true;
        public bool DisableInWalk = true;
    }
}
