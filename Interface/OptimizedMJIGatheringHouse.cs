using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.KamiToolKit.Nodes;
using OmenTools.KamiToolKit.Addons;
using OmenTools.KamiToolKit.Styles;
using OmenTools.KamiToolKit.Styles.Island;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using MJIGranaryState = FFXIVClientStructs.Expansion.FFXIV.Client.Game.MJI.MJIGranaryState;
using GranariesState = FFXIVClientStructs.Expansion.FFXIV.Client.Game.MJI.MJIGranariesState;
using GatheringHouseAgent = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMJIGatheringHouse;

namespace DailyRoutines.ModulesPublic;

internal unsafe class OptimizedMJIGatheringHouse : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMJIGatheringHouseTitle"),
        Description = Lang.Get("OptimizedMJIGatheringHouseDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedMJIGatheringHouse/preview-2.webp",
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedMJIGatheringHouse/preview-1.webp"
        ]
    };

    public override string ModuleName => nameof(OptimizedMJIGatheringHouse);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private NativeListController listController = null!;

    private Hook<GatheringHouseAgent.Delegates.RefreshExpeditions> RefreshExpeditionsHook;

    private readonly Dictionary<uint, DestinationNodes> itemNodes = [];

    private GatheringHousePanel panel = null!;

    private bool dispatchPending;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        panel = new(this)
        {
            InternalName = "DROptimizedMJIGatheringHouse",
            Title        = Info.Title,
            Size         = new(400, 300),
            Style        = new IslandUIStyle()
        };

        RefreshExpeditionsHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(GatheringHouseAgent.MemberFunctionPointers),
            nameof(GatheringHouseAgent.RefreshExpeditions),
            (GatheringHouseAgent.Delegates.RefreshExpeditions)RefreshExpeditionsDetour
        );
        RefreshExpeditionsHook.Enable();
        listController = new()
        {
            AddonName           = "MJISearchArea",
            GetPopulatorNode    = addon => addon->GetComponentListById(4)->GetComponentItemRendererById(6),
            ShouldModifyElement = (_, item) => item.ItemInfo != null && item.GetNumber(1) == 1,
            UpdateElement       = UpdateDestination,
            ResetElement        = (_, item) => ResetDestination(item.TryGetListItemRenderer())
        };
        listController.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MJISearchArea", OnSearchAreaFinalize);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnSearchAreaFinalize);
        panel.Dispose();
        dispatchPending = false;

        if (MJISearchArea != null)
        {
            var list = MJISearchArea->GetComponentListById(4);

            if (list != null)
            {
                for (var index = 0; index < list->ListLength; index++)
                    ResetDestination(list->GetItemRenderer(index));
            }
        }

        listController.Dispose();
        itemNodes.Clear();
    }

    private void OnSearchAreaFinalize(AddonEvent type, AddonArgs args) => itemNodes.Clear();

    private void RefreshExpeditionsDetour(GatheringHouseAgent* agent)
    {
        if (!dispatchPending)
        {
            RefreshExpeditionsHook.Original(agent);
            return;
        }

        dispatchPending = false;

        if (agent                 == null ||
            agent->Data           == null ||
            agent->Manager        == null ||
            agent->GranariesState == null)
            return;

        foreach (var (index, destination) in GetRecommendations((AgentMJIGatheringHouse*)agent))
        {
            var granary = (MJIGranaryState*)agent->GranariesState + index;
            if (granary->RemainingDays      == 7 &&
                granary->ActiveExpeditionId == destination)
                continue;

            agent->CurGranaryIndex         = index;
            agent->CurSelectedExpeditionId = destination;
            agent->CurSelectedDays         = 7;
            MJICommand.AssignGranary(index, destination, 7);
        }

    }

    private void ClaimResources()
    {
        var agent = AgentMJIGatheringHouse.Instance();
        if (agent == null                 ||
            !agent->IsAgentActive()       ||
            agent->Data == null           ||
            !agent->Data->Initialized     ||
            agent->Manager        == null ||
            agent->GranariesState == null)
            return;

        for (byte index = 0; index < 2; index++)
        {
            if (!IsGranaryAvailable(agent, index))
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;

            if (!HasResources(granary) ||
                !CanClaim(agent, granary))
                continue;

            agent->CurGranaryIndex = index;
            agent->GranariesState->CollectResources(index);
        }

    }

    private void DispatchExpeditions()
    {
        var agent = AgentMJIGatheringHouse.Instance();
        if (dispatchPending               ||
            agent == null                 ||
            !agent->IsAgentActive()       ||
            agent->Data == null           ||
            !agent->Data->Initialized     ||
            agent->Manager        == null ||
            agent->GranariesState == null)
            return;

        for (byte index = 0; index < 2; index++)
        {
            if (!IsGranaryAvailable(agent, index))
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;
            if (HasResources(granary))
                continue;

            dispatchPending        = true;
            agent->CurGranaryIndex = index;
            MJICommand.ViewGranaryDestinations(index);
            return;
        }

    }

    private static bool IsGranaryAvailable(AgentMJIGatheringHouse* agent, int index) =>
        ((GranariesState*)agent->GranariesState)->IsGranaryAvailable((byte)index);

    private static bool HasResources(MJIGranaryState* granary)
    {
        if (granary->RareResourceCount > 0)
            return true;

        foreach (var amount in granary->NormalResourceAmounts)
        {
            if (amount != 0)
                return true;
        }

        return false;
    }

    private bool CanClaim(AgentMJIGatheringHouse* agent, MJIGranaryState* granary)
    {
        if (config.StopOnRareExceed &&
            WouldExceed(agent, granary->RareResourcePouchId, granary->RareResourceCount, true))
            return false;

        if (!config.StopOnNormalExceed)
            return true;

        for (var index = 0; index < granary->NormalResourceAmounts.Length; index++)
            if (WouldExceed(agent, granary->NormalResourcePouchIds[index], granary->NormalResourceAmounts[index], false))
                return false;

        return true;
    }

    private static bool WouldExceed(AgentMJIGatheringHouse* agent, ushort pouchID, int amount, bool rare)
    {
        if (amount <= 0)
            return false;

        foreach (var resource in agent->Data->Resources)
        {
            if (resource.PouchId != pouchID)
                continue;

            var currency = CurrencyManager.Instance();
            if (currency->GetItemCount(resource.ItemId) + amount <= currency->GetItemMaxCount(resource.ItemId))
                return false;

            var key = rare ?
                          "OptimizedMJIGatheringHouse-RareExceed" :
                          "OptimizedMJIGatheringHouse-NormalExceed";
            var message = Lang.Get
            (
                key,
                new Dictionary<string, object>
                {
                    ["name"] = LuminaGetter.GetRow<Item>(resource.ItemId)?.Name.ToString()
                }
            );
            NotifyHelper.Instance().Chat(message);
            NotifyHelper.Instance().TrayWarning(message);
            return true;
        }

        return false;
    }

    private static List<(byte Index, byte Destination)> GetRecommendations(AgentMJIGatheringHouse* agent)
    {
        List<(byte Destination, uint Count)> destinations = [];
        var                                  currency     = CurrencyManager.Instance();

        for (var index = 0; index < agent->Data->Expeditions.Count; index++)
        {
            var expedition = agent->Data->Expeditions.First + index;
            if (expedition->RareItemId == 0 ||
                !agent->IsExpeditionUnlocked(expedition))
                continue;

            destinations.Add((expedition->ExpeditionId, currency->GetItemCount(expedition->RareItemId)));
        }

        destinations.Sort
        ((left, right) =>
            {
                var comparison = left.Count.CompareTo(right.Count);
                return comparison != 0 ?
                           comparison :
                           left.Destination.CompareTo(right.Destination);
            }
        );

        List<(byte Index, byte Destination)> recommendations = [];
        var                                  balance         = currency->GetItemCount(COWRIE_ITEM_ID);
        var                                  rank            = 0;

        for (byte index = 0; index < 2; index++)
        {
            if (!IsGranaryAvailable(agent, index))
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;
            if (HasResources(granary) ||
                rank >= destinations.Count)
                continue;

            var cost = (uint)(DAILY_EXPEDITION_COST * (7 - Math.Min((int)granary->RemainingDays, 7)));
            if (balance < cost)
                continue;

            balance -= cost;
            recommendations.Add((index, destinations[rank++].Destination));
        }

        return recommendations;
    }

    private void UpdateDestination(AtkUnitBase* addon, ListItemData item)
    {
        var agent = AgentMJIGatheringHouse.Instance();
        if (agent                 == null ||
            agent->Data           == null ||
            agent->Manager        == null ||
            agent->GranariesState == null)
            return;

        var valueIndex = 2 * ((int)item.GetNumber(2) + 1);
        if ((uint)valueIndex >= addon->AtkValuesCount)
            return;

        var destination = addon->AtkValues[valueIndex].UInt;

        foreach (var expedition in agent->Data->Expeditions)
        {
            if (expedition.ExpeditionId != destination)
                continue;

            var renderer = item.TryGetListItemRenderer();

            if (!itemNodes.TryGetValue(item.NodeId, out var nodes))
            {
                const float HEIGHT = 18f;

                nodes = new()
                {
                    Layout = new()
                    {
                        Height            = HEIGHT,
                        Position          = new(11, 44),
                        FitToContentWidth = true,
                        Alignment         = HorizontalListAnchor.Left,
                        ItemSpacing       = 4
                    },
                    Icon = new()
                    {
                        Size = new(HEIGHT)
                    },
                    Text = new()
                    {
                        Height             = HEIGHT,
                        AlignmentType      = AlignmentType.Left,
                        TextFlags          = TextFlags.AutoAdjustNodeSize | TextFlags.Edge,
                        TextColorID        = 50,
                        TextOutlineColorID = 51,
                        FontSize           = 12
                    }
                };

                nodes.Layout.AddNode(nodes.Icon);
                nodes.Layout.AddNode(nodes.Text);
                nodes.Layout.AttachNode(renderer->OwnerNode);

                listController.AddNode(item, nodes.Icon);
                listController.AddNode(item, nodes.Text);
                listController.AddNode(item, nodes.Layout);

                itemNodes.Add(item.NodeId, nodes);
            }

            nodes.Icon.ItemID    = expedition.RareItemId;
            nodes.Icon.IsVisible = true;
            nodes.Text.IsVisible = true;
            nodes.Text.String    = $"x{CurrencyManager.Instance()->GetItemCount(expedition.RareItemId):N0}";
            nodes.Layout.RecalculateLayout();
            return;
        }
    }

    private void ResetDestination(AtkComponentListItemRenderer* renderer)
    {
        if (renderer == null ||
            !itemNodes.TryGetValue(renderer->OwnerNode->NodeId, out var nodes))
            return;

        nodes.Icon.IsVisible = false;
        nodes.Text.IsVisible = false;
    }

    private sealed class GatheringHousePanel
    (
        OptimizedMJIGatheringHouse module
    ) : AttachedAddon("MJIGatheringHouse")
    {
        protected override Vector2 PositionOffset => new(-12, 0);

        protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
        {
            var layout = new VerticalListNode
            {
                Position    = ContentStartPosition,
                Width       = ContentSize.X,
                FitContents = true,
                FitWidth    = true,
                ItemSpacing = 4
            };
            layout.AttachNode(this);

            var claim = Style.CreateButton();
            claim.Size    = new(layout.Width, 36);
            claim.String  = Lang.Get("OptimizedMJIGatheringHouse-Claim");
            claim.OnClick = module.ClaimResources;
            layout.AddNode(claim);

            var dispatch = Style.CreateButton();
            dispatch.Size    = new(layout.Width, 36);
            dispatch.String  = Lang.Get("OptimizedMJIGatheringHouse-Dispatch");
            dispatch.OnClick = module.DispatchExpeditions;
            layout.AddNode(dispatch);

            layout.AddDummy();

            layout.AddNode(Style.CreateSeparator());

            layout.AddDummy();

            var heading = Style.CreateText();
            heading.Width     = layout.Width;
            heading.FontSize  = 16;
            heading.String    = Lang.Get("OptimizedMJIGatheringHouse-StopOnExceed");
            heading.TextFlags = TextFlags.WordWrap | TextFlags.MultiLine;
            heading.Height    = 28;
            layout.AddNode(heading);

            for (var index = 0; index < 2; index++)
            {
                var rare = index == 1;
                var row = new HorizontalListNode
                {
                    Size             = new(layout.Width, 32),
                    Alignment        = HorizontalListAnchor.Left,
                    FirstItemSpacing = 20
                };
                var checkbox = new CheckboxNode
                {
                    IsChecked = rare ?
                                    module.config.StopOnRareExceed :
                                    module.config.StopOnNormalExceed
                };
                Style.ApplyTextStyle(checkbox.Label, UITextRole.Body);
                checkbox.String = LuminaWrapper.GetAddonTextSeString
                (
                    rare ?
                        RARE_RESOURCE_TEXT_ID :
                        NORMAL_RESOURCE_TEXT_ID
                );
                checkbox.Size            = new(row.Width - 20, 32);
                checkbox.Label.Width     = checkbox.Width - checkbox.Height;
                checkbox.Label.TextFlags = TextFlags.WordWrap | TextFlags.MultiLine;
                checkbox.Label.Height    = 32;
                checkbox.OnClick = value =>
                {
                    if (rare)
                        module.config.StopOnRareExceed = value;
                    else
                        module.config.StopOnNormalExceed = value;

                    module.config.Save(module);
                };
                row.AddNode(checkbox);
                layout.AddNode(row);
            }

            SetWindowSize(Size.X, Math.Max(Style.MinimumSize.Y, layout.Height + Size.Y - ContentSize.Y));
        }

        protected override void OnHostAddon(AddonEvent type, AddonArgs? args)
        {
            if (type == AddonEvent.PreFinalize)
                module.dispatchPending = false;
        }
    }

    private sealed class DestinationNodes
    {
        public HorizontalListNode Layout;
        public ItemIconNode       Icon;
        public TextNode           Text;
    }

    private class Config : ModuleConfig
    {
        public bool StopOnNormalExceed;
        public bool StopOnRareExceed = true;
    }

    #region 常量

    private const uint COWRIE_ITEM_ID          = 37549;
    private const int  DAILY_EXPEDITION_COST   = 50;
    private const uint NORMAL_RESOURCE_TEXT_ID = 14334;
    private const uint RARE_RESOURCE_TEXT_ID   = 14335;

    #endregion
}
