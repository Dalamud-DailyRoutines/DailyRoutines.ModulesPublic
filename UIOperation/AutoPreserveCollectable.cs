using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DailyRoutines.Modules;

public class AutoPreserveCollectable : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoPreserveCollectableTitle"),
        Description = GetLoc("AutoPreserveCollectableDescription"),
        Category    = ModuleCategories.UIOperation,
    };

    protected override void Init() => DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddon);

    private static void OnAddon(AddonEvent type, AddonArgs args) =>
        ClickSelectYesnoYes((LuminaGetter.GetRow<Addon>(1463)!.Value.Text.ToDalamudString().Payloads[0] as TextPayload).Text);

    protected override void Uninit() => DService.AddonLifecycle.UnregisterListener(OnAddon);
}
