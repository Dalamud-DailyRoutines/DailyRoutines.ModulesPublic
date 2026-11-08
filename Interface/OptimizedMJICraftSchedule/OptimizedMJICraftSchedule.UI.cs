using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Styles;
using ScheduleAgent = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;
using NativeScheduleAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;

namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

internal partial class OptimizedMJICraftSchedule
{
    private UIStyle uiStyle = null!;

    private SchedulePanel? panel;
    private TextNode?      previewIndicator;

    private bool isPreviewing;

    private unsafe void OnSchedule(AddonEvent type, AddonArgs args)
    {
        isPreviewing = false;

        previewIndicator?.Dispose();
        previewIndicator = null;

        calculator.Reset();

        if (type != AddonEvent.PostSetup)
            return;

        previewIndicator               = uiStyle.CreateText(UITextRole.Subtitle);
        previewIndicator.AlignmentType = AlignmentType.Left;
        previewIndicator.TextFlags     = TextFlags.Edge;
        previewIndicator.Position      = new(442, 114);
        previewIndicator.Size          = new(270, 20);
        previewIndicator.IsVisible     = false;
        previewIndicator.String        = $"（{Lang.Get("OptimizedMJICraftSchedule-PreviewMode")}）";
        previewIndicator.AttachNode(MJICraftSchedule->RootNode);

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestMJIWorkshopDemand);
        MJIManager.Instance()->RequestFavorData();
        panel?.Refresh();
    }

    private unsafe void TogglePreview()
    {
        if (calculator.IsBusy ||
            calculator.Result == null)
            return;

        if (isPreviewing)
        {
            CancelPreview();
            panel.Refresh();
            return;
        }

        var agent = ScheduleAgent.Instance();
        if (agent       == null ||
            agent->Data == null)
            return;

        if (agent->Data->CycleInProgress != calculator.Input.CurrentCycle ||
            agent->Data->RestCycles      != calculator.Input.RestCycles   ||
            calculator.HasTimeChanged)
        {
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return;
        }

        isPreviewing = true;
        if (calculator.Result.Days.ContainsKey(agent->Data->CycleDisplayed))
            agent->UpdateMainAddon();
        else
            agent->SetDisplayedCycle(calculator.Result.Days.Keys.Min());
        panel.Refresh();
    }

    private unsafe void CancelPreview()
    {
        if (!isPreviewing)
            return;

        isPreviewing                = false;
        previewIndicator?.IsVisible = false;
        var agent = ScheduleAgent.Instance();
        if (agent       != null                      &&
            agent->Data != null                      &&
            MJICraftSchedule->IsAddonAndNodesReady() &&
            (agent->Data->Flags2 & NativeScheduleAgent.DataFlags2.ScheduleDataAvailable) != 0)
            agent->UpdateMainAddon();
    }

    private sealed class SchedulePanel
    (
        OptimizedMJICraftSchedule module
    ) : AttachedAddon("MJICraftSchedule")
    {
        protected override AttachedAddonPosition AttachPosition => AttachedAddonPosition.RightTop;
        protected override Vector2               PositionOffset => new(12, 0);

        private readonly List<TextButtonNode> solveButtons = [];
        private          VerticalListNode?    root;
        private          TextButtonNode?      previewButton;
        private          TextButtonNode?      applyButton;
        private          TextNode?            status;

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
        {
            root = new()
            {
                Position    = ContentStartPosition,
                Width       = ContentSize.X,
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 10
            };
            root.AttachNode(this);
            root.AddNode(CreateCycleControls(0));
            root.AddNode(CreateCycleControls(1));
            var separator = module.uiStyle.CreateSeparator();
            separator.Width = root.Width;
            root.AddNode(separator);

            status      = module.uiStyle.CreateText();
            status.Size = new(root.Width, 24);
            status.OnStringUpdated = () =>
            {
                status.Height = Math.Max(24, status.GetTextDrawSize().Y + 4);
                root.RecalculateLayout();
                SetWindowSize(Size.X, root.Height + Size.Y - ContentSize.Y);
            };
            root.AddNode(status);

            previewButton         = module.uiStyle.CreateButton();
            previewButton.Size    = new(root.Width, 32);
            previewButton.OnClick = module.TogglePreview;
            root.AddNode(previewButton);

            applyButton         = module.uiStyle.CreateButton();
            applyButton.Size    = new(root.Width, 32);
            applyButton.String  = Lang.Get("OptimizedMJICraftSchedule-Apply");
            applyButton.OnClick = module.calculator.Apply;
            root.AddNode(applyButton);

            Refresh();
        }

        protected override unsafe void OnSetupComplete(AtkUnitBase* addon)
        {
            base.OnSetupComplete(addon);
            Refresh();
        }

        private VerticalListNode CreateCycleControls(int week)
        {
            var group = new VerticalListNode
            {
                Width       = root.Width,
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 4
            };

            var heading = module.uiStyle.CreateText(UITextRole.Heading);
            heading.FontSize      = 16;
            heading.Size          = new(group.Width, 24);
            heading.AlignmentType = AlignmentType.Left;
            heading.String = Lang.Get
            (
                week == 0 ?
                    "OptimizedMJICraftSchedule-ThisWeek" :
                    "OptimizedMJICraftSchedule-NextWeek"
            );
            group.AddNode(heading);

            var row = new HorizontalListNode
            {
                Size             = new(group.Width, 32),
                FirstItemSpacing = 20,
                ItemSpacing      = 8
            };

            var solveButton = module.uiStyle.CreateButton();
            solveButton.Size    = new(104, 32);
            solveButton.String  = Lang.Get("OptimizedMJICraftSchedule-Solve");
            solveButton.OnClick = () => module.calculator.Start(week, false);

            var favorButton = module.uiStyle.CreateButton();
            favorButton.Size    = new(group.Width - 132, 32);
            favorButton.String  = Lang.Get("OptimizedMJICraftSchedule-SolveFavors");
            favorButton.OnClick = () => module.calculator.Start(week, true);

            solveButtons.Add(solveButton, favorButton);
            row.AddNode([solveButton, favorButton]);

            group.AddNode(row);
            return group;
        }

        public void SetStatus(string key)
        {
            Refresh();
            status?.String = Lang.Get(key);
        }

        public void Refresh()
        {
            if (status == null)
                return;

            var busy = module.calculator.IsBusy;
            foreach (var button in solveButtons)
                button.IsEnabled = !busy;

            previewButton.IsVisible = module.calculator.Result != null;
            previewButton.IsEnabled = !busy;
            previewButton.String = Lang.Get
            (
                module.isPreviewing ?
                    "OptimizedMJICraftSchedule-CancelPreview" :
                    "Preview"
            );

            applyButton.IsVisible = module.calculator.Result != null;
            applyButton.IsEnabled = !busy && module.calculator.Result is { Complete: true };

            if (!busy &&
                module.calculator.Result != null)
            {
                status.String = Lang.Get
                (
                    "OptimizedMJICraftSchedule-Summary",
                    new Dictionary<string, object>
                    {
                        ["value"] = module.calculator.Result.Value
                    }
                );
                return;
            }

            status.String = Lang.Get
            (
                busy ?
                    "OptimizedMJICraftSchedule-Busy" :
                    "OptimizedMJICraftSchedule-Hint"
            );
        }

        protected override unsafe void OnFinalize(AtkUnitBase* addon)
        {
            base.OnFinalize(addon);

            solveButtons.Clear();
            root          = null;
            previewButton = null;
            applyButton   = null;
            status        = null;
        }
    }
}
