using System;
using System.Threading;
using System.Timers;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Conditions;
using Timer = System.Timers.Timer;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayDutyReadyLeftTime : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayDutyReadyLeftTimeTitle"),
        Description = GetLoc("AutoDisplayDutyReadyLeftTimeDescription"),
        Category    = ModuleCategories.UIOptimization
    };

    private static CountdownTimer? Timer;

    protected override void Init() => DService.Condition.ConditionChange += OnConditionChanged;

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.WaitingForDuty) return;
        
        Timer?.Stop();
        Timer?.Dispose();
        Timer = null;

        if (value)
        {
            OnCountdownRunning(null, 45);
            
            Timer = new(45);
            Timer.Start();
            Timer.TimeChanged += OnCountdownRunning;
        }
    }

    private static void OnCountdownRunning(object? sender, int second)
    {
        if (!IsAddonAndNodesReady(ContentsFinderReady)) return;
        
        var textNode = ContentsFinderReady->GetTextNodeById(3);
        if (textNode == null) return;
        
        textNode->SetText($"{LuminaWrapper.GetAddonText(2780)} ({second})");
    }

    protected override void Uninit()
    {
        DService.Condition.ConditionChange -= OnConditionChanged;
        OnConditionChanged(ConditionFlag.WaitingForDuty, false);
    }
}
