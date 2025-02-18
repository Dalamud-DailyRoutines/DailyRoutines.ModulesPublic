using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ClickLib.Clicks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Infos.Clicks;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.Modules;

public unsafe class AutoExpertDelivery : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoExpertDeliveryTitle"),
        Description = GetLoc("AutoExpertDeliveryDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private class Config : ModuleConfiguration
    {
        public bool SkipWhenHQ         = true;
        public bool SkipWhenMateria    = true;
        public bool AutoSwitchWhenOpen = true;
    }

    private static readonly Dictionary<uint, uint> ZoneToEventID = new()
    {
        // 黑涡团
        [128] = 1441793,
        // 双蛇党
        [132] = 1441794,
        // 恒辉队
        [130] = 1441795,
    };

    private static Config ModuleConfig = null!;
    
    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new();
        Overlay ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "GrandCompanySupplyList", OnAddonSupplyList);
        if (IsAddonAndNodesReady(GrandCompanySupplyList)) OnAddonSupplyList(AddonEvent.PostSetup, null);
    }
    
    public override void OverlayUI()
    {
        var addon = GrandCompanySupplyList;
        if (!IsAddonAndNodesReady(addon)) return;

        using var font = FontManager.UIFont80.Push();
        
        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);
        
        ImGui.TextColored(LightSkyBlue, GetLoc("AutoExpertDeliveryTitle"));

        var playerState = PlayerState.Instance();
        var rank        = playerState->GetGrandCompanyRank();
        var rankText = (GrandCompany)playerState->GrandCompany switch
        {
            GrandCompany.Maelstrom      => LuminaCache.GetRow<GCRankLimsaMaleText>(rank)?.Singular.ExtractText(),
            GrandCompany.TwinAdder      => LuminaCache.GetRow<GCRankGridaniaMaleText>(rank)?.Singular.ExtractText(),
            GrandCompany.ImmortalFlames => LuminaCache.GetRow<GCRankUldahMaleText>(rank)?.Singular.ExtractText(),
            _                           => string.Empty,
        };
        if (string.IsNullOrEmpty(rankText)) return;

        if (!LuminaCache.TryGetRow<GrandCompanyRank>(rank, out var rankData)) return;
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
        
        ImGuiOm.HelpMarker($"{Lang.Get("ConflictKey")}: {Service.Config.ConflictKey}");
        
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
                    ClickGrandCompanySupplyList.Using((nint)addon).ExpertDelivery();
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
        
        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(LuminaCache.GetRow<Addon>(3280)!.Value.Text.ExtractText()))
            {
                if (!ZoneToEventID.TryGetValue(DService.ClientState.TerritoryType, out var eventID)) return;
                
                TaskHelper.Enqueue(() =>
                {
                    if (!OccupiedInEvent) return true;
                    
                    if (IsAddonAndNodesReady(GrandCompanySupplyList))
                        GrandCompanySupplyList->Close(true);
                    
                    if (IsAddonAndNodesReady(SelectString))
                        SelectString->Close(true);

                    return false;
                });

                TaskHelper.Enqueue(
                    () => GamePacketManager.SendPackt(
                        new EventStartPackt(DService.ClientState.LocalPlayer.GameObjectId, eventID)));
            }
        }
    }

    private void EnqueueDelivery()
    {
        foreach (var item in ExpertDeliveryItem.Parse().Where(x => x.GetIndex() != -1))
        {
            TaskHelper.Enqueue(() =>
            {
                if (item.HandIn())
                {
                    if (item.IsHQ() || item.HasMateria())
                    {
                        TaskHelper.Enqueue(() => IsAddonAndNodesReady(SelectYesno), null, null, null, 2);
                        TaskHelper.Enqueue(() => ClickSelectYesNo.Using((nint)SelectYesno).Yes(),
                                           null, null, null, 2);
                    }
                            
                    TaskHelper.Enqueue(() => IsAddonAndNodesReady(GrandCompanySupplyReward), null, null, null, 2);
                    TaskHelper.Enqueue(
                        () => ClickGrandCompanySupplyReward.Using((nint)GrandCompanySupplyReward).Deliver(),
                        null, null, null, 2);
                    
                    TaskHelper.Enqueue(() => ExpertDeliveryItem.IsAddonReady(), null, null, null, 2);
                    TaskHelper.Enqueue(() => ExpertDeliveryItem.Refresh(),      null, null, null, 2);
                    TaskHelper.Enqueue(() => ExpertDeliveryItem.IsAddonReady(), null, null, null, 2);
                    TaskHelper.Enqueue(() => EnqueueDelivery(),                 null, null, null, 2);
                }
            });
            
            break;
        }
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

        if (ModuleConfig.AutoSwitchWhenOpen && type == AddonEvent.PostSetup)
            ClickGrandCompanySupplyList.Using((nint)GrandCompanySupplyList).ExpertDelivery();
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

        public static void Refresh() => AgentGrandCompanySupply.Instance()->Show();

        public static bool IsAddonReady()
            => GrandCompanySupplyReward                      == null &&
               AgentGrandCompanySupply.Instance()->ItemArray != null &&
               GrandCompanySupplyList->AtkValues->UInt       == 2;

        public bool HandIn()
        {
            if (IsNeedToSkip()) return false;
            SendEvent(AgentId.GrandCompanySupply, 0, 1, GetIndex());
            return true;
        }

        public bool IsNeedToSkip()
        {
            if (GetSlot() == null) return true;
            if (ModuleConfig.SkipWhenHQ && IsHQ()) return true;
            if (ModuleConfig.SkipWhenMateria && HasMateria()) return true;

            var grandCompany = PlayerState.Instance()->GrandCompany;
            if ((GrandCompany)grandCompany == GrandCompany.None) return true;

            if (!LuminaCache.TryGetRow<GrandCompanyRank>(PlayerState.Instance()->GetGrandCompanyRank(), out var rank))
                return true;

            var companySeals = InventoryManager.Instance()->GetCompanySeals(grandCompany);
            var capAmount = rank.MaxSeals;
            if (companySeals + SealReward > capAmount) return true;

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

        public bool HasMateria() => GetSlot()->Materia.ToArray().Count(x => x != 0 && x <= 25) > 0;

        public bool IsHQ() => GetSlot()->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        
        public InventoryItem* GetSlot() => InventoryManager.Instance()->GetInventorySlot(Container, Slot);

        public override string ToString() => $"ExpertDeliveryItem-{ItemID}_{Container}_{Slot}_{SealReward}";
    }
}
