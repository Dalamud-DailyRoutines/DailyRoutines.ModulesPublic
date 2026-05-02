using System.Numerics;
using DailyRoutines.Common.KamiToolKit.Addons;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedFreeShop : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("OptimizedFreeShopTitle"),
        Description         = Lang.Get("OptimizedFreeShopDescription"),
        Category            = ModuleCategory.UIOptimization,
        ModulesPrerequisite = ["AutoClaimItemIgnoringMismatchJobAndLevel"]
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Config config = null!;

    private OptimizedFreeShopAddon? addon;

    private TaskHelper? clickYesnoHelper;

    protected override void Init()
    {
        TaskHelper       ??= new();
        clickYesnoHelper ??= new();

        config = Config.Load(this) ?? new();

        addon ??= new(this)
        {
            InternalName          = "DROptimizedFreeShop",
            Title                 = Info.Title,
            Size                  = new(220f, 128f),
            RememberClosePosition = false
        };

        DService.Instance().AgentLifecycle.RegisterListener(AgentEvent.PostReceiveEvent, Dalamud.Game.Agent.AgentId.FreeShop, OnAgent);

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FreeShop", OnAddon);
    }

    protected override void Uninit()
    {
        DService.Instance().AgentLifecycle.UnregisterListener(OnAgent);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);

        addon?.Dispose();
        addon = null;

        clickYesnoHelper = null;
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        if (!config.IsEnabled)
            return;

        var receiveEventArgs = args as AgentReceiveEventArgs;
        var atkValues        = (AtkValue*)receiveEventArgs.AtkValues;

        if (receiveEventArgs.EventKind == 0 && atkValues[0].Int == 0)
        {
            clickYesnoHelper.Abort();
            clickYesnoHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());
        }
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PreFinalize)
            clickYesnoHelper?.Abort();
    }

    internal bool IsFastClaimEnabled
    {
        get => config.IsEnabled;
        set
        {
            config.IsEnabled = value;
            config.Save(this);
        }
    }

    internal void BatchClaim(List<(int Index, uint ID)> itemData)
    {
        TaskHelper.Abort();

        var anythingNotInBag = false;

        foreach (var (index, itemID) in itemData)
        {
            if (LocalPlayerState.GetItemCount(itemID) > 0) continue;

            anythingNotInBag = true;

            TaskHelper.Enqueue(() => AgentId.FreeShop.SendEvent(0, 0, index));
            TaskHelper.DelayNext(10);
        }

        if (anythingNotInBag)
            TaskHelper.Enqueue(() => BatchClaim(itemData));
    }

    private class Config : ModuleConfig
    {
        public bool IsEnabled = true;
    }

    internal class OptimizedFreeShopAddon
    (
        OptimizedFreeShop module
    ) : AttachedAddon("FreeShop")
    {
        private readonly Dictionary<uint, IconButtonNode> jobButtons = [];

        private uint selectedClassJob;

        protected override AttachedAddonPosition AttachPosition =>
            AttachedAddonPosition.LeftTop;

        protected override void OnDraw(AtkUnitBase* addon)
        {
            if (!HostAddon->IsAddonAndNodesReady()) return;

            var dropDownComponent = HostAddon->GetComponentByNodeId(3);
            if (dropDownComponent == null) return;

            var checkBoxComponent = dropDownComponent->UldManager.SearchNodeById(2)->GetAsAtkComponentCheckBox();
            if (checkBoxComponent == null) return;

            var textNode = checkBoxComponent->ButtonTextNode;
            if (textNode == null) return;

            var selectedItemText = new ReadOnlySeString(textNode->NodeText);

            var firstPayload = selectedItemText.First();

            // 没法拿到职业图标, 肯定不是
            if (firstPayload.Type            != ReadOnlySePayloadType.Macro ||
                firstPayload.MacroCode       != MacroCode.Icon              ||
                firstPayload.ExpressionCount != 1                           ||
                !firstPayload.TryGetExpression(out var expr)                ||
                !expr.TryGetUInt(out var bitmapFontIconID))
            {
                UpdateSelectedJob(0);
                return;
            }

            var classJob = ((BitmapFontIcon)bitmapFontIconID).ToClassJob();
            UpdateSelectedJob(classJob.RowId);
        }

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            jobButtons.Clear();
            selectedClassJob = 0;

            if (WindowNode is WindowNode windowNode)
                windowNode.CloseButtonNode.IsVisible = false;

            var verticalLayout = new VerticalListNode
            {
                IsVisible   = true,
                ItemSpacing = 4f,
                Position    = ContentStartPosition,
                Size        = ContentSize with { Y = 28f }
            };

            var enabledNode = new CheckboxNode
            {
                Size      = ContentSize with { Y = 28f },
                IsVisible = true,
                IsChecked = module.IsFastClaimEnabled,
                IsEnabled = true,
                String    = Lang.Get("OptimizedFreeShop-FastClaim"),
                OnClick   = isChecked => module.IsFastClaimEnabled = isChecked
            };
            verticalLayout.AddNode(enabledNode); // 第一行

            var jobLineNode = new HorizontalListNode
            {
                IsVisible = true,
                Size      = ContentSize with { Y = 36f },
                Position  = ContentStartPosition.WithY(0)
            };

            var jobCount  = 0;
            var lineCount = 0;

            foreach (var (classJobCategory, items) in GetClaimItems())
            {
                if (!LuminaGetter.TryGetRow(classJobCategory, out ClassJobCategory categoryData)) continue;
                if (LuminaGetter.Get<ClassJob>()
                                .FirstOrDefault(x => categoryData.IsClassJobIn(x.RowId))
                    is not { RowId: > 0 } classJobData)
                    continue;

                if (jobCount >= 5)
                {
                    verticalLayout.AddNode(jobLineNode);
                    jobLineNode = new()
                    {
                        IsVisible = true,
                        Size      = ContentSize with { Y = 36f },
                        Position  = ContentStartPosition.WithY(0)
                    };

                    jobCount = 0;
                    lineCount++;
                }

                var jobButton = new IconButtonNode
                {
                    Size        = new(36f),
                    IsVisible   = true,
                    IsEnabled   = true,
                    IconId      = classJobData.RowId + 62100,
                    OnClick     = () => module.BatchClaim(items),
                    TextTooltip = $"{Lang.Get("OptimizedFreeShop-BatchClaim")}: {classJobData.Name}"
                };

                AddHighlightTimeline(jobButton);
                PlayHighlight(jobButton, false);
                jobButtons[classJobData.RowId] = jobButton;

                jobLineNode.AddNode(jobButton);
                jobLineNode.AddDummy(4f);
                jobCount++;
            }

            if (jobCount > 0)
            {
                verticalLayout.AddNode(jobLineNode);
                lineCount++;
            }

            SetWindowSize(Size.X, 128f + Math.Max(lineCount - 1, 0) * 40f);

            verticalLayout.AttachNode(this);
        }

        private static Dictionary<uint, List<(int Index, uint ID)>> GetClaimItems()
        {
            var itemCount = FreeShop->AtkValues[76].UInt;
            var itemIDs   = new Dictionary<uint, List<(int Index, uint ID)>>();

            for (var i = 0; i < itemCount; i++)
            {
                var itemID = FreeShop->AtkValues[138 + i].UInt;
                if (!LuminaGetter.TryGetRow(itemID, out Item itemData)) continue;

                itemIDs.TryAdd(itemData.ClassJobCategory.RowId, []);
                itemIDs[itemData.ClassJobCategory.RowId].Add((i, itemID));
            }

            return itemIDs;
        }

        private void UpdateSelectedJob(uint classJobID)
        {
            if (classJobID == 0)
            {
                if (selectedClassJob != 0)
                {
                    foreach (var (_, buttonNode) in jobButtons)
                        PlayHighlight(buttonNode, true);
                }

                selectedClassJob = 0;
                return;
            }

            if (selectedClassJob == classJobID)
                return;

            if (jobButtons.TryGetValue(selectedClassJob, out var previousButton))
                PlayHighlight(previousButton, false);

            selectedClassJob = classJobID;

            if (jobButtons.TryGetValue(selectedClassJob, out var currentButton))
                PlayHighlight(currentButton, true);
        }

        private static void AddHighlightTimeline(IconButtonNode button)
        {
            button.AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(1, 220)
                    .AddLabelPair(1,   10,  1)
                    .AddLabelPair(11,  17,  2)
                    .AddLabelPair(18,  26,  3)
                    .AddLabelPair(27,  36,  7)
                    .AddLabelPair(37,  46,  6)
                    .AddLabelPair(47,  53,  4)
                    .AddLabelPair(201, 210, 101)
                    .AddLabelPair(211, 220, 102)
                    .EndFrameSet()
                    .Build()
            );

            button.BackgroundNode.AddTimeline
            (
                new TimelineBuilder()
                    .AddFrameSetWithFrame(1, 10, 1, Vector2.Zero, 255, multiplyColor: new Vector3(100f))
                    .BeginFrameSet(11, 17)
                    .AddFrame(11, Vector2.Zero, 255, multiplyColor: new Vector3(100f))
                    .AddFrame(13, Vector2.Zero, 255, multiplyColor: new Vector3(100f), addColor: new Vector3(16f))
                    .EndFrameSet()
                    .AddFrameSetWithFrame(18, 26, 18, new Vector2(0f, 1f), 255, new Vector3(16f))
                    .AddFrameSetWithFrame(27, 36, 27, Vector2.Zero,        178, multiplyColor: new Vector3(50f))
                    .AddFrameSetWithFrame(37, 46, 37, Vector2.Zero,        255, multiplyColor: new Vector3(100f), addColor: new Vector3(16f))
                    .BeginFrameSet(47, 53)
                    .AddFrame(47, Vector2.Zero, 255, multiplyColor: new Vector3(100f), addColor: new Vector3(16f))
                    .AddFrame(53, Vector2.Zero, 255, multiplyColor: new Vector3(100f))
                    .EndFrameSet()
                    .BeginFrameSet(201, 210)
                    .AddFrame(201, addColor: Vector3.Zero,     multiplyColor: new Vector3(100f))
                    .AddFrame(210, addColor: new Vector3(32f), multiplyColor: new Vector3(115f))
                    .EndFrameSet()
                    .BeginFrameSet(211, 220)
                    .AddFrame(211, addColor: new Vector3(32f), multiplyColor: new Vector3(115f))
                    .AddFrame(220, addColor: Vector3.Zero,     multiplyColor: new Vector3(100f))
                    .EndFrameSet()
                    .Build()
            );

            button.ImageNode.AddTimeline
            (
                new TimelineBuilder()
                    .AddFrameSetWithFrame(1,  10, 1,  new Vector2(8f),     255, multiplyColor: new Vector3(100f))
                    .AddFrameSetWithFrame(11, 17, 11, new Vector2(8f),     255, multiplyColor: new Vector3(100f))
                    .AddFrameSetWithFrame(18, 26, 18, new Vector2(8f, 9f), 255, multiplyColor: new Vector3(100f))
                    .AddFrameSetWithFrame(27, 36, 27, new Vector2(8f),     153, multiplyColor: new Vector3(80f))
                    .AddFrameSetWithFrame(37, 46, 37, new Vector2(8f),     255, multiplyColor: new Vector3(100f))
                    .AddFrameSetWithFrame(47, 53, 47, new Vector2(8f),     255, multiplyColor: new Vector3(100f))
                    .BeginFrameSet(201, 210)
                    .AddFrame(201, new Vector2(8f), addColor: Vector3.Zero,     multiplyColor: new Vector3(100f))
                    .AddFrame(210, new Vector2(8f), addColor: new Vector3(20f), multiplyColor: new Vector3(110f))
                    .EndFrameSet()
                    .BeginFrameSet(211, 220)
                    .AddFrame(211, new Vector2(8f), addColor: new Vector3(20f), multiplyColor: new Vector3(110f))
                    .AddFrame(220, new Vector2(8f), addColor: Vector3.Zero,     multiplyColor: new Vector3(100f))
                    .EndFrameSet()
                    .Build()
            );
        }

        private static void PlayHighlight(IconButtonNode button, bool isSelected)
        {
            var labelID = isSelected ? 101 : 102;

            button.Timeline?.PlayAnimation(labelID);
        }
    }
}
