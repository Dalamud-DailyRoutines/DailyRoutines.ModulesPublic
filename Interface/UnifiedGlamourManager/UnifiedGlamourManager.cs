using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Lumina.Excel.Sheets;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["ErxCharlotte"]
    };
    
    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();

        selectedPreset = config.Presets
                               .OrderByDescending(x => x.CreatedAt)
                               .FirstOrDefault();

        TaskHelper ??= new() { TimeoutMS = TASK_TIMEOUT_MS };

        Overlay ??= new(this);

        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName =  $"{Info.Title}###UnifiedGlamourManagerOverlay";

        var addonLifecycle = DService.Instance().AddonLifecycle;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw,    PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw,    "CharacterInspect", OnInspectAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterInspect", OnInspectAddon);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("UnifiedGlamourManager-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        DService.Instance().AddonLifecycle.UnregisterListener(OnInspectAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnPlateEditorAddon);

        inspectSaveButtonNode?.Dispose();
        inspectSaveButtonNode = null;

        plateSaveButtonNode?.Dispose();
        plateSaveButtonNode = null;
    }

    private void OnCommand(string command, string arguments) => Overlay!.IsOpen = true;

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);
}
