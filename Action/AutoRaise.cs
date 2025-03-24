using System;
using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using ImGuiNET;

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

    // 字段定义
    private static readonly HashSet<uint> ClassJobArr = [6, 7, 15, 24, 26, 27, 28, 33, 35, 40];
    private static readonly uint WhiteMageJobId = 24;
    private static readonly uint ScholarJobId = 28;
    private static readonly uint AstrologianJobId = 33;
    private static readonly uint SageJobId = 40;
    private static readonly uint SummonerJobId = 27;
    private static readonly uint RedMageJobId = 35;
    
    private static readonly uint SwiftcastActionId = 7561; // 即刻咏唱
    private static readonly uint ThinAirActionId = 7430;   // 无中生有(白魔)
    
    // 各职业复活技能ID
    private static readonly uint WhiteMageRaiseId = 125;   // 复活(白魔)
    private static readonly uint ScholarRaiseId = 173;     // 复苏(学者)
    private static readonly uint AstrologianRaiseId = 3603; // 生辰(占星)
    private static readonly uint SageRaiseId = 24287;      // 复苏(贤者)
    private static readonly uint SummonerRaiseId = 7670;   // 复生(召唤)
    private static readonly uint RedMageRaiseId = 7523;    // 赤复活(赤魔)
    private static readonly uint DefaultRaiseId = 7523;    // 默认复活技能ID
    
    // 常量定义
    private const int AbilityLockTimeMs = 800;
    private const int PlayerInputIgnoreTimeMs = 300;
    private const float UseInGcdWindowStart = 60;
    private const float UseInGcdWindowEnd = 95;
    
    // 状态字段
    private static DateTime LastSwiftcastUseTime = DateTime.MinValue;
    private static DateTime LastThinAirUseTime = DateTime.MinValue;
    private static DateTime LastRaiseUseTime = DateTime.MinValue;
    private static DateTime LastPlayerActionTime = DateTime.MinValue;
    private static bool IsAbilityLocked = false;
    private static bool IsRaiseSequenceActive = false;
    private static ulong LastFailedTargetId = 0;
    private static DateTime LastFailedTime = DateTime.MinValue;
    private static readonly TimeSpan FailedTargetCooldown = TimeSpan.FromSeconds(5); // 失败目标冷却时间
    
    private static Configs Config = null!;

    // 辅助工具方法
    private static void SetAbilityLock(bool locked) => IsAbilityLocked = locked;

    // 更新玩家操作时间的方法
    public static void UpdatePlayerActionTime()
    {
        LastPlayerActionTime = DateTime.Now;
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
        if (ImGui.Checkbox(GetLoc("AutoRaise-OnlyInDuty"), ref Config.OnlyInDuty))
        {
            SaveConfig(Config);
            TaskHelper.Abort();
            TaskHelper.Enqueue(OneTimeConditionCheck);
        }

        if (ImGui.DragInt("##MpThresholdSlider", ref Config.MpThreshold, 100f, 2400, 9000, $"{GetLoc("AutoRaise-MpThreshold")}: %d"))
            SaveConfig(Config);

        if (ImGui.Checkbox(GetLoc("AutoRaise-UseWhiteMageThinAir"), ref Config.UseWhiteMageThinAir))
            SaveConfig(Config);

        if (ImGui.Checkbox(GetLoc("AutoRaise-RaiseOnlyHealers"), ref Config.RaiseOnlyHealers))
        {
            // 如果启用了仅复活治疗，则禁用仅复活坦克
            if (Config.RaiseOnlyHealers)
                Config.RaiseOnlyTanks = false;
            SaveConfig(Config);
        }

        if (ImGui.Checkbox(GetLoc("AutoRaise-RaiseOnlyTanks"), ref Config.RaiseOnlyTanks))
        {
            // 如果启用了仅复活坦克，则禁用仅复活治疗
            if (Config.RaiseOnlyTanks)
                Config.RaiseOnlyHealers = false;
            SaveConfig(Config);
        }
            
        if (ImGui.Checkbox(GetLoc("AutoRaise-ForceRaiseMode"), ref Config.ForceRaiseMode))
            SaveConfig(Config);
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

    // 事件处理方法
    private void OnDutyRecommenced(object? sender, ushort e) => ResetTaskHelperAndCheck();
    private void OnLevelChanged(uint classJobId, uint level) => ResetTaskHelperAndCheck();

    private void OnTerritoryChanged(ushort zone)
    {
        TaskHelper.Abort();
        if (Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) return;
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
        if ((Config.OnlyInDuty && GameMain.Instance()->CurrentContentFinderConditionId == 0) ||
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
            return Cycle(1000);
            
        // 职业和技能检查
        if (!ClassJobArr.Contains(localPlayer.ClassJob.RowId) ||
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
            DService.Chat?.PrintError($"能力技已锁定 ({remainingLockTime}ms)");
            TaskHelper.DelayNext(Math.Min(remainingLockTime, 100));
        }
        
        return true;
    }

    private bool? TryRaiseDeadPartyMember()
    {
        try
        {
            // 基础状态检查
            if (DService.ClientState.LocalPlayer is not { } localPlayer) return false;
            var actionManager = ActionManager.Instance();
            var character = localPlayer.ToBCStruct();
            if (character == null) return true;

            var statusManager = character->StatusManager;
            var currentMp = localPlayer.CurrentMp;
            var timeSinceLastAction = (DateTime.Now - LastPlayerActionTime).TotalMilliseconds;
            var timeSinceLastSwiftcastUse = (DateTime.Now - LastSwiftcastUseTime).TotalMilliseconds;
            var timeSinceLastRaiseUse = (DateTime.Now - LastRaiseUseTime).TotalMilliseconds;

            // 检查是否有即刻咏唱状态
            var hasSwiftcast = statusManager.HasStatus(167); // 即刻咏唱状态ID

            // 检查是否正在执行复活序列
            if (IsRaiseSequenceActive && !Config.ForceRaiseMode)
            {
                // 如果不是强制模式，且复活序列已经开始，则继续执行
                // 不做任何额外检查
            }
            else if (!IsRaiseSequenceActive)
            {
                // 如果不在强制复活模式下，且玩家最近有操作，则不执行自动复活
                // 这可以防止模块与玩家手动操作冲突，避免消耗掉即刻咏唱
                if (!Config.ForceRaiseMode && 
                    (timeSinceLastAction < PlayerInputIgnoreTimeMs || 
                    (hasSwiftcast && timeSinceLastAction < PlayerInputIgnoreTimeMs * 3)))
                    return true;
                
                // 基本状态检查，无论是否为强制模式都需要检查
                if (timeSinceLastSwiftcastUse < AbilityLockTimeMs ||
                    timeSinceLastRaiseUse < AbilityLockTimeMs ||
                    currentMp < Config.MpThreshold ||
                    character->Mode == CharacterModes.AnimLock ||
                    character->IsCasting ||
                    actionManager->AnimationLock > 0)
                    return true;
            }


            // 获取阵亡队友
            var deadPartyMember = FindDeadPartyMember();
            if (deadPartyMember == null)
                return true;

            // 确认复活技能可用
            var raiseActionId = GetRaiseActionId(localPlayer.ClassJob.RowId);

            // 检查复活技能是否可用
            var raiseStatus = actionManager->GetActionStatus(ActionType.Action, raiseActionId);
            if (raiseStatus != 0)
                return true;

            // 如果没有即刻咏唱状态且即刻咏唱可用，先使用即刻咏唱
            if (!hasSwiftcast)
            {
                // 检查即刻咏唱是否可用
                var swiftcastStatus = actionManager->GetActionStatus(ActionType.Action, SwiftcastActionId);
                if (swiftcastStatus != 0)
                    return true;

                // 白魔且开启无中生有优先设置，检查无中生有是否可用
                if (localPlayer.ClassJob.RowId == WhiteMageJobId && Config.UseWhiteMageThinAir &&
                    actionManager->GetActionStatus(ActionType.Action, ThinAirActionId) == 0)
                {
                    // 使用无中生有
                    var capturedTime = DateTime.Now;
                    TaskHelper.Enqueue(UseThinAir, $"UseAction_{ThinAirActionId}", 5_000, true, 1);
                    
                    // 添加短暂延迟，确保无中生有状态生效
                    TaskHelper.DelayNext(100);
                    
                    // 本地方法定义
                    bool? UseThinAir()
                    {
                        if (IsAbilityLocked) return false;
                        
                        var result = UseActionManager.UseAction(ActionType.Action, ThinAirActionId);
                        if (result) 
                        {
                            LastThinAirUseTime = capturedTime;
                            UpdatePlayerActionTime(); // 更新玩家操作时间
                        }
                        return true;
                    }
                }

                // GCD检测逻辑 - 如果在强制复活模式下，无视GCD窗口
                if (!Config.ForceRaiseMode)
                {
                    var gcdRecast = actionManager->GetRecastGroupDetail(58);
                    if (gcdRecast->IsActive != 0)
                    {
                        var gcdTotal = actionManager->GetRecastTimeForGroup(58);
                        var gcdElapsed = gcdRecast->Elapsed;

                        // 使用百分比窗口模式
                        var gcdProgressPercent = (gcdElapsed / gcdTotal) * 100;
                        if (gcdProgressPercent < UseInGcdWindowStart || gcdProgressPercent > UseInGcdWindowEnd)
                            return true;
                    }
                }

                // 再次检查动画锁和施法状态
                if (character->Mode == CharacterModes.AnimLock || character->IsCasting || actionManager->AnimationLock > 0)
                    return true;

                // 标记复活序列已开始
                IsRaiseSequenceActive = true;
                
                // 使用即刻咏唱
                var capturedSwiftCastTime = DateTime.Now;
                // 强制复活模式下使用高优先级
                var priority = Config.ForceRaiseMode ? 10 : 1;
                TaskHelper.Enqueue(UseSwiftcast, $"UseAction_{SwiftcastActionId}", 5_000, true, (uint)priority);
                
                // 添加短暂延迟，确保即刻咏唱状态生效
                TaskHelper.DelayNext(300);
                
                // 增加延迟后再次检查即刻咏唱状态
                TaskHelper.DelayNext(300);
                hasSwiftcast = statusManager.HasStatus(167);
                if (!hasSwiftcast) return true;
                
                // 本地方法定义
                bool? UseSwiftcast()
                {
                    if (IsAbilityLocked) return false;
                    
                    var result = UseActionManager.UseAction(ActionType.Action, SwiftcastActionId);
                    if (result) 
                    {
                        LastSwiftcastUseTime = capturedSwiftCastTime;
                        UpdatePlayerActionTime(); // 更新玩家操作时间
                        // 设置复活序列正在进行
                        IsRaiseSequenceActive = true;
                    }
                    return true;
                }
            }

            // 有即刻咏唱状态时，直接使用复活技能
            if (hasSwiftcast)
            {
                var capturedRaiseTime = DateTime.Now;

                // 安全获取目标ID
                if (deadPartyMember != null)
                {
                    try
                    {
                        // 获取目标GameObject ID
                        var targetId = (ulong)deadPartyMember->GetGameObjectId();

                        // 再次检查复活技能是否可用
                        var raiseStatusCheck = actionManager->GetActionStatus(ActionType.Action, raiseActionId);
                        if (raiseStatusCheck != 0)
                            return true;
                        
                        // 使用复活技能
                        // 强制复活模式下使用高优先级
                        var priority = Config.ForceRaiseMode ? 10 : 1;
                        TaskHelper.Enqueue(UseRaise, $"UseAction_{raiseActionId}", 5_000, true, (uint)priority);
                        
                        // 本地方法定义
                        bool? UseRaise()
                        {
                            if (IsAbilityLocked) return false;
                            
                            var result = UseActionManager.UseAction(ActionType.Action, raiseActionId, targetId);
                            if (result) 
                            {
                                LastRaiseUseTime = capturedRaiseTime;
                                UpdatePlayerActionTime(); // 更新玩家操作时间
                                // 复活序列完成
                                IsRaiseSequenceActive = false;
                            }
                            else
                            {
                                // 复活失败，记录目标ID和时间
                                LastFailedTargetId = targetId;
                                LastFailedTime = DateTime.Now;
                                // 复活序列完成
                                IsRaiseSequenceActive = false;
                            }
                            return true;
                        }
                    }
                    catch
                    {
                        return true;
                    }
                }
                else
                {
                    return true; // 如果deadPartyMember为null，添加返回值
                }
            }
            return true;
        }
        catch
        {
            return null; // 确保异常情况下也有返回值
        }
    }

    // 寻找阵亡队友
    private Character* FindDeadPartyMember()
    {
        try
        {
            // 使用DService获取本地玩家信息
            if (DService.ClientState.LocalPlayer is not { } localPlayer) return null;

            var partyList = DService.PartyList;
            if (partyList == null || partyList.Length == 0) return null;

            // 复活技能的最大施法距离（约30米）
            const float maxCastDistance = 30f;
            // Y轴差异最大允许值（米）
            const float maxYDifference = 5.0f;
            
            // 获取本地玩家位置
            var playerPosition = localPlayer.Position;
            
            // 获取ActionManager实例，用于检查技能是否可用
            var actionManager = ActionManager.Instance();
            
            // 获取复活技能ID
            var raiseActionId = GetRaiseActionId(localPlayer.ClassJob.RowId);
            
            // 检查复活技能是否已解锁
            if (!IsActionUnlocked(raiseActionId)) return null;
            
            // 直接查找符合条件的队友，优先考虑治疗和坦克
            Character* bestTarget = null;
            var highestPriority = 0;
            
            foreach (var partyMember in partyList)
            {
                // 快速检查：跳过null和自己
                if (partyMember == null || 
                    partyMember.GameObject == null || 
                    partyMember.GameObject.Address == IntPtr.Zero ||
                    partyMember.GameObject.Address == localPlayer.Address) 
                    continue;

                // 快速检查：确保是玩家类型
                if (partyMember.GameObject.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player) 
                    continue;

                // 快速检查：确保已死亡
                var playerObj = partyMember.GameObject;
                if (playerObj == null || (!playerObj.IsDead && partyMember.CurrentHP > 0)) 
                    continue;
                
                // 检查是否在施法距离内
                var targetPosition = playerObj.Position;
                var distance = Math.Sqrt(
                    Math.Pow(playerPosition.X - targetPosition.X, 2) +
                    Math.Pow(playerPosition.Y - targetPosition.Y, 2) +
                    Math.Pow(playerPosition.Z - targetPosition.Z, 2)
                );
                
                if (distance > maxCastDistance)
                    continue;
                
                // 检查Y轴差异是否过大
                var yDifference = Math.Abs(playerPosition.Y - targetPosition.Y);
                if (yDifference > maxYDifference) 
                    continue;
                
                // 检查目标是否可选中
                var targetCharacter = (Character*)playerObj.Address;
                var targetId = (ulong)targetCharacter->GetGameObjectId();
                
                // 检查是否是最近失败的目标
                if (targetId == LastFailedTargetId && (DateTime.Now - LastFailedTime) < FailedTargetCooldown)
                    continue;
                
                // 使用ActionManager检查技能是否可用于该目标
                if (actionManager->GetActionStatus(ActionType.Action, raiseActionId, targetId) != 0)
                    continue;
                
                // 确定优先级
                var priority = 0;
                var jobId = partyMember.ClassJob.RowId;
                
                // 根据用户设置和职业类型确定优先级
                if (Config.RaiseOnlyHealers && IsHealerJob(jobId))
                    priority = 1;
                else if (Config.RaiseOnlyTanks && IsTankJob(jobId))
                    priority = 1;
                else if (!Config.RaiseOnlyHealers && !Config.RaiseOnlyTanks)
                {
                    // 如果没有特殊设置，则按职业类型优先级排序
                    if (IsHealerJob(jobId))
                        priority = 3; // 治疗最高优先级
                    else if (IsTankJob(jobId))
                        priority = 2; // 坦克次高优先级
                    else
                        priority = 1; // DPS最低优先级
                }
                else
                    priority = 0; // 不符合用户设置的优先级条件
                
                // 如果优先级更高，则更新最佳目标
                if (priority > highestPriority)
                {
                    highestPriority = priority;
                    bestTarget = targetCharacter;
                }
            }
            
            // 返回优先级最高的队友
            return bestTarget;
        }
        catch
        {
            // 捕获并忽略异常，防止模块崩溃
            return null;
        }
    }

    // 获取对应职业的复活技能ID
    private static uint GetRaiseActionId(uint jobId)
    {
        return jobId switch
        {
            24 => WhiteMageRaiseId,    // 白魔
            28 => ScholarRaiseId,      // 学者
            33 => AstrologianRaiseId,  // 占星
            40 => SageRaiseId,         // 贤者
            27 => SummonerRaiseId,     // 召唤
            35 => RedMageRaiseId,      // 赤魔
            _ => DefaultRaiseId        // 默认
        };
    }

    // 判断是否为治疗职业
    private static bool IsHealerJob(uint jobId)
    {
        // 白魔(24), 学者(28), 占星(33), 贤者(40)
        return jobId == WhiteMageJobId || jobId == ScholarJobId || 
               jobId == AstrologianJobId || jobId == SageJobId;
    }

    // 判断是否为坦克职业
    private static bool IsTankJob(uint jobId)
    {
        // 战士(21), 骑士(19), 暗黑骑士(32), 绝枪战士(37)
        return jobId == 21 || jobId == 19 || jobId == 32 || jobId == 37;
    }

    private class Configs : ModuleConfiguration
    {
        public bool OnlyInDuty = true;
        public int MpThreshold = 1400;  // MP阈值
        public bool UseWhiteMageThinAir = true;
        public bool RaiseOnlyHealers = false;
        public bool RaiseOnlyTanks = false;
        public bool ForceRaiseMode = false;
    }
}
