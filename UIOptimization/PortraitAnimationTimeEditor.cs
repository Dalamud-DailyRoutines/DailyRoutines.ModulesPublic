using System;
using System.Runtime.InteropServices;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;

namespace DailyRoutines.ModulesPublic;

public unsafe class PortraitAnimationTimeEditor : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("PortraitAnimationTimeEditorTitle"),
        Description = GetLoc("PortraitAnimationTimeEditorDescription"),
        Author      = ["Yarukon"],
        Category    = ModuleCategories.UIOptimization
    };

    private static AgentBannerEditorState* EditorState   => AgentBannerEditor.Instance()->EditorState;
    private static CharaViewPortrait*      CharaView     => EditorState != null ? EditorState->CharaView : null;
    private static Character*              PortraitChara => CharaView   != null ? CharaView->GetCharacter() : null;

    private static float Duration;
    private static int   FrameCount;
    private static float CurrentFrame;

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                        ImGuiWindowFlags.NoMove     | ImGuiWindowFlags.AlwaysAutoResize;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "BannerEditor", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "BannerEditor", OnAddon);
        if (IsAddonAndNodesReady(BannerEditor)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

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

        var nodeState = NodeState.Get(charaResNode);

        using var font = FontManager.UIFont80.Push();

        ImGui.SetWindowPos(nodeState.Position with { Y = nodeState.Position.Y - ImGui.GetWindowSize().Y - (2f * GlobalFontScale) });

        var control = GetAnimationControl(PortraitChara);
        using (ImRaii.Group())
        {
            if (ImGuiOm.ButtonIcon("###LastTenFrame", FontAwesomeIcon.Backward, "-10"))
            {
                CurrentFrame = Math.Max(0, CurrentFrame - 10);
                UpdatePortraitCurrentFrame(CurrentFrame);
            }

            ImGui.SameLine();
            if (ImGui.ArrowButton("###LastFrame", ImGuiDir.Left))
            {
                CurrentFrame = Math.Max(0, CurrentFrame - 1);
                UpdatePortraitCurrentFrame(CurrentFrame);
            }
            ImGuiOm.TooltipHover("-1");

            var isPlaying = control->PlaybackSpeed > 0;
            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGuiOm.ButtonIcon("PauseAndPlay", isPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play))
            {
                CharaView->ToggleAnimationPlayback(isPlaying);
                ((AddonBannerEditor*)BannerEditor)->PlayAnimationCheckbox->AtkComponentButton.IsChecked = false;
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Ceiling", FontAwesomeIcon.GripLines))
            {
                CurrentFrame = MathF.Ceiling(CurrentFrame);
                UpdatePortraitCurrentFrame(CurrentFrame);
            }

            ImGui.SameLine(0, 8f * GlobalFontScale);
            if (ImGui.ArrowButton("###NextFrame", ImGuiDir.Right))
            {
                CurrentFrame = Math.Min(CurrentFrame + 1, FrameCount);
                UpdatePortraitCurrentFrame(CurrentFrame);
            }

            ImGuiOm.TooltipHover("+1");

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###NextTenFrame", FontAwesomeIcon.Forward, "+10"))
            {
                CurrentFrame = Math.Min(CurrentFrame + 10, FrameCount);
                UpdatePortraitCurrentFrame(CurrentFrame);
            }
        }

        ImGui.SetNextItemWidth(MathF.Max(nodeState.Size.X - (4 * ImGui.GetStyle().ItemSpacing.X), 200f * GlobalFontScale));
        if (ImGui.SliderFloat("###TimestampSlider", ref CurrentFrame, 0f, FrameCount,
                              FrameCount < 100 ? $"%.3f / {FrameCount}" : $"%.2f / {FrameCount}"))
            UpdatePortraitCurrentFrame(CurrentFrame);
        
        CurrentFrame = CharaView->GetAnimationTime();
        UpdateDuration(PortraitChara);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        base.Uninit();
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

    private static void UpdateDuration(Character* chara)
    {
        var animation = GetAnimationControl(chara);
        if (animation == null)
            return;

        var baseTimeline = PortraitChara->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
        if (baseTimeline == null)
            return;

        var timelineKey = (nint)baseTimeline->ActionTimelineKey.Value;
        var timelineStr = timelineKey != 0 ? Marshal.PtrToStringUTF8(timelineKey) : null;
        if (timelineStr is "normal/idle")
            return;

        Duration   = animation->hkaAnimationControl.Binding.ptr->Animation.ptr->Duration - 0.5f;
        FrameCount = (int)Math.Round(30f * Duration);
    }

    public static hkaDefaultAnimationControl* GetAnimationControl(Character* charaActor)
    {
        if (charaActor == null) return null;

        if (DService.ClientState.ClientLanguage == (ClientLanguage)4)
        {
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
        else
        {
            var actor = (ActorGlobal*)charaActor;
            if (actor->Model                                                                                      == null ||
                actor->Model->Skeleton                                                                            == null ||
                actor->Model->Skeleton->PartialSkeletons                                                          == null ||
                actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)                             == null ||
                actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls.Length   == 0    ||
                actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls[0].Value == null)
                return null;

            return actor->Model->Skeleton->PartialSkeletons->GetHavokAnimatedSkeleton(0)->AnimationControls[0];
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Actor : IActor
    {
        [FieldOffset(240)]
        public ActorModel* model;

        public ActorModel* Model => model;
    }
    
    [StructLayout(LayoutKind.Explicit)]
    private struct ActorGlobal
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
    
    private interface IActor
    {
        public ActorModel* Model { get; }
    }
}
