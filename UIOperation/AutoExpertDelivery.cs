using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Extensions;
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
using KamiToolKit;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Abstractions;
using OmenTools.Dalamud.Attributes;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoExpertDelivery : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoExpertDeliveryTitle"),
        Description         = Lang.Get("AutoExpertDeliveryDescription"),
        Category            = ModuleCategory.UIOperation,
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

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostDraw,  "GrandCompanySupplyList", OnAddonSupplyList);
        if (GrandCompanySupplyList->IsAddonAndNodesReady())
            OnAddonSupplyList(AddonEvent.PostSetup, null);
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

        if (!DService.Instance().Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }

    private void EnqueueGrandCompanyExchangeOpen(bool isAutoExchange)
    {
        if (!ZoneInfo.TryGetValue(GameState.TerritoryType, out var info)) return;

        TaskHelper.Enqueue
        (() =>
            {
                if (!DService.Instance().Condition.IsOccupiedInEvent) return true;

                if (GrandCompanySupplyList->IsAddonAndNodesReady())
                    GrandCompanySupplyList->Close(true);

                if (SelectString->IsAddonAndNodesReady())
                    SelectString->Close(true);

                return false;
            }
        );

        TaskHelper.Enqueue(() => new EventStartPackt(DService.Instance().ObjectTable.LocalPlayer.GameObjectID, info.EventID).Send());
        TaskHelper.Enqueue(() => GrandCompanyExchange->IsAddonAndNodesReady());

        if (isAutoExchange && ModuleManager.Instance().IsModuleEnabled(typeof(FastGrandCompanyExchange)))
        {
            TaskHelper.Enqueue(() => EnqueueByNameIPC.InvokeFunc("default", -1));
            TaskHelper.Enqueue(() => IsCurrentlyBusyIPC.Value);
            TaskHelper.Enqueue(() => !IsCurrentlyBusyIPC.Value);
            TaskHelper.Enqueue(() => GrandCompanyExchange->Close(true));
        }

        // 还有没交的
        if (GrandCompanySupplyList->AtkValues[8].UInt != 0)
        {
            TaskHelper.Enqueue(() => !GrandCompanyExchange->IsAddonAndNodesReady() && !DService.Instance().Condition.IsOccupiedInEvent);
            TaskHelper.Enqueue
            (() => DService.Instance().ObjectTable
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

    private static bool IsAboutToReachTheCap(uint sealReward)
    {
        var grandCompany = PlayerState.Instance()->GrandCompany;
        if ((GrandCompany)grandCompany == GrandCompany.None) return true;

        if (!LuminaGetter.TryGetRow<GrandCompanyRank>(PlayerState.Instance()->GetGrandCompanyRank(), out var rank))
            return true;

        var buffMultiplier = 1f;
        if (LocalPlayerState.HasStatus(1078, out var index) || LocalPlayerState.HasStatus(414, out index))
            buffMultiplier += DService.Instance().ObjectTable.LocalPlayer.StatusList[index].Param / 100f;

        var companySeals = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        var capAmount    = rank.MaxSeals;

        if (companySeals + (uint)(sealReward * buffMultiplier) > capAmount)
        {
            NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoExpertDelivery-ReachdSealCap"));
            return true;
        }

        return false;
    }

    // 悬浮窗控制
    private void OnAddonSupplyList(AddonEvent type, AddonArgs? args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                if (GrandCompanySupplyList == null) return;

                if (config.AutoSwitchWhenOpen)
                    GrandCompanySupplyList->Callback(0, config.DefaultPage);
                break;
            case AddonEvent.PostDraw:
                if (TaskHelper.IsBusy || addonExpertDelivery.IsOpen || !GrandCompanySupplyList->IsAddonAndNodesReady()) return;
                addonExpertDelivery.Open();
                break;
        }
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddonSupplyList);

        addonExpertDelivery?.Dispose();
        addonExpertDelivery = null;
    }

    private class Config : ModuleConfig
    {
        public bool AutoSwitchWhenOpen = true;

        public int  DefaultPage     = 2;
        public bool SkipWhenHQ      = true;
        public bool SkipWhenMateria = true;
    }

    private class DRAutoExpertDelivery
    (
        AutoExpertDelivery instance
    ) : NativeAddon
    {
        private static VerticalListNode ControlTabLayout;
        private static VerticalListNode SettingTabLayout;

        private static List<CheckboxNode> DefaultPageCheckboxes = [];

        protected override void OnSetup(AtkUnitBase* addon)
        {
            // 禁止 ESC 键关闭
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x4, true);

            // 禁止聚焦
            FlagHelper.UpdateFlag(ref addon->Flags1A0, 0x80, true);

            // 禁止自动聚焦
            FlagHelper.UpdateFlag(ref addon->Flags1A1, 0x40, true);

            // 禁止右键菜单
            FlagHelper.UpdateFlag(ref addon->Flags1A3, 0x1, true);

            DefaultPageCheckboxes.Clear();

            var tabNode = new TabBarNode
            {
                IsVisible = true,
                Size      = new(275, 28),
                Position  = ContentStartPosition
            };

            var tabContentPosition = tabNode.Position + new Vector2(0, tabNode.Size.Y + 5f);

            tabNode.AddTab
            (
                Lang.Get("Operation"),
                () =>
                {
                    ControlTabLayout.IsVisible = true;
                    SettingTabLayout.IsVisible = false;
                }
            );

            tabNode.AddTab
            (
                Lang.Get("Settings"),
                () =>
                {
                    ControlTabLayout.IsVisible = false;
                    SettingTabLayout.IsVisible = true;
                }
            );

            tabNode.AttachNode(this);

            ControlTabLayout = new()
            {
                IsVisible = true,
                Position  = tabContentPosition + new Vector2(0, 5)
            };

            var startNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                String    = Lang.Get("Start"),
                OnClick = () =>
                {
                    if (instance.TaskHelper.IsBusy) return;
                    instance.EnqueueDelivery();
                }
            };

            var stopNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                String    = Lang.Get("Stop"),
                OnClick = () =>
                {
                    if (!instance.TaskHelper.IsBusy) return;
                    instance.TaskHelper.Abort();
                }
            };

            var exchangeShopNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                String    = LuminaWrapper.GetAddonText(3280),
                OnClick = () =>
                {
                    if (instance.TaskHelper.IsBusy) return;
                    instance.EnqueueGrandCompanyExchangeOpen(false);
                }
            };

            var exchangeShopAndExchangeNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 5, 38),
                String    = $"{LuminaWrapper.GetAddonText(3280)} [{Lang.Get("Exchange")}]",
                OnClick = () =>
                {
                    if (instance.TaskHelper.IsBusy) return;
                    instance.EnqueueGrandCompanyExchangeOpen(true);
                }
            };

            ControlTabLayout.AddNode([startNode, stopNode, exchangeShopNode, exchangeShopAndExchangeNode]);
            ControlTabLayout.AttachNode(this);

            SettingTabLayout = new()
            {
                IsVisible   = false,
                Position    = tabContentPosition + new Vector2(5, 3),
                FitContents = true
            };

            var skipHQSettingNode = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                IsChecked = instance.config.SkipWhenHQ,
                Size      = new(100, 27),
                String    = Lang.Get("AutoExpertDelivery-SkipHQ"),
                OnClick = x =>
                {
                    instance.config.SkipWhenHQ = x;
                    instance.config.Save(instance);
                }
            };

            skipHQSettingNode.Label.Width = tabNode.Size.X - 20;
            skipHQSettingNode.Label.AutoAdjustTextSize();
            skipHQSettingNode.Height = skipHQSettingNode.Label.FontSize * 1.5f;

            SettingTabLayout.AddNode(skipHQSettingNode);

            var skipMateriaSettingNode = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                IsChecked = instance.config.SkipWhenMateria,
                Size      = new(100, 27),
                String    = Lang.Get("AutoExpertDelivery-SkipMaterias"),
                OnClick = x =>
                {
                    instance.config.SkipWhenMateria = x;
                    instance.config.Save(instance);
                }
            };

            skipMateriaSettingNode.Label.Width = tabNode.Size.X - 20;
            skipMateriaSettingNode.Label.AutoAdjustTextSize();
            skipMateriaSettingNode.Height = skipMateriaSettingNode.Label.FontSize * 1.5f;

            SettingTabLayout.AddNode(skipMateriaSettingNode);
            SettingTabLayout.AddDummy(5f);

            var defaultPageTitleNode = new TextNode
            {
                IsVisible = true,
                Size      = new(tabNode.Size.X - 20, 27),
                FontSize  = 16,
                String    = Lang.Get("AutoExpertDelivery-DefaultPage")
            };

            defaultPageTitleNode.AutoAdjustTextSize();
            defaultPageTitleNode.Height = defaultPageTitleNode.FontSize * 1.5f;

            SettingTabLayout.AddNode(defaultPageTitleNode);
            SettingTabLayout.AddDummy(3f);

            for (var i = 0U; i < 3; i++)
            {
                var index = i;

                var defaultPageNode = new CheckboxNode
                {
                    IsVisible = true,
                    IsEnabled = true,
                    IsChecked = instance.config.DefaultPage == i,
                    Size      = new(100, 27),
                    String    = LuminaWrapper.GetAddonText(4572 + i)
                };

                defaultPageNode.OnClick = x =>
                {
                    if (!x)
                    {
                        defaultPageNode.IsChecked = true;
                        return;
                    }

                    instance.config.DefaultPage = (int)index;
                    instance.config.Save(instance);

                    for (var d = 0; d < DefaultPageCheckboxes.Count; d++)
                    {
                        var node = DefaultPageCheckboxes[d];
                        node.IsChecked = instance.config.DefaultPage == d;
                    }
                };

                DefaultPageCheckboxes.Add(defaultPageNode);
                SettingTabLayout.AddNode(defaultPageNode);
            }

            SettingTabLayout.AttachNode(this);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (GrandCompanySupplyList == null)
            {
                Close();
                return;
            }

            var position = new Vector2
            (
                GrandCompanySupplyList->RootNode->ScreenX - addon->GetScaledWidth(true),
                GrandCompanySupplyList->RootNode->ScreenY
            );

            SetWindowPosition(position);
        }

        protected override void OnFinalize(AtkUnitBase* addon)
        {
            if (GrandCompanySupplyList == null || instance.TaskHelper.IsBusy) return;
            GrandCompanySupplyList->Close(true);
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
                if (item.ItemId == 0 || item.IsBonusReward || item.ExpReward > 0 || item.SealReward <= 0) continue;
                returnValues.Add(new(item.ItemId, item.Inventory, item.Slot, (uint)item.SealReward));
            }

            return returnValues;
        }

        public void HandIn() => GrandCompanySupplyList->Callback(1, GetIndex());

        public bool IsNeedToSkip(AutoExpertDelivery instance)
        {
            if (GetSlot() == null) return true;
            if (instance.config.SkipWhenHQ      && IsHQ()) return true;
            if (instance.config.SkipWhenMateria && HasMateria()) return true;

            return false;
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

    #endregion

    #region IPC

    [IPCSubscriber("DailyRoutines.Modules.FastGrandCompanyExchange.IsBusy")]
    private IPCSubscriber<bool> IsCurrentlyBusyIPC;
    
    [IPCSubscriber("DailyRoutines.Modules.FastGrandCompanyExchange.EnqueueByName")]
    private IPCSubscriber<string, int, bool> EnqueueByNameIPC;

    #endregion
}
