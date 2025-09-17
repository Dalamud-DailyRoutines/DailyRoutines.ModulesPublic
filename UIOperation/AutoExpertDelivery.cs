using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Addon;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.TabBar;
using Lumina.Excel.Sheets;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoExpertDelivery : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoExpertDeliveryTitle"),
        Description         = GetLoc("AutoExpertDeliveryDescription"),
        Category            = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["FastGrandCompanyExchange"]
    };

    private static Config ModuleConfig = null!;
    
    private static DRAutoExpertDelivery? Addon;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new() { TimeLimitMS = int.MaxValue };
        
        Addon ??= new(this)
        {
            InternalName          = "DRAutoExpertDelivery",
            Title                 = Info.Title,
            Size                  = new(300f, 250f),
            Position              = new(800f, 350f),
            NativeController      = Service.AddonController,
            RememberClosePosition = true,
        };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "GrandCompanySupplyList", OnAddonSupplyList);
        if (IsAddonAndNodesReady(GrandCompanySupplyList)) 
            OnAddonSupplyList(AddonEvent.PostSetup, null);
    }
    
    private bool? EnqueueDelivery()
    {
        if (GrandCompanySupplyReward != null)
        {
            if (!IsAddonAndNodesReady(GrandCompanySupplyReward)) return false;
            
            ((AddonGrandCompanySupplyReward*)GrandCompanySupplyReward)->DeliverButton->ClickAddonButton(GrandCompanySupplyReward);

            TaskHelper.Abort();
            TaskHelper.Enqueue(EnqueueRefresh);
            TaskHelper.Enqueue(EnqueueDelivery);
            return true;
        }

        if (SelectYesno != null)
        {
            var state = ClickSelectYesnoYes();
            if (!state) return false;
            
            TaskHelper.Abort();
            TaskHelper.Enqueue(EnqueueDelivery);
            return true;
        }

        if (GrandCompanySupplyList != null)
        {
            if (!IsAddonAndNodesReady(GrandCompanySupplyList)         ||
                AgentGrandCompanySupply.Instance()->ItemArray == null ||
                GrandCompanySupplyList->AtkValues->UInt       != 2)
                return false;
            
            var items = ExpertDeliveryItem.Parse().Where(x => x.GetIndex() != -1 && !x.IsNeedToSkip()).ToList();
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

        if (!DService.Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            TaskHelper.Abort();
            return true;
        }

        return false;
    }

    private void EnqueueGrandCompanyExchangeOpen(bool isAutoExchange)
    {
        if (!ZoneInfo.TryGetValue(DService.ClientState.TerritoryType, out var info)) return;

        TaskHelper.Enqueue(() =>
        {
            if (!OccupiedInEvent) return true;

            if (IsAddonAndNodesReady(GrandCompanySupplyList))
                GrandCompanySupplyList->Close(true);

            if (IsAddonAndNodesReady(SelectString))
                SelectString->Close(true);

            return false;
        });

        TaskHelper.Enqueue(() => new EventStartPackt(DService.ObjectTable.LocalPlayer.GameObjectId, info.EventID).Send());
        TaskHelper.Enqueue(() => IsAddonAndNodesReady(GrandCompanyExchange));

        if (isAutoExchange && ModuleManager.IsModuleEnabled(typeof(FastGrandCompanyExchange)))
        {
            TaskHelper.Enqueue(() => ModuleManager.GetModule<FastGrandCompanyExchange>().EnqueueByName("default"));
            TaskHelper.Enqueue(() => ModuleManager.GetModule<FastGrandCompanyExchange>().IsExchanging);
            TaskHelper.Enqueue(() => !ModuleManager.GetModule<FastGrandCompanyExchange>().IsExchanging);
            TaskHelper.Enqueue(() => GrandCompanyExchange->Close(true));
        }

        // 还有没交的
        if (GrandCompanySupplyList->AtkValues[8].UInt != 0)
        {
            TaskHelper.Enqueue(() => !IsAddonAndNodesReady(GrandCompanyExchange) && !OccupiedInEvent);
            TaskHelper.Enqueue(() => DService.ObjectTable
                                             .FirstOrDefault(x => x.ObjectKind == ObjectKind.EventNpc && x.DataId == info.DataID)
                                             .TargetInteract());
            TaskHelper.Enqueue(() => ClickSelectString(0));
            if (isAutoExchange)
                TaskHelper.Enqueue(EnqueueDelivery);
        }
    }

    private static bool? EnqueueRefresh()
    {
        if (GrandCompanySupplyReward != null              ||
            !IsAddonAndNodesReady(GrandCompanySupplyList) ||
            AgentGrandCompanySupply.Instance()->ItemArray == null)
            return false;

        SendEvent(AgentId.GrandCompanySupply, 0, 0, 2);
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
            buffMultiplier += DService.ObjectTable.LocalPlayer.StatusList[index].Param / 100f;
        
        var companySeals   = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        var capAmount      = rank.MaxSeals;
        if (companySeals + (uint)(sealReward * buffMultiplier) > capAmount)
        {
            NotificationInfo(GetLoc("AutoExpertDelivery-ReachdSealCap")); 
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
        
                if (ModuleConfig.AutoSwitchWhenOpen)
                    Callback(GrandCompanySupplyList, true, 0, ModuleConfig.DefaultPage);
                break;
            case AddonEvent.PostDraw:
                if (TaskHelper.IsBusy || Addon.IsOpen || !IsAddonAndNodesReady(GrandCompanySupplyList)) return;
                Addon.Open();
                break;
        }
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonSupplyList);
        
        Addon?.Dispose();
        Addon = null;
    }

    private class Config : ModuleConfiguration
    {
        public bool SkipWhenHQ         = true;
        public bool SkipWhenMateria    = true;
        
        public bool AutoSwitchWhenOpen = true;

        public int DefaultPage = 2;
    }

    private class DRAutoExpertDelivery(AutoExpertDelivery Instance) : NativeAddon
    {
        private static VerticalListNode ControlTabLayout;
        private static VerticalListNode SettingTabLayout;

        private static List<CheckboxNode> DefaultPageCheckboxes = [];
        
        protected override void OnSetup(AtkUnitBase* addon)
        {
            DefaultPageCheckboxes.Clear();
            
            var tabNode = new TabBarNode
            {
                IsVisible = true,
                Size      = new(275, 28),
                Position  = ContentStartPosition - new Vector2(0, 10),
            };

            var tabContentPosition = tabNode.Position + new Vector2(0, tabNode.Size.Y + 5f);
            
            tabNode.AddTab(GetLoc("Operation"), () =>
            {
                ControlTabLayout.IsVisible = true;
                SettingTabLayout.IsVisible = false;
            });
            
            tabNode.AddTab(GetLoc("Settings"), () =>
            {
                ControlTabLayout.IsVisible = false;
                SettingTabLayout.IsVisible = true;
            });
            
            AttachNode(tabNode);
            
            ControlTabLayout = new()
            {
                IsVisible   = true,
                Position    = tabContentPosition + new Vector2(0, 5),
            };

            var startNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                SeString  = GetLoc("Start"),
                OnClick = () =>
                {
                    if (Instance.TaskHelper.IsBusy) return;
                    Instance.EnqueueDelivery();
                }
            };
            
            var stopNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                SeString  = GetLoc("Stop"),
                OnClick = () =>
                {
                    if (!Instance.TaskHelper.IsBusy) return;
                    Instance.TaskHelper.Abort();
                }
            };
            
            var exchangeShopNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 10, 38),
                SeString  = LuminaWrapper.GetAddonText(3280),
                OnClick = () =>
                {
                    if (Instance.TaskHelper.IsBusy) return;
                    Instance.EnqueueGrandCompanyExchangeOpen(false);
                }
            };
            
            var exchangeShopAndExchangeNode = new TextButtonNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(tabNode.Size.X - 5, 38),
                SeString  = $"{LuminaWrapper.GetAddonText(3280)} [{GetLoc("Exchange")}]",
                OnClick = () =>
                {
                    if (Instance.TaskHelper.IsBusy) return;
                    Instance.EnqueueGrandCompanyExchangeOpen(true);
                }
            };
            
            ControlTabLayout.AddNode(startNode, stopNode, exchangeShopNode, exchangeShopAndExchangeNode);
            AttachNode(ControlTabLayout);
            
            SettingTabLayout = new()
            {
                Position    = tabContentPosition + new Vector2(5, 3),
                FitContents = true,
            };

            var skipHQSettingNode = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                IsChecked = ModuleConfig.SkipWhenHQ,
                Size      = new(100, 27),
                SeString  = GetLoc("AutoExpertDelivery-SkipHQ"),
                OnClick = x =>
                {
                    ModuleConfig.SkipWhenHQ = x;
                    ModuleConfig.Save(Instance);
                }
            };
            
            skipHQSettingNode.Label.Width = tabNode.Size.X - 20;
            while (skipHQSettingNode.Label.FontSize                                      >= 1 && 
                   skipHQSettingNode.Label.GetTextDrawSize(skipHQSettingNode.SeString).X > skipHQSettingNode.Label.Width)
                skipHQSettingNode.Label.FontSize--;
            skipHQSettingNode.Height = skipHQSettingNode.Label.FontSize * 1.5f;
            
            SettingTabLayout.AddNode(skipHQSettingNode);
            
            var skipMateriaSettingNode = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                IsChecked = ModuleConfig.SkipWhenMateria,
                Size      = new(100, 27),
                SeString  = GetLoc("AutoExpertDelivery-SkipMaterias"),
                OnClick = x =>
                {
                    ModuleConfig.SkipWhenMateria = x;
                    ModuleConfig.Save(Instance);
                }
            };
            
            skipMateriaSettingNode.Label.Width = tabNode.Size.X - 20;
            while (skipMateriaSettingNode.Label.FontSize                                           >= 1 && 
                   skipMateriaSettingNode.Label.GetTextDrawSize(skipMateriaSettingNode.SeString).X > skipMateriaSettingNode.Label.Width)
                skipMateriaSettingNode.Label.FontSize--;
            skipMateriaSettingNode.Height = skipMateriaSettingNode.Label.FontSize * 1.5f;
            
            SettingTabLayout.AddNode(skipMateriaSettingNode);
            SettingTabLayout.AddDummy(5f);

            var defaultPageTitleNode = new TextNode
            {
                IsVisible = true,
                Size      = new(tabNode.Size.X - 20, 27),
                FontSize  = 16,
                SeString  = GetLoc("AutoExpertDelivery-DefaultPage"),
            };
            
            while (defaultPageTitleNode.FontSize                                         >= 1 && 
                   defaultPageTitleNode.GetTextDrawSize(defaultPageTitleNode.SeString).X > defaultPageTitleNode.Width)
                defaultPageTitleNode.FontSize--;
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
                    IsChecked = ModuleConfig.DefaultPage == i,
                    Size      = new(100, 27),
                    SeString  = LuminaWrapper.GetAddonText(4572 + i)
                };
                
                defaultPageNode.OnClick = x =>
                {
                    if (!x)
                    {
                        defaultPageNode.IsChecked = true;
                        return;
                    }

                    ModuleConfig.DefaultPage = (int)index;
                    ModuleConfig.Save(Instance);

                    for (var d = 0; d < DefaultPageCheckboxes.Count; d++)
                    {
                        var node = DefaultPageCheckboxes[d];
                        node.IsChecked = ModuleConfig.DefaultPage == d;
                    }
                };
                
                DefaultPageCheckboxes.Add(defaultPageNode);
                SettingTabLayout.AddNode(defaultPageNode);
            }

            AttachNode(SettingTabLayout);
        }

        protected override void OnUpdate(AtkUnitBase* addon)
        {
            if (GrandCompanySupplyList == null)
            {
                Close();
                return;
            }

            var position = new Vector2(GrandCompanySupplyList->RootNode->ScreenX - addon->GetScaledWidth(true), GrandCompanySupplyList->RootNode->ScreenY);
            SetPosition(addon,           position);
            SetPosition(addon->RootNode, position);
        }

        protected override void OnFinalize(AtkUnitBase* addon) 
        {
            if (GrandCompanySupplyList == null || Instance.TaskHelper.IsBusy) return;
            GrandCompanySupplyList->Close(true);
        }
    }
    
    private record ExpertDeliveryItem(uint ItemID, InventoryType Container, ushort Slot, uint SealReward)
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

        public void HandIn() => Callback(GrandCompanySupplyList, true, 1, GetIndex());

        public bool IsNeedToSkip()
        {
            if (GetSlot() == null) return true;
            if (ModuleConfig.SkipWhenHQ && IsHQ()) return true;
            if (ModuleConfig.SkipWhenMateria && HasMateria()) return true;

            return false;
        }
        
        public int GetIndex()
        {
            var agent = AgentGrandCompanySupply.Instance();
            if (agent == null) return -1;

            var addon = GrandCompanySupplyList;
            if (!IsAddonAndNodesReady(addon)) return -1;

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
    
    private static readonly Dictionary<uint, (uint EventID, uint DataID)> ZoneInfo = new()
    {
        // 黑涡团
        [128] = (1441793, 1002388),
        // 双蛇党
        [132] = (1441794, 1002394),
        // 恒辉队
        [130] = (1441795, 1002391),
    };
}
