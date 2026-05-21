using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;

namespace DailyRoutines.ModulesPublic.OptimizedRecipeNote;

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

        public TextButtonNode ExecuteButton { get; private set; }

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
                        Size         = new(500f, 160f + (50f                                                                           * (rowCount - 1)))
                    };
                    Addon.Open();
                },
                TimeSpan.FromMilliseconds(isAddonExisted ? 500 : 0)
            ).ContinueWith(_ => OpenAddonTask = null);
        }

        protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            if (Result.Actions.Count == 0) return;

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

            var operationRow = new HorizontalFlexNode
            {
                IsVisible = true,
                Position  = new(8, 65),
                Size      = new(0, 44)
            };

            ExecuteButton = new TextButtonNode
            {
                IsVisible = true,
                Size      = new(100, 24),
                String    = Lang.Get("Execute"),
                OnClick = () =>
                {
                    if (Synthesis == null) return;
                    if (Result.Actions is not { Count: > 0 } actions) return;
                    if (!TaskHelper.TryGetTarget(out var taskHelper)) return;

                    for (var index = 0; index < actions.Count; index++)
                    {
                        var x = actions[index];
                        var i = index;
                        taskHelper.Enqueue
                        (() =>
                            {
                                if (DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]) return true;

                                ChatManager.Instance().SendMessage($"/ac {LuminaWrapper.GetActionName(x)}");
                                return false;
                            }
                        );
                        taskHelper.Enqueue(() => Nodes[i].Alpha = 0.2f);
                        taskHelper.Enqueue(() => !DService.Instance().Condition[ConditionFlag.ExecutingCraftingAction]);
                    }
                }
            };
            operationRow.Width += ExecuteButton.Width;
            operationRow.AddNode(ExecuteButton);

            operationRow.Width += 4;
            operationRow.AddDummy(4);

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
                operationRow.Width += copyMacroButton.Width;
                operationRow.AddNode(copyMacroButton);

                operationRow.Width += 4;
                operationRow.AddDummy(4);
            }

            operationRow.AttachNode(this);

            var container = new VerticalListNode
            {
                IsVisible = true,
                Position  = new(12, 97),
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
                        (TaskHelper.TryGetTarget(out var taskHelper) && taskHelper.IsBusy))
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

            if (ExecuteButton != null && TaskHelper.TryGetTarget(out var taskHelper))
                ExecuteButton.IsEnabled = Synthesis != null && !taskHelper.IsBusy;
        }
    }
}
