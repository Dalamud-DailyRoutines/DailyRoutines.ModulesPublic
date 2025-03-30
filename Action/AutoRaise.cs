using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ImGuiNET;
using OmenTools.Helpers;
using Lumina.Excel.Sheets;


namespace DailyRoutines.Modules;

public unsafe class AutoRaise : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoRaiseTitle"),
        Description = GetLoc("AutoRaiseDescription"),
        Category = ModuleCategories.Action,
        Author = ["qingsiweisan"]
    };

    private class Config : ModuleConfiguration
    {
        public bool OnlyInDuty = true;
        public int MpThreshold = 1400;  // MP阈值
        public bool UseWhiteMageThinAir = true;
        public int RaiseTargetType = 0;  // 0: 所有人, 1: 仅治疗, 2: 仅坦克
        public bool ForceRaiseMode = false;
    }
    private static Config ModuleConfig                    = null!;

    private static readonly uint WhiteMageJobId = 24;
    
    private const byte ROLE_TANK = 1;
    private const byte ROLE_HEALER = 4;
    
    private const uint SwiftcastActionId         = 7561; // 即刻咏唱
    private const uint ThinAirActionId           = 7430; // 无中生有(白魔)
    
    private static readonly Dictionary<uint, uint> JobToRaiseActionMap = new()
    {
        { 24, 125 },    // 白魔-复活
        { 28, 173 },    // 学者-复苏
        { 33, 3603 },   // 占星-生辰
        { 40, 24287 },  // 贤者-复苏
        { 27, 7670 },   // 召唤-复生
        { 35, 7523 },   // 赤魔-赤复活
    };
    
    private const int AbilityLockTimeMs           = 800;
    private const int PlayerInputIgnoreTimeMs     = 300;
    private const float UseInGcdWindowStart       = 40;
    private const float UseInGcdWindowEnd         = 95;
    
    private static DateTime LastSwiftcastUseTime  = DateTime.MinValue;
    private static DateTime LastThinAirUseTime    = DateTime.MinValue;
    private static DateTime LastRaiseUseTime      = DateTime.MinValue;
    private static DateTime LastPlayerActionTime  = DateTime.MinValue;
    private static bool IsAbilityLocked           = false;
    private static bool IsRaiseSequenceActive     = false;
    private static ulong LastFailedTargetId       = 0;
    private static DateTime LastFailedTime        = DateTime.MinValue;
    private static readonly TimeSpan FailedTargetCooldown = TimeSpan.FromSeconds(5);

    private static void SetAbilityLock(bool locked) => IsAbilityLocked = locked;

    public static void UpdatePlayerActionTime() => LastPlayerActionTime = DateTime.Now;


    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 30_000 };
        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.ClientState.TerritoryChanged   += OnTerritoryChanged;
        DService.DutyState.DutyRecommenced      += OnDutyRecommenced;
        DService.Condition.ConditionChange      += OnConditionChanged;
        DService.ClientState.LevelChanged       += OnLevelChanged;
        DService.ClientState.ClassJobChanged    += OnClassJobChanged;

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

        if (ImGui.DragInt("##MpThresholdSlider", ref ModuleConfig.MpThreshold, 100f, 2400, 9000, $"{GetLoc("MpThreshold")}: %d"))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("UseWhiteMageThinAir"), ref ModuleConfig.UseWhiteMageThinAir))
            SaveConfig(ModuleConfig);

        ImGui.Text(GetLoc("RaiseTargetType"));
        
        var currentType = ModuleConfig.RaiseTargetType;
        if (ImGui.RadioButton(GetLoc("RaiseAll"), currentType == 0))
        {
            ModuleConfig.RaiseTargetType = 0;
            SaveConfig(ModuleConfig);
        }
        
        if (ImGui.RadioButton(GetLoc("RaiseOnlyHealers"), currentType == 1))
        {
            ModuleConfig.RaiseTargetType = 1;
            SaveConfig(ModuleConfig);
        }
        
        if (ImGui.RadioButton(GetLoc("RaiseOnlyTanks"), currentType == 2))
        {
            ModuleConfig.RaiseTargetType = 2;
            SaveConfig(ModuleConfig);
        }
            
        if (ImGui.Checkbox(GetLoc("ForceRaiseMode"), ref ModuleConfig.ForceRaiseMode))
            SaveConfig(ModuleConfig);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged   -= OnTerritoryChanged;
        DService.DutyState.DutyRecommenced      -= OnDutyRecommenced;
        DService.Condition.ConditionChange      -= OnConditionChanged;
        DService.ClientState.LevelChanged       -= OnLevelChanged;
        DService.ClientState.ClassJobChanged    -= OnClassJobChanged;

        if (ModuleConfig != null) SaveConfig(ModuleConfig);
        base.Uninit();
    }

    private void OnDutyRecommenced(object? sender, ushort e) => ResetTaskHelperAndCheck();
    
    private void OnLevelChanged(uint classJobId, uint level) => ResetTaskHelperAndCheck();

    private void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();
        if (ModuleConfig.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
        TaskHelper.Enqueue(OneTimeConditionCheck);
    }

    private void OnClassJobChanged(uint classJobId)
    {
        TaskHelper.Abort();
        if (GetRaiseActionId(classJobId) == 0) return;
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

    private bool? OneTimeConditionCheck()
    {
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
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent ||
            DService.ClientState.LocalPlayer is null ||
            !DService.Condition[ConditionFlag.InCombat])
            return Cycle(1000);
            
        if (GetRaiseActionId(localPlayer.ClassJob.RowId) == 0 ||
            !IsActionUnlocked(SwiftcastActionId))
            return true;

        TaskHelper.Enqueue(PreventAbilityUse, "PreventAbilityUse", 5_000, true, 1);
        TaskHelper.Enqueue(TryRaiseDeadPartyMember, "TryRaiseDeadPartyMember", 5_000, true, 1);
        return Cycle(1000);
    }

    private bool? PreventAbilityUse()
    {
        var timeSinceLastUse = (DateTime.Now - LastSwiftcastUseTime).TotalMilliseconds;
        var shouldLock = timeSinceLastUse < AbilityLockTimeMs;
        
        SetAbilityLock(shouldLock);
        
        if (shouldLock)
        {
            var remainingLockTime = AbilityLockTimeMs - (int)timeSinceLastUse;
            TaskHelper.DelayNext(Math.Min(remainingLockTime, 100));
        }
        
        return true;
    }

    private bool? TryRaiseDeadPartyMember()
    {
        try
        {
            var checkResult = CheckBasicConditions();
            if (checkResult != null) return checkResult;
            
            if (FindDeadPartyMember() is not { } deadPartyMember) return true;
            
            return ProcessRaiseSequence(deadPartyMember);
        }
        catch
        {
            return null;
        }
    }
    private bool? CheckBasicConditions()
    {
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return true;
        
        var actionManager = ActionManager.Instance();
        var character = localPlayer.ToBCStruct();
        var statusManager = character->StatusManager;
        var currentMp = localPlayer.CurrentMp;
        var timeSinceLastAction = (DateTime.Now - LastPlayerActionTime).TotalMilliseconds;
        var timeSinceLastSwiftcastUse = (DateTime.Now - LastSwiftcastUseTime).TotalMilliseconds;
        var timeSinceLastRaiseUse = (DateTime.Now - LastRaiseUseTime).TotalMilliseconds;

        var hasSwiftcast = statusManager.HasStatus(167);

        if (IsRaiseSequenceActive && !ModuleConfig.ForceRaiseMode)
        {
            return null;
        }
        
        if (!IsRaiseSequenceActive)
        {
            if (!ModuleConfig.ForceRaiseMode &&
                (timeSinceLastAction < PlayerInputIgnoreTimeMs ||
                (hasSwiftcast && timeSinceLastAction < PlayerInputIgnoreTimeMs * 3)))
                return true;
            

            if (timeSinceLastSwiftcastUse < AbilityLockTimeMs ||
                timeSinceLastRaiseUse < AbilityLockTimeMs ||
                currentMp < ModuleConfig.MpThreshold ||
                character->Mode == CharacterModes.AnimLock ||
                character->IsCasting ||
                actionManager->AnimationLock > 0)
                return true;
        }
        
        return null;
    }
    
    private bool? ProcessRaiseSequence(Character* deadPartyMember)
    {
        var localPlayer = DService.ClientState.LocalPlayer;
        var actionManager = ActionManager.Instance();
        var character = localPlayer.ToBCStruct();
        var statusManager = character->StatusManager;
        
        var hasSwiftcast = statusManager.HasStatus(167);
        
        var raiseActionId = GetRaiseActionId(localPlayer.ClassJob.RowId);
        
        var raiseStatus = actionManager->GetActionStatus(ActionType.Action, raiseActionId);
        if (raiseStatus != 0) return true;
        
        if (!hasSwiftcast)
        {
            return TryUseSwiftcast(localPlayer, character, actionManager, &statusManager);
        }
        
        return UseRaiseWithSwiftcast(deadPartyMember, raiseActionId);
    }
    
    private void TryUseThinAir()
    {
        var capturedTime = DateTime.Now;
        TaskHelper.Enqueue(() =>
        {

            if (IsAbilityLocked) return false;
            
            var result = UseActionManager.UseActionLocation(ActionType.Action, ThinAirActionId);
            if (result)
            {
                LastThinAirUseTime = capturedTime;
                UpdatePlayerActionTime();
            }
            return true;
        }, $"UseAction_{ThinAirActionId}", 5_000, true, 1);
        
        TaskHelper.DelayNext(100);
        
        return;
    }
    
    private static bool CheckGCDWindow(ActionManager* actionManager)
    {
        var gcdRecast = actionManager->GetRecastGroupDetail(58);
        if (gcdRecast->IsActive != 0)
        {
            var gcdTotal = actionManager->GetRecastTimeForGroup(58);
            var gcdElapsed = gcdRecast->Elapsed;


            var gcdProgressPercent = (gcdElapsed / gcdTotal) * 100;
            
            // 非强制模式下，只在GCD的特定百分比窗口内使用技能
            // 这个窗口比原先的更宽松，不容易错过时机
            if (gcdProgressPercent < UseInGcdWindowStart || gcdProgressPercent > UseInGcdWindowEnd)
                return false;
        }
        return true;
    }
    
    private bool? UseRaiseWithSwiftcast(Character* deadPartyMember, uint raiseActionId)
    {
        var capturedRaiseTime = DateTime.Now;
        var actionManager = ActionManager.Instance();

        // 安全获取目标ID
        if (deadPartyMember is not null)
        {
            try
            {
                var targetId = (ulong)deadPartyMember->GetGameObjectId();

                var raiseStatusCheck = actionManager->GetActionStatus(ActionType.Action, raiseActionId);
                if (raiseStatusCheck != 0)
                    return true;
                
                var priority = ModuleConfig.ForceRaiseMode ? 10 : 1;
                TaskHelper.Enqueue(() =>
                {
                    if (IsAbilityLocked) return false;
                    
                    var result = UseActionManager.UseActionLocation(ActionType.Action, raiseActionId, targetId);
                    if (result)
                    {
                        LastRaiseUseTime = capturedRaiseTime;
                        UpdatePlayerActionTime();
                        IsRaiseSequenceActive = false;
                    }
                    else
                    {
                        LastFailedTargetId = targetId;
                        LastFailedTime = DateTime.Now;
                        IsRaiseSequenceActive = false;
                    }
                    return true;
                }, $"UseAction_{raiseActionId}", 5_000, true, (uint)priority);
            }
            catch
            {
                return true;
            }
        }
        
        return true;
    }

    private Character* FindDeadPartyMember()
    {
        try
        {
            if (DService.ClientState.LocalPlayer is not { } localPlayer) return null;

            if (DService.PartyList is not { Length: > 1 } partyList) return null;

            var playerPosition = localPlayer.Position;
            
            var actionManager = ActionManager.Instance();
            
            var raiseActionId = GetRaiseActionId(localPlayer.ClassJob.RowId);
            
            var maxCastDistance = ActionManager.GetActionRange(raiseActionId);
            var maxYDifference = maxCastDistance * 0.15f;
            
            if (!IsActionUnlocked(raiseActionId)) return null;
            
            Character* bestTarget = null;
            var highestPriority = 0;
            
            foreach (var partyMember in partyList)
            {
    
                if (partyMember == null ||
                    partyMember.GameObject == null ||
                    partyMember.GameObject.Address == IntPtr.Zero ||
                    partyMember.GameObject.Address == localPlayer.Address)
                    continue;


                if (partyMember.GameObject.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
                    continue;

                var playerObj = partyMember.GameObject;
                if (playerObj is not { IsDead: true } || partyMember.CurrentHP > 0)
                    continue;
                
                var targetPosition = playerObj.Position;
                var distanceSquared = Vector3.DistanceSquared(playerPosition, targetPosition);
                
                if (distanceSquared > maxCastDistance * maxCastDistance)
                    continue;
                
                var yDifference = Math.Abs(playerPosition.Y - targetPosition.Y);
                if (yDifference > maxYDifference)
                    continue;
                
                var targetCharacter = (Character*)playerObj.Address;
                var targetId = (ulong)targetCharacter->GetGameObjectId();
                
                if (targetId == LastFailedTargetId && (DateTime.Now - LastFailedTime) < FailedTargetCooldown)
                    continue;
                
                var gameObject = (GameObject*)targetCharacter;
                if (!ActionManager.CanUseActionOnTarget(raiseActionId, gameObject))
                    continue;
                

                var priority = 0;
                var jobId = partyMember.ClassJob.RowId;
                

                if (ModuleConfig.RaiseTargetType == 1 && IsHealerJob(jobId))
                    priority = 1;
                else if (ModuleConfig.RaiseTargetType == 2 && IsTankJob(jobId))
                    priority = 1;
                else if (ModuleConfig.RaiseTargetType == 0)
                {

                    if (IsHealerJob(jobId))
                        priority = 3;
                    else if (IsTankJob(jobId))
                        priority = 2;
                    else
                        priority = 1;
                }
                else
                    priority = 0;
                

                if (priority > highestPriority)
                {
                    highestPriority = priority;
                    bestTarget = targetCharacter;
                }
            }
            

            return bestTarget;
        }
        catch
        {

            return null;
        }
    }

    private static uint GetRaiseActionId(uint jobId) =>
        JobToRaiseActionMap.TryGetValue(jobId, out var actionId) ? actionId : 0;

    private static bool IsHealerJob(uint jobId)
    {
        return LuminaGetter.TryGetRow<ClassJob>(jobId, out var job) && job.Role == ROLE_HEALER;
    }
    
    private static bool IsTankJob(uint jobId)
    {
        return LuminaGetter.TryGetRow<ClassJob>(jobId, out var job) && job.Role == ROLE_TANK;
    }

    private bool? TryUseSwiftcast(IPlayerCharacter localPlayer,
                                BattleChara* character,
                               ActionManager* actionManager,
                               StatusManager* statusManager)
    {
        var swiftcastStatus = actionManager->GetActionStatus(ActionType.Action, SwiftcastActionId);
        if (swiftcastStatus != 0) return true;
        
        if (localPlayer.ClassJob.RowId == WhiteMageJobId &&
            ModuleConfig.UseWhiteMageThinAir &&
            actionManager->GetActionStatus(ActionType.Action, ThinAirActionId) == 0)
        {
            TryUseThinAir();
        }
        
        if (!ModuleConfig.ForceRaiseMode && !CheckGCDWindow(actionManager))
        {
            TaskHelper.DelayNext(100);
            return true;
        }
    
        if (character->Mode == CharacterModes.AnimLock ||
            character->IsCasting ||
            actionManager->AnimationLock > 0)
            return true;
            
        IsRaiseSequenceActive = true;
        
        var capturedSwiftCastTime = DateTime.Now;
        var priority = ModuleConfig.ForceRaiseMode ? 10 : 1;
        TaskHelper.Enqueue(() =>
        {
            if (IsAbilityLocked) return false;
            
            var result = UseActionManager.UseActionLocation(ActionType.Action, SwiftcastActionId);
            if (result)
            {
                LastSwiftcastUseTime = capturedSwiftCastTime;
                UpdatePlayerActionTime();

                IsRaiseSequenceActive = true;
            }
            return true;
        }, $"UseAction_{SwiftcastActionId}", 5_000, true, (uint)priority);
        

        TaskHelper.DelayNext(300);
        

        TaskHelper.DelayNext(300);
        var hasSwiftcastAfterDelay = statusManager->HasStatus(167);
        if (!hasSwiftcastAfterDelay) return true;
        
        return true;
    }
}
