using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayDutyReadyLeftTime : ModuleBase
{
    private static CountdownTimer? Timer;

    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoDisplayDutyReadyLeftTimeTitle"),
        Description = Lang.Get("AutoDisplayDutyReadyLeftTimeDescription"),
        Category    = ModuleCategory.Combat,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/AutoDisplayDutyReadyLeftTime/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        DService.Instance().Condition.ConditionChange += OnConditionChanged;
    
    protected override void Uninit()
    {
        DService.Instance().Condition.ConditionChange -= OnConditionChanged;
        OnConditionChanged(ConditionFlag.WaitingForDuty, false);
    }

    private static void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.WaitingForDutyFinder) return;

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
        if (!ContentsFinderReady->IsAddonAndNodesReady()) return;

        var textNode = ContentsFinderReady->GetTextNodeById(3);
        if (textNode == null) return;

        var builder = new SeStringBuilder();
        builder.AddText($"{LuminaWrapper.GetAddonText(2780)} ")
               .AddUiForeground(32)
               .AddText($"[{DService.Instance().SeStringEvaluator.EvaluateFromAddon(9169, [second])}]")
               .AddUiForegroundOff();

        textNode->SetText(builder.Build().EncodeWithNullTerminator());
    }
}
