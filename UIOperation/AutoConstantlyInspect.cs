using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class AutoConstantlyInspect : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoConstantlyInspectTitle"),
        Description = Lang.Get("AutoConstantlyInspectDescription"),
        Category    = ModuleCategory.UIOperation
    };

    protected override void Init() =>
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemInspectionResult", OnAddon);
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void ConfigUI() => ImGuiOm.ConflictKeyText();

    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
        {
            NotifyHelper.Instance().NotificationSuccess(Lang.Get("ConflictKey-InterruptMessage"));
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null) return;

        var nextButton = addon->GetComponentButtonById(74);
        if (nextButton == null || !nextButton->IsEnabled) return;

        AgentId.ItemInspection.SendEvent(3, 0);
        addon->Close(true);
    }
}
