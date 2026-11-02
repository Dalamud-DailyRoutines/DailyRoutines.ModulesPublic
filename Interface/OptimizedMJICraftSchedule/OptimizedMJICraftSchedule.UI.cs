using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Styles;

namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

public partial class OptimizedMJICraftSchedule
{
    private UIStyle uiStyle = null!;
    
    private SchedulePanel? panel;
    private TextNode?      previewIndicator;

    private unsafe void OnSchedule(AddonEvent type, AddonArgs args)
    {
        isPreviewing = false;
        previewIndicator?.Dispose();
        previewIndicator = null;
        StopOperation();
        input  = null;
        result = null;
        
        if (type != AddonEvent.PostSetup)
            return;

        previewIndicator               = uiStyle.CreateText(UITextRole.Subtitle);
        previewIndicator.AlignmentType = AlignmentType.Left;
        previewIndicator.TextFlags     = TextFlags.Edge;
        previewIndicator.Position      = new(442, 114);
        previewIndicator.Size          = new(270, 20);
        previewIndicator.IsVisible     = false;
        previewIndicator.String        = Lang.Get("OptimizedMJICraftSchedule-PreviewMode");
        previewIndicator.AttachNode(MJICraftSchedule->RootNode);

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RequestMJIWorkshopDemand);
        MJIManager.Instance()->RequestFavorData();
        panel?.Refresh();
    }
    
    private sealed class SchedulePanel
    (
        OptimizedMJICraftSchedule module
    ) : AttachedAddon("MJICraftSchedule")
    {
        private readonly   List<TextButtonNode>  solveButtons = [];
        private            VerticalListNode      root;
        private            TextButtonNode        previewButton;
        private            TextButtonNode        applyButton;
        private            TextNode              status;
        protected override AttachedAddonPosition AttachPosition => AttachedAddonPosition.RightTop;

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
            applyButton.OnClick = module.Apply;
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
            solveButton.OnClick = () => module.StartCalculation(week, false);
            
            var favorButton = module.uiStyle.CreateButton();
            favorButton.Size    = new(group.Width - 132, 32);
            favorButton.String  = Lang.Get("OptimizedMJICraftSchedule-SolveFavors");
            favorButton.OnClick = () => module.StartCalculation(week, true);
            
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

            var busy = module.operation != Operation.Idle;
            foreach (var button in solveButtons)
                button.IsEnabled = !busy;

            previewButton.IsVisible = module.result != null;
            previewButton.IsEnabled = !busy;
            previewButton.String = Lang.Get
            (
                module.isPreviewing ?
                    "OptimizedMJICraftSchedule-CancelPreview" :
                    "Preview"
            );
            applyButton.IsVisible = module.result != null;
            applyButton.IsEnabled = !busy && module.result is { Complete: true };

            if (!busy &&
                module.result != null)
            {
                status.String = Lang.Get
                (
                    "OptimizedMJICraftSchedule-Summary",
                    new Dictionary<string, object>
                    {
                        ["value"] = module.result.Value
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
