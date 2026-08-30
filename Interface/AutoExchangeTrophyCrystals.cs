using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper.Enums;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoExchangeTrophyCrystals : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoExchangeTrophyCrystalsTitle"),
        Description = Lang.Get("AutoExchangeTrophyCrystalsDescription"),
        Category    = ModuleCategory.Script,
        Author      = ["ToxicStar"]
    };

    private Config config = null!;

    private List<ExchangeItem> availableItems = [];
    private Dictionary<uint, ExchangeItem> availableItemsByID = [];

    private uint selectedItemID;
    private int selectedAmount = 1;

    private bool waitingForPurchase;
    private uint expectedCurrencyAfterPurchase;
    private string pendingItemName = string.Empty;

    protected override void Init()
    {
        config                 =   Config.Load(this) ?? new();
        TaskHelper             ??= new() { TimeoutMS = 30_000 };
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ["SelectYesno", "ShopExchangeItemDialog", "ShopExchangeCurrencyDialog"], OnConfirmAddon);
        LoadAvailableItems();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnConfirmAddon);
        Abort();
        config.Save(this);
    }

    #region 配置界面

    protected override void ConfigUI()
    {
        if (availableItems.Count == 0)
            LoadAvailableItems();

        ImGuiOm.ConflictKeyText();
        ImGui.Spacing();

        ImGui.TextColored
        (
            KnownColor.LightSkyBlue.ToVector4(),
            $"{GetTrophyCrystalName()}: {GetTrophyCrystalCount():N0}"
        );

        if (TaskHelper.IsBusy)
            ImGui.TextUnformatted($"{Lang.Get("Status")}: {TaskHelper.CurrentTaskName}");

        if (availableItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored
            (
                KnownColor.OrangeRed.ToVector4(),
                Lang.Get("Loading")
            );
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(TaskHelper.IsBusy || availableItems.Count == 0))
        {
            DrawItemSelector();
            DrawPresetTable();
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(TaskHelper.IsBusy || config.Requests.Count == 0 || availableItems.Count == 0))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                Start();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                Abort();
        }

        if (!vnavmeshIPC.IsPluginEnabled())
        {
            ImGui.Spacing();
            ImGui.TextColored
            (
                KnownColor.OrangeRed.ToVector4(),
                $"{Lang.Get("PluginPrerequisite")}: vnavmesh"
            );
        }
    }

    private void DrawItemSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("Add")} {Lang.Get("Item")}:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(360f * GlobalUIScale);
        var preview = availableItemsByID.TryGetValue(selectedItemID, out var selected) ?
                          $"{selected.ItemName} ({selected.ShopName})" :
                          Lang.Get("PleaseSelect");

        if (ImGui.BeginCombo("###TrophyCrystalItem", preview))
        {
            foreach (var item in availableItems)
            {
                var isSelected = item.ItemID == selectedItemID;
                if (ImGui.Selectable($"{item.ItemName} | {item.ShopName} | {item.Cost:N0}###Item_{item.ItemID}", isSelected))
                    selectedItemID = item.ItemID;

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * GlobalUIScale);
        if (ImGui.InputInt("###TrophyCrystalItemAmount", ref selectedAmount))
            selectedAmount = Math.Clamp(selectedAmount, 1, 999);

        ImGui.SameLine();
        using (ImRaii.Disabled(selectedItemID == 0))
        {
            if (!ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add"))) return;

            var request = config.Requests.FirstOrDefault(x => x.ItemID == selectedItemID);
            if (request == null)
                config.Requests.Add(new() { ItemID = selectedItemID, Amount = selectedAmount });
            else
                request.Amount = Math.Clamp(request.Amount + selectedAmount, 1, 999);

            config.Save(this);
        }
    }

    private void DrawPresetTable()
    {
        ImGui.Spacing();

        foreach (var request in config.Requests.ToList())
        {
            ImGui.PushID((int)request.ItemID);

            if (availableItemsByID.TryGetValue(request.ItemID, out var item))
                ImGui.TextUnformatted($"{item.ItemName} | {item.Cost:N0}");
            else
                ImGui.TextColored
                (
                    KnownColor.OrangeRed.ToVector4(),
                    $"{Lang.Get("Unknown")} ({request.ItemID})"
                );

            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * GlobalUIScale);
            if (ImGui.InputInt("###Amount", ref request.Amount))
            {
                request.Amount = Math.Clamp(request.Amount, 1, 999);
                config.Save(this);
            }

            ImGui.SameLine();
            var maximum = item is { Cost: > 0 } ?
                              (int)Math.Min(999U, GetTrophyCrystalCount() / item.Cost) :
                              0;
            using (ImRaii.Disabled(maximum == 0))
            {
                if (ImGui.Button(Lang.Get("Maximum")))
                {
                    request.Amount = maximum;
                    config.Save(this);
                }
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                config.Requests.Remove(request);
                config.Save(this);
            }

            ImGui.PopID();
        }
    }

    #endregion

    #region 流程

    // 整体流程：检查预设和余额 → 传送 → 寻路 → 按商店分类逐项兑换 → 退出商店。
    private void Start()
    {
        if (TaskHelper.IsBusy) return;
        if (DService.Instance().ObjectTable.LocalPlayer == null) return;
        if (DService.Instance().Condition.IsOccupiedInEvent) return;
        if (!vnavmeshIPC.IsPluginEnabled())
        {
            NotifyHelper.Instance().NotificationError($"{Lang.Get("PluginPrerequisite")}: vnavmesh");
            return;
        }
        if (config.Requests.Count == 0 ||
            config.Requests.Any(x => x.Amount <= 0 || !availableItemsByID.ContainsKey(x.ItemID)))
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoExchangeTrophyCrystals-Error-InvalidPreset"));
            return;
        }

        var requests = config.Requests
                             .Select(x => (Item: availableItemsByID[x.ItemID], x.Amount))
                             .ToList();

        // 出发前算好全部商品的总价，避免跑到兑换员面前才发现水晶不够。
        var totalCost = requests.Aggregate(0UL, (sum, x) => sum + (ulong)x.Item.Cost * (uint)x.Amount);
        var currency = GetTrophyCrystalCount();
        if (currency < totalCost)
        {
            NotifyHelper.Instance().NotificationError
            (
                Lang.Get("AutoExchangeTrophyCrystals-Error-InsufficientCurrency", totalCost, currency)
            );
            return;
        }

        TaskHelper.Abort();
        vnavmeshIPC.StopPathfind();
        waitingForPurchase = false;
        pendingItemName = string.Empty;

        var travelTaskName = $"{Lang.Get("Teleport")} / {Lang.Get("Pathfind")}";

        if (GameState.TerritoryType != TARGET_TERRITORY_ID)
            Enqueue(TeleportToWolvesDen, travelTaskName);

        Enqueue
        (
            () => GameState.TerritoryType == TARGET_TERRITORY_ID &&
                  UIModule.IsScreenReady() &&
                  !DService.Instance().Condition.IsBetweenAreas &&
                  vnavmeshIPC.GetIsNavReady(),
            travelTaskName,
            120_000
        );
        Enqueue(StartNavigation, travelTaskName);
        Enqueue(WaitForArrival, travelTaskName);

        // 商品按商店分类分组，打开一个分类后一次买完这个分类里的全部预设商品。
        foreach (var group in requests.GroupBy(x => x.Item.ShopName))
        {
            var shop = group.First().Item;
            var shopTaskName = $"{Lang.Get("Exchange")}: {shop.ShopName}";

            Enqueue(OpenCategoryMenu, shopTaskName);
            Enqueue(() => SelectShop(shop), shopTaskName);
            Enqueue(() => ShopExchangeCurrency->IsAddonAndNodesReady(), shopTaskName);

            foreach (var request in group)
            {
                var purchaseTaskName = $"{Lang.Get("Exchange")}: {request.Item.ItemName} x{request.Amount}";
                Enqueue
                (
                    () => BeginPurchase(request.Item, request.Amount),
                    purchaseTaskName
                );
                Enqueue(WaitForPurchase, purchaseTaskName);
            }

        }

        Enqueue(Finish, $"{Lang.Get("Close")} {Lang.Get("Exchange")}");
    }

    private bool TeleportToWolvesDen()
    {
        var aetheryte = AetheryteRecordManager.Instance().GetNearestAetheryte
        (
            TARGET_TERRITORY_ID,
            QuartermasterPosition,
            excludeAethernet: true
        );
        if (aetheryte != null)
            return aetheryte.TeleportTo();

        Abort(Lang.Get("AutoExchangeTrophyCrystals-Error-AetheryteNotUnlocked"));
        return true;
    }

    private bool StartNavigation()
    {
        var targetPosition = FindQuartermaster()?.Position ?? QuartermasterPosition;
        return LocalPlayerState.DistanceTo3DSquared(targetPosition) <= INTERACT_DISTANCE_SQUARED ||
               vnavmeshIPC.PathfindAndMoveToClosely(targetPosition, false, 0.1f);
    }

    private bool WaitForArrival()
    {
        var targetPosition = FindQuartermaster()?.Position ?? QuartermasterPosition;
        if (LocalPlayerState.DistanceTo3DSquared(targetPosition) <= INTERACT_DISTANCE_SQUARED)
        {
            vnavmeshIPC.StopPathfind();
            return true;
        }

        var isNavigating = vnavmeshIPC.GetIsPathfindRunning() ||
                           vnavmeshIPC.GetIsPathfindInProgress() ||
                           vnavmeshIPC.GetIsNavPathfindInProgress();

        if (!isNavigating && Throttler.Shared.Throttle("AutoExchangeTrophyCrystals-RetryPath", 1_000))
            vnavmeshIPC.PathfindAndMoveToClosely(targetPosition, false, 0.1f);

        return false;
    }

    private bool OpenCategoryMenu()
    {
        if (SelectIconString->IsAddonAndNodesReady())
            return true;

        if (ShopExchangeCurrency->IsAddonAndNodesReady())
        {
            // 先退出当前商品列表，游戏才会重新显示分类菜单供下一轮选择。
            ShopExchangeCurrency->Callback(-1);
            return false;
        }

        if (DService.Instance().Condition.IsOccupiedInEvent)
            return false;

        var quartermaster = FindQuartermaster();
        if (quartermaster == null)
            return false;

        if (LocalPlayerState.DistanceTo3DSquared(quartermaster.Position) > INTERACT_DISTANCE_SQUARED)
        {
            if (Throttler.Shared.Throttle("AutoExchangeTrophyCrystals-ApproachNPC", 1_000))
                vnavmeshIPC.PathfindAndMoveToClosely(quartermaster.Position, false, 0.1f);
            return false;
        }

        if (Throttler.Shared.Throttle("AutoExchangeTrophyCrystals-Interact", 1_000))
            quartermaster.TargetInteract();

        return false;
    }

    private static bool SelectShop
    (
        ExchangeItem shop
    ) => SelectIconString->IsAddonAndNodesReady() && AddonSelectIconStringEvent.Select(shop.ShopName);

    private bool BeginPurchase
    (
        ExchangeItem item,
        int amount
    )
    {
        var entry = FindShopEntry(item.ItemID);
        if (entry == null)
            return false;

        if (entry.Value.Cost != item.Cost)
        {
            Abort(Lang.Get("AutoExchangeTrophyCrystals-Error-ExchangeStateChanged", item.ItemName));
            return true;
        }

        var currency = GetTrophyCrystalCount();
        var required = (ulong)entry.Value.Cost * (uint)amount;
        if (required > currency)
        {
            Abort(Lang.Get("AutoExchangeTrophyCrystals-Error-InsufficientCurrency", required, currency));
            return true;
        }

        expectedCurrencyAfterPurchase = currency - (uint)required;
        pendingItemName = item.ItemName;
        waitingForPurchase = true;

        // 向商店界面发送“购买指定商品、指定数量”的操作。
        ShopExchangeCurrency->Callback(0, entry.Value.CallbackIndex, amount);
        return true;
    }

    private bool WaitForPurchase()
    {
        if (!waitingForPurchase)
            return true;

        // 水晶必须准确减少本次价格；少扣或多扣都说明兑换结果与预期不一致。
        var currency = GetTrophyCrystalCount();
        if (currency > expectedCurrencyAfterPurchase || AnyConfirmationAddonReady())
            return false;

        if (currency < expectedCurrencyAfterPurchase)
        {
            Abort(Lang.Get("AutoExchangeTrophyCrystals-Error-ExchangeStateChanged", pendingItemName));
            return true;
        }

        waitingForPurchase = false;
        pendingItemName = string.Empty;
        return true;
    }

    private void OnConfirmAddon
    (
        AddonEvent type,
        AddonArgs args
    )
    {
        if (!TaskHelper.IsBusy || !waitingForPurchase || args.Addon == nint.Zero)
            return;

        switch (args.AddonName)
        {
            case "SelectYesno":
                // 二次确认不显示目标物品名，只显示用于支付的战利水晶。
                AddonSelectYesnoEvent.ClickYes(GetTrophyCrystalName());
                break;

            case "ShopExchangeItemDialog":
                args.Addon.ToStruct()->Callback(0);
                break;

            case "ShopExchangeCurrencyDialog":
                var button = args.Addon.ToStruct()->GetComponentButtonById(17);
                if (button != null)
                    button->Click();
                break;
        }
    }

    private static ShopEntry? FindShopEntry
    (
        uint itemID
    )
    {
        var addon = ShopExchangeCurrency;
        if (!addon->IsAddonAndNodesReady() || addon->AtkValuesCount <= SHOP_CALLBACK_INDEX_OFFSET)
            return null;

        if (addon->AtkValues[4].Type != AtkValueType.UInt)
            return null;

        // 从商店界面的内部数据中逐行匹配商品，同时取得当前价格和购买操作编号。
        var itemCount = (int)addon->AtkValues[4].UInt;
        for (var index = 0; index < itemCount; index++)
        {
            var itemOffset = SHOP_ITEM_ID_OFFSET + index;
            var costOffset = SHOP_COST_OFFSET + index;
            var callbackOffset = SHOP_CALLBACK_INDEX_OFFSET + index;
            if (callbackOffset >= addon->AtkValuesCount)
                break;

            if (addon->AtkValues[itemOffset].Type != AtkValueType.UInt ||
                addon->AtkValues[costOffset].Type != AtkValueType.UInt ||
                addon->AtkValues[callbackOffset].Type != AtkValueType.UInt ||
                addon->AtkValues[itemOffset].UInt != itemID)
                continue;

            var cost = addon->AtkValues[costOffset].UInt;
            var callbackIndex = addon->AtkValues[callbackOffset].UInt;
            if (cost > 0 && callbackIndex < itemCount)
                return new(callbackIndex, cost);
        }

        return null;
    }

    private static bool AnyConfirmationAddonReady() =>
        SelectYesno->IsAddonAndNodesReady() ||
        ShopExchangeItemDialog->IsAddonAndNodesReady() ||
        ShopExchangeCurrencyDialog->IsAddonAndNodesReady();

    // 商品列表和分类菜单会分两层关闭，确认角色完全退出交互后才报告完成。
    private bool Finish()
    {
        if (ShopExchangeCurrency->IsAddonAndNodesReady())
        {
            ShopExchangeCurrency->Callback(-1);
            return false;
        }

        if (SelectIconString->IsAddonAndNodesReady())
        {
            SelectIconString->Callback(-1);
            return false;
        }

        if (DService.Instance().Condition.IsOccupiedInEvent)
            return false;

        vnavmeshIPC.StopPathfind();
        waitingForPurchase = false;
        pendingItemName = string.Empty;
        NotifyHelper.Instance().NotificationSuccess($"{Info.Title}: {Lang.Get("Finished")}");
        return true;
    }

    private void Enqueue
    (
        Func<bool> task,
        string name,
        int timeoutMS = 30_000
    ) => TaskHelper.Enqueue
    (
        () =>
        {
            if (TaskHelper.AbortByConflictKey(this))
            {
                Abort(abortTasks: false);
                return true;
            }

            return task();
        },
        name,
        timeoutMS: timeoutMS
    );

    private void Abort
    (
        string? error = null,
        bool abortTasks = true
    )
    {
        if (abortTasks)
            TaskHelper?.Abort();

        vnavmeshIPC.StopPathfind();
        waitingForPurchase = false;
        pendingItemName = string.Empty;

        if (!string.IsNullOrEmpty(error))
            NotifyHelper.Instance().NotificationError(error);
    }

    #endregion

    #region 工具

    private void LoadAvailableItems()
    {
        // 从 OmenTools 获取所有使用战利水晶购买的商品，只保留这名兑换员出售的内容。
        var result = ItemSourceInfo.QueryExchangeItems(TROPHY_CRYSTAL_ITEM_ID);
        if (result is not { State: ItemSourceQueryState.Ready, Data: { } data })
            return;

        List<ExchangeItem> items = [];
        foreach (var item in data.Items)
        foreach (var npc in item.NPCInfos.Where(x => x.ID == QUARTERMASTER_DATA_ID))
        foreach (var cost in npc.CostInfos.Where(x => x.ItemID == TROPHY_CRYSTAL_ITEM_ID && x.Cost > 0))
        {
            if (!string.IsNullOrWhiteSpace(npc.ShopName))
                items.Add(new(item.ItemID, npc.ShopName, item.GetItemName(), cost.Cost));
        }

        availableItems = items
                             .GroupBy(x => x.ItemID)
                             .Select(x => x.First())
                             .OrderBy(x => x.ShopName)
                             .ThenBy(x => x.ItemName)
                             .ToList();
        availableItemsByID = availableItems.ToDictionary(x => x.ItemID);

        if (!availableItemsByID.ContainsKey(selectedItemID))
            selectedItemID = availableItems.FirstOrDefault()?.ItemID ?? 0;
    }

    private static uint GetTrophyCrystalCount() => LocalPlayerState.GetItemCount(TROPHY_CRYSTAL_ITEM_ID);

    private static string GetTrophyCrystalName() =>
        LuminaGetter.GetRowOrDefault<Item>(TROPHY_CRYSTAL_ITEM_ID).Name.ToString();

    private static IGameObject? FindQuartermaster() =>
        DService.Instance().ObjectTable.FirstOrDefault
        (
            x => x.ObjectKind == ObjectKind.EventNpc && x.DataID == QUARTERMASTER_DATA_ID
        );

    #endregion

    #region 数据

    private sealed class Config : ModuleConfig
    {
        public List<ExchangeRequest> Requests = [];
    }

    private sealed class ExchangeRequest
    {
        public uint ItemID;
        public int Amount = 1;
    }

    private sealed record ExchangeItem
    (
        uint ItemID,
        string ShopName,
        string ItemName,
        uint Cost
    );

    private readonly record struct ShopEntry
    (
        uint CallbackIndex,
        uint Cost
    );

    #endregion

    #region 常量

    // 狼狱停船场
    private const uint TARGET_TERRITORY_ID = 250;

    // 战利水晶兑换员
    private const uint QUARTERMASTER_DATA_ID = 1038441;

    // 战利水晶
    private const uint TROPHY_CRYSTAL_ITEM_ID = 36656;

    // 商品、价格和购买回调序号的起始下标
    private const int SHOP_ITEM_ID_OFFSET = 1066;
    private const int SHOP_COST_OFFSET = 456;
    private const int SHOP_CALLBACK_INDEX_OFFSET = 1310;

    // NPC 交互距离平方（4 × 4）
    private const float INTERACT_DISTANCE_SQUARED = 16f;

    // 战利水晶兑换员备用坐标
    private static readonly Vector3 QuartermasterPosition = new(-4.89825f, 2.05696f, -0.503601f);

    #endregion
}
