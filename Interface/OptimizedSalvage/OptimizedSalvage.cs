using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using OmenTools.Info.Game.Data;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class OptimizedSalvage : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedSalvageTitle"),
        Description = Lang.Get("OptimizedSalvageDescription"),
        Category    = ModuleCategory.Interface,
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedSalvage/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config = null!;

    protected override void Init()
    {
        config = LoadConfig<Config>() ?? new();

        TaskHelper ??= new();

        TaskHelper.EnterBusyState += OnEnterBusyState;
        TaskHelper.LeaveBusyState += OnLeaveBusyState;

        TooltipManager.Instance().RegItem(OnItemTooltip);

        addon = new(this)
        {
            InternalName = "DROptimizedSalvage",
            Title        = Info.Title,
            Size         = new(300f, 280f)
        };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup, "SalvageDialog", OnAddon);

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "SalvageItemSelector", OnSalvageItemSelector);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostDraw,    "SalvageItemSelector", OnSalvageItemSelector);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "SalvageItemSelector", OnSalvageItemSelector);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        IAddonLifecycle.Instance().UnregisterListener(OnSalvageItemSelector);
        TooltipManager.Instance().Unreg(OnItemTooltip);

        RemoveSkillColumn();

        TaskHelper.EnterBusyState -= OnEnterBusyState;
        TaskHelper.LeaveBusyState -= OnLeaveBusyState;

        addon?.Dispose();
        addon = null;
    }

    private void OnEnterBusyState() =>
        addon?.OperationButton?.String = Lang.Get("Stop");

    private void OnLeaveBusyState() =>
        addon?.OperationButton?.String = Lang.Get("OptimizedSalvage-BatchDesynthesize");

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!config.ConfirmWhenManual &&
            !TaskHelper.IsBusy)
            return;

        if (!Throttler.Shared.Throttle("AutoDesynthesizeItems.Confirm", 100))
            return;

        SalvageDialog->Callback(13, true);
        SalvageDialog->Callback(0, 0);
    }

    private bool EnqueueDesynthesize
    (
        uint batchCount = 0
    )
    {
        if (ICondition.Instance().IsOccupiedInEvent)
            return false;
        if (!SalvageItemSelector->IsAddonAndNodesReady())
            return false;

        // 背包满了
        if (Inventories.Player.IsFull(3))
        {
            RaptureLogModule.Instance()->ShowLogMessage(3974);
            TaskHelper.Abort();
            return true;
        }

        var agent = AgentSalvage.Instance();
        if (agent == null)
            return false;

        if (agent->ItemCount == 0)
        {
            TaskHelper.Abort();
            NotifyCount();
            return true;
        }

        for (var i = 0; i < agent->ItemCount; i++)
        {
            var itemData = agent->ItemList[i];
            if (itemData.ItemId == 0)
                continue;

            var slot = InventoryManager.Instance()->GetInventorySlot(itemData.InventoryType, (int)itemData.InventorySlot);
            if (slot == null)
                continue;

            var itemID = slot->GetBaseItemId();
            if (itemID == 0)
                continue;

            if (config.SkipHQ && slot->IsHighQuality())
                continue;

            AgentId.Salvage.SendEvent(0, 12, i);

            batchCount += 1;
            TaskHelper.Enqueue(() => EnqueueDesynthesize(batchCount));
            return true;
        }

        NotifyCount();
        TaskHelper.Abort();
        return true;

        void NotifyCount()
        {
            var message = Lang.Get
            (
                "OptimizedSalvage-Notification-BatchDesynthesized",
                new Dictionary<string, object>
                {
                    ["count"] = batchCount
                }
            );

            NotifyHelper.Toast(message);
            NotifyHelper.Instance().Chat(message);
        }
    }

    private class Config : ModuleConfig
    {
        public bool SkipHQ            = true;
        public bool ConfirmWhenManual = true;
    }

    #region 常量

    private const ushort COLOR_LEVEL_TOO_HIGH = 32; // 金色
    private const ushort COLOR_LEVEL_HIGH     = 45; // 绿色
    private const ushort SKILL_COLUMN_WIDTH   = 120;

    #endregion
}
