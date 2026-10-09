using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using DailyRoutines.Manager;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes.ComponentNode;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Addons;
using OmenTools.OmenService;
using OmenTools.Threading.TaskHelper.Enums;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoExpertDelivery : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoExpertDeliveryTitle"),
        Description         = Lang.Get("AutoExpertDeliveryDescription"),
        Category            = ModuleCategory.Interface,
        ModulesPrerequisite = ["FastGrandCompanyExchange"]
    };

    private Config config = null!;

    private DRAutoExpertDelivery? addonExpertDelivery;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        TaskHelper ??= new() { TimeoutMS = int.MaxValue };

        addonExpertDelivery ??= new(this)
        {
            InternalName          = "DRAutoExpertDelivery",
            Title                 = Info.Title,
            Size                  = new(300f, 250f),
            RememberClosePosition = true
        };

        _ = UltimateTotemExchangeItemIDs;
    }

    protected override void Uninit()
    {
        addonExpertDelivery?.Dispose();
        addonExpertDelivery = null;
    }

    private bool EnqueueDelivery()
    {
        if (GrandCompanySupplyReward != null)
        {
            if (!GrandCompanySupplyReward->IsAddonAndNodesReady()) return false;

            ((AddonGrandCompanySupplyReward*)GrandCompanySupplyReward)->DeliverButton->Click();

            TaskHelper.Abort();
            TaskHelper.Enqueue(EnqueueRefresh);
            TaskHelper.Enqueue(EnqueueDelivery);
            return true;
        }

        if (SelectYesno != null)
        {
            var state = AddonSelectYesnoEvent.ClickYes();
            if (!state) return false;

            TaskHelper.Abort();
            TaskHelper.Enqueue(EnqueueDelivery);
            return true;
        }

        if (GrandCompanySupplyList != null)
        {
            if (!GrandCompanySupplyList->IsAddonAndNodesReady()       ||
                AgentGrandCompanySupply.Instance()->ItemArray == null ||
                GrandCompanySupplyList->AtkValues->UInt       != 2)
                return false;

            var items = ExpertDeliveryItem.Parse().Where(x => x.GetIndex() != -1 && !x.IsNeedToSkip(this)).ToList();

            if (items.Count > 0)
            {
                if (IsAboutToReachTheCap(items[0].SealReward))
                {
                    TaskHelper.Abort();
                    return true;
                }

                items.First().HandIn();

                TaskHelper.Abort();
                TaskHelper.Enqueue(EnqueueDelivery);
                return true;
            }

            TaskHelper.Abort();
            return true;
        }

        if (!ICondition.Instance()[ConditionFlag.OccupiedInQuestEvent])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }

    private void EnqueueGrandCompanyExchangeOpen
    (
        bool isAutoExchange
    )
    {
        if (!ZoneInfo.TryGetValue(GameState.TerritoryType, out var info)) return;

        TaskHelper.Enqueue
        (() =>
            {
                if (!ICondition.Instance().IsOccupiedInEvent) return true;

                if (GrandCompanySupplyList->IsAddonAndNodesReady())
                    GrandCompanySupplyList->Close(true);

                if (SelectString->IsAddonAndNodesReady())
                    SelectString->Close(true);

                return false;
            }
        );

        TaskHelper.Enqueue(() => new EventStartPackt(IObjectTable.Instance().LocalPlayer.GameObjectID, info.EventID).Send());
        TaskHelper.Enqueue(() => GrandCompanyExchange->IsAddonAndNodesReady());

        if (isAutoExchange && ModuleManager.Instance().IsModuleEnabled(typeof(FastGrandCompanyExchange)))
        {
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (IsCurrentlyBusyIPC.Value)
                        return true;

                    EnqueueByNameIPC.InvokeFunc("default", -1);
                    return false;
                },
                timeoutMS: 5_000,
                timeoutBehaviour: TaskAbortBehaviour.AbortCurrent
            );
            TaskHelper.Enqueue(() => !IsCurrentlyBusyIPC.Value, timeoutMS: 5_000, timeoutBehaviour: TaskAbortBehaviour.AbortCurrent);
            TaskHelper.Enqueue(() => GrandCompanyExchange->Close(true));
        }

        // 还有没交的
        if (GrandCompanySupplyList->AtkValues[8].UInt != 0)
        {
            TaskHelper.Enqueue(() => !GrandCompanyExchange->IsAddonAndNodesReady() && !ICondition.Instance().IsOccupiedInEvent);
            TaskHelper.Enqueue
            (() => IObjectTable.Instance()
                               .FirstOrDefault(x => x.ObjectKind == ObjectKind.EventNpc && x.DataID == info.DataID)
                               .TargetInteract()
            );
            TaskHelper.Enqueue(() => AddonSelectStringEvent.Select(0));
            if (isAutoExchange)
                TaskHelper.Enqueue(EnqueueDelivery);
        }
    }

    private static bool EnqueueRefresh()
    {
        if (GrandCompanySupplyReward != null                ||
            !GrandCompanySupplyList->IsAddonAndNodesReady() ||
            AgentGrandCompanySupply.Instance()->ItemArray == null)
            return false;

        AgentId.GrandCompanySupply.SendEvent(0, 0, 2);
        return true;
    }

    private static bool IsAboutToReachTheCap
    (
        uint sealReward
    )
    {
        var grandCompany = PlayerState.Instance()->GrandCompany;
        if ((GrandCompany)grandCompany == GrandCompany.None) return true;

        if (!LuminaGetter.TryGetRow<GrandCompanyRank>(PlayerState.Instance()->GetGrandCompanyRank(), out var rank))
            return true;

        var buffMultiplier = 1f;
        if (LocalPlayerState.HasStatus(1078, out var index) || LocalPlayerState.HasStatus(414, out index))
            buffMultiplier += IObjectTable.Instance().LocalPlayer.StatusList[index].Param / 100f;

        var companySeals = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        var capAmount    = rank.MaxSeals;

        if (companySeals + (uint)(sealReward * buffMultiplier) > capAmount)
        {
            var message = Lang.Get("AutoExpertDelivery-Notification-Message");

            NotifyHelper.Instance().Chat(message);
            NotifyHelper.Instance().TrayWarning
            (
                message,
                Lang.Get("AutoExpertDelivery-Notification-Title")
            );
            return true;
        }

        return false;
    }

    private class Config : ModuleConfig
    {
        public int  DefaultPage                    = 2;
        public bool SkipWhenHQ                     = true;
        public bool SkipWhenMateria                = true;
        public bool SkipUltimateTotemExchangeItems = true;
    }

    private class DRAutoExpertDelivery
    (
        AutoExpertDelivery module
    ) : AttachedAddon("GrandCompanySupplyList", AddonEvent.PostSetup)
    {
        protected override bool CanOpenAddon =>
            !module.TaskHelper.IsBusy            &&
            HostAddon                    != null &&
            HostAddon->AtkValues[5].UInt == 2;

        private VerticalListNode? ControlTabLayout;
        private VerticalListNode? SettingTabLayout;

        private TextButtonNode? OperateButtonNode;

        protected override void OnHostAddon
        (
            AddonEvent type,
            AddonArgs? args
        )
        {
            if (type != AddonEvent.PostSetup || GrandCompanySupplyList == null) return;

            GrandCompanySupplyList->Callback(0, module.config.DefaultPage);
        }

        protected override void OnUpdate
        (
            AtkUnitBase* addon
        )
        {
            OperateButtonNode?.String = module.TaskHelper.IsBusy ?
                                            Lang.Get("Stop") :
                                            Lang.Get("AutoExpertDelivery-StartBatch");

            base.OnUpdate(addon);
        }

        protected override void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var tabNode = new TabBarNode
            {
                Size     = ContentSize with { Y = 32 },
                Position = ContentStartPosition,
                NavIndex = 1,
                NavDown  = 3
            };

            var tabContentPosition = tabNode.Position + tabNode.Size with { X = 0 };

            tabNode.AddTab
            (
                Lang.Get("Operation"),
                () =>
                {
                    ControlTabLayout.IsVisible = true;
                    SettingTabLayout.IsVisible = false;
                    ApplyControllerNavigation(tabNode);

                    SetWindowSize(Size with { Y = tabNode.Height + ControlTabLayout.Height + 28f });
                }
            );

            tabNode.AddTab
            (
                Lang.Get("Settings"),
                () =>
                {
                    ControlTabLayout.IsVisible = false;
                    SettingTabLayout.IsVisible = true;
                    ApplyControllerNavigation(tabNode);

                    SetWindowSize(Size with { Y = tabNode.Height + SettingTabLayout.Height + 28f });
                }
            );

            tabNode.AttachNode(this);

            ControlTabLayout = new()
            {
                Position         = tabContentPosition,
                FirstItemSpacing = 4f,
                ItemSpacing      = 4f,
                FitContents      = true
            };

            OperateButtonNode = new TextButtonNode
            {
                Size        = ContentSize with { Y = 42 },
                String      = Lang.Get("Start"),
                TextureType = ButtonTextureType.ButtonB,
                OnClick = () =>
                {
                    if (module.TaskHelper.IsBusy)
                    {
                        module.TaskHelper.Abort();
                        return;
                    }

                    module.EnqueueDelivery();
                }
            };

            var exchangeShopNode = new TextButtonNode
            {
                Size   = ContentSize with { Y = 42 },
                String = Lang.Get("AutoExpertDelivery-OpenShop"),
                OnClick = () =>
                {
                    if (module.TaskHelper.IsBusy) return;
                    module.EnqueueGrandCompanyExchangeOpen(false);
                }
            };

            var exchangeShopAndExchangeNode = new TextButtonNode
            {
                Size   = ContentSize with { Y = 42 },
                String = Lang.Get("AutoExpertDelivery-OpenShopAndExchange"),
                OnClick = () =>
                {
                    if (module.TaskHelper.IsBusy) return;
                    module.EnqueueGrandCompanyExchangeOpen(true);
                }
            };

            ControlTabLayout.AddNode([OperateButtonNode, exchangeShopNode, exchangeShopAndExchangeNode]);

            ControlTabLayout.AttachNode(this);

            SettingTabLayout = new()
            {
                IsVisible        = false,
                Position         = tabContentPosition,
                FitContents      = true,
                FirstItemSpacing = 4f,
                ItemSpacing      = 5f
            };

            var skipHQSettingNode = new CheckboxNode
            {
                IsChecked = module.config.SkipWhenHQ,
                Size      = ContentSize with { Y = 28 },
                String    = Lang.Get("SkipHQItem"),
                OnClick = x =>
                {
                    module.config.SkipWhenHQ = x;
                    module.config.Save(module);
                }
            };
            skipHQSettingNode.Label.TextFlags |= TextFlags.MultiLine | TextFlags.WordWrap;
            SettingTabLayout.AddNode(skipHQSettingNode);

            var skipMateriaSettingNode = new CheckboxNode
            {
                IsChecked = module.config.SkipWhenMateria,
                Size      = ContentSize with { Y = 28 },
                String    = Lang.Get("AutoExpertDelivery-SkipMaterias"),
                OnClick = x =>
                {
                    module.config.SkipWhenMateria = x;
                    module.config.Save(module);
                }
            };
            skipMateriaSettingNode.Label.TextFlags |= TextFlags.MultiLine | TextFlags.WordWrap;
            SettingTabLayout.AddNode(skipMateriaSettingNode);

            var skipUltimateTotemExchangeItemsNode = new CheckboxNode
            {
                IsChecked = module.config.SkipUltimateTotemExchangeItems,
                Size      = ContentSize with { Y = 28 },
                String    = Lang.Get("AutoExpertDelivery-SkipUltimateWeapons"),
                OnClick = x =>
                {
                    module.config.SkipUltimateTotemExchangeItems = x;
                    module.config.Save(module);
                }
            };
            skipUltimateTotemExchangeItemsNode.Label.TextFlags |= TextFlags.MultiLine | TextFlags.WordWrap;
            SettingTabLayout.AddNode(skipUltimateTotemExchangeItemsNode);

            SettingTabLayout.AddDummy(5f);

            var defaultPageTitleNode = new TextNode
            {
                Size     = ContentSize with { Y = 32 },
                FontSize = 16,
                String   = Lang.Get("AutoExpertDelivery-DefaultPage")
            };
            SettingTabLayout.AddNode(defaultPageTitleNode);

            var defaultPageGroupNode = new RadioButtonGroupNode
            {
                ItemSpacing = 5f
            };

            for (var i = 0U; i < 3; i++)
                defaultPageGroupNode.AddButton(LuminaWrapper.GetAddonTextSeString(4572 + i));

            defaultPageGroupNode.SelectedIndex = module.config.DefaultPage;
            defaultPageGroupNode.OnSelectionChanged = _ =>
            {
                module.config.DefaultPage = defaultPageGroupNode.SelectedIndex;
                module.config.Save(module);
            };
            SettingTabLayout.AddNode(defaultPageGroupNode);

            SettingTabLayout.AttachNode(this);

            ApplyControllerNavigation(tabNode);

            addon->FocusNode = tabNode.TabButtons[0];

            SetWindowSize(Size with { Y = tabNode.Height + ControlTabLayout.Height + 28f });
        }

        private void ApplyControllerNavigation
        (
            TabBarNode tabBarNode
        )
        {
            var activeLayout = ControlTabLayout.IsVisible ?
                                   ControlTabLayout :
                                   SettingTabLayout;
            var idleLayout = ControlTabLayout.IsVisible ?
                                 SettingTabLayout :
                                 ControlTabLayout;

            foreach (var node in idleLayout.Nodes.OfType<ComponentNode>())
            {
                node.NavIndex = 0;
            }

            List<ComponentNode> navigationNodes = [.. tabBarNode.TabButtons];

            foreach (var node in activeLayout.Nodes)
            {
                switch (node)
                {
                    case TextButtonNode buttonNode:
                        navigationNodes.Add(buttonNode);
                        break;
                    case CheckboxNode checkboxNode:
                        navigationNodes.Add(checkboxNode);
                        break;
                    case RadioButtonGroupNode radioButtonGroupNode:
                        navigationNodes.AddRange(radioButtonGroupNode.RadioButtons);
                        break;
                }
            }

            for (var index = 0; index < navigationNodes.Count; index++)
            {
                navigationNodes[index].NavIndex = index + 1;
                navigationNodes[index].NavUp = index == 0 ?
                                                   navigationNodes.Count :
                                                   index;
                navigationNodes[index].NavDown = index == navigationNodes.Count - 1 ?
                                                     1 :
                                                     index + 2;
            }
        }
    }

    private record ExpertDeliveryItem
    (
        uint          ItemID,
        InventoryType Container,
        ushort        Slot,
        uint          SealReward
    )
    {
        public static List<ExpertDeliveryItem> Parse()
        {
            List<ExpertDeliveryItem> returnValues = [];

            var agent = AgentGrandCompanySupply.Instance();
            if (agent == null || agent->ItemArray == null) return returnValues;

            for (var i = 0U; i < agent->NumItems; i++)
            {
                var item = agent->ItemArray[i];
                if (item.ItemId == 0    ||
                    item.IsBonusReward  ||
                    item.ExpReward  > 0 ||
                    item.SealReward <= 0)
                    continue;

                returnValues.Add(new(item.ItemId, item.Inventory, item.Slot, (uint)item.SealReward));
            }

            return returnValues;
        }

        public void HandIn() => GrandCompanySupplyList->Callback(1, GetIndex());

        public bool IsNeedToSkip
        (
            AutoExpertDelivery instance
        )
        {
            if (GetSlot() == null) return true;
            if (instance.config.SkipWhenHQ                        && IsHQ()) return true;
            if (instance.config.SkipWhenMateria                   && HasMateria()) return true;
            return instance.config.SkipUltimateTotemExchangeItems && IsUltimateTotemExchangeItem();
        }

        public int GetIndex()
        {
            var agent = AgentGrandCompanySupply.Instance();
            if (agent == null) return -1;

            var addon = GrandCompanySupplyList;
            if (!addon->IsAddonAndNodesReady()) return -1;

            var loadState = addon->AtkValues[0].UInt;
            if (loadState != 2) return -1;

            var tab = addon->AtkValues[5].UInt;
            if (tab != 2) return -1;

            var itemCount = addon->AtkValues[6].UInt;
            if (itemCount == 0) return -1;

            for (var i = 0; i < Math.Min(40, itemCount); i++)
            {
                var sealReward = addon->AtkValues[265 + i].UInt;
                var container  = (InventoryType)addon->AtkValues[345 + i].UInt;
                var slot       = addon->AtkValues[385 + i].UInt;
                var itemID     = addon->AtkValues[425 + i].UInt;

                if (itemID != ItemID || slot != Slot || container != Container || sealReward != SealReward) continue;
                return i;
            }

            return -1;
        }

        public bool HasMateria()
        {
            if (!LuminaGetter.TryGetRow<Item>(ItemID, out var row)) return false;
            if (row.MateriaSlotCount <= 0) return false;

            for (var i = 0; i < Math.Min(row.MateriaSlotCount, GetSlot()->Materia.Length); i++)
            {
                var materia = GetSlot()->Materia[i];
                if (materia != 0) return true;
            }

            return false;
        }

        public bool IsHQ() => GetSlot()->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

        public bool IsUltimateTotemExchangeItem() => UltimateTotemExchangeItemIDs.Contains(ItemID);

        public InventoryItem* GetSlot() => InventoryManager.Instance()->GetInventorySlot(Container, Slot);

        public override string ToString() => $"ExpertDeliveryItem-{ItemID}_{Container}_{Slot}_{SealReward}";
    }

    #region 常量

    private static readonly FrozenDictionary<uint, (uint EventID, uint DataID)> ZoneInfo = new Dictionary<uint, (uint EventID, uint DataID)>
    {
        // 黑涡团
        [128] = (1441793, 1002388),
        // 双蛇党
        [132] = (1441794, 1002394),
        // 恒辉队
        [130] = (1441795, 1002391)
    }.ToFrozenDictionary();

    private static readonly FrozenSet<uint> UltimateTotemItemIDs = new HashSet<uint>
    {
        21197, // 龙神图腾
        23175, // 究极图腾
        28633, // 机神城图腾
        36810, // 龙诗图腾
        38951, // 欧米茄图腾
        44743, // 巫女图腾
        52321  // 小丑图腾
    }.ToFrozenSet();

    private static FrozenSet<uint> UltimateTotemExchangeItemIDs
    {
        get
        {
            if (field != null) return field;

            HashSet<uint> itemIDs = [];

            foreach (var shop in LuminaGetter.Get<SpecialShop>())
            {
                foreach (var entry in shop.Item)
                {
                    if (!entry.ItemCosts.Any(x => UltimateTotemItemIDs.Contains(x.ItemCost.RowId))) continue;

                    foreach (var receiveItem in entry.ReceiveItems)
                    {
                        if (receiveItem.Item.RowId == 0) continue;

                        var item = receiveItem.Item.Value;
                        if (item.FilterGroup is 1 or 2 or 3)
                            itemIDs.Add(item.RowId);
                    }
                }
            }

            return field = itemIDs.ToFrozenSet();
        }
    }

    #endregion

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy")]
    private IPCSubscriber<bool> IsCurrentlyBusyIPC;

    [IPCSubscriber("DailyRoutines.Modules.FastGrandCompanyExchange.EnqueueByName")]
    private IPCSubscriber<string, int, bool> EnqueueByNameIPC;

    #endregion
}
