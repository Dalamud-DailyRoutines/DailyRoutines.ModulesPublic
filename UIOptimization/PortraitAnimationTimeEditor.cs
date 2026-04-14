using System.Numerics;
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class PortraitAnimationTimeEditor : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = Lang.Get("PortraitAnimationTimeEditorTitle"),
        Description = Lang.Get("PortraitAnimationTimeEditorDescription"),
        Author      = ["Yarukon"],
        Category    = ModuleCategory.UIOptimization
    };
    
    private float duration;
    private int   frameCount;
    private float currentFrame;

    private Vector2 componentSize = new(100);

    protected override void Init()
    {
        Overlay ??= new(this);
        Overlay.Flags = ImGuiWindowFlags.NoTitleBar  |
                        ImGuiWindowFlags.NoResize    |
                        ImGuiWindowFlags.NoMove      |
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "BannerEditor", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", OnAddon);
        if (BannerEditor->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }
    
    protected override void Uninit() =>
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

    protected override void OverlayUI()
    {
        var addon = BannerEditor;

        if (addon == null)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (PortraitChara == null) return;

        var charaResNode = addon->GetNodeById(107);
        if (charaResNode == null) return;

        var nodeState = charaResNode->GetNodeState();

        using var font = FontManager.Instance().UIFont80.Push();

        ImGui.SetWindowPos(nodeState.TopLeft with { Y = nodeState.Y - ImGui.GetWindowSize().Y - 2f * GlobalUIScale });
        ImGui.SetWindowSize(nodeState.Size with { Y = 3f * ImGui.GetTextLineHeightWithSpacing() - 1 * ImGui.GetStyle().ItemSpacing.Y });

        var control = GetAnimationControl(PortraitChara);
        ImGuiHelpers.CenterCursorFor(componentSize.X);

        using (ImRaii.Group())
        {
            if (ImGuiOm.ButtonIcon("###LastTenFrame", FontAwesomeIcon.Backward, "-10"))
            {
                currentFrame = Math.Max(0, currentFrame - 10);
                UpdatePortraitCurrentFrame(currentFrame);
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("###LastFrame", FontAwesomeIcon.ArrowLeft))
            {
                currentFrame = Math.Max(0, currentFrame - 1);
                UpdatePortraitCurrentFrame(currentFrame);
            }

            ImGuiOm.TooltipHover("-1");

            var isPlaying = control->PlaybackSpeed > 0;
            ImGui.SameLine(0, 8f * GlobalUIScale);

            if (ImGuiOm.ButtonIcon("PauseAndPlay", isPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play))
            {
                CharaView->ToggleAnimationPlayback(isPlaying);
                ((AddonBannerEditor*)BannerEditor)->PlayAnimationCheckbox->AtkComponentButton.IsChecked = false;
            }

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("Ceiling", FontAwesomeIcon.GripLines))
            {
                currentFrame = MathF.Ceiling(currentFrame);
                UpdatePortraitCurrentFrame(currentFrame);
            }

            ImGui.SameLine(0, 8f * GlobalUIScale);

            if (ImGuiOm.ButtonIcon("###NextFrame", FontAwesomeIcon.ArrowRight))
            {
                currentFrame = Math.Min(currentFrame + 1, frameCount);
                UpdatePortraitCurrentFrame(currentFrame);
            }

            ImGuiOm.TooltipHover("+1");

            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("###NextTenFrame", FontAwesomeIcon.Forward, "+10"))
            {
                currentFrame = Math.Min(currentFrame + 10, frameCount);
                UpdatePortraitCurrentFrame(currentFrame);
            }
        }

        componentSize = ImGui.GetItemRectSize();

        ImGui.SetNextItemWidth(nodeState.Size.X - 4 * ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.SliderFloat
            (
                "###TimestampSlider",
                ref currentFrame,
                0f,
                frameCount,
                frameCount < 100 ? $"%.3f / {frameCount}" : $"%.2f / {frameCount}"
            ))
            UpdatePortraitCurrentFrame(currentFrame);

        currentFrame = CharaView->GetAnimationTime();
        UpdateDuration(PortraitChara);
    }
    
    private void OnAddon(AddonEvent type, AddonArgs? args) =>
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };

    private static void UpdatePortraitCurrentFrame(float frame)
    {
        var baseTimeline = PortraitChara->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
        if (baseTimeline == null) return;

        var delta = frame - baseTimeline->TimelineController.CurrentTimestamp;
        if (delta < 0)
            CharaView->SetPoseTimed(PortraitChara->Timeline.BannerTimelineRowId, frame);
        else
            baseTimeline->UpdateBanner(delta);

        CharaView->ToggleAnimationPlayback(true);
        ((AddonBannerEditor*)BannerEditor)->PlayAnimationCheckbox->AtkComponentButton.IsChecked = false;

        if (!EditorState->HasDataChanged)
            EditorState->SetHasChanged(true);
    }

    private void UpdateDuration(Character* chara)
    {
        var animation = GetAnimationControl(chara);
        if (animation == null)
            return;

        var baseTimeline = PortraitChara->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
        if (baseTimeline == null)
            return;

        duration   = animation->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration - 0.5f;
        frameCount = (int)Math.Round(30f * duration);
    }

    public static hkaDefaultAnimationControl* GetAnimationControl(Character* charaActor)
    {
        if (charaActor == null) return null;

        var actor = (Actor*)charaActor;
        if (actor->Model                                                                                      == null ||
            actor->Model->Skeleton                                                                            == null ||
            actor->Model->Skeleton->PartialSkeletons                                                          == null ||
            actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)                             == null ||
            actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls.Length   == 0    ||
            actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls[0].Value == null)
            return null;

        return actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls[0];
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Actor
    {
        [FieldOffset(256)]
        public ActorModel* Model;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ActorModel
    {
        [FieldOffset(160)]
        public Skeleton* Skeleton;
    }

    #region 常量

    private static AgentBannerEditorState* EditorState   => AgentBannerEditor.Instance()->EditorState;
    private static CharaViewPortrait*      CharaView     => EditorState != null ? EditorState->CharaView : null;
    private static Character*              PortraitChara => CharaView   != null ? CharaView->GetCharacter() : null;

    #endregion
}
