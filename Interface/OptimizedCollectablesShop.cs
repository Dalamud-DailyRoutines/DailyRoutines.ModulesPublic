using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Internal;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;
using ShopData = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentCollectablesShop.CollectablesShopData;

namespace DailyRoutines.ModulesPublic;

public unsafe class OptimizedCollectablesShop : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedCollectablesShopTitle"),
        Description = Lang.Get("OptimizedCollectablesShopDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedCollectablesShop/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<AddonCollectablesShop.Delegates.RenderListItemCommon> RenderListItemCommonHook;
    private Hook<AgentCollectablesShop.Delegates.HandleEvent>?         HandleEventHook;
    private Hook<AgentCollectablesShop.Delegates.HandIn>?              HandInHook;
    private Hook<AgentCollectablesShop.Delegates.EndData>?             EndDataHook;

    private Config config = null!;

    private ShopControls  controls  = null!;
    private ShopSelection selection = null!;

    private BatchExchange? exchange;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        
        controls  = new(this);
        selection = new(this);

        TaskHelper ??= new()
        {
            RetryIntervalMS = 100
        };

        RenderListItemCommonHook = IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AddonCollectablesShop.MemberFunctionPointers),
            nameof(AddonCollectablesShop.RenderListItemCommon),
            (AddonCollectablesShop.Delegates.RenderListItemCommon)RenderListItemCommonDetour
        );
        RenderListItemCommonHook.Enable();

        HandleEventHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentCollectablesShop.MemberFunctionPointers),
            nameof(AgentCollectablesShop.MemberFunctionPointers.HandleEvent),
            (AgentCollectablesShop.Delegates.HandleEvent)HandleEventDetour
        );
        HandleEventHook.Enable();

        HandInHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentCollectablesShop.MemberFunctionPointers),
            nameof(AgentCollectablesShop.MemberFunctionPointers.HandIn),
            (AgentCollectablesShop.Delegates.HandIn)HandInDetour
        );
        HandInHook.Enable();

        EndDataHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(AgentCollectablesShop.MemberFunctionPointers),
            nameof(AgentCollectablesShop.MemberFunctionPointers.EndData),
            (AgentCollectablesShop.Delegates.EndData)EndDataDetour
        );
        EndDataHook.Enable();

        IGameInventory.Instance().InventoryChanged += OnInventoryChanged;
        LogMessageManager.Instance().RegPre(OnLogMessage);
        foreach (var addonEvent in new[] { AddonEvent.PostSetup, AddonEvent.PreRefresh, AddonEvent.PostRefresh, AddonEvent.PostDraw, AddonEvent.PreFinalize })
            IAddonLifecycle.Instance().RegisterListener(addonEvent, "CollectablesShop", OnAddon);

        if (CollectablesShopAddon->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        selection.Save();
        exchange?.Stop();
        
        var agent = AgentCollectablesShop.Instance();
        if (agent->Data != null)
            agent->Data->RefreshFlags |= AgentCollectablesShop.RefreshFlags.List;
        
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        IGameInventory.Instance().InventoryChanged -= OnInventoryChanged;
        LogMessageManager.Instance().Unreg(OnLogMessage);
        
        var addon = (AddonCollectablesShop*)CollectablesShopAddon;
        controls.Dispose(addon);
    }

    #region 事件

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs? args
    )
    {
        var agent = AgentCollectablesShop.Instance();
        var addon = (AddonCollectablesShop*)CollectablesShopAddon;

        switch (type)
        {
            case AddonEvent.PostSetup:
                if (agent->Data == null)
                    return;
                selection.Restore(agent);
                controls.Setup(addon);
                break;

            case AddonEvent.PreRefresh when args is AddonRefreshArgs refreshArgs:
                var values = (AtkValue*)refreshArgs.AtkValues;
                if (refreshArgs.AtkValueCount >= LIST_VALUE_COUNT &&
                    values[13].UInt           == 0                &&
                    agent->Data               != null)
                    selection.Prepare(values, agent->Data);
                break;

            case AddonEvent.PostRefresh when args is AddonRefreshArgs refreshArgs:
                if (refreshArgs.AtkValueCount                   < LIST_VALUE_COUNT ||
                    ((AtkValue*)refreshArgs.AtkValues)[13].UInt != 0               ||
                    agent->Data                                 == null)
                    return;

                selection.Refresh(agent, addon, exchange != null);
                break;

            case AddonEvent.PostDraw:
                selection.Scroll(addon);

                controls.Update(addon);
                break;

            case AddonEvent.PreFinalize:
                selection.Save();
                exchange?.Stop();

                controls.Reset();
                selection.Reset();
                break;
        }
    }

    private void OnInventoryChanged
    (
        IReadOnlyCollection<InventoryEventArgs> events
    )
    {
        var agent = AgentCollectablesShop.Instance();
        if (agent->Data == null ||
            !agent->AgentInterface.IsAgentActive())
            return;
        if (events.All(x => x.Item.ItemId == 0))
            return;
        exchange?.ConfirmSubmission(agent);

        agent->Data->RefreshFlags |= AgentCollectablesShop.RefreshFlags.List;
    }

    private void OnLogMessage
    (
        ref bool                isPrevented,
        ref uint                logMessageID,
        ref LogMessageQueueItem item
    )
    {
        if (logMessageID == 1941 &&
            exchange     != null)
            isPrevented = true;
    }

    private void RenderListItemCommonDetour
    (
        AddonCollectablesShop*                      addon,
        AtkComponentListItemPopulator.ListItemInfo* itemInfo,
        AtkResNode**                                nodeList
    )
    {
        RenderListItemCommonHook.Original(addon, itemInfo, nodeList);

        var listItem = itemInfo->ListItem;
        if (listItem == null || listItem->UIntValues.Count < 3) return;

        var itemID = listItem->UIntValues[2] % 50_0000;
        if (itemID == 0 || !LuminaGetter.TryGetRow<Item>(itemID, out var itemRow)) return;

        var imageNode = nodeList[0]->GetAsAtkImageNode();
        if (imageNode == null) return;

        imageNode->LoadIconTexture(itemRow.Icon, 0);
    }
    
    private bool HandleEventDetour
    (
        AgentCollectablesShop* agent,
        AtkValue*              values
    )
    {
        var eventID = values[0].Int;

        if (exchange != null)
        {
            switch (eventID)
            {
                case 15 when !exchange.IsIssuing:
                    exchange.Stop();
                    return false;
                case 12:
                case 14:
                    return false;
            }
        }

        var result = HandleEventHook.Original(agent, values);
        if (!selection.IsRestoring &&
            eventID is 12 or 14)
            selection.Save();
        return result;
    }

    private void HandInDetour
    (
        AgentCollectablesShop* agent
    )
    {
        if (exchange is { IsIssuing: true })
        {
            HandInHook.Original(agent);
            return;
        }

        if (exchange != null)
        {
            exchange.Stop();
            return;
        }

        if (!PluginConfig.Instance().ConflictKeyBinding.IsPressed())
        {
            HandInHook.Original(agent);
            return;
        }

        if (TaskHelper.IsBusy ||
            agent->Data == null)

            return;
        var data = agent->Data;
        if (data->SelectedCategoryIndex >= data->Categories.Length)
            return;
        ref var category = ref data->Categories[data->SelectedCategoryIndex];
        if (data->SelectedItemIndex >= category.ItemCount)
            return;
        exchange = new
        (
            this,
            agent->AgentInterface.AddonId,
            data->EventID,
            data->SelectedCategoryIndex,
            category.Items[data->SelectedItemIndex].ItemID
        );
        exchange.Enqueue();
    }

    private void EndDataDetour
    (
        AgentCollectablesShop* agent
    )
    {
        EndDataHook.Original(agent);

        exchange?.ConfirmSubmission(agent, true);
    }

    #endregion

    private void EnqueueOpenScripExchange()
    {
        if (LocalPlayerState.Object is not { } localPlayer)
            return;

        if (!EventFramework.Instance()->TryGetNearestEventID
            (
                x => x.EventId.ContentId is EventHandlerContent.PreHandler,
                x => LuminaWrapper.GetENPCName(x.BaseId).Equals
                (
                    LuminaWrapper.GetENPCName(1001617),
                    StringComparison.OrdinalIgnoreCase
                ),
                localPlayer.Position,
                out var eventID
            ))
        {
            NotifyHelper.ToastError(Lang.Get("OptimizedCollectablesShop-Notification-NoInclusionShop"));
            return;
        }

        var entityID = localPlayer.EntityID;
        TaskHelper.Enqueue
        (() =>
            {
                if (CollectablesShopAddon->IsAddonAndNodesReady())
                    CollectablesShopAddon->Close(true);
            }
        );
        TaskHelper.Enqueue(() => !ICondition.Instance().IsOccupiedInEvent, timeoutMS: SUBMISSION_TIMEOUT_MS);
        TaskHelper.Enqueue(() => new EventStartPackt(entityID, eventID).Send());
    }

    private sealed class BatchExchange
    (
        OptimizedCollectablesShop module,
        uint                      addonID,
        uint                      eventID,
        byte                      categoryIndex,
        uint                      itemID
    )
    {
        private readonly Submission submission = new();

        private uint completedCount;

        public bool IsIssuing { get; private set; }

        private bool IsRunning => module.exchange == this;

        public void Enqueue() => module.TaskHelper.Enqueue
        (
            () =>
            {
                if (!IsRunning)
                    return true;

                var agent = AgentCollectablesShop.Instance();
                var data  = agent->Data;

                if (data                          == null    ||
                    agent->AgentInterface.AddonId != addonID ||
                    data->EventID                 != eventID ||
                    data->SelectedCategoryIndex   != categoryIndex)
                {
                    Stop();
                    return true;
                }

                ref var category = ref data->Categories[categoryIndex];

                if (data->SelectedItemIndex                        >= category.ItemCount ||
                    category.Items[data->SelectedItemIndex].ItemID != itemID)
                {
                    Stop();
                    return true;
                }

                if (submission.IsPending)
                {
                    submission.Confirm(itemID);

                    if (!submission.IsConfirmed)
                    {
                        if (!submission.HasTimedOut)
                            return false;

                        Stop("OptimizedCollectablesShop-Notification-SubmissionFailed");
                        return true;
                    }

                    submission.Reset();
                    completedCount++;
                    Enqueue();
                    return true;
                }

                agent->RebuildSubmitItemsQuiet();

                if (data->SubmitItems.Count == 0)
                {
                    Stop();
                    return true;
                }

                var item = category.Items + data->SelectedItemIndex;

                if (!submission.TryBegin(data, item, data->SubmitItems[0].Collectability))
                {
                    Stop("OptimizedCollectablesShop-Notification-RewardOverflow");
                    return true;
                }

                using var values = new RentedAtkValues(2);
                values[0].SetInt(15);
                values[1].SetUInt(0);

                IsIssuing = true;

                try
                {
                    module.HandleEventHook.Original(agent, values);
                }
                finally
                {
                    IsIssuing = false;
                }

                return false;
            },
            timeoutMS: SUBMISSION_TIMEOUT_MS * 2,
            timeoutAction: () => Stop("OptimizedCollectablesShop-Notification-SubmissionFailed"),
            exceptionAction: () => Stop("OptimizedCollectablesShop-Notification-SubmissionFailed")
        );

        public void Stop
        (
            string? reason = null
        )
        {
            if (!IsRunning)
                return;

            module.exchange = null;
            submission.Reset();
            module.TaskHelper.Abort();
            var agent = AgentCollectablesShop.Instance();
            if (agent->Data != null)
                agent->Data->RefreshFlags |= AgentCollectablesShop.RefreshFlags.List;

            if (reason != null)
                NotifyHelper.ToastError(Lang.Get(reason));

            if (completedCount > 0)
            {
                var message = Lang.Get
                (
                    "OptimizedCollectablesShop-Notification-Finished",
                    new Dictionary<string, object>
                    {
                        ["count"] = completedCount
                    }
                );
                NotifyHelper.Instance().TrayInfo(message, Lang.Get("OptimizedCollectablesShop-Notification-Finished-Title"));
                NotifyHelper.Instance().Chat(message);
                NotifyHelper.Toast(message);
            }
        }

        public void ConfirmSubmission
        (
            AgentCollectablesShop* agent,
            bool                   acknowledged = false
        )
        {
            if (!submission.IsPending ||
                agent->AgentInterface.AddonId != addonID)
                return;

            submission.Confirm(itemID, acknowledged);
        }
    }

    private sealed class Submission
    {
        private readonly List<Reward> rewards = [];
        
        private InventoryType container;
        private ushort        slotIndex;
        
        private int             itemCount;
        private long            started;
        private SubmissionState state;

        public bool IsPending => state != SubmissionState.None;

        public bool IsConfirmed => state == SubmissionState.Confirmed;

        public bool HasTimedOut => Environment.TickCount64 - started >= SUBMISSION_TIMEOUT_MS;

        public bool TryBegin
        (
            ShopData*      data,
            ShopData.Item* item,
            ushort         collectability
        )
        {
            rewards.Clear();
            if (item->RefineIndex >= data->Refines.Count ||
                item->Key is < 1 or > 3)
                return false;
            var refine = data->Refines[item->RefineIndex];
            var tier   = 0;
            if (item->Key      > 1 &&
                collectability >= refine.MidCollectability)
                tier = 1;
            if (item->Key      > 2 &&
                collectability >= refine.HighCollectability)
                tier = 2;

            switch (data->RewardType)
            {
                case 1:
                {
                    if (item->RewardScripIndex >= data->RewardScrips.Count)
                        return false;
                    var reward = data->RewardScrips[item->RewardScripIndex];
                    var amount = tier switch
                    {
                        1 => reward.MidReward,
                        2 => reward.HighReward,
                        _ => reward.LowReward
                    };
                    var itemID = CurrencyManager.Instance()->GetItemIdBySpecialId(reward.Currency);
                    if (itemID == 0)
                        return false;
                    rewards.Add(new(itemID, amount, false, GetRewardCount(itemID, false)));
                    break;
                }
                case 0:
                {
                    if (item->RewardScripIndex >= data->RewardItems.Count)
                        return false;
                    var reward  = data->RewardItems[item->RewardScripIndex];
                    var amounts = &reward.RewardLow;

                    if (reward.ItemID != 0)
                    {
                        var isHQ = (reward.Unknown4 & 1) != 0;
                        rewards.Add(new(reward.ItemID, amounts[tier], isHQ, GetRewardCount(reward.ItemID, isHQ)));
                    }

                    if (reward.Unknown0 != 0)
                    {
                        var isHQ = (reward.Unknown4 & 2) != 0;
                        amounts = &reward.Unknown1;
                        rewards.Add(new(reward.Unknown0, amounts[tier], isHQ, GetRewardCount(reward.Unknown0, isHQ)));
                    }

                    break;
                }
                default:
                    return false;
            }

            var requiredSlots  = 0u;
            var availableSlots = InventoryManager.Instance()->GetEmptySlotsInBag();
            var submittedSlot  = InventoryManager.Instance()->GetInventorySlot(data->SubmitItems[0].Container, data->SubmitItems[0].Slot);
            if (Inventories.Player.Contains(data->SubmitItems[0].Container) &&
                submittedSlot                != null                        &&
                submittedSlot->GetQuantity() == 1)
                availableSlots++;

            foreach (var reward in rewards)
            {
                var currency = CurrencyManager.Instance();

                if (currency->IsItemLimited(reward.ItemID))
                {
                    if ((ulong)reward.Before + reward.Amount > currency->GetItemMaxCount(reward.ItemID))
                        return false;
                }
                else if (!currency->HasItem(reward.ItemID) &&
                         reward.Amount > 0)
                {
                    if (!LuminaGetter.TryGetRow(reward.ItemID, out Item itemData) ||
                        (itemData.IsUnique &&
                         (reward.Before > 0 ||
                          reward.Amount > 1)))
                        return false;
                    var stackSize = itemData.StackSize;
                    if (stackSize == 0)
                        return false;
                    var remaining = reward.Amount;

                    foreach (var inventoryType in Inventories.Player)
                    {
                        var container = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
                        if (container == null ||
                            !container->IsLoaded)
                            return false;

                        for (var i = 0; i < container->Size && remaining > 0; i++)
                        {
                            var slot = container->GetInventorySlot(i);
                            if (slot                  == null          ||
                                slot->GetBaseItemId() != reward.ItemID ||
                                slot->IsHighQuality() != reward.IsHQ   ||
                                slot->IsCollectable())
                                continue;
                            var quantity = slot->GetQuantity();
                            if (quantity < stackSize)
                                remaining -= Math.Min(remaining, stackSize - quantity);
                        }
                    }

                    requiredSlots += (remaining + stackSize - 1) / stackSize;
                    if (requiredSlots > availableSlots)
                        return false;
                }
            }

            itemCount = InventoryManager.Instance()->GetInventoryItemCount(item->ItemID, false, false);
            started   = Environment.TickCount64;
            container = data->SubmitItems[0].Container;
            slotIndex = data->SubmitItems[0].Slot;
            state     = SubmissionState.Pending;
            return true;
        }

        private static uint GetRewardCount
        (
            uint itemID,
            bool isHQ
        )
        {
            var currency = CurrencyManager.Instance();
            return currency->HasItem(itemID) ?
                       currency->GetItemCount(itemID) :
                       (uint)InventoryManager.Instance()->GetInventoryItemCount(itemID, isHQ, false, false);
        }

        public void Confirm
        (
            uint itemID,
            bool acknowledged = false
        )
        {
            if (state is SubmissionState.None or SubmissionState.Confirmed)
                return;
            if (acknowledged)
                state = SubmissionState.Acknowledged;
            if (state != SubmissionState.Acknowledged)
                return;

            var inventory = InventoryManager.Instance();
            var slot      = inventory->GetInventorySlot(container, slotIndex);
            if (slot              == null ||
                slot->GetItemId() == itemID)
                return;
            if (inventory->GetInventoryItemCount(itemID, false, false) != itemCount - 1)
                return;
            if (rewards.Any(x => GetRewardCount(x.ItemID, x.IsHQ) < x.Before + x.Amount))
                return;
            state = SubmissionState.Confirmed;
        }

        public void Reset()
        {
            state = SubmissionState.None;
            rewards.Clear();
        }

        private enum SubmissionState
        {
            None,
            Pending,
            Acknowledged,
            Confirmed
        }
    }

    private sealed class ShopControls
    (
        OptimizedCollectablesShop module
    )
    {
        private readonly List<IAddonEventHandle> eventHandles = [];

        private CheckboxNode?   ownedItemsCheckbox;
        private TextButtonNode? scripExchangeButton;

        private string? currentExchangeText;
        private bool    navigationConfigured;

        private AtkCursorNavigationInfo categoryListNavigation;
        private AtkCursorNavigationInfo submitButtonNavigation;

        public void Setup
        (
            AddonCollectablesShop* addon
        )
        {
            if (addon->SubmitButton == null)
                return;
            var exchangeNode = addon->SubmitButton->OwnerNode;
            ownedItemsCheckbox ??= new()
            {
                String    = Lang.Get("OptimizedCollectablesShop-OnlyOwnedItems"),
                Position  = new(378, 42),
                Height    = 24,
                IsChecked = module.config.OnlyOwnedItems,
                NavIndex  = OWNED_ITEMS_NAV_INDEX,
                NavUp     = 0,
                NavDown   = addon->CategoryList->CursorNavigationInfo.Index,
                NavLeft   = addon->CategoryList->CursorNavigationInfo.Index,
                NavRight  = addon->ItemList->CursorNavigationInfo.Index,
                OnClick = isChecked =>
                {
                    module.config.OnlyOwnedItems = isChecked;
                    module.config.Save(module);
                    var agent = AgentCollectablesShop.Instance();
                    if (agent->Data != null)
                        agent->Data->RefreshFlags |= AgentCollectablesShop.RefreshFlags.List;
                }
            };
            ownedItemsCheckbox.AttachNode((AtkUnitBase*)addon);

            scripExchangeButton ??= new()
            {
                Position = new(exchangeNode->X - exchangeNode->Width - 8f, exchangeNode->Y + 2f),
                Size     = new(exchangeNode->Width, exchangeNode->Height),
                String   = LuminaGetter.GetRowOrDefault<InclusionShop>(3801094).ShopName.ToString(),
                OnClick  = module.EnqueueOpenScripExchange,
                NavIndex = SCRIP_EXCHANGE_NAV_INDEX,
                NavUp    = addon->CategoryList->CursorNavigationInfo.Index,
                NavDown  = addon->CategoryList->CursorNavigationInfo.Index,
                NavLeft  = addon->CategoryList->CursorNavigationInfo.Index,
                NavRight = addon->SubmitButton->CursorNavigationInfo.Index
            };
            scripExchangeButton.AttachNode((AtkUnitBase*)addon);

            if (!navigationConfigured)
            {
                categoryListNavigation = addon->CategoryList->CursorNavigationInfo;
                submitButtonNavigation = addon->SubmitButton->CursorNavigationInfo;
                addon->CategoryList->SetCursorNavigationIndex(AtkCursorNavigationDirection.Up, OWNED_ITEMS_NAV_INDEX);
                addon->CategoryList->SetCursorNavigationIndex(AtkCursorNavigationDirection.Down, SCRIP_EXCHANGE_NAV_INDEX);
                addon->SubmitButton->SetCursorNavigationIndex(AtkCursorNavigationDirection.Left, SCRIP_EXCHANGE_NAV_INDEX);
                navigationConfigured = true;
            }

            foreach (var eventType in new[] { AddonEventType.MouseOver, AddonEventType.MouseOut })
            {
                var handle = IAddonEventManager.Instance().AddEvent
                (
                    (nint)addon,
                    (nint)exchangeNode,
                    eventType,
                    (type, _) =>
                    {
                        var host = (AddonCollectablesShop*)CollectablesShopAddon;
                        if (host == null)
                            return;

                        if (type == AddonEventType.MouseOut)
                        {
                            AtkStage.Instance()->TooltipManager.HideTooltip(host->Id);
                            return;
                        }

                        if (module.exchange != null ||
                            PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                            return;

                        AtkStage.Instance()->TooltipManager.ShowTooltip
                        (
                            host->Id,
                            (AtkResNode*)host->SubmitButton->OwnerNode,
                            Lang.Get
                            (
                                "OptimizedCollectablesShop-ConflictKeyHint",
                                new Dictionary<string, object>
                                {
                                    ["conflictKey"] = PluginConfig.Instance().ConflictKeyBinding
                                }
                            )
                        );
                    }
                );
                if (handle != null)
                    eventHandles.Add(handle);
            }

            Update(addon);
        }

        public void Update
        (
            AddonCollectablesShop* addon
        )
        {
            if (addon               == null ||
                addon->SubmitButton == null)
                return;
            var text = LuminaWrapper.GetAddonText(13788);
            if (module.exchange != null)
                text = Lang.Get("Stop");
            else if (PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                text = Lang.Get("OptimizedCollectablesShop-BatchExchange");

            if (currentExchangeText != text)
            {
                addon->SubmitButton->SetText(text);
                if (module.exchange != null ||
                    PluginConfig.Instance().ConflictKeyBinding.IsPressed())
                    AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
                currentExchangeText = text;
            }

            if (module.exchange != null)
                addon->SubmitButton->SetEnabledState(true);
            if (scripExchangeButton is { } exchangeButton)
                exchangeButton.IsEnabled = module.exchange == null && !module.TaskHelper.IsBusy;
            if (ownedItemsCheckbox is { } checkbox)
                checkbox.IsEnabled = module.exchange == null;
        }

        public void Dispose
        (
            AddonCollectablesShop* addon
        )
        {
            if (navigationConfigured &&
                ((AtkUnitBase*)addon)->IsAddonAndNodesReady())
            {
                addon->CategoryList->CursorNavigationInfo = categoryListNavigation;
                addon->SubmitButton->CursorNavigationInfo = submitButtonNavigation;
            }

            ownedItemsCheckbox?.Dispose();
            scripExchangeButton?.Dispose();
            Reset();
        }

        public void Reset()
        {
            foreach (var handle in eventHandles)
                IAddonEventManager.Instance().RemoveEvent(handle);
            eventHandles.Clear();
            ownedItemsCheckbox   = null;
            scripExchangeButton  = null;
            currentExchangeText  = null;
            navigationConfigured = false;
        }
    }

    private sealed class ShopSelection
    (
        OptimizedCollectablesShop module
    )
    {
        private short  selectedListRow = -1;
        private bool   scrollPending;
        private ushort selectedItemIndex;

        public bool IsRestoring { get; private set; }

        public void Restore
        (
            AgentCollectablesShop* agent
        )
        {
            var data = agent->Data;
            IsRestoring = true;
            var restoredItem = false;
            if (LocalPlayerState.ClassJob is not (>= 8 and <= 18)                                     &&
                module.config.LastCategoryIndex                             < data->Categories.Length &&
                data->Categories[module.config.LastCategoryIndex].ItemCount > 0)
                data->SelectedCategoryIndex = module.config.LastCategoryIndex;

            if (module.config.SelectedItems.TryGetValue(GetSelectionKey(), out var itemID))
            {
                ref var category = ref data->Categories[data->SelectedCategoryIndex];

                for (ushort i = 0; i < category.ItemCount; i++)
                {
                    if (category.Items[i].ItemID != itemID)
                        continue;
                    data->SelectedItemIndex = i;
                    data->HoveredItemIndex  = i;
                    restoredItem            = true;
                    break;
                }
            }

            if (!restoredItem)
            {
                data->SelectedItemIndex = 0;
                data->HoveredItemIndex  = 0;
            }

            agent->RebuildSubmitItems();
            data->RefreshFlags |= AgentCollectablesShop.RefreshFlags.List;
        }

        public void Refresh
        (
            AgentCollectablesShop* agent,
            AddonCollectablesShop* addon,
            bool                   isExchanging
        )
        {
            var list = addon->CategoryList;
            if (list == null)
                return;

            if (selectedListRow >= 0)
            {
                list->SelectItem(selectedListRow);
                scrollPending = true;

                if (!isExchanging)
                {
                    agent->Data->SelectedItemIndex = selectedItemIndex;
                    agent->Data->HoveredItemIndex  = selectedItemIndex;
                    agent->RebuildSubmitItems();
                }
            }

            IsRestoring = false;
            Save();
        }

        public void Scroll
        (
            AddonCollectablesShop* addon
        )
        {
            if (scrollPending               &&
                addon->CategoryList != null &&
                !addon->CategoryList->LayoutRefreshPending)
            {
                addon->CategoryList->ScrollToItem(selectedListRow);
                scrollPending = false;
            }
        }

        public void Prepare
        (
            AtkValue* values,
            ShopData* data
        )
        {
            scrollPending = false;
            var count = (int)values[20].UInt;

            if (count is <= 0 or > MAX_LIST_ITEMS)
            {
                selectedListRow = -1;
                return;
            }

            using var buffer  = new RentedAtkValues(count * (UINT_VALUES_PER_ITEM + STRING_VALUES_PER_ITEM));
            AtkValue* numbers = buffer;
            var       strings = numbers + (count * UINT_VALUES_PER_ITEM);
            for (var i = 0; i < count * UINT_VALUES_PER_ITEM; i++)
                numbers[i].Copy(values + UINT_VALUES_OFFSET + i);
            for (var i = 0; i < count * STRING_VALUES_PER_ITEM; i++)
                strings[i].Copy(values + STRING_VALUES_OFFSET + i);

            var rows      = new List<(int Row, bool Owned)>(count);
            var output    = new List<int>(count);
            var inventory = InventoryManager.Instance();

            for (var start = 0; start < count;)
            {
                var header = start++;
                rows.Clear();

                while (start < count && (numbers[start * UINT_VALUES_PER_ITEM].UInt & (uint)TreeListItemType.Group) == 0)
                {
                    var itemID = numbers[(start * UINT_VALUES_PER_ITEM) + 2].UInt;
                    var owned  = inventory->GetInventoryItemCount(itemID, false, false) > 0;
                    if (!module.config.OnlyOwnedItems ||
                        owned)
                        rows.Add((start, owned));
                    start++;
                }

                if (module.config.OnlyOwnedItems &&
                    rows.Count == 0)

                    continue;
                output.Add(header);
                foreach (var row in rows.OrderByDescending(x => x.Owned))
                    output.Add(row.Row);
            }

            selectedListRow = -1;
            var firstItemRow = -1;

            for (var i = 0; i < output.Count; i++)
            {
                var source       = output[i];
                var numberOffset = UINT_VALUES_OFFSET + (i * UINT_VALUES_PER_ITEM);
                for (var j = 0; j < UINT_VALUES_PER_ITEM; j++)
                    values[numberOffset + j].Copy(numbers + (source * UINT_VALUES_PER_ITEM) + j);
                for (var j = 0; j < STRING_VALUES_PER_ITEM; j++)
                    values[STRING_VALUES_OFFSET + (i * STRING_VALUES_PER_ITEM) + j]
                        .Copy(strings + (source * STRING_VALUES_PER_ITEM) + j);

                ref var flags   = ref values[numberOffset].UInt;
                var     isGroup = (flags & (uint)TreeListItemType.Group) != 0;
                flags &= ~(uint)TreeListItemType.LastItemInGroup;

                if (isGroup)
                {
                    if (module.config.OnlyOwnedItems)
                        flags |= GROUP_EXPANDED_FLAG;
                    continue;
                }

                if (i + 1                                                                               == output.Count ||
                    (numbers[output[i + 1] * UINT_VALUES_PER_ITEM].UInt & (uint)TreeListItemType.Group) != 0)
                    flags |= (uint)TreeListItemType.LastItemInGroup;
                if (firstItemRow < 0)
                    firstItemRow = i;
                if (values[numberOffset + 1].UInt == data->SelectedItemIndex)
                    selectedListRow = (short)i;
            }

            if (selectedListRow < 0)
                selectedListRow = (short)firstItemRow;

            if (selectedListRow >= 0)
            {
                selectedItemIndex = (ushort)values[UINT_VALUES_OFFSET + (selectedListRow * UINT_VALUES_PER_ITEM) + 1].UInt;

                for (var i = selectedListRow - 1; i >= 0; i--)
                {
                    ref var flags = ref values[UINT_VALUES_OFFSET + (i * UINT_VALUES_PER_ITEM)].UInt;
                    if ((flags & (uint)TreeListItemType.Group) == 0)
                        continue;
                    flags |= GROUP_EXPANDED_FLAG;
                    break;
                }
            }

            values[20].UInt = (uint)output.Count;
        }

        public void Save()
        {
            if (IsRestoring)
                return;

            var agent = AgentCollectablesShop.Instance();
            var data  = agent->Data;
            if (data                        == null ||
                data->SelectedCategoryIndex >= data->Categories.Length)
                return;

            ref var category = ref data->Categories[data->SelectedCategoryIndex];
            if (data->SelectedItemIndex >= category.ItemCount)
                return;

            var key    = GetSelectionKey();
            var itemID = category.Items[data->SelectedItemIndex].ItemID;
            if (module.config.LastCategoryIndex == data->SelectedCategoryIndex       &&
                module.config.SelectedItems.TryGetValue(key, out var previousItemID) &&
                previousItemID == itemID)
                return;

            module.config.LastCategoryIndex  = data->SelectedCategoryIndex;
            module.config.SelectedItems[key] = itemID;
            module.config.Save(module);
        }

        public void Reset()
        {
            selectedListRow = -1;
            scrollPending   = false;
            IsRestoring     = false;
        }

        private static uint GetSelectionKey() =>
            LocalPlayerState.ClassJob is >= 8 and <= 18 ?
                LocalPlayerState.ClassJob :
                0;
    }

    private class Config : ModuleConfig
    {
        public byte                   LastCategoryIndex = 11;
        public Dictionary<uint, uint> SelectedItems     = [];
        public bool                   OnlyOwnedItems;
    }

    private readonly record struct Reward
    (
        uint ItemID,
        uint Amount,
        bool IsHQ,
        uint Before
    );

    #region 常量

    private const byte OWNED_ITEMS_NAV_INDEX    = 4;
    private const byte SCRIP_EXCHANGE_NAV_INDEX = 5;

    private const uint GROUP_EXPANDED_FLAG = 0x10000;

    private const int LIST_VALUE_COUNT       = 5146;
    private const int MAX_LIST_ITEMS         = 300;
    private const int UINT_VALUES_OFFSET     = 21;
    private const int UINT_VALUES_PER_ITEM   = 11;
    private const int STRING_VALUES_OFFSET   = 3321;
    private const int STRING_VALUES_PER_ITEM = 5;
    private const int SUBMISSION_TIMEOUT_MS  = 10000;

    #endregion
}
