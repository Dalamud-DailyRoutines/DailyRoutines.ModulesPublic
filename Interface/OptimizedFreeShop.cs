using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.KamiToolKit.Addons;
using OmenTools.OmenService;
using AgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedFreeShop : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedFreeShopTitle"),
        Description = Lang.Get("OptimizedFreeShopDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private static readonly CompSig CheckItemBarterSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC ?? 0F B7 C2");
    private delegate byte CheckItemBarterDelegate
    (
        nint       checker,
        ushort     checkType,
        Character* character,
        nint       trade
    );
    private Hook<CheckItemBarterDelegate>? CheckItemBarterHook;
    
    private static readonly CompSig FreeShopCheckerSig = new
    (
        "48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? EB ?? 48 8D 1D ?? ?? ?? ?? 49 8B 4E"
    );
    private nint freeShopChecker;

    private OptimizedFreeShopAddon? addon;

    protected override void Init()
    {
        TaskHelper ??= new();

        freeShopChecker = FreeShopCheckerSig.GetStatic(3);
        
        CheckItemBarterHook ??= CheckItemBarterSig.GetHook<CheckItemBarterDelegate>(CheckItemBarterDetour);
        CheckItemBarterHook.Enable();

        addon ??= new(this)
        {
            InternalName = "DROptimizedFreeShop",
            Title        = Info.Title,
            Size         = new(230f, 128f),
        };
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;
    }

    private byte CheckItemBarterDetour
    (
        nint       checker,
        ushort     checkType,
        Character* character,
        nint       trade
    )
    {
        if (checker == freeShopChecker && checkType is >= 1000 and <= 1012)
            return 0;

        return CheckItemBarterHook.Original(checker, checkType, character, trade);
    }

    private void BatchClaim
    (
        List<(int Index, uint ItemID)> items
    )
    {
        TaskHelper.Abort();

        var agent = AgentFreeShop.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive()) return;

        var addonID = agent->AgentInterface.AddonId;

        foreach (var (index, itemID) in items)
        {
            if (LocalPlayerState.GetItemCount(itemID) > 0) continue;

            var requested = false;
            TaskHelper.Enqueue
            (() =>
                {
                    var currentAgent = AgentFreeShop.Instance();

                    if (currentAgent                         == null    ||
                        currentAgent->AgentInterface.AddonId != addonID ||
                        !currentAgent->AgentInterface.IsAgentActive())
                    {
                        TaskHelper.Abort();
                        return true;
                    }

                    if ((uint)index >= currentAgent->ItemCount || currentAgent->Items[index].ItemID != itemID)
                    {
                        TaskHelper.Abort();
                        return true;
                    }

                    if (currentAgent->Items[index].IsOwned || LocalPlayerState.GetItemCount(itemID) > 0)
                        return true;

                    if (requested) return false;
                    if (currentAgent->Items[index].IsUnavailable) return true;
                    if (currentAgent->IsInteractionBlocked || currentAgent->IsLoadingItems) return false;

                    requested = true;
                    AgentId.FreeShop.SendEvent(0, 0, index);
                    return false;
                }
            );
        }
    }

    internal class OptimizedFreeShopAddon
    (
        OptimizedFreeShop module
    )
        : AttachedAddon("FreeShop", AddonEvent.PostRefresh, AddonEvent.PostReceiveEvent)
    {
        private readonly Dictionary<uint, List<(int Index, uint ItemID)>>                             jobItems      = [];
        private readonly Dictionary<uint, (IconButtonNode Button, ResNode Background, ResNode Image)> jobHighlights = [];
        
        private VerticalListNode? jobLayout;
        private uint?             highlightedClassJobID;

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            jobItems.Clear();
            jobHighlights.Clear();

            jobLayout = new()
            {
                Position    = ContentStartPosition,
                ItemSpacing = 4f,
                Width       = ContentSize.X,
                FitContents = true
            };
            jobLayout.AttachNode(this);

            RefreshItems();
        }

        protected override void OnHostAddon
        (
            AddonEvent type,
            AddonArgs? args
        )
        {
            switch (type)
            {
                case AddonEvent.PostRefresh when IsAllocated:
                    RefreshItems();
                    break;
                case AddonEvent.PostReceiveEvent when args is AddonReceiveEventArgs receiveEventArgs &&
                                                      (AtkEventType)receiveEventArgs.AtkEventType is AtkEventType.ListItemHighlight or AtkEventType.ButtonClick:
                    UpdateHighlights();
                    break;
                case AddonEvent.PostClose:
                case AddonEvent.PreFinalize:
                    module.TaskHelper?.Abort();
                    break;
            }
        }

        protected override void OnFinalize
        (
            AtkUnitBase* addon
        )
        {
            jobItems.Clear();
            jobHighlights.Clear();
            
            highlightedClassJobID = null;
            jobLayout             = null;
            
            base.OnFinalize(addon);
        }

        private void RefreshItems()
        {
            if (jobLayout == null || !HostAddon->IsAddonAndNodesReady()) return;

            var host        = (AddonFreeShop*)HostAddon;
            var classJobs   = host->ClassJobIDs[..(int)host->ClassJobCount];
            var jobsChanged = jobItems.Count != classJobs.Length - 1;

            for (var i = 1; i < classJobs.Length; i++)
            {
                if (jobItems.TryGetValue(classJobs[i], out var items))
                    items.Clear();
                else
                {
                    jobsChanged = true;
                    jobItems.Add(classJobs[i], []);
                }
            }

            foreach (var classJobID in jobItems.Keys.ToArray())
            {
                if (!classJobs.Contains(classJobID))
                    jobItems.Remove(classJobID);
            }

            foreach (var item in host->Items[..(int)host->ItemCount])
            {
                if (!LuminaGetter.TryGetRow(item.ClassJobCategoryID, out ClassJobCategory category)) continue;

                foreach (var (classJobID, items) in jobItems)
                {
                    if (category.IsClassJobIn(classJobID))
                        items.Add((item.Index, item.ItemID));
                }
            }

            if (jobsChanged || jobHighlights.Count != jobItems.Count)
                RebuildButtons(classJobs);

            UpdateHighlights();
            SetWindowSize(Size.X, ContentStartPosition.Y + jobLayout.Height + 16f);
        }

        private void RebuildButtons
        (
            Span<uint> classJobs
        )
        {
            jobLayout.Clear();
            jobHighlights.Clear();
            highlightedClassJobID = null;
            HorizontalListNode? row      = null;
            var                 jobCount = 0;

            foreach (var classJobID in classJobs)
            {
                if (classJobID == 0 || !LuminaGetter.TryGetRow(classJobID, out ClassJob classJob)) continue;

                if (jobCount % 4 == 0)
                {
                    row = new()
                    {
                        ItemSpacing = 4f,
                        Size        = ContentSize with { Y = 48f },
                    };
                    jobLayout.AddNode(row);
                }

                var button = new IconButtonNode
                {
                    Size    = new(48f),
                    IconId  = classJobID + 62100,
                    OnClick = () => module.BatchClaim(jobItems[classJobID]),
                    TextTooltip = Lang.Get
                    (
                        "OptimizedFreeShop-BatchClaim",
                        new Dictionary<string, object>
                        {
                            ["classJob"] = classJob.Name
                        }
                    )
                };
                var background = AddHighlightTimeline
                (
                    button,
                    button.BackgroundNode,
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
                        .EndFrameSet(),
                    32f,
                    115f
                );

                var image = AddHighlightTimeline
                (
                    button,
                    button.ImageNode,
                    new TimelineBuilder()
                        .AddFrameSetWithFrame(1,  10, 1,  Vector2.Zero,        255, multiplyColor: new Vector3(100f))
                        .AddFrameSetWithFrame(11, 17, 11, Vector2.Zero,        255, multiplyColor: new Vector3(100f))
                        .AddFrameSetWithFrame(18, 26, 18, new Vector2(0f, 1f), 255, multiplyColor: new Vector3(100f))
                        .AddFrameSetWithFrame(27, 36, 27, Vector2.Zero,        153, multiplyColor: new Vector3(80f))
                        .AddFrameSetWithFrame(37, 46, 37, Vector2.Zero,        255, multiplyColor: new Vector3(100f))
                        .AddFrameSetWithFrame(47, 53, 47, Vector2.Zero,        255, multiplyColor: new Vector3(100f)),
                    20f,
                    110f
                );
                jobHighlights.Add(classJobID, (button, background, image));
                row.AddNode(button);
                jobCount++;
            }
        }

        private static ResNode AddHighlightTimeline
        (
            IconButtonNode  button,
            NodeBase        content,
            TimelineBuilder interactionTimeline,
            float           addColor,
            float           multiplyColor
        )
        {
            var position = content.Position;
            var animation = new ResNode
            {
                Size = button.Size
            };
            content.DetachNode();
            animation.AttachNode(button);
            content.AttachNode(animation);
            animation.AddTimeline
            (
                interactionTimeline
                    .BeginFrameSet(HIGHLIGHT_ON_START_FRAME, HIGHLIGHT_OFF_END_FRAME)
                    .AddLabelPair(HIGHLIGHT_ON_START_FRAME,  HIGHLIGHT_ON_END_FRAME,  HIGHLIGHT_ON_LABEL)
                    .AddLabelPair(HIGHLIGHT_OFF_START_FRAME, HIGHLIGHT_OFF_END_FRAME, HIGHLIGHT_OFF_LABEL)
                    .EndFrameSet()
                    .Build()
            );
            content.AddTimeline
            (
                new TimelineBuilder()
                    .BeginFrameSet(HIGHLIGHT_ON_START_FRAME, HIGHLIGHT_ON_END_FRAME)
                    .AddFrame(HIGHLIGHT_ON_START_FRAME, position, addColor: Vector3.Zero,          multiplyColor: new Vector3(100f))
                    .AddFrame(HIGHLIGHT_ON_END_FRAME,   position, addColor: new Vector3(addColor), multiplyColor: new Vector3(multiplyColor))
                    .EndFrameSet()
                    .BeginFrameSet(HIGHLIGHT_OFF_START_FRAME, HIGHLIGHT_OFF_END_FRAME)
                    .AddFrame(HIGHLIGHT_OFF_START_FRAME, position, addColor: new Vector3(addColor), multiplyColor: new Vector3(multiplyColor))
                    .AddFrame(HIGHLIGHT_OFF_END_FRAME,   position, addColor: Vector3.Zero,          multiplyColor: new Vector3(100f))
                    .EndFrameSet()
                    .Build()
            );
            return animation;
        }

        private void UpdateHighlights()
        {
            if (!HostAddon->IsAddonAndNodesReady()) return;

            var selectedClassJobID = ((AddonFreeShop*)HostAddon)->SelectedClassJobID;
            if (highlightedClassJobID == selectedClassJobID) return;

            foreach (var (classJobID, highlight) in jobHighlights)
            {
                var isHighlighted  = selectedClassJobID    == 0 || selectedClassJobID    == classJobID;
                var wasHighlighted = highlightedClassJobID == 0 || highlightedClassJobID == classJobID;
                if (highlightedClassJobID.HasValue && isHighlighted == wasHighlighted) continue;

                var startFrame = isHighlighted ?
                                     HIGHLIGHT_ON_START_FRAME :
                                     HIGHLIGHT_OFF_START_FRAME;
                var background = highlight.Button.BackgroundNode;
                var image      = highlight.Button.ImageNode;
                background.Timeline.UpdateKeyFrame
                (
                    startFrame,
                    KeyFrameGroupType.Tint,
                    addColor: background.AddColor           * 255f,
                    multiplyColor: background.MultiplyColor * 100f
                );
                image.Timeline.UpdateKeyFrame
                (
                    startFrame,
                    KeyFrameGroupType.Tint,
                    addColor: image.AddColor           * 255f,
                    multiplyColor: image.MultiplyColor * 100f
                );

                var labelID = isHighlighted ?
                                  HIGHLIGHT_ON_LABEL :
                                  HIGHLIGHT_OFF_LABEL;
                highlight.Background.Timeline.PlayAnimation(labelID);
                highlight.Image.Timeline.PlayAnimation(labelID);
            }

            highlightedClassJobID = selectedClassJobID;
        }

        #region 常量

        private const int HIGHLIGHT_ON_LABEL        = 101;
        private const int HIGHLIGHT_OFF_LABEL       = 102;
        private const int HIGHLIGHT_ON_START_FRAME  = 201;
        private const int HIGHLIGHT_ON_END_FRAME    = 210;
        private const int HIGHLIGHT_OFF_START_FRAME = 211;
        private const int HIGHLIGHT_OFF_END_FRAME   = 220;

        #endregion
    }
}
