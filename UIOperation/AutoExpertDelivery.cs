using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.ModulesPublic;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.Modules;

public unsafe class AutoExpertDelivery : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = GetLoc("AutoExpertDeliveryTitle"),
        Description         = GetLoc("AutoExpertDeliveryDescription"),
        Category            = ModuleCategories.UIOperation,
        ModulesPrerequisite = ["FastGrandCompanyExchange"]
    };

    private class Config : ModuleConfiguration
    {
        public bool SkipWhenHQ         = true;
        public bool SkipWhenMateria    = true;
        public bool AutoSwitchWhenOpen = true;
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

    private static Config ModuleConfig = null!;
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new() { TimeLimitMS = int.MaxValue };
        Overlay ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "GrandCompanySupplyList", OnAddonSupplyList);
        if (IsAddonAndNodesReady(GrandCompanySupplyList)) 
            OnAddonSupplyList(AddonEvent.PostSetup, null);
    }

    public override void OverlayUI()
    {
        var addon = GrandCompanySupplyList;
        if (!IsAddonAndNodesReady(addon) || addon->AtkValues[5].UInt != 2) return;

        using var font = FontManager.UIFont80.Push();
        
        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);
        
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoExpertDeliveryTitle"));

        DrawGrandCompanyInfo();
        
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("AutoExpertDelivery-SkipHQ"), ref ModuleConfig.SkipWhenHQ))
                SaveConfig(ModuleConfig);
            
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("AutoExpertDelivery-SkipMaterias"), ref ModuleConfig.SkipWhenMateria))
                SaveConfig(ModuleConfig);
            
            if (ImGui.Checkbox(GetLoc("AutoExpertDelivery-AutoSwitch"), ref ModuleConfig.AutoSwitchWhenOpen))
            {
                if (ModuleConfig.AutoSwitchWhenOpen)
                {
                    var buttonNode = GrandCompanySupplyList->GetNodeById(13)->GetAsAtkComponentRadioButton();
                    buttonNode->ClickAddonRadioButton(GrandCompanySupplyList, 4);
                }
                SaveConfig(ModuleConfig);
            }
        }

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                EnqueueDelivery();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }
        
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(LuminaGetter.GetRow<Addon>(3280)!.Value.Text.ExtractText()))
                EnqueueGrandCompanyExchangeOpen(false);

            ImGuiOm.DisableZoneWithHelp(() =>
            {
                ImGui.SameLine();
                if (ImGui.Button($"{LuminaGetter.GetRow<Addon>(3280)!.Value.Text.ExtractText()} ({GetLoc("Exchange")})"))
                    EnqueueGrandCompanyExchangeOpen(true);
            }, 
            [
                new(!ModuleManager.IsModuleEnabled(typeof(FastGrandCompanyExchange)), 
                    $"{GetLoc("Module")}: {GetLoc("FastGrandCompanyExchangeTitle")}")
            ],
            GetLoc("DisableZoneHeader"));
        }

        void EnqueueGrandCompanyExchangeOpen(bool isAutoExchange)
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
    }

    private static void DrawGrandCompanyInfo()
    {
        var playerState = PlayerState.Instance();
        var rank        = playerState->GetGrandCompanyRank();
        var rankText = (GrandCompany)playerState->GrandCompany switch
        {
            GrandCompany.Maelstrom      => LuminaGetter.GetRow<GCRankLimsaMaleText>(rank)?.Singular.ExtractText(),
            GrandCompany.TwinAdder      => LuminaGetter.GetRow<GCRankGridaniaMaleText>(rank)?.Singular.ExtractText(),
            GrandCompany.ImmortalFlames => LuminaGetter.GetRow<GCRankUldahMaleText>(rank)?.Singular.ExtractText(),
            _                           => string.Empty,
        };
        if (string.IsNullOrEmpty(rankText)) return;

        if (!LuminaGetter.TryGetRow<GrandCompanyRank>(rank, out var rankData)) return;
        var iconID = (GrandCompany)playerState->GrandCompany switch
        {
            GrandCompany.Maelstrom      => rankData.IconMaelstrom,
            GrandCompany.TwinAdder      => rankData.IconSerpents,
            GrandCompany.ImmortalFlames => rankData.IconFlames,
            _                           => 0,
        };
        if (iconID == 0) return;

        var icon = DService.Texture.GetFromGameIcon(new((uint)iconID)).GetWrapOrDefault();
        if (icon == null) return;

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.Image(icon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
            
            ImGui.SameLine();
            ImGui.Text(rankText);
        }
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

        var companySeals = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        var capAmount    = rank.MaxSeals;
        if (companySeals + sealReward > capAmount)
        {
            NotificationInfo(GetLoc("AutoExpertDelivery-ReachdSealCap")); 
            return true;
        }
        
        return false;
    }

    // 悬浮窗控制
    private void OnAddonSupplyList(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };

        if (ModuleConfig.AutoSwitchWhenOpen && type == AddonEvent.PostSetup && GrandCompanySupplyList != null)
            Callback(GrandCompanySupplyList, true, 0, 2);
    }
    
    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonSupplyList);

        base.Uninit();
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
}
