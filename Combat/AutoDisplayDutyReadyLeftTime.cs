using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayDutyReadyLeftTime : ModuleBase
{
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
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ContentsFinderReady", OnAddon);

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
    
    private static void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (!ContentsFinderReady->IsAddonAndNodesReady()) return;
        
        var textNode = ContentsFinderReady->GetTextNodeById(3);
        if (textNode == null) return;
        
        var contentFinder = ContentsFinder.Instance();
        if (contentFinder == null) return;

        var readyTimestamp = contentFinder->QueueInfo.QueueReadyTimestamp;
        if (readyTimestamp == 0) return;

        var readyTime = readyTimestamp.ToUTCDateTimeFromUnixSeconds();
        var dueTime   = readyTime + TimeSpan.FromSeconds(45);
        var leftTime  = dueTime   - StandardTimeManager.Instance().UTCNow;
        if (leftTime.TotalSeconds < 0) return;
        
        using var rented  = new RentedSeStringBuilder();
        var       builder = rented.Builder;
        
        builder.Append($"{LuminaWrapper.GetAddonText(2780)} ")
               .PushColorType(32)
               .Append($"[{DService.Instance().SeStringEvaluator.EvaluateFromAddon(9169, [(int)leftTime.TotalSeconds])}]")
               .PopColorType();

        textNode->SetText(builder.GetViewAsSpan());
    }
}
