using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.ModulesPublic;

public class AutoHummer : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoCTSTitle"),
        Description = Lang.Get("AutoCTSDescription"),
        Category    = ModuleCategory.GoldSaucer
    };

    protected override void Init()
    {
        TaskHelper ??= new();
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Hummer", OnAddonSetup);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSetup);

    protected override void ConfigUI() => ImGuiOm.ConflictKeyText();

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (TaskHelper.AbortByConflictKey(this)) return;

        TaskHelper.Enqueue(WaitSelectStringAddon);
        TaskHelper.Enqueue(ClickGameButton);
    }

    private bool WaitSelectStringAddon()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        return AddonSelectStringEvent.Select(0);
    }

    private unsafe bool ClickGameButton()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;

        if (!Hummer->IsAddonAndNodesReady())
            return false;

        var button = Hummer->GetComponentButtonById(29);
        if (button == null || !button->IsEnabled) return false;

        Hummer->IsVisible = false;

        Hummer->Callback(11, 3, 0);

        // 只是纯粹因为游玩动画太长了而已
        TaskHelper.DelayNext(5000);
        TaskHelper.Enqueue(StartAnotherRound);
        return true;
    }

    private unsafe bool StartAnotherRound()
    {
        if (TaskHelper.AbortByConflictKey(this)) return true;
        if (DService.Instance().Condition.IsOccupiedInEvent) return false;

        var machineTarget = TargetManager.PreviousTarget;
        var machine =
            machineTarget.Name.TextValue.Contains
            (
                LuminaGetter.GetRow<EObjName>(2005035)!.Value.Singular.ToString(),
                StringComparison.OrdinalIgnoreCase
            )
                ? (GameObject*)machineTarget.Address
                : null;

        if (machine != null)
        {
            TargetSystem.Instance()->InteractWithObject(machine);
            return true;
        }

        return false;
    }
}
