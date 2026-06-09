using System.Text;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.Interface.OptimizedRecipeNote;

public partial class OptimizedRecipeNote
{
    private class AddonActionsPreview
    (
        TaskHelper       taskHelper,
        CaculationResult result
    ) : NativeAddon
    {
        private static Task?                OpenAddonTask;
        public static  AddonActionsPreview? Addon  { get; set; }
        public         CaculationResult     Result { get; private set; } = result;
        public         List<DragDropNode>   Nodes  { get; set; }         = [];

        public WeakReference<TaskHelper> TaskHelper { get; private set; } = new(taskHelper);

        public TextButtonNode   CraftOnceButton     { get; private set; }
        public TextButtonNode   CraftMultipleButton { get; private set; }
        public NumericInputNode CraftCountInput     { get; private set; }
        public TextNode         CraftProgressText   { get; private set; }

        private int currentCraftRound;
        private int totalCraftRounds;

        public static void OpenWithActions(TaskHelper taskHelper, CaculationResult result)
        {
            if (OpenAddonTask != null) return;

            var isAddonExisted = Addon?.IsOpen ?? false;

            if (Addon != null)
            {
                Addon.Dispose();
                Addon = null;
            }

            OpenAddonTask = DService.Instance().Framework.RunOnTick
            (
                () =>
                {
                    var rowCount = MathF.Ceiling(result.Actions.Count / 10f);
                    Addon ??= new(taskHelper, result)
                    {
                        InternalName = "DRRecipeNoteActionsPreview",
                        Title        = $"{Lang.Get("OptimizedRecipeNote-AddonTitle")}",
                        Subtitle     = $"{Lang.Get("OptimizedRecipeNote-Message-StepsInfo", result.Actions.Count, result.Actions.Count * 3)}",
                        Size         = new(500f, 192f + (50f                                                                           * (rowCount - 1)))
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (Result.Actions.Count == 0) return;

            // Row 1: 职业 + 三维数据
            var statsRow = new HorizontalListNode
            {
                IsVisible = true,
                Position  = new(12, 40),
                Size      = new(0, 44)
            };

            var jobTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String = new SeStringBuilder()
                         .AddText($"{LuminaWrapper.GetAddonText(294)}: ")
                         .AddIcon(Result.GetJob().ToBitmapFontIcon())
                         .AddText(Result.GetJob().Name.ToString())
                         .Build()
                         .Encode()
            };
            jobTextNode.Size =  jobTextNode.GetTextDrawSize($"{jobTextNode.String}123");
            statsRow.Width   += jobTextNode.Width;
            statsRow.AddNode(jobTextNode);

            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var craftmanshipTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3261)}: {Result.Craftmanship}"
            };
            statsRow.Width += craftmanshipTextNode.Width;
            statsRow.AddNode(craftmanshipTextNode);

            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var controlTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3262)}: {Result.Control}"
            };
            statsRow.Width += controlTextNode.Width;
            statsRow.AddNode(controlTextNode);

            statsRow.Width += 12;
            statsRow.AddDummy(12);

            var craftPointTextNode = new TextNode
            {
                IsVisible = true,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                String    = $"{LuminaWrapper.GetAddonText(3223)}: {Result.CraftPoint}"
            };
            statsRow.Width += craftPointTextNode.Width;
            statsRow.AddNode(craftPointTextNode);

            statsRow.AttachNode(this);

            // Row 2: 复制宏按钮
            var macroRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Position  = new(8, 65),
                Size      = new(0, 28)
            };

            var macroButtonCount = (int)Math.Ceiling(Result.Actions.Count / 15.0);

            for (var i = 0; i < macroButtonCount; i++)
            {
                var macroIndex = i;
                var copyMacroButton = new TextButtonNode
                {
                    IsVisible = true,
                    Size      = new(120, 24),
                    String    = Lang.Get("OptimizedRecipeNote-Button-CopyMacro", macroIndex + 1),
                    OnClick = () =>
                    {
                        var startIndex      = macroIndex * 15;
                        var endIndex        = Math.Min(startIndex                           + 15, Result.Actions.Count);
                        var actionsForMacro = Result.Actions.Skip(startIndex).Take(endIndex - startIndex);

                        var builder = new StringBuilder();
                        foreach (var action in actionsForMacro)
                            builder.AppendLine($"/ac {LuminaWrapper.GetActionName(action)} <wait.3>");
                        ImGui.SetClipboardText(builder.ToString());

                        NotifyHelper.Instance().NotificationSuccess($"{Lang.Get("CopiedToClipboard")}");
                    }
                };
                macroRow.Width += copyMacroButton.Width;
                macroRow.AddNode(copyMacroButton);

                macroRow.Width += 4;
                macroRow.AddDummy(4);
            }

            macroRow.AttachNode(this);

            // Row 3: 制作按钮行
            var craftRow = new HorizontalListNode
            {
                IsVisible = true,
                Position  = new(8, 93),
                Size      = new(120, 28)
            };

            CraftOnceButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(120, 28),
                String    = Lang.Get("Execute"),
                OnClick   = () =>
                {
                    if (!TaskHelper.TryGetTarget(out var th)) return;
                    if (Result.Actions is not { Count: > 0 } actions) return;

                    EnqueueActionSequence(th, actions);
                }
            };
            craftRow.AddNode(CraftOnceButton);

            craftRow.AddDummy(16);

            CraftCountInput = new NumericInputNode
            {
                IsVisible = true,
                Size      = new(120, 28),
                Position  = new(0, -2),
                Min       = 0,
                Max       = 0,
                Step      = 1,
                Value     = 0,
                OnValueUpdate =  _ =>
                {
                    CraftMultipleButton.String    = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", CraftCountInput.Value);
                    CraftMultipleButton.IsEnabled = CraftCountInput.Value > 0;
                }
            };
            craftRow.AddNode(CraftCountInput);

            craftRow.AddDummy(2);

            CraftMultipleButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(120, 28),
                String    = Lang.Get("OptimizedRecipeNote-Button-CraftMultiple", 0),
                IsEnabled = false,
                OnClick   = () =>
                {
                    if (!TaskHelper.TryGetTarget(out var th)) return;
                    if (Result.Actions is not { Count: > 0 } actions) return;
                    if (CraftCountInput is not { Value: > 0 }) return;

                    var totalCount = CraftCountInput.Value;
                    currentCraftRound = 0;
                    totalCraftRounds  = totalCount;

                    CraftProgressText.IsVisible = true;
                    CraftProgressText.String    = $"{currentCraftRound}/{totalCraftRounds}";

                    LogMessageManager.Instance().RegPost(OnCraftLogMessage);

                    for (var round = 0; round < totalCount; round++)
                    {
                        var currentRound = round;

                        th.Enqueue
                        (() =>
                            {
                                currentCraftRound        = currentRound + 1;
                                CraftProgressText.String = $"{currentCraftRound}/{totalCraftRounds}";

                                RecipeNoteAddon->Callback(8);
                                return true;
                            }
                        );

                        th.Enqueue(() => Synthesis != null);

                        th.DelayNext(500);

                        EnqueueActionSequence(th, actions);

                        th.Enqueue(() => Synthesis == null);

                        th.Enqueue(() => DService.Instance().Condition[ConditionFlag.PreparingToCraft]);

                        th.DelayNext(300);
                    }

                    th.Enqueue(() => OnCraftingLoopFinished(totalCount));
                }
            };
            craftRow.AddNode(CraftMultipleButton);
            
            craftRow.AddDummy(4f);
            
            // 制作进度文本 (平时隐藏)
            CraftProgressText = new TextNode
            {
                IsVisible = false,
                TextFlags = TextFlags.AutoAdjustNodeSize,
                TextColor = ColorHelper.GetColor(3),
                FontSize  = 14,
            };
            craftRow.AddNode(CraftProgressText);
            
            craftRow.AttachNode(this);

            // Row 4: 技能序列
            var container = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(12, 125),
                Size      = new(44)
            };

            var currentRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Size      = new(0, 44)
            };

            var itemsInCurrentRow = 0;

            for (var index = 0; index < Result.Actions.Count; index++)
            {
                var actionID = Result.Actions[index];
                var iconID   = LuminaWrapper.GetActionIconID(actionID);
                if (iconID == 0) continue;

                if (itemsInCurrentRow >= 10)
                {
                    container.AddNode(currentRow);
                    container.AddDummy(4f);

                    currentRow = new HorizontalFlexNode
                    {
                        IsVisible = true,
                        Size      = new(0, 44)
                    };
                    itemsInCurrentRow = 0;
                }

                var dragDropNode = new DragDropNode
                {
                    Size         = new(44f),
                    IsVisible    = true,
                    IconId       = iconID,
                    AcceptedType = DragDropType.Nothing,
                    IsDraggable  = true,
                    IsClickable  = true,
                    Payload = new()
                    {
                        Type = actionID > 10_0000 ? DragDropType.CraftingAction : DragDropType.Action,
                        Int2 = (int)actionID
                    },
                    OnRollOver = node =>
                    {
                        var tooltipArgs = new AtkTooltipManager.AtkTooltipArgs();

                        tooltipArgs.ActionArgs.Flags = 1;
                        tooltipArgs.ActionArgs.Kind  = actionID > 10_0000 ? DetailKind.CraftingAction : DetailKind.Action;
                        tooltipArgs.ActionArgs.Id    = (int)actionID;

                        AtkStage.Instance()->TooltipManager.ShowTooltip(AtkTooltipType.Action, addon->Id, node, &tooltipArgs);
                    },
                    OnRollOut = node => node.HideTooltip()
                };
                dragDropNode.OnClicked = _ =>
                {
                    if (DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction] ||
                        (TaskHelper.TryGetTarget(out var th) && th.IsBusy))
                        return;

                    if (Synthesis != null)
                        dragDropNode.Alpha = 0.2f;
                    ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(actionID)}");
                };
                Nodes.Add(dragDropNode);

                var actionIndexNode = new TextNode
                {
                    IsVisible        = true,
                    Position         = new(-4),
                    String           = $"{index + 1}",
                    FontType         = FontType.MiedingerMed,
                    TextFlags        = TextFlags.Edge | TextFlags.Emboss,
                    TextColor        = ColorHelper.GetColor(50),
                    TextOutlineColor = ColorHelper.GetColor(28)
                };
                actionIndexNode.AttachNode(dragDropNode);

                currentRow.AddNode(dragDropNode);
                currentRow.AddDummy(4);
                currentRow.Width += dragDropNode.Size.X + 4;

                itemsInCurrentRow++;
            }

            if (itemsInCurrentRow > 0)
                container.AddNode(currentRow);

            container.AttachNode(this);
        }

        protected override unsafe void OnUpdate(AtkUnitBase* addon)
        {
            if (DService.Instance().KeyState[VirtualKey.ESCAPE])
            {
                Close();

                if (SystemMenu != null)
                    SystemMenu->Close(true);

                return;
            }

            if (!TaskHelper.TryGetTarget(out var th)) return;

            CraftOnceButton.IsEnabled     = Synthesis != null                 && !th.IsBusy;
            CraftMultipleButton.IsEnabled = CraftCountInput is { Value: > 0 } && Synthesis == null && !th.IsBusy;

            // 制作时不更新
            if (Synthesis == null)
            {
                if (TryGetCurrentRecipe(out var recipe, out _) && Result.RecipeID == recipe)
                    CraftCountInput.Max = SimpleCraftGetAmountUpperLimitDetour(nint.Zero, false);
                else
                    CraftCountInput.Max = 0;
            }
        }

        private void EnqueueActionSequence(TaskHelper th, List<uint> actions)
        {
            for (var index = 0; index < actions.Count; index++)
            {
                var x = actions[index];
                var i = index;
                th.Enqueue
                (() =>
                    {
                        if (DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]) return true;

                        ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(x)}");
                        return false;
                    }
                );
                th.Enqueue(() => Nodes[i].Alpha = 0.2f);
                th.Enqueue(() => !DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]);
            }
        }

        private void OnCraftLogMessage(uint logMessageID, LogMessageQueueItem item)
        {
            if (!CraftFailedLogMessages.Contains(logMessageID)) return;
            OnCraftingLoopFinished(0, true);
        }

        private void OnCraftingLoopFinished(int completedCount, bool isCraftFailed = false)
        {
            LogMessageManager.Instance().Unreg(OnCraftLogMessage);
            if (TaskHelper.TryGetTarget(out var th))
                th.Abort();

            CraftProgressText.IsVisible = false;
            currentCraftRound           = 0;
            totalCraftRounds            = 0;

            var message = isCraftFailed ? Lang.Get("OptimizedRecipeNote-Message-CraftFailed") : Lang.Get("OptimizedRecipeNote-Message-CraftComplete", completedCount);
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
            
            foreach (var node in Nodes)
                node.Alpha = 1;
        }
    }
}
