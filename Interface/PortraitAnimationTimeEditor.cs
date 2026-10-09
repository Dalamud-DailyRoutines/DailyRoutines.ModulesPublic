using System.Runtime.InteropServices;
using DailyRoutines.Common.Info;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Havok.Animation.Playback.Control.Default;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Nodes.CameraSettingsButton;
using ButtonBase = KamiToolKit.Nodes.ButtonBase;
using CircleButtonIcon = OmenTools.KamiToolKit.Nodes.CircleButton.CircleButtonIcon;
using CircleButtonNode = OmenTools.KamiToolKit.Nodes.CircleButton.CircleButtonNode;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class PortraitAnimationTimeEditor : ModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = Lang.Get("PortraitAnimationTimeEditorTitle"),
        Description = Lang.Get("PortraitAnimationTimeEditorDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Yarukon"]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private int   frameCount;
    private float currentFrame;

    private PortraitAnimationTimeEditorAddon? addon;

    protected override void Init() =>
        addon ??= new(this)
        {
            InternalName = "DRPortraitAnimationTimeEditor",
            Title        = Info.Title,
            Size         = new(300f, 136f)
        };

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;
    }

    private void SetCurrentFrame
    (
        float frame
    )
    {
        currentFrame = Math.Clamp(frame, 0f, frameCount);
        UpdatePortraitCurrentFrame(currentFrame);
        addon?.SetPlaybackState(true);
    }

    private hkaDefaultAnimationControl* UpdateAnimationState()
    {
        var view = CharaView;
        var chara = view == null ?
                        null :
                        view->GetCharacter();
        var animation = GetAnimationControl(chara);
        var binding = animation == null ?
                          null :
                          animation->hkaAnimationControl.Binding.ptr;
        var clip = binding == null ?
                       null :
                       binding->Animation.ptr;
        var timeline = chara == null ?
                           null :
                           chara->Timeline.TimelineSequencer.GetSchedulerTimeline(0);

        if (view == null || clip == null || timeline == null)
        {
            frameCount   = 0;
            currentFrame = 0;
            return null;
        }

        frameCount   = Math.Max(0, (int)Math.Round(30f * (clip->Duration - 0.5f)));
        currentFrame = Math.Clamp(view->GetAnimationTime(), 0f, frameCount);
        return animation;
    }

    private static void UpdatePortraitCurrentFrame
    (
        float frame
    )
    {
        var editorState = EditorState;
        var view = editorState == null ?
                       null :
                       editorState->CharaView;
        var chara = view == null ?
                        null :
                        view->GetCharacter();
        var banner = (AddonBannerEditor*)BannerEditor;
        if (editorState == null || view == null || chara == null || banner == null)
            return;

        var baseTimeline = chara->Timeline.TimelineSequencer.GetSchedulerTimeline(0);
        if (baseTimeline == null)
            return;

        var delta = frame - baseTimeline->TimelineController.CurrentTimestamp;
        if (delta < 0)
            view->SetPoseTimed(chara->Timeline.BannerTimelineRowId, frame);
        else
            baseTimeline->UpdateBanner(delta);

        view->ToggleAnimationPlayback(true);
        banner->PlayAnimationCheckbox->AtkComponentButton.IsChecked = false;

        if (!editorState->HasDataChanged)
            editorState->SetHasChanged(true);
    }

    private static hkaDefaultAnimationControl* GetAnimationControl
    (
        Character* charaActor
    )
    {
        if (charaActor == null) return null;

        var model = ((Actor*)charaActor)->Model;
        var skeleton = model == null ?
                           null :
                           model->Skeleton;
        var partialSkeletons = skeleton == null ?
                                   null :
                                   skeleton->PartialSkeletons;
        var animatedSkeleton = partialSkeletons == null ?
                                   null :
                                   partialSkeletons->GetHavokAnimatedSkeleton(0);
        if (animatedSkeleton == null)
            return null;

        var animationControls = animatedSkeleton->AnimationControls;
        return animationControls.Length == 0 ?
                   null :
                   animationControls[0];
    }

    private sealed class PortraitAnimationTimeEditorAddon
    (
        PortraitAnimationTimeEditor module
    ) : AttachedAddon("BannerEditor")
    {
        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.RightBottom;

        private TextNode                 frameLabel     = null!;
        private FloatSliderNode          frameSlider    = null!;
        private CameraSettingsButtonNode playbackButton = null!;

        private ButtonBase[] controlButtons = [];
        private bool         isSyncing;
        private bool?        pendingPlaybackState;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var verticalList = new VerticalListNode
            {
                FitContents = true,
                Position    = ContentStartPosition
            };
            verticalList.AttachNode(this);

            var controlRow = new HorizontalListNode
            {
                Size             = ContentSize with { Y = 32 },
                FirstItemSpacing = (ContentSize.X - CONTROL_WIDTH) / 2f,
                ItemSpacing      = BUTTON_SPACING
            };

            controlButtons =
            [
                CreateControlButton
                (
                    CircleButtonIcon.LeftArrow,
                    Lang.Get("PortraitAnimationTimeEditor-LastFrame"),
                    () => module.SetCurrentFrame(MathF.Ceiling(module.currentFrame) - 1f)
                ),
                playbackButton = new CameraSettingsButtonNode
                {
                    Size        = new(BUTTON_SIZE),
                    Icon        = CameraSettingsButtonIcon.Play,
                    TextTooltip = LuminaWrapper.GetAddonText(14704),
                    OnClick     = TogglePlayback
                },
                CreateControlButton
                (
                    CircleButtonIcon.RightArrow,
                    Lang.Get("PortraitAnimationTimeEditor-NextFrame"),
                    () => module.SetCurrentFrame(MathF.Floor(module.currentFrame) + 1f)
                )
            ];
            controlRow.AddNode(controlButtons);
            controlRow.RecalculateLayout();

            verticalList.AddNode(controlRow);

            frameLabel = new TextNode
            {
                AlignmentType = AlignmentType.Center,
                TextFlags     = TextFlags.Edge | TextFlags.AutoAdjustNodeSize,
                String        = "000.00 / 000",
                Size          = ContentSize with { Y = 28f }
            };
            AtkColors.ValueEmphasize.ApplyTo(frameLabel);
            verticalList.AddNode(frameLabel);

            frameSlider = new FloatSliderNode
            {
                Min  = 0f,
                Max  = 1f,
                Step = 1f,
                Size = new(Size.X + 20f, 28f),
                OnValueChanged = value =>
                {
                    if (!isSyncing)
                        module.SetCurrentFrame(value);
                }
            };
            frameSlider.FloatValueNode.FontSize = 0;

            verticalList.AddNode(frameSlider);

            SetWindowSize(Size.X, ContentStartPosition.Y + verticalList.Height + 16f);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (HostAddon != null && HostAddon->GetNodeById(107) != null)
                SyncControls(module.UpdateAnimationState());

            base.OnUpdate(addon);
        }

        private static CircleButtonNode CreateControlButton
        (
            CircleButtonIcon icon,
            string           tooltip,
            Action           onClick
        ) =>
            new()
            {
                Size        = new(BUTTON_SIZE),
                Icon        = icon,
                TextTooltip = tooltip,
                OnClick     = onClick
            };

        private void TogglePlayback()
        {
            var view = CharaView;
            var control = GetAnimationControl
            (
                view == null ?
                    null :
                    view->GetCharacter()
            );
            var banner = (AddonBannerEditor*)BannerEditor;
            if (view == null || control == null || banner == null)
                return;

            var isPlaying = pendingPlaybackState ?? control->PlaybackSpeed > 0;
            view->ToggleAnimationPlayback(isPlaying);
            banner->PlayAnimationCheckbox->AtkComponentButton.IsChecked = false;
            SetPlaybackState(!isPlaying, true);
        }

        private void SyncControls
        (
            hkaDefaultAnimationControl* animation
        )
        {
            var hasAnimation = animation != null;
            var maxFrame     = MathF.Max(1f, module.frameCount);

            foreach (var button in controlButtons)
                button.IsEnabled = hasAnimation;

            frameSlider.IsEnabled = hasAnimation;
            var actualIsPlaying = hasAnimation && animation->PlaybackSpeed > 0;
            if (!hasAnimation || pendingPlaybackState == actualIsPlaying)
                pendingPlaybackState = null;

            UpdatePlaybackButton(pendingPlaybackState ?? actualIsPlaying);

            frameLabel.String = module.frameCount < 100 ?
                                    $"{module.currentFrame:F3} / {module.frameCount}" :
                                    $"{module.currentFrame:F2} / {module.frameCount}";

            isSyncing = true;
            if (MathF.Abs(frameSlider.Max - maxFrame) > 0.001f)
                frameSlider.Max = maxFrame;
            frameSlider.Value = module.currentFrame;
            isSyncing         = false;
        }

        public void SetPlaybackState
        (
            bool isPlaying,
            bool refreshTooltip = false
        )
        {
            pendingPlaybackState = isPlaying;
            UpdatePlaybackButton(isPlaying);

            if (!refreshTooltip)
                return;

            playbackButton.HideTooltip();
            playbackButton.ShowTooltip();
        }

        private void UpdatePlaybackButton
        (
            bool isPlaying
        )
        {
            playbackButton.Icon = isPlaying ?
                                      CameraSettingsButtonIcon.Pause :
                                      CameraSettingsButtonIcon.Play;
            playbackButton.TextTooltip = isPlaying ?
                                             LuminaWrapper.GetAddonText(14705) :
                                             LuminaWrapper.GetAddonText(14704);
        }

        #region 常量

        private const float BUTTON_SIZE    = 28f;
        private const float BUTTON_SPACING = 4f;
        private const float CONTROL_WIDTH  = (3f * BUTTON_SIZE) + (2f * BUTTON_SPACING);

        #endregion
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

    private static AgentBannerEditorState* EditorState =>
        AgentBannerEditor.Instance()->EditorState;

    private static CharaViewPortrait* CharaView =>
        EditorState != null ?
            EditorState->CharaView :
            null;

    #endregion
}
