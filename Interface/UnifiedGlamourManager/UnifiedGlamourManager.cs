using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Lumina.Excel.Sheets;
using KamiToolKit.Nodes;
using OmenTools.Threading;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("UnifiedGlamourManagerTitle"),
        Description = Lang.Get("UnifiedGlamourManagerDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["ErxCharlotte"]
    };

    // 保存模版的按钮（在调查其他玩家的页面、编辑/选择自己幻化模版的页面）
    private TextButtonNode? inspectSaveButtonNode;
    private TextButtonNode? plateSaveButtonNode;

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
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, PLATE_EDITOR_ADDON_NAME, OnPlateEditorAddon);
        
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "CharacterInspect", OnInspectAddon);
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
            
            case AddonEvent.PostDraw:
                if (!MiragePrismMiragePlate->IsAddonAndNodesReady()) return;

                if (plateSaveButtonNode == null)
                {
                    plateSaveButtonNode = new()
                    {
                        Size     = new(200, 28),
                        Position = new(200, 10)
                    };

                    plateSaveButtonNode.AttachNode(MiragePrismMiragePlate->RootNode);
                }

                if (Throttler.Shared.Throttle("UnifiedGlamourManager-Preset-plateSaveButton"))
                {
                    plateSaveButtonNode.IsEnabled = GetCurrentPlateItems().Count > 0;
                    plateSaveButtonNode.String    = Lang.Get("UnifiedGlamourManager-Preset-SaveGlamourPreset");
                    plateSaveButtonNode.OnClick   = () => SaveCurrentPlateAsPreset(string.Empty, string.Empty, PresetSource.Self);
                }
                break;

            case AddonEvent.PreFinalize:
                plateSaveButtonNode = null;
                TaskHelper?.Abort();
                Overlay.IsOpen = false;
                break;
        }
    }

    private void OnInspectAddon(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (!CharacterInspect->IsAddonAndNodesReady()) return;

                var designButtonNode = CharacterInspect->GetNodeById(6);
                if (designButtonNode == null) return;

                if (inspectSaveButtonNode == null)
                {
                    inspectSaveButtonNode = new()
                    {
                        Size     = new(designButtonNode->Width, designButtonNode->Height),
                        Position = new(180, 10)
                    };

                    inspectSaveButtonNode.AttachNode(CharacterInspect->RootNode);
                }

                if (Throttler.Shared.Throttle("UnifiedGlamourManager-Preset-InspectButton"))
                {
                    inspectSaveButtonNode.IsEnabled = GetInspectPlateItems().Count > 0;
                    inspectSaveButtonNode.String    = Lang.Get("UnifiedGlamourManager-Preset-SaveGlamourPreset");
                    inspectSaveButtonNode.OnClick   = () => SaveCurrentPlateAsPreset(string.Empty, string.Empty, PresetSource.OtherPlayer);
                }
                break;

            case AddonEvent.PreFinalize:
                inspectSaveButtonNode = null;
                TaskHelper?.Abort();
                break;
        }
    }

    private static bool IsEquipSlotCategoryCompatibleWithPlateSlot(EquipSlotCategory category, uint slotIndex) =>
        slotIndex < PlateSlotDefinitions.Length && PlateSlotDefinitions[slotIndex].CanUse(category);
}
