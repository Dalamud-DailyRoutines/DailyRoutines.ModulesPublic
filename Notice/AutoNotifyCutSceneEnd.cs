using System;
using System.Diagnostics;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoNotifyCutSceneEnd : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoNotifyCutSceneEndTitle"),
        Description = GetLoc("AutoNotifyCutSceneEndDescription"),
        Category = ModuleCategories.Notice,
    };

    private static Config ModuleConfig = null!;
    
    private static bool IsDutyEnd;
    private static Stopwatch? Stopwatch;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        Stopwatch  ??= new Stopwatch();
        TaskHelper ??= new() { TimeLimitMS = 30_000 };

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);
    }

    public override void Uninit()
    {
        OnZoneChanged(0);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
    }
    
    private void OnZoneChanged(ushort zone)
    {
        DService.DutyState.DutyCompleted -= OnDutyComplete;
        FrameworkManager.Unregister(OnUpdate);
        Stopwatch?.Reset();
        Stopwatch = null;
        IsDutyEnd = false;
        
        if (!LuminaGetter.TryGetRow<TerritoryType>(zone, out var zoneRow) ||
            zoneRow.ContentFinderCondition.ValueNullable == null) return;
        
        TaskHelper.Enqueue(() => !BetweenAreas, "WaitForEnteringDuty");
        TaskHelper.Enqueue(CheckIsDutyStateEligibleThenEnqueue, "CheckIsDutyStateEligibleThenEnqueue");
    }

    private void CheckIsDutyStateEligibleThenEnqueue()
    {
        if (DService.PartyList.Length <= 1)
        {
            TaskHelper.Abort();
            return;
        }

        Stopwatch = new();
        
        DService.DutyState.DutyCompleted += OnDutyComplete;
        FrameworkManager.Register(OnUpdate, throttleMS: 500);
    }
    
    private static void OnDutyComplete(object? sender, ushort zone) => IsDutyEnd = true;

    private void OnUpdate(IFramework _)
    {
        // PVP 或不在副本内 → 结束检查
        if (DService.ClientState.IsPvP || !BoundByDuty)
        {
            OnZoneChanged(0);
            return;
        }
        
        // 副本已经结束, 不再检查
        if (IsDutyEnd) return;
        
        // 本地玩家为空, 暂时不检查
        if (DService.ObjectTable.LocalPlayer is null) return;

        if (DService.Condition[ConditionFlag.InCombat])
        {
            // 进战时还在检查
            if (Stopwatch.IsRunning)
                CheckStopwatchStateThenRelay();
            
            return;
        }
        
        // 计时器运行中
        if (Stopwatch.IsRunning)
        {
            // 副本还未开始 → 先检查是否有玩家没加载出来 → 如有, 不继续检查
            if (!DService.DutyState.IsDutyStarted &&
                DService.PartyList.Any(x => x.GameObject == null || !x.GameObject.IsTargetable))
                return;
            
            // 检查是否任一玩家仍在剧情状态
            if (DService.PartyList.Any(x => x.GameObject != null &&
                                            ((Character*)x.GameObject.Address)->CharacterData.OnlineStatus == 15))
                return;

            CheckStopwatchStateThenRelay();
        }
        else
        {
            // 居然无一人正在看剧情
            if (!DService.PartyList.Any(x => x.GameObject != null &&
                                            ((Character*)x.GameObject.Address)->CharacterData.OnlineStatus == 15))
                return;
            
            Stopwatch.Restart();
        }
    }

    private static void CheckStopwatchStateThenRelay()
    {
        if (!Stopwatch.IsRunning) return;

        var elapsedTime = Stopwatch.Elapsed;
        Stopwatch.Reset();
        
        // 小于四秒 → 不播报
        if (elapsedTime < TimeSpan.FromSeconds(4)) return;
        
        var message = GetLoc("AutoNotifyCutSceneEnd-NotificationMessage", $"{elapsedTime.TotalSeconds:F0}");
        if (ModuleConfig.SendChat) 
            Chat(message);
        if (ModuleConfig.SendNotification) 
            NotificationInfo(message);
        if (ModuleConfig.SendTTS) 
            Speak(message);
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat = true;
        public bool SendNotification = true;
        public bool SendTTS = true;
    }
}
