using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using ImGuiNET;

namespace DailyRoutines.Modules;

public class AutoLucidDreaming : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoLucidDreamingTitle"),
        Description = GetLoc("AutoLucidDreamingDescription"),
        Category = ModuleCategories.Action,
        Author = ["qingsiweisan"]
    };

    private readonly HashSet<uint> s_ClassJobArr = [6, 7, 15, 19, 20, 21, 23, 24, 26, 27, 28, 33, 35, 36, 40];
    private readonly uint s_LucidDreamingActionId = 7562;
    private DateTime LastLucidDreamingUseTime = DateTime.MinValue;
    private static DateTime LastPlayerActionTime = DateTime.MinValue;
    private Configs Config = null!;
    private static bool _isAbilityLocked = false;

    private static void SetAbilityLock(bool locked) => _isAbilityLocked = locked;

    private static unsafe bool UseAction(ActionType actionType, uint actionId, ulong targetId = 0, uint targetIndex = 0)
    {
        if (actionId != 7562) LastPlayerActionTime = DateTime.Now;

        var actionManager = ActionManager.Instance();
        if (!actionManager->IsActionOffCooldown(actionType, actionId)) return false;

        if (_isAbilityLocked)
        {
            var cooldownGroupId = actionManager->GetRecastGroup((int)actionType, actionId);
            if (cooldownGroupId != 58) return false;
        }

        return actionManager->UseAction(actionType, actionId, targetId, targetIndex);
    }

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };
        Config = LoadConfig<Configs>() ?? new();

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced += OnDutyRecommenced;
        DService.Condition.ConditionChange += OnConditionChanged;
        DService.ClientState.LevelChanged += OnLevelChanged;
        DService.ClientState.ClassJobChanged += OnClassJobChanged;

        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("AutoLucidDreaming-OnlyInDuty"), ref Config.OnlyInDuty))
        {
            SaveConfig(Config);
            TaskHelper.Abort();
            TaskHelper.Enqueue(OneTimeConditionCheck);
        }

        if (ImGui.DragInt("##MpThresholdSlider", ref Config.MpThreshold, 100f, 3000, 9000, $"{GetLoc("AutoLucidDreaming-MpThreshold")}: %d"))
            SaveConfig(Config);
            
        ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "GCD优化和动画锁相关设置已使用内置优化值，无需手动调整");
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;
        DService.Condition.ConditionChange -= OnConditionChanged;
        DService.ClientState.LevelChanged -= OnLevelChanged;
        DService.ClientState.ClassJobChanged -= OnClassJobChanged;

        if (Config != null) SaveConfig(Config);
        base.Uninit();
    }

    // 统一事件处理方法
    private void OnDutyRecommenced(object? sender, ushort e) => ResetTaskHelperAndCheck();
    private void OnLevelChanged(uint classJobId, uint level) => ResetTaskHelperAndCheck();
    
    private unsafe void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();
        if (Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private void OnClassJobChanged(uint classJobId)
    {
        TaskHelper.Abort();
        if (!s_ClassJobArr.Contains(classJobId)) return;
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag is not ConditionFlag.InCombat) return;
        TaskHelper.Abort();
        if (value) TaskHelper.Enqueue(OneTimeConditionCheck);
    }
    
    private void ResetTaskHelperAndCheck()
    {
        TaskHelper.Abort();
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private unsafe bool? OneTimeConditionCheck()
    {
        // 快速返回条件检查
        if (Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0 ||
            GameMain.IsInPvPArea() || GameMain.IsInPvPInstance() ||
            !DService.Condition[ConditionFlag.InCombat])
            return true;

        TaskHelper.Enqueue(MainProcess);
        return true;
    }

    private bool Cycle(int delayMs = 0)
    {
        if (delayMs > 0) TaskHelper.DelayNext(delayMs);
        TaskHelper.Enqueue(MainProcess);
        return true;
    }

    private unsafe bool? MainProcess()
    {
        // 基本状态检查
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent ||
            DService.ClientState.LocalPlayer is not { } localPlayer ||
            !DService.Condition[ConditionFlag.InCombat])
            return Cycle(1_000);
            
        // 职业和技能检查
        if (!s_ClassJobArr.Contains(localPlayer.ClassJob.RowId) ||
            !IsActionUnlocked(s_LucidDreamingActionId))
            return true;

        TaskHelper.Enqueue(PreventAbilityUse, "PreventAbilityUse", 5_000, true, 1);
        TaskHelper.Enqueue(UseLucidDreaming, "UseLucidDreaming", 5_000, true, 1);
        return Cycle(1_000);
    }

    private unsafe bool? PreventAbilityUse()
    {
        var timeSinceLastUse = (DateTime.Now - LastLucidDreamingUseTime).TotalMilliseconds;
        bool shouldLock = timeSinceLastUse < Config.AbilityLockTimeMs;
        
        SetAbilityLock(shouldLock);
        
        if (shouldLock)
        {
            var remainingLockTime = Config.AbilityLockTimeMs - (int)timeSinceLastUse;
            DService.Chat?.PrintError($"能力技已锁定 ({remainingLockTime}ms)");
            TaskHelper.DelayNext(Math.Min(remainingLockTime, 100));
        }
        
        return true;
    }

    private unsafe bool? UseLucidDreaming()
    {
        // 基础状态检查
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        var actionManager = ActionManager.Instance();
        var character = localPlayer.ToBCStruct();
        if (character == null) return true;
        
        var statusManager = character->StatusManager;
        var currentMp = localPlayer.CurrentMp;
        var timeSinceLastAction = (DateTime.Now - LastPlayerActionTime).TotalMilliseconds;
        var timeSinceLastUse = (DateTime.Now - LastLucidDreamingUseTime).TotalMilliseconds;
        
        // 快速返回条件检查
        if (timeSinceLastAction < Config.PlayerInputIgnoreTimeMs ||
            timeSinceLastUse < Config.AbilityLockTimeMs ||
            currentMp >= Config.MpThreshold ||
            actionManager->GetActionStatus(ActionType.Action, s_LucidDreamingActionId) != 0 ||
            statusManager.HasStatus(1204) ||
            character->Mode == CharacterModes.AnimLock ||
            character->IsCasting ||
            actionManager->AnimationLock > 0)
            return true;

        // GCD检测逻辑 - 使用内置的优化百分比窗口模式
        var gcdRecast = actionManager->GetRecastGroupDetail(58);
        if (gcdRecast->IsActive != 0)
        {
            float gcdTotal = actionManager->GetRecastTimeForGroup(58);
            float gcdElapsed = gcdRecast->Elapsed;
            
            // 使用百分比窗口模式
            float gcdProgressPercent = (gcdElapsed / gcdTotal) * 100;
            if (gcdProgressPercent < Config.UseInGCDWindowStart || gcdProgressPercent > Config.UseInGCDWindowEnd)
                return true;
        }

        // 使用醒梦
        DateTime capturedTime = DateTime.Now;
        TaskHelper.Enqueue(() =>
        {
            var result = UseAction(ActionType.Action, s_LucidDreamingActionId);
            if (result) LastLucidDreamingUseTime = capturedTime;
            return result;
        }, $"UseAction_{s_LucidDreamingActionId}", 5_000, true, 1);
        return true;
    }

    private class Configs : ModuleConfiguration
    {
        public bool OnlyInDuty = true;
        public int MpThreshold = 7000;
        public int AbilityLockTimeMs = 800;
        public int PlayerInputIgnoreTimeMs = 300;
        public int GCDStartThresholdMs = 200;
        public int GCDEndThresholdMs = 500;
        public float UseInGCDWindowStart = 60;
        public float UseInGCDWindowEnd = 95;
    }
}