using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Interop.Windows.Helpers;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public class AutoQTE : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoQTETitle"),
        Description = Lang.Get("AutoQTEDescription"),
        Category    = ModuleCategory.Combat
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private static readonly string[] QTETypes = ["_QTEKeep", "_QTEMash", "_QTEKeepTime", "_QTEButton"];

    protected override void Init()
    {
        InputIDManager.Instance().RegPrePressed(OnPreIsInputIDPressed);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw, QTETypes, OnQTEAddon);
    }

    private static void OnPreIsInputIDPressed(ref bool? overrideResult, ref InputId id)
    {
        if (GameState.ContentFinderCondition == 0)
            return;

        if (!Throttler.Shared.Check("AutoQTE-QTE"))
            overrideResult = false;
    }

    private static unsafe void OnQTEAddon(AddonEvent type, AddonArgs args)
    {
        Throttler.Shared.Throttle("AutoQTE-QTE", 1_000, true);
        KeyEmulationHelper.SendKeypress(Keys.Space);
        AtkStage.Instance()->ClearFocus();
    }

    protected override void Uninit()
    {
        InputIDManager.Instance().UnregPrePressed(OnPreIsInputIDPressed);
        DService.Instance().AddonLifecycle.UnregisterListener(OnQTEAddon);
    }
}
