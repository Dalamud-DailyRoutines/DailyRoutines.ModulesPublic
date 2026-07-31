using System.Numerics;
using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using Action = Lumina.Excel.Sheets.Action;
using AgentShowDelegate = OmenTools.Interop.Game.Models.Native.AgentShowDelegate;
using DetailKind = FFXIVClientStructs.FFXIV.Client.Enums.DetailKind;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class BetterMKDSupportJobList : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterMKDSupportJobListTitle"),
        Description = Lang.Get("BetterMKDSupportJobListDescription"),
        Category    = ModuleCategory.Duty,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/BetterMKDSupportJobList/preview-1.png"
        ]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private Hook<AgentShowDelegate>? agentMKDSupportJobShowHook;

    private TextButtonNode?    jobChangeButton;
    private AddonDRMKDJobList? mkdJobListAddon;

    private int dragDropJobIndex = -1;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        var addedJobs        = config.AddonSupportJobOrder.ToHashSet();
        var isAnyNewJobOrder = false;

        foreach (var job in LuminaGetter.Get<MKDSupportJob>())
        {
            if (addedJobs.Contains(job.RowId)) continue;
            config.AddonSupportJobOrder.Add(job.RowId);
            isAnyNewJobOrder = true;
        }

        if (isAnyNewJobOrder)
            config.Save(this);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,    "MKDInfo", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MKDInfo", OnAddon);

        mkdJobListAddon ??= new(this)
        {
            InternalName          = "DRMKDJobList",
            Title                 = LuminaWrapper.GetAddonText(16658),
            Size                  = new(500f, 450f),
            RememberClosePosition = true
        };

        agentMKDSupportJobShowHook ??= DService.Instance().Hook.HookFromAddress<AgentShowDelegate>
        (
            AgentMKDSupportJob.Instance()->VirtualTable->GetVFuncByName("Show"),
            AgentMKDSupportJobShowDetour
        );
        agentMKDSupportJobShowHook.Enable();

        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("BetterMKDSupportJobList-CommandHelp") });
    }

    protected override void Uninit()
    {
        CommandManager.Instance().RemoveSubCommand(COMMAND);

        agentMKDSupportJobShowHook?.Dispose();
        agentMKDSupportJobShowHook = null;

        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);

        jobChangeButton?.Dispose();
        jobChangeButton = null;

        mkdJobListAddon?.Dispose();
        mkdJobListAddon = null;
    }

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToUInt(), Lang.Get("Command"));
        using (ImRaii.PushIndent())
            ImGui.TextUnformatted($"/pdr {COMMAND} → Lang.Get(\"BetterMKDSupportJobList-CommandHelp\")");
        
        
        ImGui.NewLine();
        
        if (ImGui.CollapsingHeader(Lang.Get("BetterMKDSupportJobList-ModifySupportJobOrder")))
        {
            if (ImGui.SmallButton(Lang.Get("Save")))
                config.Save(this);

            ImGui.SameLine();

            if (ImGui.SmallButton(Lang.Get("Reset")))
            {
                config.AddonSupportJobOrder = config.AddonSupportJobOrder.Order().ToList();
                config.Save(this);
            }

            ImGui.NewLine();

            var longestJobName = config.AddonSupportJobOrder
                                       .Select(LuminaWrapper.GetMKDSupportJobName)
                                       .MaxBy(x => x.Length);

            for (var i = 0; i < config.AddonSupportJobOrder.Count; i++)
            {
                var supportJob = config.AddonSupportJobOrder[i];
                var name       = LuminaWrapper.GetMKDSupportJobName(supportJob);

                ImGui.Button(name, new(ImGui.CalcTextSize(longestJobName).X * 2, ImGui.GetTextLineHeightWithSpacing()));

                using (var source = ImRaii.DragDropSource())
                {
                    if (source)
                    {
                        if (ImGui.SetDragDropPayload("JobReorder", []))
                            dragDropJobIndex = i;
                        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), name);
                    }
                }

                using (var target = ImRaii.DragDropTarget())
                {
                    if (target)
                    {
                        ImGui.AcceptDragDropPayload("JobReorder");

                        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && dragDropJobIndex >= 0)
                        {
                            (config.AddonSupportJobOrder[dragDropJobIndex], config.AddonSupportJobOrder[i]) =
                                (config.AddonSupportJobOrder[i], config.AddonSupportJobOrder[dragDropJobIndex]);

                            dragDropJobIndex = -1;
                        }
                    }
                }
            }
        }
    }

    private void AgentMKDSupportJobShowDetour
    (
        AgentInterface* agent
    )
    {
        if (agent->IsAgentActive())
            agent->Hide();

        mkdJobListAddon.Toggle();
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostDraw:
                if (MKDInfo == null) return;

                if (jobChangeButton == null)
                {
                    jobChangeButton = new()
                    {
                        Position    = new(18, 4),
                        Size        = new(200, 32f),
                        IsVisible   = true,
                        TextTooltip = LuminaWrapper.GetAddonText(16647),
                        OnClick     = () => mkdJobListAddon.Toggle()
                    };
                    jobChangeButton.BackgroundNode.IsVisible = false;

                    jobChangeButton.AttachNode(MKDInfo->GetNodeById(29));
                }

                break;
            case AddonEvent.PreFinalize:
                jobChangeButton = null;
                break;
        }
    }

    private void OnCommand
    (
        string command,
        string arguments
    ) =>
        mkdJobListAddon?.Toggle();

    private class Config : ModuleConfig
    {
        // 辅助职业技能是否为真
        public bool AddonIsDragRealAction = true;

        // 辅助职业排序
        public List<uint> AddonSupportJobOrder = [];
    }

    public class AddonDRMKDJobList
    (
        BetterMKDSupportJobList module
    ) : NativeAddon
    {
        private const int   MAX_ITEMS_PER_ROW  = 5;
        private const float WINDOW_WIDTH       = 500f;
        private const float ACTION_PANEL_WIDTH = 200f;
        private const float EXPANDED_WIDTH     = WINDOW_WIDTH + ACTION_PANEL_WIDTH + 50f;
        private const float HEADER_HEIGHT      = 70f;
        private const float ROW_HEIGHT         = 72f;
        private const float ROW_SPACING        = 21f;
        private const float MIN_HEIGHT         = 450f;
        private const float BOTTOM_PADDING     = 30f;
        private const float LERP_SPEED         = 0.2f;

        private readonly Dictionary<uint, TextureButtonNode> supportJobButtons = [];

        private SimpleNineGridNode backgroundNode;
        private SimpleNineGridNode borderNode;

        private TextureButtonNode  closeButtonNode;
        private SimpleNineGridNode headerBackgroundNode;
        private SimpleNineGridNode headerBorderNode;

        private bool isFocused;

        private readonly Dictionary<uint, SupportJobActionListNode> jobActionNodes = [];

        private VerticalListNode jobContainer;

        private SimpleNineGridNode moonPatternNode;
        private SimpleNineGridNode patternLeftCornerNode;
        private SimpleNineGridNode patternLeftNode;
        private SimpleNineGridNode patternRightNode;

        public bool PressedButtonOnce { get; set; }

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var supportJobs = new List<(MKDSupportJob Data, CrescentSupportJob Job)>();

            foreach (var data in CrescentSupportJob.AllJobs)
                supportJobs.Add((data.GetData(), data));

            supportJobs = supportJobs
                          .OrderBy
                          (x =>
                              {
                                  var index = module.config.AddonSupportJobOrder.IndexOf(x.Data.RowId);
                                  return index < 0 ?
                                             int.MaxValue :
                                             index;
                              }
                          )
                          .ThenBy(x => x.Data.RowId)
                          .ToList();

            var rowCount = Math.Max(1, (supportJobs.Count + MAX_ITEMS_PER_ROW - 1) / MAX_ITEMS_PER_ROW);
            var contentHeight = HEADER_HEIGHT                             +
                                (ROW_HEIGHT  * rowCount)                  +
                                (ROW_SPACING * Math.Max(0, rowCount - 1)) +
                                BOTTOM_PADDING;
            var windowHeight = Math.Max(MIN_HEIGHT, contentHeight);

            PressedButtonOnce = false;
            SetWindowSize(WINDOW_WIDTH, windowHeight);
            RootNode.Size = Size + new Vector2(ACTION_PANEL_WIDTH, 0);

            var windowNode = (WindowNode)WindowNode;

            windowNode.CloseButtonNode.IsVisible       = false;
            windowNode.BackgroundTextureNode.IsVisible = false;
            windowNode.BorderTextureNode.Alpha         = 0f;
            windowNode.TitleNode.IsVisible             = false;

            CreateWindowStyle();

            CreateJobContainer(supportJobs, rowCount);

            CreateWindowControll();
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (MKDInfo == null || DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();
                return;
            }

            var windowNode = (WindowNode)WindowNode;

            isFocused                               = windowNode.BorderTextureNode.IsVisible;
            windowNode.BackgroundImageNode.Position = new(0);
            windowNode.BackgroundImageNode.Size     = new(windowNode.Width - 2f, windowNode.Height - 12f);

            if (!Throttler.Shared.Throttle("OccultCrescentHelper-OthersManager-UpdateAddon", 10)) return;

            foreach (var node in jobActionNodes.Values)
            {
                if (!node.IsVisible) continue;

                if (node.BorderNode != null)
                {
                    Vector3 targetColor = isFocused ?
                                              new(0.19607843f) :
                                              new(-0.19607843f);
                    node.BorderNode.AddColor = Vector3.Lerp(node.BorderNode.AddColor, targetColor, LERP_SPEED);
                }

                if (node.BackgroundNode != null)
                {
                    var targetAlpha = isFocused ?
                                          0.9f :
                                          0.7f;
                    node.BackgroundNode.Alpha = float.Lerp(node.BackgroundNode.Alpha / 255f, targetAlpha, LERP_SPEED);
                }
            }

            if (borderNode != null)
            {
                Vector3 targetColor = isFocused ?
                                          new(0.19607843f) :
                                          new(-0.19607843f);
                borderNode.AddColor = Vector3.Lerp(borderNode.AddColor, targetColor, LERP_SPEED);
            }

            if (backgroundNode != null)
            {
                var targetAlpha = isFocused ?
                                      0.9f :
                                      0.7f;
                backgroundNode.Alpha = float.Lerp(backgroundNode.Alpha / 255f, targetAlpha, LERP_SPEED);
            }

            if (moonPatternNode != null)
            {
                var targetAlpha = isFocused ?
                                      0.9f :
                                      0.7f;
                moonPatternNode.Alpha = float.Lerp(moonPatternNode.Alpha / 255f, targetAlpha, LERP_SPEED);
            }

            if (patternLeftNode != null)
            {
                var targetAlpha = isFocused ?
                                      0.3f :
                                      0.2f;
                patternLeftNode.Alpha = float.Lerp(patternLeftNode.Alpha / 255f, targetAlpha, LERP_SPEED);
            }

            if (patternLeftCornerNode != null)
            {
                var targetAlpha = isFocused ?
                                      0.3f :
                                      0.2f;
                patternLeftCornerNode.Alpha = float.Lerp(patternLeftCornerNode.Alpha / 255f, targetAlpha, LERP_SPEED);
            }

            if (patternRightNode != null)
            {
                var targetAlpha = isFocused ?
                                      0.3f :
                                      0.2f;
                patternRightNode.Alpha = float.Lerp(patternRightNode.Alpha / 255f, targetAlpha, LERP_SPEED);
            }
        }

        private void CreateJobContainer
        (
            List<(MKDSupportJob Data, CrescentSupportJob Job)> supportJobs,
            int                                                rowCount
        )
        {
            jobContainer = new VerticalListNode
            {
                Position         = new(0, 0),
                Size             = new(WINDOW_WIDTH, Size.Y - BOTTOM_PADDING),
                IsVisible        = true,
                FirstItemSpacing = HEADER_HEIGHT,
                ItemSpacing      = ROW_SPACING
            };

            var rows = new List<HorizontalFlexNode>();

            for (var i = 0; i < rowCount; i++)
            {
                var row = new HorizontalFlexNode
                {
                    Position       = new(0, 0),
                    Size           = new(WINDOW_WIDTH, ROW_HEIGHT),
                    IsVisible      = true,
                    AlignmentFlags = FlexFlags.CenterHorizontally,
                    ItemSpacing    = 10
                };
                rows.Add(row);
            }

            for (var i = 0; i < supportJobs.Count; i++)
            {
                var (data, presetJob) = supportJobs[i];

                var rowIndex = i / MAX_ITEMS_PER_ROW;

                // 预览用的
                var jobActionContainer = new SupportJobActionListNode(this)
                {
                    Position = new(WINDOW_WIDTH, 0),
                    Size     = new(ACTION_PANEL_WIDTH, backgroundNode.Height)
                };
                jobActionContainer.AttachNode(this);
                jobActionNodes[data.RowId] = jobActionContainer;

                var unlockLink = string.Empty;

                if (presetJob.UnlockType != CrescentSupportJobUnlockType.None)
                {
                    unlockLink = $"{Lang.Get("BetterMKDSupportJobList-SupportJobUnlockLink")}:\n" +
                                 $"{presetJob.UnlockLinkName}\n"                                  +
                                 $"[{presetJob.UnlockTypeName}]";
                }

                var iconButton = new TextureButtonNode
                {
                    Size               = new(53f),
                    IsVisible          = true,
                    IsEnabled          = true,
                    TextureCoordinates = new((int)(data.RowId % 5) * 28, (int)(data.RowId / 5) * 28),
                    TexturePath        = "ui/uld/MKDSupportJobIcon_hr1.tex",
                    TextureSize        = new(28, 28),
                    OnClick = () =>
                    {
                        if (DService.Instance().Condition[ConditionFlag.InCombat] || presetJob.IsThisJob() || presetJob.CurrentLevel == 0) return;
                        presetJob.ChangeTo();
                        Close();
                    },
                    TextTooltip = !string.IsNullOrEmpty(unlockLink) && presetJob.CurrentLevel == 0 ?
                                      unlockLink :
                                      LuminaWrapper.GetMKDSupportJobDescription(presetJob.DataID)
                };
                supportJobButtons[data.RowId] = iconButton;

                if (presetJob.IsThisJob())
                    iconButton.AddColor = new(0.5882353f);
                else
                {
                    iconButton.AddTimeline
                    (
                        new TimelineBuilder()
                            .BeginFrameSet(1, 59)
                            .AddLabelPair(1,  9,  1)
                            .AddLabelPair(10, 19, 2)
                            .AddLabelPair(20, 29, 3)
                            .AddLabelPair(30, 39, 7)
                            .AddLabelPair(40, 49, 6)
                            .AddLabelPair(50, 59, 4)
                            .EndFrameSet()
                            .Build()
                    );

                    iconButton.ImageNode.AddTimeline
                    (
                        new TimelineBuilder()
                            .AddFrameSetWithFrame
                            (
                                1,
                                9,
                                1,
                                Vector2.Zero,
                                255,
                                multiplyColor: new(100),
                                scale: new(1f)
                            )
                            .BeginFrameSet(10, 19)
                            .AddFrame
                            (
                                10,
                                Vector2.Zero,
                                255,
                                multiplyColor: new(100),
                                scale: new(1f)
                            )
                            .AddFrame
                            (
                                12,
                                new(-1),
                                255,
                                multiplyColor: new(100),
                                addColor: new(50),
                                scale: new(1.05f)
                            )
                            .EndFrameSet()
                            .AddFrameSetWithFrame
                            (
                                20,
                                29,
                                20,
                                new(-1),
                                255,
                                multiplyColor: new(100),
                                addColor: new(50),
                                scale: new(1.05f)
                            )
                            .AddFrameSetWithFrame
                            (
                                30,
                                39,
                                30,
                                Vector2.Zero,
                                178,
                                multiplyColor: new(50),
                                scale: new(1f)
                            )
                            .AddFrameSetWithFrame
                            (
                                40,
                                49,
                                40,
                                new(-1),
                                255,
                                multiplyColor: new(100),
                                addColor: new(50),
                                scale: new(1.05f)
                            )
                            .BeginFrameSet(50, 59)
                            .AddFrame
                            (
                                50,
                                new(-1),
                                255,
                                multiplyColor: new(100),
                                addColor: new(50),
                                scale: new(1.05f)
                            )
                            .AddFrame
                            (
                                52,
                                Vector2.Zero,
                                255,
                                multiplyColor: new(100),
                                scale: new(1f)
                            )
                            .EndFrameSet()
                            .AddFrameSetWithFrame
                            (
                                130,
                                139,
                                130,
                                new(-1),
                                255,
                                new(50),
                                new(100),
                                scale: new(1.05f)
                            )
                            .AddFrameSetWithFrame
                            (
                                140,
                                149,
                                140,
                                Vector2.Zero,
                                255,
                                multiplyColor: new(100),
                                scale: new(1f)
                            )
                            .AddFrameSetWithFrame
                            (
                                150,
                                159,
                                150,
                                Vector2.Zero,
                                255,
                                multiplyColor: new(100),
                                scale: new(1f)
                            )
                            .Build()
                    );
                }

                iconButton.AddEvent
                (
                    AtkEventType.MouseOver,
                    () =>
                    {
                        if (PressedButtonOnce) return;

                        ShowJobActions();
                    }
                );

                iconButton.AddEvent
                (
                    AtkEventType.ButtonPress,
                    () =>
                    {
                        PressedButtonOnce = true;

                        ShowJobActions();
                    }
                );

                if (presetJob.CurrentLevel == 0)
                    iconButton.Alpha = 0.5f;

                iconButton.ImageNode.Size = new(53);

                var jobName = data.Name.ToString()
                                  .Replace("Phantom", string.Empty)
                                  .Replace("辅助",      string.Empty)
                                  .Replace("サポート",    string.Empty)
                                  .Replace("서포트",     string.Empty)
                                  .Trim();
                using var nameBuilder = new RentedSeStringBuilder();
                var jobNameNode = new TextNode
                {
                    String = nameBuilder.Builder.PushEdgeColorType(32)
                                        .Append(jobName)
                                        .PopEdgeColorType()
                                        .GetViewAsSpan(),
                    FontSize      = 12,
                    IsVisible     = true,
                    Size          = new(73, 24),
                    Position      = new(-10, 48),
                    TextColor     = ColorHelper.GetColor(50),
                    AlignmentType = AlignmentType.Center,
                    TextFlags     = TextFlags.Glare
                };
                jobNameNode.AutoAdjustTextSize();
                jobNameNode.AttachNode(iconButton);

                var imageFullLevelNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(64, 62),
                    TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                    TextureSize        = new(32, 20),
                    IsVisible          = presetJob.CurrentLevel == presetJob.MaxLevel,
                    Size               = new(32, 20),
                    Position           = new(10.5f, -15f),
                    AddColor = presetJob.IsThisJob() ?
                                   new(-0.39215687f) :
                                   new()
                };
                imageFullLevelNode.AttachNode(iconButton);

                var maxLevelText = presetJob.MaxLevel == 0 ?
                                       "∞" :
                                       $"{presetJob.MaxLevel}";
                var currentLevelNode = new TextNode
                {
                    String = new SeStringBuilder()
                             .AddUiGlow(34)
                             .Append($"{presetJob.CurrentLevel} / {maxLevelText}")
                             .AddUiGlowOff()
                             .Build()
                             .Encode(),
                    FontSize      = 14,
                    IsVisible     = presetJob.CurrentLevel > 0 && presetJob.CurrentLevel != presetJob.MaxLevel,
                    Size          = new(53f, 24),
                    Position      = new(0, -19),
                    TextColor     = ColorHelper.GetColor(50),
                    AlignmentType = AlignmentType.Center,
                    FontType      = FontType.JupiterLarge
                };
                currentLevelNode.AttachNode(iconButton);

                rows[rowIndex].AddNode(iconButton);
                continue;

                void ShowJobActions()
                {
                    foreach (var (jobID, node) in jobActionNodes)
                    {
                        node.IsVisible = jobID == data.RowId;
                        if (node is { IsVisible: true, BackgroundNode: null })
                            node.LoadNodes(module, presetJob, isFocused);
                    }

                    WindowNode.CollisionNode.Size = WindowNode.CollisionNode.Size with { X = EXPANDED_WIDTH };
                    WindowNode.Size               = WindowNode.Size with { X = EXPANDED_WIDTH };
                }
            }

            jobContainer.AddNode(rows);

            jobContainer.AttachNode(this);
        }

        private void CreateWindowStyle()
        {
            backgroundNode = new SimpleNineGridNode
            {
                TextureCoordinates = new(0),
                TextureSize        = new(500, 490),
                TexturePath        = "ui/uld/MKDWallPaper_hr1.tex",
                IsVisible          = true,
                Size               = new(WINDOW_WIDTH + 2f, Size.Y - 7f),
                Position           = new(-2),
                Alpha              = 0.9f
            };
            backgroundNode.AttachNode(this);

            headerBackgroundNode = new()
            {
                TextureCoordinates = new(110, 0),
                TextureSize        = new(9, 41),
                TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                IsVisible          = true,
                Size               = new(WINDOW_WIDTH + 5f, 40),
                Position           = new(-2),
                Alpha              = 1f
            };
            headerBackgroundNode.AttachNode(this);

            headerBorderNode = new()
            {
                TextureCoordinates = new(63, 55),
                TextureSize        = new(47, 6),
                TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                IsVisible          = true,
                Size               = new(WINDOW_WIDTH + 20f, 6),
                Position           = new(0, 36),
                Alpha              = 1f,
                AddColor           = new(-0.196078f)
            };
            headerBorderNode.AttachNode(this);

            moonPatternNode = new SimpleNineGridNode
            {
                TextureCoordinates = new(0),
                TextureSize        = new(190),
                TexturePath        = "ui/uld/MKDWallMoon_hr1.tex",
                IsVisible          = true,
                Size               = new(190),
                Position           = new(WINDOW_WIDTH - 190f, Size.Y - 205f),
                Alpha              = 0.9f
            };
            moonPatternNode.AttachNode(this);

            patternLeftNode = new()
            {
                TextureCoordinates = new(349, 140),
                TextureSize        = new(98, 132),
                TexturePath        = "ui/uld/MKDWindowPattern_hr1.tex",
                IsVisible          = true,
                Size               = new(128, 132),
                Position           = new(0, 40),
                Alpha              = 0.3f
            };
            patternLeftNode.AttachNode(this);

            patternLeftCornerNode = new()
            {
                TextureCoordinates = new(0, 174),
                TextureSize        = new(140, 146),
                TexturePath        = "ui/uld/MKDWindowPattern_hr1.tex",
                IsVisible          = true,
                Size               = new(128, 132),
                Position           = new(0, Size.Y - 190f),
                Alpha              = 0.3f
            };
            patternLeftCornerNode.AttachNode(this);

            patternRightNode = new SimpleNineGridNode
            {
                TextureCoordinates = new(0, 45),
                TextureSize        = new(176, 125),
                TexturePath        = "ui/uld/MKDWindowPattern_hr1.tex",
                IsVisible          = true,
                Size               = new(236, 125),
                Position           = new(WINDOW_WIDTH - 240f, 5),
                Alpha              = 0.3f
            };
            patternRightNode.AttachNode(this);

            var anotherWindowTitleNode = new TextNode
            {
                LineSpacing      = 23,
                AlignmentType    = AlignmentType.Left,
                FontSize         = 23,
                FontType         = FontType.TrumpGothic,
                NodeFlags        = NodeFlags.AnchorTop | NodeFlags.AnchorLeft | NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents,
                TextColor        = ColorHelper.GetColor(50),
                TextOutlineColor = ColorHelper.GetColor(7),
                Size             = new(86f, 31f),
                Position         = new(12f, 7f),
                IsVisible        = true,
                String           = Title
            };
            anotherWindowTitleNode.AttachNode(this);

            borderNode = new SimpleNineGridNode
            {
                TextureCoordinates = new(1, 0),
                TextureSize        = new(60, 70),
                TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                IsVisible          = true,
                Size               = new(WINDOW_WIDTH + 15f, Size.Y + 7f),
                Position           = new(-8, -5),
                Alpha              = 0.9f,
                Offsets            = new(24),
                AddColor           = new(0.19607843f)
            };
            borderNode.AttachNode(this);
        }

        private void CreateWindowControll()
        {
            closeButtonNode = new TextureButtonNode
            {
                Size               = new(28f),
                Position           = new(WINDOW_WIDTH - 42f, 8f),
                IsVisible          = true,
                TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                TextureCoordinates = new(0),
                TextureSize        = new(28f),
                OnClick            = Close
            };

            closeButtonNode.ImageNode.AddColor = new(-0.19607843f);

            closeButtonNode.AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(1, 20)
                    .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                    .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                    .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                    .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                    .EndFrameSet()
                    .Build()
            );

            closeButtonNode.ImageNode.AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(1, 10)
                    .AddFrame(1, addColor: new(0))
                    .AddFrame(4, addColor: new(-50))
                    .EndFrameSet()
                    .BeginFrameSet(11, 20)
                    .AddFrame(11, addColor: new(0))
                    .AddFrame(14, addColor: new(50))
                    .EndFrameSet()
                    .Build()
            );
            closeButtonNode.AttachNode(this);
        }

        private class SupportJobActionListNode
        (
            AddonDRMKDJobList addon
        ) : SimpleComponentNode
        {
            public SimpleNineGridNode      BackgroundNode       { get; private set; }
            public SimpleNineGridNode      BorderNode           { get; private set; }
            public SimpleNineGridNode      HeaderBackgroundNode { get; private set; }
            public SimpleNineGridNode      HeaderBorderNode     { get; private set; }
            public VerticalListNode        ActionListNode       { get; private set; }
            public TextureButtonNode       CloseButtonNode      { get; private set; }
            public TextureButtonNode       SettingButtonNode    { get; private set; }
            public CheckboxNode            IsRealActionNode     { get; private set; }
            public List<SupportActionNode> ActionDragDropNodes  { get; private set; } = [];

            public void LoadNodes
            (
                BetterMKDSupportJobList module,
                CrescentSupportJob      presetJob,
                bool                    isCurrentFoucused
            )
            {
                BackgroundNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(0),
                    TextureSize        = new(500, 380),
                    TexturePath        = "ui/uld/MKDWallPaper_hr1.tex",
                    IsVisible          = true,
                    Size               = Size + new Vector2(50, 0),
                    Position           = new(-2),
                    Alpha = isCurrentFoucused ?
                                0.9f :
                                0.6f
                };
                BackgroundNode.AttachNode(this);

                HeaderBackgroundNode = new()
                {
                    TextureCoordinates = new(110, 0),
                    TextureSize        = new(9, 41),
                    TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                    IsVisible          = true,
                    Size               = Size with { Y = 40 } + new Vector2(54, 0),
                    Position           = new(-2),
                    Alpha              = 1f
                };
                HeaderBackgroundNode.AttachNode(this);

                HeaderBorderNode = new()
                {
                    TextureCoordinates = new(63, 55),
                    TextureSize        = new(47, 6),
                    TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                    IsVisible          = true,
                    Size               = Size with { Y = 6 } + new Vector2(58, 0),
                    Position           = new(0, 36),
                    Alpha              = 1f,
                    AddColor           = new(-0.196078f)
                };
                HeaderBorderNode.AttachNode(this);

                BorderNode = new SimpleNineGridNode
                {
                    TextureCoordinates = new(1, 0),
                    TextureSize        = new(60, 70),
                    TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                    IsVisible          = true,
                    Size               = Size + new Vector2(64, 14),
                    Position           = new(-8.5f, -5),
                    Alpha              = 0.9f,
                    Offsets            = new(24),
                    AddColor = isCurrentFoucused ?
                                   new(0.19607843f) :
                                   new(-0.19607843f)
                };
                BorderNode.AttachNode(this);

                ActionListNode = new VerticalListNode
                {
                    Size      = Size + new Vector2(50, 0),
                    IsVisible = true,
                    Position  = new(10, 30)
                };
                ActionListNode.AttachNode(this);

                ActionListNode.AddDummy(25f);

                foreach (var (jobAction, jobLevel) in presetJob.Actions)
                {
                    if (!LuminaGetter.TryGetRow<Action>(jobAction, out var action)) continue;

                    var row = new HorizontalListNode
                    {
                        IsVisible = true,
                        Size      = new(40f)
                    };

                    var dragDropNode = new SupportActionNode
                    (
                        addon,
                        presetJob,
                        this,
                        action.RowId,
                        ActionDragDropNodes.Count,
                        module.config.AddonIsDragRealAction
                    )
                    {
                        Size         = new(40f),
                        IsVisible    = true,
                        IconId       = action.Icon,
                        AcceptedType = DragDropType.Nothing,
                        IsDraggable  = true,
                        IsClickable  = true
                    };
                    ActionDragDropNodes.Add(dragDropNode);

                    row.AddNode(dragDropNode);
                    row.AddDummy(10);

                    var actionTextNode = new TextNode
                    {
                        String        = $"\ue06a {jobLevel.ToSESmallCount()}: {action.Name.ToString()}",
                        FontSize      = 14,
                        IsVisible     = true,
                        Size          = new(Size.X - 20f, 40f),
                        AlignmentType = AlignmentType.Left,
                        TextColor     = ColorHelper.GetColor(50),
                        TextOutlineColor = ColorHelper.GetColor
                        (
                            (uint)(presetJob.CurrentLevel >= jobLevel ?
                                       32 :
                                       4)
                        ),
                        TextFlags = TextFlags.Glare
                    };
                    row.AddNode(actionTextNode);

                    while (actionTextNode.FontSize > 1 && actionTextNode.GetTextDrawSize(actionTextNode.String).X > actionTextNode.Size.X)
                        actionTextNode.FontSize--;

                    ActionListNode.AddNode(row);
                    ActionListNode.AddDummy(10f);
                }

                ActionListNode.AddDummy(20f);

                foreach (var (trait, jobLevel) in presetJob.Traits)
                {
                    if (!LuminaGetter.TryGetRow<MKDTrait>(trait, out var traitRow) ||
                        traitRow.Name.IsEmpty)
                        continue;

                    var row = new HorizontalListNode
                    {
                        IsVisible = true,
                        Size      = new(44f)
                    };

                    var dragDropNode = new DragDropNode
                    {
                        Size         = new(44f),
                        IsVisible    = true,
                        IconId       = (uint)traitRow.Icon,
                        AcceptedType = DragDropType.Nothing,
                        IsDraggable  = false,
                        Payload = new()
                        {
                            Int2 = (int)trait
                        },
                        IsClickable = false,
                        OnRollOver = node =>
                        {
                            var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();

                            tooltipArgs.ActionArgs.Flags = 1;
                            tooltipArgs.ActionArgs.Kind  = DetailKind.MKDTrait;
                            tooltipArgs.ActionArgs.Id    = (int)trait;

                            AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Action, (ushort)addon.AddonId, node, &tooltipArgs);
                        },
                        OnRollOut = node => node.HideTooltip()
                    };

                    row.AddNode(dragDropNode);
                    row.AddDummy(10);

                    var traitTextNode = new TextNode
                    {
                        String        = $"\ue06a {jobLevel.ToSESmallCount()}: {traitRow.Name.ToString()}",
                        FontSize      = 14,
                        IsVisible     = true,
                        Size          = new(Size.X - 20f, 44f),
                        AlignmentType = AlignmentType.Left,
                        TextColor     = ColorHelper.GetColor(50),
                        TextOutlineColor = ColorHelper.GetColor
                        (
                            (uint)(presetJob.CurrentLevel >= jobLevel ?
                                       32 :
                                       4)
                        ),
                        TextFlags = TextFlags.Glare
                    };
                    traitTextNode.AutoAdjustTextSize();
                    row.AddNode(traitTextNode);

                    ActionListNode.AddNode(row);
                    ActionListNode.AddDummy(10f);
                }

                CloseButtonNode = new TextureButtonNode
                {
                    Size               = new(28),
                    Position           = new(210, 8),
                    IsVisible          = true,
                    TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                    TextureCoordinates = new(0),
                    TextureSize        = new(28),
                    OnClick = () =>
                    {
                        IsVisible                                      = false;
                        module.mkdJobListAddon.PressedButtonOnce = false;

                        module.mkdJobListAddon.WindowNode.CollisionNode.Size =
                            module.mkdJobListAddon.WindowNode.CollisionNode.Size with { X = 500 };
                        module.mkdJobListAddon.WindowNode.Size =
                            module.mkdJobListAddon.WindowNode.Size with { X = 500 };
                    }
                };

                CloseButtonNode.ImageNode.AddColor = new(-0.19607843f);

                CloseButtonNode.AddTimeline
                (
                    new TimelineBuilder()
                        .BeginFrameSet(1, 20)
                        .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                        .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                        .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                        .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                        .EndFrameSet()
                        .Build()
                );

                CloseButtonNode.ImageNode.AddTimeline
                (
                    new TimelineBuilder()
                        .BeginFrameSet(1, 10)
                        .AddFrame(1, addColor: new(0))
                        .AddFrame(4, addColor: new(-50))
                        .EndFrameSet()
                        .BeginFrameSet(11, 20)
                        .AddFrame(11, addColor: new(0))
                        .AddFrame(14, addColor: new(50))
                        .EndFrameSet()
                        .Build()
                );
                CloseButtonNode.AttachNode(this);

                SettingButtonNode = new TextureButtonNode
                {
                    Size               = new(16),
                    Position           = new(192, 14),
                    IsVisible          = true,
                    TexturePath        = "ui/uld/WindowA_Button_hr1.tex",
                    TextureCoordinates = new(44, 0),
                    TextureSize        = new(16),
                    OnClick            = () => AgentModule.Instance()->GetAgentByInternalId(AgentId.MKDSettings)->Show()
                };

                SettingButtonNode.ImageNode.AddColor = new(1);

                SettingButtonNode.AddTimeline
                (
                    new TimelineBuilder()
                        .BeginFrameSet(1, 20)
                        .AddLabel(1,  1, AtkTimelineJumpBehavior.Start,    0)
                        .AddLabel(10, 0, AtkTimelineJumpBehavior.PlayOnce, 1)
                        .AddLabel(11, 2, AtkTimelineJumpBehavior.Start,    0)
                        .AddLabel(20, 0, AtkTimelineJumpBehavior.PlayOnce, 2)
                        .EndFrameSet()
                        .Build()
                );

                SettingButtonNode.ImageNode.AddTimeline
                (
                    new TimelineBuilder()
                        .BeginFrameSet(1, 10)
                        .AddFrame(1, addColor: new(200))
                        .AddFrame(4, addColor: new(100))
                        .EndFrameSet()
                        .BeginFrameSet(11, 20)
                        .AddFrame(11, addColor: new(100))
                        .AddFrame(14, addColor: new(200))
                        .EndFrameSet()
                        .Build()
                );
                SettingButtonNode.AttachNode(this);

                IsRealActionNode = new()
                {
                    IsVisible = true,
                    Position  = new(10, 8),
                    Size      = new(Width, 28),
                    String    = Lang.Get("BetterMKDSupportJobList-DragRealActionIcon"),
                    TextTooltip = new SeStringBuilder()
                                  .AddIcon(BitmapFontIcon.ExclamationRectangle)
                                  .AddText($" {Lang.Get("BetterMKDSupportJobList-DragRealActionIcon-Help")}")
                                  .Build()
                                  .Encode(),
                    IsChecked = module.config.AddonIsDragRealAction,
                    IsEnabled = true,
                    OnClick = value =>
                    {
                        module.config.AddonIsDragRealAction = value;
                        module.config.Save(module);

                        ActionDragDropNodes.ForEach(x => x.Toggle(value));
                    }
                };

                IsRealActionNode.Label.TextFlags        |= TextFlags.Edge | TextFlags.Emboss;
                IsRealActionNode.Label.TextColor        =  ColorHelper.GetColor(50);
                IsRealActionNode.Label.TextOutlineColor =  ColorHelper.GetColor(502);

                IsRealActionNode.AttachNode(this);
            }

            public class SupportActionNode : DragDropNode
            {
                [Flags]
                public enum ActionSlotHiddenFlag : byte
                {
                    Action0 = 1 << 0,
                    Action1 = 1 << 1,
                    Action2 = 1 << 2,
                    Action3 = 1 << 3,
                    Action4 = 1 << 4
                }

                public SupportActionNode
                (
                    AddonDRMKDJobList addon,
                    CrescentSupportJob         job,
                    SupportJobActionListNode   list,
                    uint                       actionID,
                    int                        actionIndex,
                    bool                       isRealAction = false
                )
                {
                    Job   = job;
                    List  = list;
                    Addon = addon;

                    IsRealAction = isRealAction;
                    ActionIndex  = actionIndex;
                    ActionID     = actionID;

                    DefaultIconNode = new SimpleNineGridNode
                    {
                        TexturePath        = "ui/uld/ContentsReplaySetting_hr1.tex",
                        TextureCoordinates = new(36, 44),
                        TextureSize        = new(36),
                        Size               = new(22),
                        Position           = new(22, 24)
                    };
                    DefaultIconNode.AttachNode(this);

                    HiddenIconNode = new SimpleNineGridNode
                    {
                        TexturePath        = "ui/uld/MKDWindow_hr1.tex",
                        TextureCoordinates = new(64, 82),
                        TextureSize        = new(20),
                        Size               = new(22),
                        Position           = new(22, 24)
                    };
                    HiddenIconNode.AttachNode(this);

                    UpdateActionInfo();

                    Toggle(IsRealAction);
                }

                public CrescentSupportJob         Job   { get; private set; }
                public SupportJobActionListNode   List  { get; private set; }
                public AddonDRMKDJobList Addon { get; private set; }

                public bool IsRealAction { get; private set; }
                public int  ActionIndex  { get; private set; }
                public uint ActionID     { get; private set; }

                public bool IsDefault { get; private set; }
                public bool IsHidden  { get; private set; }

                public SimpleNineGridNode DefaultIconNode { get; private set; }
                public SimpleNineGridNode HiddenIconNode  { get; private set; }

                public static byte                 DefaultAction     { get; private set; }
                public static ActionSlotHiddenFlag ActionHiddenFlags { get; private set; }
                public static HashSet<byte>        HiddenActions     { get; private set; } = [];

                public void Toggle
                (
                    bool isRealAction
                )
                {
                    IsRealAction = isRealAction;

                    Payload = new()
                    {
                        Type = IsRealAction ?
                                   DragDropType.Action :
                                   DragDropType.GeneralAction,
                        Int2 = IsRealAction ?
                                   (int)ActionID :
                                   31 + ActionIndex
                    };

                    OnRollOver = node =>
                    {
                        var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();

                        tooltipArgs.ActionArgs.Flags = 1;
                        tooltipArgs.ActionArgs.Kind = isRealAction ?
                                                          DetailKind.Action :
                                                          DetailKind.GeneralAction;
                        tooltipArgs.ActionArgs.Id = IsRealAction ?
                                                        (int)ActionID :
                                                        31 + ActionIndex;

                        AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Action, (ushort)Addon.AddonId, node, &tooltipArgs);
                    };
                    OnRollOut = node => node.HideTooltip();
                    OnClicked = _ =>
                    {
                        UpdateActionInfo();

                        // 当前是默认, 切换至隐藏
                        if (IsDefault)
                        {
                            // 不能全部技能都隐藏
                            if (HiddenActions.Count == Job.Actions.Count - 1)
                                return;

                            // 找还有哪个其他技能能被设成默认
                            var actions = Job.Actions.Select(x => x.Key).ToList();

                            for (var i = 0; i < actions.Count; i++)
                            {
                                if (i == ActionIndex || HiddenActions.Contains((byte)i)) continue;

                                // 当前技能变成隐藏, 找到的技能变成新默认
                                var newFlags = ActionHiddenFlags | IndexToHiddenFlag(ActionIndex);
                                AgentMKDSupportJob.UpdateJobSettings(Job.DataID, (byte)i, (byte)newFlags);
                                UpdateActionInfo();
                                break;
                            }

                            return;
                        }

                        // 当前是隐藏, 变成非隐藏
                        if (IsHidden)
                        {
                            var newFlags = ActionHiddenFlags & ~IndexToHiddenFlag(ActionIndex);
                            AgentMKDSupportJob.UpdateJobSettings(Job.DataID, DefaultAction, (byte)newFlags);
                            UpdateActionInfo();

                            return;
                        }

                        // 当前啥都不是, 变成默认
                        AgentMKDSupportJob.UpdateJobSettings(Job.DataID, (byte)ActionIndex, (byte)ActionHiddenFlags);
                        UpdateActionInfo();
                    };
                }

                public void UpdateActionInfo
                (
                    bool updateOthers = true
                )
                {
                    var defaultAction     = stackalloc byte[1];
                    var actionHiddenFlags = stackalloc byte[1];

                    AgentMKDSupportJob.GetJobSettings(Job.DataID, defaultAction, actionHiddenFlags);

                    DefaultAction     = *defaultAction;
                    ActionHiddenFlags = (ActionSlotHiddenFlag)(*actionHiddenFlags);

                    HiddenActions.Clear();
                    for (byte i = 0; i < 5; i++)
                        if (ActionHiddenFlags.HasFlag(IndexToHiddenFlag(i)))
                            HiddenActions.Add(i);

                    IsDefault = DefaultAction == (byte)ActionIndex;
                    IsHidden  = HiddenActions.Contains((byte)ActionIndex);

                    DefaultIconNode.IsVisible = IsDefault;
                    HiddenIconNode.IsVisible  = IsHidden;

                    if (updateOthers)
                    {
                        foreach (var node in List.ActionDragDropNodes)
                        {
                            if (node.ActionID == ActionID) continue;
                            node.UpdateActionInfo(false);
                        }
                    }
                }

                public static ActionSlotHiddenFlag IndexToHiddenFlag
                (
                    int index
                ) => index switch
                {
                    0 => ActionSlotHiddenFlag.Action0,
                    1 => ActionSlotHiddenFlag.Action1,
                    2 => ActionSlotHiddenFlag.Action2,
                    3 => ActionSlotHiddenFlag.Action3,
                    4 => ActionSlotHiddenFlag.Action4,
                    _ => ActionSlotHiddenFlag.Action0
                };
            }
        }
    }

    #region 常量

    private const string COMMAND = "mkdjoblist";

    #endregion
}
