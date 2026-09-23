using System.Numerics;
using System.Text;
using DailyRoutines.Common.Info;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;
using Lumina.Data.Parsing.Uld;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.CraftGather;

public partial class OptimizedRecipeNote
{
    private class AddonActionsPreview
    (
        OptimizedRecipeNote inModule,
        TaskHelper       inTaskHelper,
        CaculationResult result
    ) : NativeAddon
    {
        public static AddonActionsPreview? Addon { get; set; }

        public CaculationResult    Result     { get; init; } = result;
        public TaskHelper          TaskHelper { get; init; } = inTaskHelper;
        public OptimizedRecipeNote Module     { get; init; } = inModule;

        public VerticalListNode RootContainer { get; private set; }

        #region 基础数据

        public const float STATS_CONTAINER_HEIGHT        = 70f;
        public const float STATS_CONTAINER_INNER_PADDING = 15f;
        public const float STATS_CLASS_JOB_COLUMN_WIDTH  = 180f;
        public const float STATS_COLUMN_WIDTH            = 120F;
        public const float STATS_COLUMN_DUMMY            = 18.5f;
        
        public SimpleNineGridNode StatsContainerBackground   { get; private set; }
        public HorizontalListNode StatsContainer             { get; private set; }
        public IconImageNode      ClassJobIcon               { get; private set; }
        public VerticalListNode   ClassJobInfoContainer      { get; private set; }
        public TextNode           ClassJobLable              { get; private set; }
        public TextNode           ClassJobName               { get; private set; }
        public VerticalListNode   CraftsmanshipInfoContainer { get; private set; }
        public VerticalListNode   ControlInfoContainer       { get; private set; }
        public VerticalListNode   CraftPointInfoContainer    { get; private set; }

        #endregion

        #region 执行行

        public const float EXECUTION_CONTAINER_HEIGHT = 35f;

        public HorizontalListNode ExecutionContainer { get; private set; }
        public TextButtonNode     ExecuteButton      { get; private set; }
        public NumericInputNode   CraftCountInput    { get; private set; }

        #endregion

        #region 宏复制行

        public const float COPY_MACRO_CONTAINER_HEIGHT = 35f;
        
        public HorizontalListNode CopyMacroContainer { get; private set; }

        #endregion

        #region 技能

        public const float ACTION_CONTAINER_INNER_PADDING = 12.5f;
        public const float ACTION_BLOCK_SPACING           = 5f;
        public const float ACTION_BLOCK_SIZE              = 50f;

        public const float ACTION_USED_ALPHA   = 0.2f;
        public const float ACTION_NORMAL_ALPHA = 1f;
        
        public SimpleNineGridNode ActionContainerBackground { get; private set; }
        public VerticalListNode   ActionContainer           { get; private set; }
        public IconButtonNode     ItemIcon                  { get; private set; }
        public List<DragDropNode> ActionBlocks              { get; private set; } = [];

        #endregion
        
        private int currentCraftRound;
        private int totalCraftRounds;

        public static void OpenWithActions
        (
            OptimizedRecipeNote       module,
            CaculationResult result
        )
        {
            if (result.Actions.Count == 0) return;
            
            Addon?.Dispose();

            var rowCount = MathF.Ceiling(result.Actions.Count / 10f);
            Addon = new(module, module.TaskHelper, result)
            {
                InternalName          = "DRRecipeNoteActionsPreview",
                Title                 = Lang.Get("OptimizedRecipeNote-AddonTitle"),
                Subtitle              = Lang.Get("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3),
                Size                  = new(700f, 192f + (50f * (rowCount - 1))),
                RememberClosePosition = true,
            };
            Addon.Open();
        }

        #region 事件

        private void OnRecipeNote
        (
            AddonEvent type,
            AddonArgs  args
        )
        {
            // 重置已用过的技能界面
            foreach (var node in ActionBlocks)
                node.Alpha = 1;
        }

        protected override unsafe void OnFinalize
        (
            AtkUnitBase* addon
        ) =>
            IAddonLifecycle.Instance().UnregisterListener(OnRecipeNote);

        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "RecipeNote", OnRecipeNote);

            RootContainer = new()
            {
                FitContents      = true,
                Width            = ContentSize.X,
                Position         = ContentStartPosition,
                ItemSpacing      = 5f,
                FirstItemSpacing = 0f
            };
            RootContainer.AttachNode(this);

            #region 基本信息

            StatsContainerBackground = new()
            {
                TexturePath        = "ui/uld/img04/BgParts_hr1.tex",
                TextureCoordinates = new(61, 37),
                TextureSize        = new(16, 16),
                Offsets            = new(7),
                Size               = ContentSize with { Y = STATS_CONTAINER_HEIGHT }
            };
            RootContainer.AddNode(StatsContainerBackground);

            StatsContainer = new()
            {
                FitToContentWidth = true,
                Height            = STATS_CONTAINER_HEIGHT,
                Position          = new(STATS_CONTAINER_INNER_PADDING)
            };
            StatsContainer.AttachNode(StatsContainerBackground);

            ClassJobIcon = new()
            {
                IconId      = Result.GetJob().GetIcon(),
                Size        = new(40, 40),
                TextureSize = new(40, 40),
                FitTexture  = true
            };
            StatsContainer.AddNode(ClassJobIcon);

            StatsContainer.AddDummy(10f);

            ClassJobInfoContainer = new()
            {
                FitContents = true,
                Width       = STATS_CLASS_JOB_COLUMN_WIDTH
            };
            StatsContainer.AddNode(ClassJobInfoContainer);
            
            ClassJobLable = new()
            {
                String    = Lang.Get("ClassJob"),
                FontSize  = 12,
                TextFlags = TextFlags.AutoAdjustNodeSize,
            };
            AtkColors.LabelLight.ApplyTo(ClassJobLable);
            ClassJobInfoContainer.AddNode(ClassJobLable);

            ClassJobName = new()
            {
                String    = Result.GetJob().Name,
                FontSize  = 18,
                Size      = new(STATS_CLASS_JOB_COLUMN_WIDTH, 24),
                TextFlags = TextFlags.Ellipsis,
            };
            AtkColors.Label.ApplyTo(ClassJobName);
            ClassJobInfoContainer.AddNode(ClassJobName);
            
            StatsContainer.AddNode(CreateStatsColumnSeperator());
            
            StatsContainer.AddDummy(STATS_COLUMN_DUMMY);
            
            CraftsmanshipInfoContainer = CreateStatsColumn(3261, Result.Craftmanship);
            
            StatsContainer.AddNode(CreateStatsColumnSeperator());
            
            StatsContainer.AddDummy(STATS_COLUMN_DUMMY);
            
            ControlInfoContainer = CreateStatsColumn(3262, Result.Control);
            
            StatsContainer.AddNode(CreateStatsColumnSeperator());
            
            StatsContainer.AddDummy(STATS_COLUMN_DUMMY);
            
            CraftPointInfoContainer = CreateStatsColumn(3223, Result.CraftPoint);
            
            #endregion

            #region 执行

            ExecutionContainer = new()
            {
                FitToContentWidth = true,
                Height            = EXECUTION_CONTAINER_HEIGHT,
                ItemSpacing       = 10
            };
            RootContainer.AddNode(ExecutionContainer);

            ExecuteButton = new()
            {
                Size        = new(140, 32),
                String      = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", 1),
                TextureType = ButtonTextureType.ButtonB,
                OnClick     = () =>
                {
                    var totalCount = CraftCountInput.Value;
                    currentCraftRound = 0;
                    totalCraftRounds  = totalCount;

                    // CraftProgressText.IsVisible = true;
                    // CraftProgressText.String    = $"{currentCraftRound}/{totalCraftRounds}";

                    LogMessageManager.Instance().RegPost(OnCraftLogMessage);

                    for (var round = 0; round < totalCount; round++)
                    {
                        var currentRound = round;

                        TaskHelper.Enqueue
                        (() =>
                            {
                                if (Synthesis->IsAddonAndNodesReady()) return;
                                
                                currentCraftRound        = currentRound + 1;
                                // CraftProgressText.String = $"{currentCraftRound}/{totalCraftRounds}";

                                RecipeNoteAddon->Callback(8);
                            }
                        );

                        TaskHelper.Enqueue(() => Synthesis != null);

                        TaskHelper.DelayNext(500);

                        EnqueueActionSequence(TaskHelper, Result.Actions);

                        TaskHelper.Enqueue(() => Synthesis == null);

                        TaskHelper.Enqueue(() => ICondition.Instance()[ConditionFlag.PreparingToCraft]);

                        TaskHelper.DelayNext(300);
                    }

                    TaskHelper.Enqueue(() => OnCraftingLoopFinished(totalCount));
                }
            };
            ExecutionContainer.AddNode(ExecuteButton);

            CraftCountInput = new NumericInputNode
            {
                Size          = new(140, 32),
                Min           = 1,
                Max           = 99999,
                Step          = 1,
                Value         = 1,
                OnValueUpdate = value => ExecuteButton.String = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", value)
            };
            ExecutionContainer.AddNode(CraftCountInput);

            #endregion

            #region 复制宏

            CopyMacroContainer = new()
            {
                FitToContentWidth = true,
                Height            = COPY_MACRO_CONTAINER_HEIGHT,
                ItemSpacing       = 5f
            };
            RootContainer.AddNode(CopyMacroContainer);
            
            var macroButtonCount = (int)Math.Ceiling(Result.Actions.Count / 15f);
            for (var i = 0; i < macroButtonCount; i++)
            {
                var macroIndex = i;
                var button = new TextButtonNode
                {
                    Size      = new(140, 28f),
                    String    = Lang.Get("OptimizedRecipeNote-Button-CopyMacro", macroIndex + 1),
                    OnClick = () =>
                    {
                        var startIndex = macroIndex * 15;
                        var endIndex   = Math.Min(startIndex + 15, Result.Actions.Count);
                        
                        var actionsForMacro = Result.Actions.Skip(startIndex).Take(endIndex - startIndex);

                        var builder = new StringBuilder();
                        foreach (var action in actionsForMacro)
                            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
                        ImGui.SetClipboardText(builder.ToString());

                        var message = Lang.Get("OptimizedRecipeNote-Message-MacroCopied", macroIndex + 1);
                        NotifyHelper.Toast(message);
                    }
                };
                CopyMacroContainer.AddNode(button);
            }

            #endregion

            #region 技能

            ActionContainerBackground = new()
            {
                TexturePath        = "ui/uld/img04/BgParts_hr1.tex",
                TextureCoordinates = new(61, 37),
                TextureSize        = new(16, 16),
                Offsets            = new(7),
                Size               = ContentSize with { Y = STATS_CONTAINER_HEIGHT }
            };
            RootContainer.AddNode(ActionContainerBackground);

            ActionContainer = new()
            {
                FitContents = true,
                Width       = ActionContainerBackground.Width - (2 * ACTION_CONTAINER_INNER_PADDING),
                Position    = new(ACTION_CONTAINER_INNER_PADDING),
                ItemSpacing = ACTION_BLOCK_SPACING
            };
            ActionContainer.AttachNode(ActionContainerBackground);

            ItemIcon = new()
            {
                IconId       = Result.GetRecipe().ItemResult.Value.Icon,
                InnerPadding = Vector2.Zero,
                Size         = new(32),
                OnClick      = () => Module.OpenItemContextMenu(Result.GetRecipe().ItemResult.RowId)
            };
            ActionContainer.AddNode(ItemIcon);

            var currentRow = new HorizontalListNode
            {
                Size        = new(ActionContainer.Width, ACTION_BLOCK_SIZE),
                ItemSpacing = ACTION_BLOCK_SPACING
            };

            var itemsInCurrentRow = 0;

            for (var index = 0; index < Result.Actions.Count; index++)
            {
                var actionID = Result.Actions[index];
                var iconID   = LuminaWrapper.GetActionIconID(actionID);
                if (iconID == 0) continue;

                if (itemsInCurrentRow > 1 &&
                    (itemsInCurrentRow * ACTION_BLOCK_SIZE) + ((itemsInCurrentRow - 1) * ACTION_BLOCK_SPACING) > ActionContainer.Width)
                {
                    ActionContainer.AddNode(currentRow);

                    currentRow = new HorizontalListNode
                    {
                        Size        = new(ActionContainer.Width, ACTION_BLOCK_SIZE),
                        ItemSpacing = ACTION_BLOCK_SPACING
                    };
                    itemsInCurrentRow = 0;
                }

                var dragDropNode = new DragDropNode
                {
                    Size         = new(ACTION_BLOCK_SIZE),
                    IconId       = iconID,
                    AcceptedType = DragDropType.Nothing,
                    IsDraggable  = true,
                    IsClickable  = true,
                    Payload = new()
                    {
                        Type = actionID > 10_0000 ?
                                   DragDropType.CraftingAction :
                                   DragDropType.Action,
                        Int2 = (int)actionID
                    },
                    OnRollOver = node =>
                    {
                        var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();

                        tooltipArgs.ActionArgs.Flags = 1;
                        tooltipArgs.ActionArgs.Kind = actionID > 10_0000 ?
                                                          DetailKind.CraftingAction :
                                                          DetailKind.Action;
                        tooltipArgs.ActionArgs.Id = (int)actionID;

                        AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Action, addon->Id, node, &tooltipArgs);
                    },
                    OnRollOut = node => node.HideTooltip()
                };
                dragDropNode.OnClicked = _ =>
                {
                    if (ICondition.Instance()[ConditionFlag.ExecutingCraftingAction] ||
                        TaskHelper.IsBusy)
                        return;
                    
                    ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(actionID)}");
                    
                    if (Synthesis != null)
                        dragDropNode.Alpha = ACTION_USED_ALPHA;
                };
                ActionBlocks.Add(dragDropNode);

                var actionIndexNode = new TextNode
                {
                    Position  = new(-4),
                    String    = $"{index + 1}",
                    FontType  = FontType.MiedingerMed,
                    TextFlags = TextFlags.Edge
                };
                AtkColors.Value.ApplyTo(actionIndexNode);
                actionIndexNode.AttachNode(dragDropNode);

                currentRow.AddNode(dragDropNode);

                itemsInCurrentRow++;
            }

            if (itemsInCurrentRow > 0)
                ActionContainer.AddNode(currentRow);

            ActionContainerBackground.Height = ActionContainer.Height + (2 * ACTION_CONTAINER_INNER_PADDING);

            #endregion
            
            RootContainer.RecalculateLayout();
            SetWindowSize(Size.X, RootContainer.Height + ContentStartPosition.Y + 32f);
            
            return;

            VerticalListNode CreateStatsColumn
            (
                uint addonTextID,
                int  number
            )
            {
                var statsColumn = new VerticalListNode
                {
                    FitContents = true,
                    Width       = STATS_COLUMN_WIDTH
                };
                StatsContainer.AddNode(statsColumn);

                var lable = new TextNode
                {
                    SheetType = NodeData.SheetType.Addon,
                    TextId    = addonTextID,
                    FontSize  = 12,
                    TextFlags = TextFlags.AutoAdjustNodeSize,
                };
                AtkColors.LabelLight.ApplyTo(lable);
                statsColumn.AddNode(lable);

                var name = new TextNode
                {
                    String    = number.ToString(),
                    FontSize  = 20,
                    Size      = new(STATS_COLUMN_WIDTH, 24),
                    TextFlags = TextFlags.Ellipsis |TextFlags.Edge,
                    FontType  = FontType.Miedinger
                };
                AtkColors.ValueEmphasize.ApplyTo(name);
                statsColumn.AddNode(name);
            
                return statsColumn;
            }
        }

        protected override unsafe void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            if (TaskHelper.IsBusy)
                ExecuteButton.IsEnabled = false;
            else
            {
                ExecuteButton.IsEnabled = CraftCountInput.Value switch
                {
                    0 => false,
                    1 => true,
                    _ => RecipeNoteAddon->IsAddonAndNodesReady()
                };
            }
        }

        #endregion

        private static VerticalLineNode CreateStatsColumnSeperator() =>
            new()
            {
                Height = 45f,
                Width  = 4f,
                Y      = -2.5f
            };

        private void EnqueueActionSequence
        (
            TaskHelper th,
            List<uint> actions
        )
        {
            for (var index = 0; index < actions.Count; index++)
            {
                var x = actions[index];
                var i = index;
                th.Enqueue
                (() =>
                    {
                        if (ICondition.Instance()[ConditionFlag.ExecutingCraftingAction]) return true;

                        ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(x)}");
                        return false;
                    }
                );
                th.Enqueue(() => ActionBlocks[i].Alpha = 0.2f);
                th.Enqueue(() => !ICondition.Instance()[ConditionFlag.ExecutingCraftingAction]);
            }
        }

        private void OnCraftLogMessage
        (
            uint                logMessageID,
            LogMessageQueueItem item
        )
        {
            if (!CraftFailedLogMessages.Contains(logMessageID)) return;
            OnCraftingLoopFinished(0, true);
        }

        private void OnCraftingLoopFinished
        (
            int  completedCount,
            bool isCraftFailed = false
        )
        {
            LogMessageManager.Instance().Unreg(OnCraftLogMessage);
            TaskHelper.Abort();

            // CraftProgressText.IsVisible = false;
            currentCraftRound           = 0;
            totalCraftRounds            = 0;

            var message = isCraftFailed ?
                              Lang.Get("OptimizedRecipeNote-Message-CraftFailed") :
                              Lang.Get("OptimizedRecipeNote-Message-CraftComplete", completedCount);

            if (isCraftFailed)
            {
                NotifyHelper.Instance().ChatError(message);
                NotifyHelper.SystemWarning();
                NotifyHelper.Instance().NotificationError(message);
            }
            else
            {
                NotifyHelper.Instance().Chat(message);
                NotifyHelper.SystemInformation();
                NotifyHelper.Instance().NotificationSuccess(message);
            }

            NotifyHelper.Speak(message);

            foreach (var node in ActionBlocks)
                node.Alpha = 1;
        }
    }
}
