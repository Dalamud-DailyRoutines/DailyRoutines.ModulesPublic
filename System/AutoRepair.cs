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
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoRepair : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRepairTitle"),
        Description = Lang.Get("AutoRepairDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private Config config = null!;

    protected override void Init()
    {
        config     ??= Config.Load(this) ?? new();
        TaskHelper ??= new() { ShowDebug = true };

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
        if (TaskHelper.IsBusy         ||
            GameState.IsInPVPInstance ||
            DService.Instance().ObjectTable.LocalPlayer is not { CurrentHp: > 0 })
            return;

        var playerState      = PlayerState.Instance();
        var inventoryManager = InventoryManager.Instance();

        if (playerState == null || inventoryManager == null) return;

        // 没有需要修理的装备
        if (!InventoryType.EquippedItems.TryGetItems(x => x.Condition < config.RepairThreshold * 300f, out var items))
            return;

        TaskHelper.Abort();

        var npcNearby = EventFramework.Instance()->IsEventIDNearby(REPAIR_EVENT_ID);

        // 优先委托 NPC 修理
        if (config is { AllowNPCRepair: true, PrioritizeNPCRepair: true } && npcNearby)
        {
            TaskHelper.Enqueue(NotifyStartRepair, "通知开始自动修复");
            EnqueueNPCRepairTasks();
            return;
        }

        var (itemsUnableToRepair, isDMInsufficient) = AnalyzeItems(items);

        var hasSelfRepair   = items.Count > itemsUnableToRepair.Count;
        var needNPCFallback = config.AllowNPCRepair && itemsUnableToRepair.Count > 0 && npcNearby;

        if (!hasSelfRepair && !needNPCFallback)
            return;

        TaskHelper.Enqueue(NotifyStartRepair, "通知开始自动修复");

        if (hasSelfRepair)
            EnqueueSelfRepairTasks(items, itemsUnableToRepair, isDMInsufficient);

        if (needNPCFallback)
            EnqueueNPCRepairTasks();
    }

    private void EnqueueSelfRepairTasks(List<InventoryItem> items, List<uint> itemsUnableToRepair, bool isDMInsufficient)
    {
        TaskHelper.Enqueue
        (
            IsAbleToRepair,
            "等待可以维修状态"
        );

        // 没有暗物质不足的情况
        if (!isDMInsufficient)
        {
            TaskHelper.Enqueue
            (
                () => RepairManager.Instance()->RepairEquipped(false),
                "开始自动修复"
            );
        }
        else
        {
            var itemsSelfRepair = items.ToList();
            itemsSelfRepair.RemoveAll(x => itemsUnableToRepair.Contains(x.ItemId));

            foreach (var item in itemsSelfRepair)
            {
                TaskHelper.Enqueue
                (
                    () => RepairManager.Instance()->RepairItem(item.Container, (ushort)item.Slot, false),
                    $"修理装备: {LuminaWrapper.GetItemName(item.GetBaseItemId())}"
                );
                TaskHelper.DelayNext(3_000, "等待 3 秒, 开始下一件单独装备修理");
            }
        }

        TaskHelper.DelayNext(5_00, "等待 500 毫秒");
    }

    private void EnqueueNPCRepairTasks()
    {
        TaskHelper.Enqueue
        (
            IsAbleToRepair,
            "等待进入可以修理状态"
        );
        TaskHelper.Enqueue
        (
            () =>
                new EventStartPackt(LocalPlayerState.EntityID, REPAIR_EVENT_ID).Send(),
            "打开 NPC 修理委托界面"
        );
        TaskHelper.Enqueue
        (
            () => Repair->IsAddonAndNodesReady(),
            "等待修理界面就绪"
        );
        TaskHelper.Enqueue
        (
            () => ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RepairEquippedItemsNPC, 1000),
            "发送 NPC 修理委托请求"
        );
        TaskHelper.Enqueue
        (
            () =>
            {
                if (!Repair->IsAddonAndNodesReady()) return;
                Repair->Close(true);
            },
            "关闭修理界面"
        );
    }

    private static (List<uint> ItemsUnableToRepair, bool IsDMInsufficient) AnalyzeItems(List<InventoryItem> items)
    {
        var itemsUnableToRepair = new List<uint>();
        var isDMInsufficient    = false;

        var repairDMs = LuminaGetter.Get<ItemRepairResource>()
                                    .Where(x => x.Item.RowId != 0)
                                    .ToDictionary
                                    (
                                        x => x.RowId,
                                        x => LocalPlayerState.GetItemCount(x.Item.RowId)
                                    );

        foreach (var itemToRepair in items)
        {
            var itemID = itemToRepair.ItemId;

            if (!LuminaGetter.TryGetRow<Item>(itemID, out var data))
                continue;

            var repairJob   = data.ClassJobRepair.RowId;
            var repairLevel = Math.Max(1, Math.Max(0, data.LevelEquip - 10));
            var repairDM    = data.ItemRepair.RowId;

            var firstDM = repairDMs.OrderBy(x => x.Key)
                                   .FirstOrDefault(x => x.Key >= repairDM && x.Value - 1 >= 0)
                                   .Key;

            // 可以自己修 + 暗物质数量足够
            if (LocalPlayerState.GetClassJobLevel(repairJob) >= repairLevel && firstDM != 0)
            {
                repairDMs[firstDM]--;
                continue;
            }

            if (firstDM is 0)
                isDMInsufficient = true;

            itemsUnableToRepair.Add(itemID);
        }

        return (itemsUnableToRepair, isDMInsufficient);
    }

    private static void NotifyStartRepair()
    {
        NotifyHelper.ToastQuest
        (
            Lang.Get("AutoRepair-Notification-AutoStart"),
            new()
            {
                IconId = 106
            }
        );
        NotifyHelper.Instance().Chat(Lang.Get("AutoRepair-Notification-AutoStart"));
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

    private static void OnExecuteCommand
    (
        ExecuteCommandFlag command,
        uint               param1,
        uint               param2,
        uint               param3,
        uint               param4
    )
    {
        if (!ValidRepairFlags.Contains(command)) return;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.RefreshInventory);
    }

    #endregion

    private class Config : ModuleConfig
    {
        public bool  AllowNPCRepair = true;
        public bool  PrioritizeNPCRepair;
        public float RepairThreshold = 20;
    }

    #region 常量

    private const uint REPAIR_EVENT_ID = 720915;

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

        ExecuteCommandFlag.EventFrameworkAction
    ];

    #endregion

    #region IPC

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsBusy")]
    private bool IsBusyIPC => TaskHelper?.IsBusy ?? false;

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsNeedToRepair")]
    private bool IsNeedToRepairIPC =>
        InventoryType.EquippedItems.TryGetItems(x => x.Condition < config.RepairThreshold * 300f, out _);

    [IPCProvider("DailyRoutines.Modules.AutoRepair.IsAbleToRepair")]
    private bool IsAbleToRepairIPC => IsAbleToRepair();

    [IPCProvider("DailyRoutines.Modules.AutoRepair.EnqueueRepair")]
    private void EnqueueRepairIPC() => EnqueueRepair();

    #endregion
}
