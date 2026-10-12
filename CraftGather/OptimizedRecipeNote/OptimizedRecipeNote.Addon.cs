using System.Text;
using DailyRoutines.Common.Info;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Data.Parsing.Uld;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Nodes.Background;
using OmenTools.KamiToolKit.Nodes.CircleButton;
using OmenTools.KamiToolKit.Nodes.GaugeBarCraft;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace DailyRoutines.ModulesPublic.CraftGather;

public partial class OptimizedRecipeNote
{
    private class AddonActionsPreview
    (
        TaskHelper        inTaskHelper,
        CalculationResult result
    ) : NativeAddon
    {
        public static AddonActionsPreview? Addon { get; set; }

        public CalculationResult Result          { get; init; } = result;
        public TaskHelper        TaskHelper      { get; init; } = inTaskHelper;
        public int               CraftableAmount { get; }       = Math.Max(1, GetCraftableAmount(result.GetRecipe()));

        public VerticalListNode RootContainer { get; private set; }

        #region 基础数据

        public const float STATS_CONTAINER_HEIGHT        = 70f;
        public const float STATS_CONTAINER_INNER_PADDING = 15f;
        public const float STATS_CLASS_JOB_COLUMN_WIDTH  = 180f;
        public const float STATS_COLUMN_WIDTH            = 120F;
        public const float STATS_COLUMN_DUMMY            = 18.5f;

        public BackgroundNode     StatsContainerBackground   { get; private set; }
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

        public const float EXECUTION_CONTAINER_HEIGHT = 40f;

        public HorizontalListNode ExecutionContainer { get; private set; }
        public TextButtonNode     ExecuteButton      { get; private set; }
        public NumericInputNode   CraftCountInput    { get; private set; }
        public GaugeBarCraftNode  CraftProgressBar   { get; private set; }
        public TextNode           CraftRoundInfo     { get; private set; }

        #endregion

        #region 技能

        public const float ACTION_CONTAINER_INNER_PADDING = 12.5f;
        public const float ACTION_BLOCK_SPACING           = 5f;
        public const float ACTION_BLOCK_SIZE              = 50f;
        public const float COPY_MACRO_BUTTON_SIZE         = 28f;
        public const float COPY_MACRO_BUTTON_SPACING      = 5f;
        public const float ITEM_INFO_SIZE                 = 48f;
        public const int   MACRO_ACTION_COUNT             = 15;

        public const float ACTION_USED_ALPHA   = 0.2f;
        public const float ACTION_NORMAL_ALPHA = 1f;

        public BackgroundNode     ActionContainerBackground { get; private set; }
        public VerticalListNode   ActionContainer           { get; private set; }
        public HorizontalListNode ItemInfoContainer         { get; private set; }
        public ItemIconNode       ItemIcon                  { get; private set; }
        public TextNode           ItemName                  { get; private set; }
        public TextNode           MacroStats                { get; private set; }
        public CircleButtonNode   CopyMacroButton           { get; private set; }
        public List<DragDropNode> ActionBlocks              { get; private set; } = [];

        #endregion

        #region 预计结果

        public const float PREVIEW_GAUGE_WIDTH = 200f;
        public const float PREVIEW_INFO_WIDTH  = 110f;

        public HorizontalListNode CraftPreviewContainer    { get; private set; }
        public GaugeBarCraftNode  CraftPreviewProgressBar  { get; private set; }
        public TextNode           CraftPreviewProgressInfo { get; private set; }
        public GaugeBarCraftNode  CraftPreviewQualityBar   { get; private set; }
        public TextNode           CraftPreviewQualityInfo  { get; private set; }

        #endregion

        public static void OpenWithActions
        (
            OptimizedRecipeNote module,
            CalculationResult   result
        )
        {
            if (result.Actions.Count == 0) return;

            Addon?.Dispose();

            Addon = new(module.TaskHelper, result)
            {
                InternalName          = "DRRecipeNoteActionsPreview",
                Title                 = Lang.Get("OptimizedRecipeNote-AddonTitle"),
                Size                  = new(700f, 200f),
                RememberClosePosition = true
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
            foreach (var node in ActionBlocks)
                node.Alpha = ACTION_NORMAL_ALPHA;
        }

        private void OnCraftLogMessage
        (
            uint                logMessageID,
            LogMessageQueueItem item
        )
        {
            if (!CraftFailedLogMessages.Contains(logMessageID)) return;

            LogMessageManager.Instance().Unreg(OnCraftLogMessage);
            TaskHelper.Abort();

            CraftRoundInfo.IsVisible   = false;
            CraftProgressBar.IsVisible = false;

            foreach (var node in ActionBlocks)
                node.Alpha = ACTION_NORMAL_ALPHA;

            var message = Lang.Get("OptimizedRecipeNote-Message-CraftFailed");
            NotifyHelper.Instance().ChatError(message);
            NotifyHelper.Instance().TrayError(message);
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
                FitContents = true,
                Width       = ContentSize.X,
                Position    = ContentStartPosition,
                ItemSpacing = 5f
            };
            RootContainer.AttachNode(this);

            #region 基本信息

            StatsContainerBackground = new()
            {
                Type = BackgroundType.SoftDot,
                Size = ContentSize with { Y = STATS_CONTAINER_HEIGHT }
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
                TextFlags = TextFlags.AutoAdjustNodeSize
            };
            AtkColors.LabelLight.ApplyTo(ClassJobLable);
            ClassJobInfoContainer.AddNode(ClassJobLable);

            ClassJobName = new()
            {
                String    = Result.GetJob().Name,
                FontSize  = 18,
                Size      = new(STATS_CLASS_JOB_COLUMN_WIDTH, 24),
                TextFlags = TextFlags.Ellipsis
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
                Size        = new(140, EXECUTION_CONTAINER_HEIGHT),
                String      = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", CraftableAmount),
                TextureType = ButtonTextureType.ButtonB,
                OnClick = () =>
                {
                    if (TaskHelper.IsBusy)
                    {
                        TaskHelper.Abort();

                        CraftRoundInfo.IsVisible   = false;
                        CraftProgressBar.IsVisible = false;
                        return;
                    }

                    if (Synthesis->IsAddonAndNodesReady())
                    {
                        EnqueueActionSequence(TaskHelper, Result.Actions);
                        return;
                    }

                    var totalCraftRound = CraftCountInput.Value;

                    LogMessageManager.Instance().RegPost(OnCraftLogMessage);

                    for (var round = 0; round < totalCraftRound; round++)
                    {
                        var currentRound = round;

                        TaskHelper.Enqueue
                        (() =>
                            {
                                if (RecipeNoteAddon == null)
                                    AgentRecipeNote.Instance()->OpenRecipeByRecipeId(Result.RecipeID);

                                var recipeList = RecipeNote.Instance()->RecipeList;
                                if (recipeList == null)
                                    return false;

                                var recipe = recipeList->SelectedRecipe;
                                if (recipe == null)
                                    return false;

                                return recipe->RecipeId == Result.RecipeID;
                            }
                        );

                        TaskHelper.Enqueue
                        (() =>
                            {
                                RefreshRecipeMaterials();

                                UpdateCraftRoundProgress(currentRound + 1, totalCraftRound);

                                AgentId.RecipeNote.SendEvent(0, 8);
                            }
                        );

                        TaskHelper.Enqueue(() => Synthesis->IsAddonAndNodesReady());

                        EnqueueActionSequence(TaskHelper, Result.Actions);

                        TaskHelper.Enqueue(() => Synthesis == null);

                        TaskHelper.Enqueue(() => ICondition.Instance()[ConditionFlag.PreparingToCraft]);

                        TaskHelper.DelayNext(300);
                    }

                    TaskHelper.Enqueue
                    (() =>
                        {
                            LogMessageManager.Instance().Unreg(OnCraftLogMessage);
                            TaskHelper.Abort();

                            CraftRoundInfo.IsVisible   = false;
                            CraftProgressBar.IsVisible = false;

                            foreach (var node in ActionBlocks)
                                node.Alpha = ACTION_NORMAL_ALPHA;

                            var resultItem = Result.GetRecipe().ItemResult.Value;

                            var message = Lang.GetSe
                            (
                                "OptimizedRecipeNote-Message-CraftComplete",
                                new Dictionary<string, object>
                                {
                                    ["rounds"] = totalCraftRound,
                                    ["count"]  = totalCraftRound * Result.GetRecipe().AmountResult,
                                    ["item"]   = ReadOnlySeString.CreateItemLink(resultItem.RowId, false)
                                }
                            );
                            NotifyHelper.Instance().Chat(message);
                            NotifyHelper.Toast(message);
                            NotifyHelper.Instance().TrayInfo(message.ToString(), Lang.Get("OptimizedRecipeNote-Message-CraftComplete-Title"));
                        }
                    );
                }
            };
            ExecutionContainer.AddNode(ExecuteButton);

            CraftCountInput = new NumericInputNode
            {
                Size          = new(140, EXECUTION_CONTAINER_HEIGHT),
                Y             = 4,
                Min           = 1,
                Max           = 99999,
                Step          = 1,
                Value         = CraftableAmount,
                OnValueUpdate = value => ExecuteButton.String = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", value)
            };
            ExecutionContainer.AddNode(CraftCountInput);

            ExecutionContainer.AddDummy(12f);

            CraftProgressBar = new()
            {
                MaxValue = 1,
                Value    = 1,
                Size     = new(240, 16),
                Y        = 12
            };
            ExecutionContainer.AddNode(CraftProgressBar);

            CraftRoundInfo = new()
            {
                FontSize      = 23,
                FontType      = FontType.TrumpGothic,
                AlignmentType = AlignmentType.Center,
                Size          = new(102, EXECUTION_CONTAINER_HEIGHT),
                String        = "111/222"
            };
            AtkColors.Text.ApplyTo(CraftRoundInfo);
            ExecutionContainer.AddNode(CraftRoundInfo);

            CraftProgressBar.IsVisible = CraftRoundInfo.IsVisible = false;

            #endregion

            #region 技能

            ActionContainerBackground = new()
            {
                Type = BackgroundType.SoftDot,
                Size = ContentSize with { Y = STATS_CONTAINER_HEIGHT }
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

            ItemInfoContainer = new()
            {
                Size        = new(ActionContainer.Width, 36f),
                ItemSpacing = ACTION_BLOCK_SPACING
            };
            ActionContainer.AddNode(ItemInfoContainer);

            var resultItem = Result.GetRecipe().ItemResult.Value;

            ItemIcon = new()
            {
                IconId = resultItem.Icon,
                Size   = new(ITEM_INFO_SIZE),
                ItemID = resultItem.RowId,
                OnClick = (_, _, _, _, atkEventData) =>
                {
                    if (!atkEventData->IsRightClick) return;
                    ContextMenuManager.Instance().OpenItem(resultItem.RowId);
                }
            };
            ItemInfoContainer.AddNode(ItemIcon);

            ItemName = new()
            {
                String        = resultItem.Name,
                Size          = new(400f, 40f),
                FontSize      = 16,
                AlignmentType = AlignmentType.Left,
                TextFlags     = TextFlags.MultiLine | TextFlags.WordWrap
            };
            AtkColors.Hint.ApplyTo(ItemName);
            ItemInfoContainer.AddNode(ItemName);

            MacroStats = new()
            {
                Size          = new(0f, 40f),
                AlignmentType = AlignmentType.Right,
                String = Lang.Get
                (
                    "OptimizedRecipeNote-Text-MacroStats",
                    Result.Actions.Count,
                    Result.Actions.Count * 3
                ),
                FontSize = 14,
                X        = ActionContainer.Width - COPY_MACRO_BUTTON_SIZE - COPY_MACRO_BUTTON_SPACING
            };
            AtkColors.LabelLight.ApplyTo(MacroStats);
            MacroStats.AttachNode(ActionContainer);

            CopyMacroButton = new()
            {
                Icon     = CircleButtonIcon.Document,
                Size     = new(COPY_MACRO_BUTTON_SIZE),
                Position = new(ActionContainer.Width - COPY_MACRO_BUTTON_SIZE, 6f),
                OnClick  = OpenCopyMacroContextMenu
            };
            CopyMacroButton.AttachNode(ActionContainer);

            CraftPreviewContainer = new()
            {
                FitToContentWidth = true,
                Height            = EXECUTION_CONTAINER_HEIGHT,
                ItemSpacing       = 10f
            };
            ActionContainer.AddNode(CraftPreviewContainer);

            var recipeMaxProgress = GetRecipeMaxProgress(Result.GetRecipe());
            var recipeMaxQuality  = GetRecipeMaxQuality(Result.GetRecipe());

            CraftPreviewProgressBar = new()
            {
                Type     = CraftGaugeType.Progress,
                Size     = new(PREVIEW_GAUGE_WIDTH, 16f),
                Y        = 12f,
                MaxValue = recipeMaxProgress,
                Value    = Result.FinalProgress
            };
            CraftPreviewContainer.AddNode(CraftPreviewProgressBar);

            CraftPreviewProgressInfo = new()
            {
                FontSize      = 20,
                FontType      = FontType.TrumpGothic,
                AlignmentType = AlignmentType.Center,
                Size          = new(PREVIEW_INFO_WIDTH, EXECUTION_CONTAINER_HEIGHT),
                String        = $"{Result.FinalProgress}/{recipeMaxProgress}"
            };
            AtkColors.Text.ApplyTo(CraftPreviewProgressInfo);
            CraftPreviewContainer.AddNode(CraftPreviewProgressInfo);

            CraftPreviewQualityBar = new()
            {
                Type     = CraftGaugeType.Quality,
                Size     = new(PREVIEW_GAUGE_WIDTH, 16f),
                Y        = 12f,
                MaxValue = recipeMaxQuality,
                Value    = Result.FinalQuality
            };
            CraftPreviewContainer.AddNode(CraftPreviewQualityBar);

            CraftPreviewQualityInfo = new()
            {
                FontSize      = 20,
                FontType      = FontType.TrumpGothic,
                AlignmentType = AlignmentType.Center,
                Size          = new(PREVIEW_INFO_WIDTH, EXECUTION_CONTAINER_HEIGHT),
                String        = $"{Result.FinalQuality}/{recipeMaxQuality}"
            };
            AtkColors.Text.ApplyTo(CraftPreviewQualityInfo);
            CraftPreviewContainer.AddNode(CraftPreviewQualityInfo);

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

                if (itemsInCurrentRow                                                                          > 1 &&
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

                var blockNode = new ResNode
                {
                    Size = new(ACTION_BLOCK_SIZE)
                };

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
                dragDropNode.AttachNode(blockNode);
                ActionBlocks.Add(dragDropNode);

                var actionIndexNode = new TextNode
                {
                    Position  = new(-4),
                    String    = $"{index + 1}",
                    FontType  = FontType.MiedingerMed,
                    TextFlags = TextFlags.Edge
                };
                AtkColors.Value.ApplyTo(actionIndexNode);
                actionIndexNode.AttachNode(blockNode);

                currentRow.AddNode(blockNode);

                itemsInCurrentRow++;
            }

            if (itemsInCurrentRow > 0)
                ActionContainer.AddNode(currentRow);

            ActionContainerBackground.Height = ActionContainer.Height + (2 * ACTION_CONTAINER_INNER_PADDING);

            #endregion

            RootContainer.RecalculateLayout();
            SetWindowSize(Size.X, RootContainer.Height + ContentStartPosition.Y + 24f);
            RootContainer.Position = ContentStartPosition;

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
                    TextFlags = TextFlags.AutoAdjustNodeSize
                };
                AtkColors.LabelLight.ApplyTo(lable);
                statsColumn.AddNode(lable);

                var name = new TextNode
                {
                    String    = number.ToString(),
                    FontSize  = 20,
                    Size      = new(STATS_COLUMN_WIDTH, 24),
                    TextFlags = TextFlags.Ellipsis | TextFlags.Edge,
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
            {
                ExecuteButton.String = Lang.Get("OptimizedRecipeNote-Button-StopCraft");
                return;
            }

            if (Synthesis->IsAddonAndNodesReady())
            {
                ExecuteButton.String = Lang.Get("OptimizedRecipeNote-Button-StartCraft");
                return;
            }

            ExecuteButton.String = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", CraftCountInput.Value);
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
                th.Enqueue(() => !ICondition.Instance()[ConditionFlag.ExecutingCraftingAction]);
                th.Enqueue(() => ActionBlocks[i].Alpha = ACTION_USED_ALPHA);
            }
        }

        private void UpdateCraftRoundProgress
        (
            int currentRound,
            int totalRound
        )
        {
            CraftProgressBar.MaxValue  = totalRound;
            CraftProgressBar.Value     = currentRound;
            CraftProgressBar.IsVisible = true;

            CraftRoundInfo.String    = $"{currentRound}/{totalRound}";
            CraftRoundInfo.IsVisible = true;
        }

        private void OpenCopyMacroContextMenu()
        {
            var macroCount = (int)Math.Ceiling(Result.Actions.Count / (float)MACRO_ACTION_COUNT);

            List<ContextMenuItem> menus = [];

            for (var index = 0; index < macroCount; index++)
            {
                var macroIndex = index;

                menus.Add
                (
                    new()
                    {
                        Name      = Lang.Get("OptimizedRecipeNote-Button-CopyMacro", macroIndex + 1),
                        OnClicked = _ => CopyMacroToClipboard(macroIndex)
                    }
                );
            }

            ContextMenuManager.Instance().Open
            (
                new(),
                [
                    new ContextMenuEntryInfo
                    (
                        nameof(OptimizedRecipeNote),
                        _ => menus,
                        omitPrefix: true
                    )
                ]
            );
        }

        private void CopyMacroToClipboard
        (
            int macroIndex
        )
        {
            var startIndex = macroIndex * MACRO_ACTION_COUNT;
            var endIndex   = Math.Min(startIndex + MACRO_ACTION_COUNT, Result.Actions.Count);

            var builder = new StringBuilder();
            foreach (var action in Result.Actions.Skip(startIndex).Take(endIndex - startIndex))
                builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
            ImGui.SetClipboardText(builder.ToString());

            NotifyHelper.Toast(Lang.Get("OptimizedRecipeNote-Message-MacroCopied", macroIndex + 1));
        }
    }
}
