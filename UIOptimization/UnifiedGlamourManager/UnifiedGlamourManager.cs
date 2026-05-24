using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.UIOptimization,
        Author      = ["ErxCharlotte"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();
        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };

        Overlay ??= new(this);

        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName =  $"{Info.Title}###UnifiedGlamourManagerOverlay";

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
    }

    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnPlateEditorAddon);

    private void OnPlateEditorAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (TryGetReadyPlateEditor(out var agent) &&
                    agent->Data->OpenMode == MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW)
                {
                    Overlay.IsOpen = true;
                    StartRefreshAll();
                }

                break;

            case AddonEvent.PreFinalize:
                Overlay.IsOpen = false;
                break;
        }
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);
}
