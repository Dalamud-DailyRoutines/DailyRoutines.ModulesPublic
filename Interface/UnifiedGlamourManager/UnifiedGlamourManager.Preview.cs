using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private bool enablePresetLivePreview;
    private long nextPreviewAt;
    private bool isPreviewPending;
    private long previewVersion;
    private bool wasTryOnVisible;

    private void InvalidatePresetPreview()
    {
        previewVersion++;
        isPreviewPending = false;
    }

    private void QueuePresetPreview()
    {
        InvalidatePresetPreview();
        isPreviewPending = enablePresetLivePreview && isPresetEditorOpen && editingPreset != null &&
                           originalEditingPreset == selectedPreset;
        nextPreviewAt = Environment.TickCount64 + 300;
    }

    private void UpdatePresetLivePreview()
    {
        if (!isPresetEditorOpen || editingPreset == null || !enablePresetLivePreview || originalEditingPreset != selectedPreset)
            return;

        if (isPreviewPending && Environment.TickCount64 >= nextPreviewAt && !TaskHelper.IsBusy &&
            CanTryOnPreset())
        {
            var generation   = previewVersion;
            isPreviewPending = false;
            StartTryOnPreset
            (
                editingPreset,
                () => isPresetEditorOpen && enablePresetLivePreview && originalEditingPreset == selectedPreset && generation == previewVersion
            );
        }

        var addon = Tryon;
        if (addon == null || !addon->IsVisible)
        {
            wasTryOnVisible = false;
            return;
        }

        if (wasTryOnVisible)
            return;

        wasTryOnVisible = true;
        var position    = ImGui.GetWindowPos();
        var size        = ImGui.GetWindowSize();
        var viewport    = ImGui.GetMainViewport();
        var width       = addon->GetScaledWidth(true);
        var height      = addon->GetScaledHeight(true);
        var x           = position.X + size.X + (8 * GlobalUIScale);

        if (x + width > viewport.WorkPos.X + viewport.WorkSize.X)
            x = position.X - width - (8 * GlobalUIScale);

        x = Math.Clamp
        (
            x,
            viewport.WorkPos.X,
            Math.Max(viewport.WorkPos.X, viewport.WorkPos.X + viewport.WorkSize.X - width)
        );
        var y = Math.Clamp
        (
            position.Y,
            viewport.WorkPos.Y,
            Math.Max(viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - height)
        );

        if (addon->X != (short)x || addon->Y != (short)y)
            addon->SetPosition((short)x, (short)y);
    }
}
