using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRepair : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoRepairTitle"),
        Description = GetLoc("AutoRepairDescription"),
        Category    = ModuleCategories.General,
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    public bool IsBusy => TaskHelper?.IsBusy ?? false;

    private static readonly HashSet<ConditionFlag> ValidConditions =
    [
        ConditionFlag.InCombat, 
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Gathering, 
        ConditionFlag.Crafting
    ];

    private static Config ModuleConfig = null!;

    // 修理装备
    private static readonly CompSig                   RepairItemSig = new("48 89 6C 24 ?? 48 89 74 24 ?? 41 54 41 56 41 57 48 83 EC ?? 48 8D 0D");
    private delegate        void                      RepairItemDelegate(nint repairController, InventoryType inventory, short slot, bool isNPC);
    private static          Hook<RepairItemDelegate>? RepairItemHook;

    // 批量修理已装备装备
    // unknownBool => *(bool*)((nint)AgentRepair + 49 * sizeof(long)) == 0
    private static readonly CompSig                            RepairEquippedItemsSig = new("E8 ?? ?? ?? ?? EB ?? 83 F8 ?? 7D");
    private delegate        void                               RepairEquippedItemsDelegate(nint repairController, InventoryType inventory, bool isNPC);
    private static          Hook<RepairEquippedItemsDelegate>? RepairEquippedItemsHook;

    private static readonly CompSig                       RepairAllItemsSig = new("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 41 0F B6 E9 45 8B F0 0F B6 F2");
    private delegate        void                          RepairAllItemsDelegate(nint repairController, bool isNPC, int category, int a4);
    private static          Hook<RepairAllItemsDelegate>? RepairAllItemsHook;

    protected override void Init()
    {
        ModuleConfig ??= LoadConfig<Config>() ?? new();
        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };

        RepairItemHook ??= RepairItemSig.GetHook<RepairItemDelegate>(RepairItemDetour);
        RepairItemHook.Enable();

        RepairEquippedItemsHook ??= RepairEquippedItemsSig.GetHook<RepairEquippedItemsDelegate>(RepairEquippedItemsDetour);
        RepairEquippedItemsHook.Enable();

        RepairAllItemsHook ??= RepairAllItemsSig.GetHook<RepairAllItemsDelegate>(RepairAllItemsDetour);
        RepairAllItemsHook.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        DService.Condition.ConditionChange    += OnConditionChanged;
        DService.DutyState.DutyRecommenced    += OnDutyRecommenced;
    }

    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalFontScale);
        ImGui.InputFloat(GetLoc("AutoRepair-RepairThreshold"), ref ModuleConfig.RepairThreshold, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);
        
        if (ImGui.Checkbox(GetLoc("AutoRepair-AllowNPCRepair"), ref ModuleConfig.AllowNPCRepair))
            SaveConfig(ModuleConfig);
        ImGuiOm.HelpMarker(GetLoc("AutoRepair-AllowNPCRepairHelp"), 100f * GlobalFontScale);
        
        if (ModuleConfig.AllowNPCRepair)
        {
            if (ImGui.Checkbox(GetLoc("AutoRepair-PrioritizeNPCRepair"), ref ModuleConfig.PrioritizeNPCRepair))
                SaveConfig(ModuleConfig);
            ImGuiOm.HelpMarker(GetLoc("AutoRepair-PrioritizeNPCRepairHelp"), 100f * GlobalFontScale);
        }
    }

    public void EnqueueRepair()
    {
        if (TaskHelper.IsBusy                      ||
            DService.ClientState.IsPvPExcludingDen ||
            DService.ObjectTable.LocalPlayer is not { CurrentHp: > 0 })
            return;

        var playerState      = PlayerState.Instance();
        var inventoryManager = InventoryManager.Instance();

        if (playerState == null || inventoryManager == null) return;
        
        // 没有需要修理的装备
        if (!TryGetInventoryItems([InventoryType.EquippedItems],
                                 x => x.Condition < ModuleConfig.RepairThreshold * 300f, out var items))
            return;
        
        // 优先委托 NPC 修理
        if (ModuleConfig is { AllowNPCRepair: true, PrioritizeNPCRepair: true } && IsEventIDNearby(720915))
        {
            TaskHelper.Abort();
            TaskHelper.Enqueue(() => IsAbleToRepair());
            TaskHelper.Enqueue(() => NotificationInfo(GetLoc("AutoRepair-RepairNotice"), GetLoc("AutoRepairTitle")));
            TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 720915).Send());
            TaskHelper.Enqueue(() => IsAddonAndNodesReady(Repair));
            TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, 1000));
            TaskHelper.Enqueue(() =>
            {
                if (!IsAddonAndNodesReady(Repair)) return;
                Repair->Close(true);
            });
            
            return;
        }

        List<uint> itemsUnableToRepair = [];
        var repairDMs = LuminaGetter.Get<ItemRepairResource>()
                                   .Where(x => x.Item.RowId != 0)
                                   .ToDictionary(x => x.RowId,
                                                 x => inventoryManager->GetInventoryItemCount(x.Item.RowId));
        
        var isDMInsufficient = false;
        foreach (var itemToRepair in items)
        {
            if (!LuminaGetter.TryGetRow<Item>(itemToRepair.ItemId, out var data)) continue;
            
            var repairJob   = data.ClassJobRepair.RowId;
            var repairLevel = Math.Max(1, Math.Max(0, data.LevelEquip - 10));
            var repairDM    = data.ItemRepair.RowId;

            var firstDM = repairDMs.OrderBy(x => x.Key).FirstOrDefault(x => x.Key >= repairDM && x.Value - 1 >= 0).Key;
            // 可以自己修 + 暗物质数量足够
            if (LocalPlayerState.GetClassJobLevel(repairJob) >= repairLevel && firstDM != 0)
            {
                repairDMs[firstDM]--;
                continue;
            }
            
            if (firstDM is 0)
                isDMInsufficient = true;
            
            itemsUnableToRepair.Add(itemToRepair.ItemId);
        }
        
        TaskHelper.Abort();
        
        // 还是有能自己修的装备的
        if (items.Count > itemsUnableToRepair.Count)
        {
            TaskHelper.Enqueue(() => IsAbleToRepair());
            TaskHelper.Enqueue(() => NotificationInfo(GetLoc("AutoRepair-RepairNotice"), GetLoc("AutoRepairTitle")));
            TaskHelper.Enqueue(() => UseActionManager.UseAction(ActionType.GeneralAction, 6));
            TaskHelper.Enqueue(() => IsAddonAndNodesReady(Repair));
            
            // 没有暗物质不足的情况
            if (!isDMInsufficient)
                TaskHelper.Enqueue(() => SendEvent(AgentId.Repair, 2, 0));
            else
            {
                var itemsSelfRepair = items.ToList();
                itemsSelfRepair.RemoveAll(x => itemsUnableToRepair.Contains(x.ItemId));
                foreach (var item in itemsSelfRepair)
                {
                    TaskHelper.Enqueue(() => RepairItemDetour(nint.Zero, item.Container, item.Slot, false));
                    TaskHelper.DelayNext(3_000);
                }
            }
            
            TaskHelper.Enqueue(() =>
            {
                if (!IsAddonAndNodesReady(Repair)) return;
                Repair->Close(true);
            });
            TaskHelper.DelayNext(5_00);
        }

        // 附近存在修理工
        if (ModuleConfig.AllowNPCRepair && itemsUnableToRepair.Count > 0 && IsEventIDNearby(720915))
        {
            TaskHelper.Enqueue(() => IsAbleToRepair());
            TaskHelper.Enqueue(() => NotificationInfo(GetLoc("AutoRepair-RepairNotice"), GetLoc("AutoRepairTitle")));
            TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 720915).Send());
            TaskHelper.Enqueue(() => IsAddonAndNodesReady(Repair));
            TaskHelper.Enqueue(() => ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, 1000));
            TaskHelper.Enqueue(() =>
            {
                if (!IsAddonAndNodesReady(Repair)) return;
                Repair->Close(true);
            });
        }
    }

    #region Hooks

    private static void RepairItemDetour(nint a1, InventoryType inventory, short slot, bool isNPC)
    {
        var slotData = InventoryManager.Instance()->GetInventorySlot(inventory, slot);
        if (slotData == null) return;
        
        // NPC
        if (IsCurrentOnNPCRepair())
        {
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairItemNPC, (uint)inventory, (uint)slot, slotData->ItemId);
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.InventoryRefresh);
            return;
        }
        
        // 自己修理
        RepairItemHook.Original(a1, inventory, slot, false);
    }

    private static void RepairEquippedItemsDetour(nint a1, InventoryType inventory, bool isNPC)
    {
        // NPC
        if (IsCurrentOnNPCRepair())
        {
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, (uint)inventory);
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.InventoryRefresh);
            return;
        }
        
        // 自己修理
        RepairEquippedItemsHook.Original(a1, inventory, false);
    }
    
    private static void RepairAllItemsDetour(nint repairController, bool isNPC, int category, int a4)
    {
        // NPC
        if (IsCurrentOnNPCRepair())
        {
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RepairAllItemsNPC, (uint)category);
            ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.InventoryRefresh);
            return;
        }

        // 自己修理
        RepairAllItemsHook.Original(repairController, false, category, a4);
    }

    #endregion

    private static bool IsCurrentOnNPCRepair()
    {
        if (IsAddonAndNodesReady(Repair))
        {
            var atkValueCost = Repair->AtkValues[424].String;
            
            // 没有修理价格
            if (!atkValueCost.HasValue || string.IsNullOrWhiteSpace(atkValueCost.ExtractText()))
                return false;
            
            return true;
        }
        
        // 没有界面, 就用 Condition 凑合判断一下
        return DService.Condition[ConditionFlag.OccupiedInQuestEvent];
    }

    private static bool IsAbleToRepair() =>
        IsScreenReady() && !OccupiedInEvent && !DService.ClientState.IsPvPExcludingDen &&
        !IsOnMount      && !IsCasting       && ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 6) == 0;
    
    #region 事件

    private void OnDutyRecommenced(object? sender, ushort e) => EnqueueRepair();

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value || !ValidConditions.Contains(flag)) return;
        EnqueueRepair();
    }

    private void OnZoneChanged(ushort zoneID) => EnqueueRepair();

    #endregion

    protected override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Condition.ConditionChange -= OnConditionChanged;
        DService.DutyState.DutyRecommenced -= OnDutyRecommenced;
    }

    private class Config : ModuleConfiguration
    {
        public float RepairThreshold    = 20;
        public bool  AllowNPCRepair     = true;
        public bool  PrioritizeNPCRepair;
    }
    
    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsBusy")]
    public bool IsBusyIPC => IsBusy;
    
    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsNeedToRepair")]
    public bool IsNeedToRepairIPC => TryGetInventoryItems([InventoryType.EquippedItems],
                                                       x => x.Condition < ModuleConfig.RepairThreshold * 300f, out _);
    
    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsAbleToRepair")]
    public bool IsAbleToRepairIPC => IsAbleToRepair();

    [IPCProvider("DailyRoutines.Modules.AutoRepair.EnqueueRepair")]
    public void EnqueueRepairIPC() => EnqueueRepair();
}
