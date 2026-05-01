using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud.Attributes;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRepair : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRepairTitle"),
        Description = Lang.Get("AutoRepairDescription"),
        Category    = ModuleCategory.General
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };
    
    private bool IsBusy => TaskHelper?.IsBusy ?? false;

    private Config config = null!;
    
    protected override void Init()
    {
        config ??= Config.Load(this) ?? new();
        TaskHelper   ??= new();

        ExecuteCommandManager.Instance().RegPost(OnExecuteCommand);

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        DService.Instance().Condition.ConditionChange    += OnConditionChanged;
        DService.Instance().DutyState.DutyRecommenced    += OnDutyRecommenced;
    }
    
    protected override void Uninit()
    {
        ExecuteCommandManager.Instance().Unreg(OnExecuteCommand);
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Condition.ConditionChange    -= OnConditionChanged;
        DService.Instance().DutyState.DutyRecommenced    -= OnDutyRecommenced;
    }
    
    protected override void ConfigUI()
    {
        ImGui.SetNextItemWidth(100f * GlobalUIScale);
        ImGui.InputFloat(Lang.Get("AutoRepair-RepairThreshold"), ref config.RepairThreshold, 0, 0, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save(this);

        ImGui.NewLine();

        if (ImGui.Checkbox(Lang.Get("AutoRepair-AllowNPCRepair"), ref config.AllowNPCRepair))
            config.Save(this);
        ImGuiOm.HelpMarker(Lang.Get("AutoRepair-AllowNPCRepairHelp"), 100f * GlobalUIScale);

        if (config.AllowNPCRepair)
        {
            if (ImGui.Checkbox(Lang.Get("AutoRepair-PrioritizeNPCRepair"), ref config.PrioritizeNPCRepair))
                config.Save(this);
            ImGuiOm.HelpMarker(Lang.Get("AutoRepair-PrioritizeNPCRepairHelp"), 100f * GlobalUIScale);
        }
    }

    public void EnqueueRepair()
    {
        if (TaskHelper.IsBusy                                 ||
            DService.Instance().ClientState.IsPvPExcludingDen ||
            DService.Instance().ObjectTable.LocalPlayer is not { CurrentHp: > 0 })
            return;

        var playerState      = PlayerState.Instance();
        var inventoryManager = InventoryManager.Instance();

        if (playerState == null || inventoryManager == null) return;

        // 没有需要修理的装备
        if (!InventoryType.EquippedItems.TryGetItems(x => x.Condition < config.RepairThreshold * 300f, out var items))
            return;

        // 优先委托 NPC 修理
        if (config is { AllowNPCRepair: true, PrioritizeNPCRepair: true } && EventFramework.Instance()->IsEventIDNearby(720915))
        {
            TaskHelper.Abort();
            TaskHelper.Enqueue(() => IsAbleToRepair());
            TaskHelper.Enqueue(() => NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoRepair-RepairNotice"), Lang.Get("AutoRepairTitle")));
            TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 720915).Send());
            TaskHelper.Enqueue(() => Repair->IsAddonAndNodesReady());
            TaskHelper.Enqueue(() => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, 1000));
            TaskHelper.Enqueue
            (() =>
                {
                    if (!Repair->IsAddonAndNodesReady()) return;
                    Repair->Close(true);
                }
            );

            return;
        }

        List<uint> itemsUnableToRepair = [];
        var repairDMs = LuminaGetter.Get<ItemRepairResource>()
                                    .Where(x => x.Item.RowId != 0)
                                    .ToDictionary
                                    (
                                        x => x.RowId,
                                        x => inventoryManager->GetInventoryItemCount(x.Item.RowId)
                                    );

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
            TaskHelper.Enqueue(() => IsAbleToRepair(),                                                                                "等待可以维修状态");
            TaskHelper.Enqueue(() => NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoRepair-RepairNotice"), Lang.Get("AutoRepairTitle")), "发送开始维修通知");

            // 没有暗物质不足的情况
            if (!isDMInsufficient)
                TaskHelper.Enqueue(() => RepairManager.Instance()->RepairEquipped(false), "发送一键全修");
            else
            {
                var itemsSelfRepair = items.ToList();
                itemsSelfRepair.RemoveAll(x => itemsUnableToRepair.Contains(x.ItemId));

                foreach (var item in itemsSelfRepair)
                {
                    TaskHelper.Enqueue
                        (() => RepairManager.Instance()->RepairItem(item.Container, (ushort)item.Slot, false), $"修理: {LuminaWrapper.GetItemName(item.ItemId)}");
                    TaskHelper.DelayNext(3_000);
                }
            }

            TaskHelper.DelayNext(5_00);
        }

        // 附近存在修理工
        if (config.AllowNPCRepair && itemsUnableToRepair.Count > 0 && EventFramework.Instance()->IsEventIDNearby(720915))
        {
            TaskHelper.Enqueue(() => IsAbleToRepair());
            TaskHelper.Enqueue(() => NotifyHelper.Instance().NotificationInfo(Lang.Get("AutoRepair-RepairNotice"), Lang.Get("AutoRepairTitle")));
            TaskHelper.Enqueue(() => new EventStartPackt(LocalPlayerState.EntityID, 720915).Send());
            TaskHelper.Enqueue(() => Repair->IsAddonAndNodesReady());
            TaskHelper.Enqueue(() => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, 1000));
            TaskHelper.Enqueue
            (() =>
                {
                    if (!Repair->IsAddonAndNodesReady()) return;
                    Repair->Close(true);
                }
            );
        }
    }

    private static bool IsAbleToRepair() =>
        UIModule.IsScreenReady()                         &&
        !DService.Instance().Condition.IsOccupiedInEvent &&
        !GameState.IsInPVPInstance                       &&
        !DService.Instance().Condition.IsOnMount         &&
        !DService.Instance().Condition.IsCasting         &&
        ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 6) == 0;
    
    #region 事件

    private void OnDutyRecommenced(IDutyStateEventArgs args) =>
        EnqueueRepair();

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (value || !ValidConditions.Contains(flag)) return;
        EnqueueRepair();
    }

    private void OnZoneChanged(uint u) =>
        EnqueueRepair();

    private static void OnExecuteCommand(ExecuteCommandFlag command, uint param1, uint param2, uint param3, uint param4)
    {
        if (!ValidRepairFlags.Contains(command)) return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.InventoryRefresh);
    }

    #endregion
    
    private class Config : ModuleConfig
    {
        public bool  AllowNPCRepair = true;
        public bool  PrioritizeNPCRepair;
        public float RepairThreshold = 20;
    }
    
    #region 常量

    private static readonly FrozenSet<ConditionFlag> ValidConditions =
    [
        ConditionFlag.InCombat,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Gathering,
        ConditionFlag.Crafting
    ];

    private static readonly FrozenSet<ExecuteCommandFlag> ValidRepairFlags =
    [
        ExecuteCommandFlag.RepairItemNPC,
        ExecuteCommandFlag.RepairAllItemsNPC,
        ExecuteCommandFlag.RepairEquippedItemsNPC,

        ExecuteCommandFlag.EventFrameworkAction,
    ];

    #endregion
    
    #region IPC

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsBusy")]
    public bool IsBusyIPC => IsBusy;

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsNeedToRepair")]
    public bool IsNeedToRepairIPC =>
        InventoryType.EquippedItems.TryGetItems(x => x.Condition < config.RepairThreshold * 300f, out _);

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsAbleToRepair")]
    public bool IsAbleToRepairIPC => IsAbleToRepair();
    
    [IPCProvider("DailyRoutines.Modules.AutoRepair.EnqueueRepair")]
    public void EnqueueRepairIPC() => EnqueueRepair();

    #endregion
}
