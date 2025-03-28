using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using ImGuiNET;

namespace DailyRoutines.Modules;

public unsafe class AutoLucidDreaming : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoLucidDreamingTitle"),
        Description = GetLoc("AutoLucidDreamingDescription"),
        Category    = ModuleCategories.Action,
        Author      = ["qingsiweisan"]
    };
    
    private const int   AbilityLockTimeMs       = 800;
    private const int   PlayerInputIgnoreTimeMs = 300;
    private const int   GcdStartThresholdMs     = 200;
    private const int   GcdEndThresholdMs       = 500;
    private const float UseInGcdWindowStart     = 60;
    private const float UseInGcdWindowEnd       = 95;
    private const uint  LucidDreamingID         = 7562;

    private static readonly HashSet<uint> ClassJobArr = [6, 7, 15, 19, 20, 21, 23, 24, 26, 27, 28, 33, 35, 36, 40];

    private static Configs ModuleConfig = null!;
    
    private static DateTime LastLucidDreamingUseTime = DateTime.MinValue;
    private static DateTime LastPlayerActionTime     = DateTime.MinValue;
    private static bool     IsAbilityLocked;
    
    public override void Init()
    {
        TaskHelper   ??= new TaskHelper { TimeLimitMS = 30_000 };
        ModuleConfig =   LoadConfig<Configs>() ?? new();

        DService.ClientState.TerritoryChanged += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.ClientState.LevelChanged     += OnLevelChanged;
        DService.ClientState.ClassJobChanged  += OnClassJobChanged;

        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("OnlyInDuty"), ref ModuleConfig.OnlyInDuty))
        {
            SaveConfig(ModuleConfig);
            TaskHelper.Abort();
            TaskHelper.Enqueue(OneTimeConditionCheck);
        }

        ImGui.SetNextItemWidth(250f * GlobalFontScale);
        if (ImGui.DragInt("##MpThresholdSlider", ref ModuleConfig.MpThreshold, 100f, 3000, 9000, $"{LuminaWarpper.GetAddonText(233)}: %d"))
            SaveConfig(ModuleConfig);
        
        ImGui.Spacing();
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;
        DService.Condition.ConditionChange -= OnConditionChanged;
        DService.ClientState.LevelChanged -= OnLevelChanged;
        DService.ClientState.ClassJobChanged -= OnClassJobChanged;

        if (ModuleConfig != null) SaveConfig(ModuleConfig);
        base.Uninit();
    }

    private void OnDutyRecommenced(object? sender, ushort e) => ResetTaskHelperAndCheck();
    
    private void OnLevelChanged(uint classJobID, uint level) => ResetTaskHelperAndCheck();
    
    private void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();
        if (ModuleConfig.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private void OnClassJobChanged(uint classJobId)
    {
        TaskHelper.Abort();
        if (!ClassJobArr.Contains(classJobId)) return;
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

    // 主要处理逻辑方法
    private bool? OneTimeConditionCheck()
    {
        // 快速返回条件检查
        if ((ModuleConfig.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) ||
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

    private bool? MainProcess()
    {
        // 基本状态检查
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent ||
            DService.ClientState.LocalPlayer is not { } localPlayer ||
            !DService.Condition[ConditionFlag.InCombat])
            return Cycle(1_000);
            
        // 职业和技能检查
        if (!ClassJobArr.Contains(localPlayer.ClassJob.RowId) ||
            !IsActionUnlocked(LucidDreamingID))
            return true;

        TaskHelper.Enqueue(PreventAbilityUse, "PreventAbilityUse", 5_000, true, 1);
        TaskHelper.Enqueue(UseLucidDreaming, "UseLucidDreaming", 5_000, true, 1);
        return Cycle(1_000);
    }

    private bool? PreventAbilityUse()
    {
        var timeSinceLastUse = (DateTime.Now - LastLucidDreamingUseTime).TotalMilliseconds;
        var shouldLock = timeSinceLastUse < AbilityLockTimeMs;
        
        SetAbilityLock(shouldLock);
        
        if (shouldLock)
        {
            var remainingLockTime = AbilityLockTimeMs - (int)timeSinceLastUse;
            TaskHelper.DelayNext(Math.Min(remainingLockTime, 100));
        }
        
        return true;
    }

    private bool? UseLucidDreaming()
    {
        // 基础状态检查
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
        var character = localPlayer.ToBCStruct();
        if (character == null) return true;
        
        var statusManager = character->StatusManager;
        var currentMp = localPlayer.CurrentMp;
        var timeSinceLastAction = (DateTime.Now - LastPlayerActionTime).TotalMilliseconds;
        var timeSinceLastUse = (DateTime.Now - LastLucidDreamingUseTime).TotalMilliseconds;
        
        // 快速返回条件检查
        if (timeSinceLastAction < PlayerInputIgnoreTimeMs ||
            timeSinceLastUse < AbilityLockTimeMs ||
            currentMp >= ModuleConfig.MpThreshold)
            return true;
            
        // 检查技能状态
        var actionManager = ActionManager.Instance();
        if (actionManager->GetActionStatus(ActionType.Action, LucidDreamingID) != 0 ||
            statusManager.HasStatus(1204) ||
            character->Mode == CharacterModes.AnimLock ||
            character->IsCasting ||
            actionManager->AnimationLock > 0)
            return true;

        // GCD检测逻辑 - 使用内置的优化百分比窗口模式
        var gcdRecast = actionManager->GetRecastGroupDetail(58);
        if (gcdRecast->IsActive != 0)
        {
            var gcdTotal = actionManager->GetRecastTimeForGroup(58);
            var gcdElapsed = gcdRecast->Elapsed;
            
            // 使用百分比窗口模式
            var gcdProgressPercent = gcdElapsed / gcdTotal * 100;
            if (gcdProgressPercent < UseInGcdWindowStart || gcdProgressPercent > UseInGcdWindowEnd)
                return true;
        }

        // 使用醒梦
        var capturedTime = DateTime.Now;
        TaskHelper.Enqueue(() =>
        {
            if (IsAbilityLocked) return false;
            
            var result = UseActionManager.UseActionLocation(ActionType.Action, LucidDreamingID);
            if (result)
            {
                LastLucidDreamingUseTime = capturedTime;
                if (ModuleConfig.SendNotification && Throttler.Throttle("AutoLucidDreaming-Notification", 10_000))
                    NotificationInfo(GetLoc("AutoLucidDreaming-Notification", localPlayer.CurrentMp));
            }
            return result;
        }, $"UseAction_{LucidDreamingID}", 5_000, true, 1);
        return true;
    }
    
    private static void SetAbilityLock(bool locked) => IsAbilityLocked = locked;
    
    private class Configs : ModuleConfiguration
    {
        public bool OnlyInDuty;
        public int  MpThreshold = 7000;
        public bool SendNotification = true;
    }
}
