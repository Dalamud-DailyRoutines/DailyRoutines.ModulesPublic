using ClickLib.Clicks;
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
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoPreserveCollectableTitle"),
        Description = GetLoc("AutoPreserveCollectableDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private static readonly HashSet<uint> GatherJobs = [16, 17, 18];
    private static string PreserveMessage = string.Empty;

    public override void Init()
    {
        PreserveMessage = (LuminaCache.GetRow<Addon>(1463)!.Value.Text.ToDalamudString().Payloads[0] as TextPayload).Text;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddon);
    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon;
        if (addon == null) return;

        var localPlayer = DService.ClientState.LocalPlayer;
        if (localPlayer == null || !GatherJobs.Contains(localPlayer.ClassJob.RowId)) return;

        var title = Marshal.PtrToStringUTF8((nint)addon->AtkValues[0].String);
        if (string.IsNullOrWhiteSpace(title) || !title.Contains(PreserveMessage)) return;

        ClickSelectYesNo.Using(args.Addon).Yes();
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
    }
}
