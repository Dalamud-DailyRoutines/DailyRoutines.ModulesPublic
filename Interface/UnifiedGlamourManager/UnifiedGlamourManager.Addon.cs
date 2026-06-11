using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using KamiToolKit.Nodes;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private TextButtonNode? inspectSaveButtonNode;
    private TextButtonNode? plateSaveButtonNode;

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
                        Size     = new(200, 30),
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
}
