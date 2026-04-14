using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public class AutoPreserveCollectable : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoPreserveCollectableTitle"),
        Description = Lang.Get("AutoPreserveCollectableDescription"),
        Category    = ModuleCategory.UIOperation
    };

    protected override void Init() =>
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddon);
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args) =>
        AddonSelectYesnoEvent.ClickYes((LuminaGetter.GetRowOrDefault<Addon>(1463).Text.ToDalamudString().Payloads[0] as TextPayload).Text);
}
