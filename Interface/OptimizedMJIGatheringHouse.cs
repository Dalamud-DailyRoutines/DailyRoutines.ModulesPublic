using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Controllers;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Nodes;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using MJIGranaryState = FFXIVClientStructs.Expansion.FFXIV.Client.Game.MJI.MJIGranaryState;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedMJIGatheringHouse : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMJIGatheringHouseTitle"),
        Description = Lang.Get("OptimizedMJIGatheringHouseDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedMJIGatheringHouse/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    private NativeListController listController = null!;

    private readonly Dictionary<uint, DestinationNodes> itemNodes = [];

    private readonly byte[] dispatchedDestinations = new byte[2];

    private byte claimedGranaries;
    private byte dispatchedGranaries;
    private bool stopped;
    private bool processing;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        listController = new()
        {
            AddonName           = "MJISearchArea",
            GetPopulatorNode    = addon => addon->GetComponentListById(4)->GetComponentItemRendererById(6),
            ShouldModifyElement = (_, item) => item.ItemInfo != null && item.GetNumber(1) == 1,
            UpdateElement       = UpdateDestination,
            ResetElement        = (_, item) => ResetDestination(item.TryGetListItemRenderer())
        };
        listController.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "MJIGatheringHouse", OnGatheringHouse);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "MJIGatheringHouse", OnGatheringHouse);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MJIGatheringHouse", OnGatheringHouse);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MJISearchArea",     OnSearchAreaFinalize);

        if (MJIGatheringHouse->IsAddonAndNodesReady())
            OnGatheringHouse(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnGatheringHouse, OnSearchAreaFinalize);

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

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OptimizedMJIGatheringHouse-AutoClaim"), ref config.AutoClaim))
            config.Save(this);

        if (config.AutoClaim)
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox(Lang.Get("OptimizedMJIGatheringHouse-StopOnNormalExceed"), ref config.StopOnNormalExceed))
                config.Save(this);

            if (ImGui.Checkbox(Lang.Get("OptimizedMJIGatheringHouse-StopOnRareExceed"), ref config.StopOnRareExceed))
                config.Save(this);
        }

        if (ImGui.Checkbox(Lang.Get("OptimizedMJIGatheringHouse-AutoDispatch"), ref config.AutoDispatch))
            config.Save(this);
    }

    private void OnGatheringHouse(AddonEvent type, AddonArgs args)
    {
        if (type is AddonEvent.PostSetup or AddonEvent.PreFinalize)
        {
            claimedGranaries    = 0;
            dispatchedGranaries = 0;
            stopped             = type == AddonEvent.PreFinalize;
        }

        if (stopped ||
            processing)
            return;

        processing = true;

        try
        {
            ProcessAutomation();
        }
        finally
        {
            processing = false;
        }
    }

    private void OnSearchAreaFinalize(AddonEvent type, AddonArgs args) => itemNodes.Clear();

    private void ProcessAutomation()
    {
        var agent = AgentMJIGatheringHouse.Instance();
        if (agent == null                              ||
            !agent->IsAgentActive()                    ||
            agent->Data == null                        ||
            !agent->Data->Initialized                  ||
            agent->Manager                     == null ||
            agent->GranariesState              == null ||
            agent->ConfirmAddonHandle          != 0    ||
            agent->SelectExpeditionAddonHandle != 0)
            return;

        if (config.AutoClaim)
        {
            for (byte index = 0; index < 2; index++)
            {
                if (!IsGranaryAvailable(agent, index))
                    continue;

                var granary = (MJIGranaryState*)agent->GranariesState + index;
                if (!HasResources(granary))
                    continue;

                if ((claimedGranaries & (1 << index)) != 0)
                    return;

                if (!CanClaim(agent, granary))
                {
                    stopped = true;
                    return;
                }

                claimedGranaries       |= (byte)(1 << index);
                agent->CurGranaryIndex =  index;
                AgentId.MJIGatheringHouse.SendEvent(4, 0);
                return;
            }
        }

        if (!config.AutoDispatch)
            return;

        for (var index = 0; index < dispatchedDestinations.Length; index++)
        {
            if ((dispatchedGranaries & (1 << index)) == 0)
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;
            if (granary->RemainingDays      != 7 ||
                granary->ActiveExpeditionId != dispatchedDestinations[index])
                return;
        }

        foreach (var (index, destination) in GetRecommendations(agent))
        {
            if ((dispatchedGranaries & (1 << index)) != 0)
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;
            if (HasResources(granary))
                continue;

            if (granary->RemainingDays      == 7 &&
                granary->ActiveExpeditionId == destination)
                continue;

            dispatchedGranaries            |= (byte)(1 << index);
            agent->CurGranaryIndex         =  index;
            agent->CurSelectedExpeditionId =  destination;
            agent->CurSelectedDays         =  7;
            dispatchedDestinations[index]  =  destination;
            AgentId.MJIGatheringHouse.SendEvent(5, 0);
            return;
        }
    }

    private static bool IsGranaryAvailable(AgentMJIGatheringHouse* agent, int index)
    {
        var granaries = &agent->Manager->IslandState.Granaries;
        return granaries->PlaceId[index] != 0 &&
               (granaries->UnderConstruction[index] == 0 ||
                granaries->BuildingLevel[index]     != 0);
    }

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
            if (!IsGranaryAvailable(agent, index) ||
                rank >= destinations.Count)
                continue;

            var granary = (MJIGranaryState*)agent->GranariesState + index;
            var cost    = (uint)(DAILY_EXPEDITION_COST * (7 - Math.Min((int)granary->RemainingDays, 7)));
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
                        FontSize           = 12,
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

    private sealed class DestinationNodes
    {
        public HorizontalListNode Layout;
        public ItemIconNode       Icon;
        public TextNode           Text;
    }

    private class Config : ModuleConfig
    {
        public bool AutoClaim;
        public bool StopOnNormalExceed;
        public bool StopOnRareExceed = true;
        public bool AutoDispatch;
    }

    #region 常量

    private const uint COWRIE_ITEM_ID        = 37549;
    private const int  DAILY_EXPEDITION_COST = 50;

    #endregion
}
