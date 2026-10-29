using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Nodes.CircleButton;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private CircleButtonNode? inspectSaveButtonNode;
    private CircleButtonNode? plateSaveButtonNode;

    private void OnStorageAddon
    (
        AddonEvent type,
        AddonArgs  args
    ) =>
        StartRefreshAll();

    private void OnPlateEditorAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostRefresh:
                if (MiragePrismMiragePlate == null) 
                    return;
                
                if (TryGetReadyPlateEditor(out var agent) &&
                    agent->Data->OpenMode == MIRAGE_PLATE_OPEN_MODE_AGENT_SHOW)
                {
                    Overlay.IsOpen = true;
                    StartRefreshAll();
                }

                if (plateSaveButtonNode == null)
                {
                    plateSaveButtonNode = new()
                    {
                        Size        = new(24),
                        Position    = new(405, 40),
                        TextTooltip = Lang.Get("UnifiedGlamourManager-Preset-SaveGlamourPreset"),
                        Icon        = CircleButtonIcon.Document,
                        OnClick     = () => SaveCurrentPlateAsPreset(string.Empty, string.Empty, PresetSource.Self)
                    };

                    plateSaveButtonNode.AttachNode(MiragePrismMiragePlate->RootNode);
                }
                
                plateSaveButtonNode.IsEnabled = GetCurrentPlateItems().Count > 0;

                break;

            case AddonEvent.PreFinalize:
                plateSaveButtonNode = null;
                TaskHelper?.Abort();
                
                Overlay.IsOpen = false;
                break;
        }
    }

    private void OnInspectAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        switch (type)
        {
            case AddonEvent.PostRefresh:
                if (CharacterInspect == null) return;

                if (inspectSaveButtonNode == null)
                {
                    inspectSaveButtonNode = new()
                    {
                        Size        = new(24),
                        Position    = new(220, 510),
                        TextTooltip = Lang.Get("UnifiedGlamourManager-Preset-SaveGlamourPreset"),
                        Icon        = CircleButtonIcon.Document,
                        OnClick     = () => SaveCurrentPlateAsPreset(string.Empty, string.Empty, PresetSource.OtherPlayer)
                    };

                    inspectSaveButtonNode.AttachNode(CharacterInspect->RootNode);
                }
                
                inspectSaveButtonNode.IsEnabled = GetInspectPlateItems().Count > 0;

                break;

            case AddonEvent.PreFinalize:
                inspectSaveButtonNode = null;
                TaskHelper?.Abort();
                break;
        }
    }
}
