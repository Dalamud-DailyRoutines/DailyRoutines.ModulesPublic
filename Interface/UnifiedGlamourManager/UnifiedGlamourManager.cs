using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private DalamudLinkPayload? openOverlayPlateTabPayload;

    protected override unsafe void Init()
    {
        config = Config.Load(this) ?? new();
        NormalizeConfig();

        selectedPreset = config.Presets
                               .OrderByDescending(x => x.CreatedAt)
                               .FirstOrDefault();

        TaskHelper = new() { TimeoutMS = TASK_TIMEOUT_MS };

        GameState.Instance().Login  += ResetStoredItemsSession;
        GameState.Instance().Logout += ResetStoredItemsSession;
        if (GameState.IsLoggedIn)
            StartRefreshAll();

        Overlay ??= new(this);

        Overlay.Flags      &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags      &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Overlay.WindowName =  $"{Info.Title}###UnifiedGlamourManagerOverlay";

        var addonLifecycle = IAddonLifecycle.Instance();
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "MiragePrismMiragePlate", OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MiragePrismMiragePlate", OnPlateEditorAddon);
        if (MiragePrismMiragePlate->IsAddonAndNodesReady())
            OnPlateEditorAddon(AddonEvent.PostRefresh, null);

        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "CharacterInspect", OnInspectAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharacterInspect", OnInspectAddon);
        if (CharacterInspect->IsAddonAndNodesReady())
            OnInspectAddon(AddonEvent.PostRefresh, null);

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   ["MiragePrismPrismBox", "Cabinet"], OnStorageAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ["MiragePrismPrismBox", "Cabinet"], OnStorageAddon);

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("UnifiedGlamourManager-CommandHelp") });

        openOverlayPlateTabPayload = LinkPayloadManager.Instance().Reg
        (
            (_, _) =>
            {
                manualSelectedTabIndex = 1;
                Overlay.IsOpen = true;
            },
            out _
        );
    }

    protected override void Uninit()
    {
        GameState.Instance().Login  -= ResetStoredItemsSession;
        GameState.Instance().Logout -= ResetStoredItemsSession;
        EndPresetEditing();
        presetItemRows?.Dispose();
        presetItemRows = null;
        presetItemIndex.Clear();
        enablePresetLivePreview = false;
        ResetStoredItemsSession();

        if (openOverlayPlateTabPayload != null)
            LinkPayloadManager.Instance().Unreg(openOverlayPlateTabPayload.CommandId);
        
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        IAddonLifecycle.Instance().UnregisterListener(OnInspectAddon);
        IAddonLifecycle.Instance().UnregisterListener(OnPlateEditorAddon);
        IAddonLifecycle.Instance().UnregisterListener(OnStorageAddon);

        inspectSaveButtonNode?.Dispose();
        inspectSaveButtonNode = null;

        plateSaveButtonNode?.Dispose();
        plateSaveButtonNode = null;
    }

    private void OnCommand
    (
        string command,
        string arguments
    )
    {
        StartRefreshAll();
        Overlay.IsOpen = true;
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot
    (
        EquipSlotCategory category,
        uint              slotIndex
    ) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);
}
