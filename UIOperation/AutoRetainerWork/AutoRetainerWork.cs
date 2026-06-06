using DailyRoutines.Common.Extensions;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading;
using Action = System.Action;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoRetainerWork : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title               = Lang.Get("AutoRetainerWorkTitle"),
        Description         = Lang.Get("AutoRetainerWorkDescription"),
        Category            = ModuleCategory.UIOperation,
        ModulesPrerequisite = ["AutoTalkSkip", "AutoRefreshMarketSearchResult"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private          Config            config            = null!;
    private readonly Throttler<string> retainerThrottler = new();
    private readonly HashSet<ulong>    playerRetainers   = [];

    private DRAutoRetainerWork? addon;

    private readonly RetainerWorkerBase[] workers;

    public AutoRetainerWork()
    {
        workers =
        [
            new CollectWorker(this),
            new EntrustDupsWorker(this),
            new GilsShareWorker(this),
            new GilsWithdrawWorker(this),
            new RefreshWorker(this),
            new TownDispatchWorker(this),
            new PriceAdjustWorker(this)
        ];
    }

    protected override void Init()
    {
        config = Config.Load(this) ?? new();

        foreach (var worker in workers)
            worker.Init();

        addon ??= new(this)
        {
            InternalName          = "DRAutoRetainerWork",
            Title                 = Info.Title,
            Size                  = new(260f, 320f),
            RememberClosePosition = true
        };
    }

    protected override void Uninit()
    {
        addon?.Dispose();
        addon = null;

        foreach (var worker in workers)
            worker.Uninit();
    }

    #region 模块界面

    protected override void ConfigUI()
    {
        foreach (var worker in workers)
        {
            if (!worker.DrawConfigCondition()) continue;

            worker.DrawConfig();

            ImGui.NewLine();
        }
    }

    #endregion

    #region 单独操作

    /// <summary>
    ///     打开指定索引对应的雇员
    /// </summary>
    private bool EnterRetainer(uint index)
    {
        if (!retainerThrottler.Throttle("EnterRetainer", 100)) return false;

        if (!RetainerList->IsAddonAndNodesReady()) return false;

        RetainerList->Callback(2, (int)index, 0, 0);
        return true;
    }

    /// <summary>
    ///     离开雇员界面
    /// </summary>
    private static bool LeaveRetainer()
    {
        // 如果存在
        if (SelectYesno->IsAddonAndNodesReady())
        {
            SelectYesno->Callback(0);
            return false;
        }

        if (SelectString->IsAddonAndNodesReady())
        {
            SelectString->Callback(-1);
            return false;
        }

        return RetainerList->IsAddonAndNodesReady();
    }

    /// <summary>
    ///     根据条件获取符合要求的雇员数量
    /// </summary>
    private static uint GetValidRetainerCount(Func<RetainerManager.Retainer, bool> predicateFunc, out List<uint> validRetainers)
    {
        validRetainers = [];

        var manager = RetainerManager.Instance();
        if (manager == null) return 0;

        var counter = 0U;

        for (var i = 0U; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null) continue;
            if (!predicateFunc(*retainer)) continue;

            validRetainers.Add(i);
            counter++;
        }

        return counter;
    }

    /// <summary>
    ///     离开雇员背包界面, 防止右键菜单残留
    /// </summary>
    private static bool ExitRetainerInventory()
    {
        var agent  = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        var agent2 = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || agent2 == null || !agent->IsAgentActive()) return false;

        var addon  = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->GetAddonId());
        var addon2 = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent2->GetAddonId());

        if (addon != null)
            addon->Close(true);
        if (addon2 != null)
            addon2->Callback(-1);

        AgentId.Retainer.SendEvent(0, -1);
        return true;
    }

    /// <summary>
    ///     搜索背包物品
    /// </summary>
    private static bool TrySearchItemInInventory(uint itemID, bool isHQ, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in Inventories.Player)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->ItemId == itemID &&
                    (!isHQ || (isHQ && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))))
                    foundItem.Add(*slot);
            }
        }

        return foundItem.Count > 0;
    }

    /// <summary>
    ///     将雇员 ID 添加至列表
    /// </summary>
    private void ObtainPlayerRetainers()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null) return;

        for (var i = 0U; i < retainerManager->GetRetainerCount(); i++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(i);
            if (retainer == null) break;

            playerRetainers.Add(retainer->RetainerId);
        }
    }

    /// <summary>
    ///     是否有其他 Worker 正在运行
    /// </summary>
    private bool IsAnyOtherWorkerBusy(Type current)
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;
            if (current == worker.GetType()) continue;

            return true;
        }

        return false;
    }

    /// <summary>
    ///     是否有 Worker 正在运行
    /// </summary>
    private bool IsAnyWorkerBusy()
    {
        foreach (var worker in workers)
        {
            if (!worker.IsWorkerBusy()) continue;

            return true;
        }

        return false;
    }

    #endregion

    #region 预定义

    private enum AdjustBehavior
    {
        固定值,
        百分比
    }

    [Flags]
    private enum AbortCondition
    {
        无        = 1,
        低于最小值    = 2,
        低于预期值    = 4,
        低于收购价    = 8,
        大于可接受降价值 = 16,
        高于预期值    = 32,
        高于最大值    = 64
    }

    private enum AbortBehavior
    {
        无,
        收回至雇员,
        收回至背包,
        出售至系统商店,
        改价至最小值,
        改价至预期值,
        改价至最高值
    }

    private enum SortOrder
    {
        上架顺序,
        物品ID,
        物品类型
    }

    private abstract class RetainerWorkerBase
    (
        AutoRetainerWork module
    )
    {
        protected AutoRetainerWork Module = module;

        public abstract bool IsWorkerBusy();

        public virtual bool DrawConfigCondition() => true;

        public abstract void Init();

        public virtual TreeListCategoryNode? CreateOverlayCategory(float width) => null;

        public virtual void DrawConfig() { }

        public abstract void Uninit();

        protected static TreeListCategoryNode CreateOverlayCategory(string title, float width, params NodeBase[] nodes)
        {
            var contentNode = new VerticalListNode
            {
                IsVisible        = true,
                Size             = new(width, 0f),
                FitContents      = true,
                FitWidth         = true,
                FirstItemSpacing = 4f,
                ItemSpacing      = 4f
            };
            contentNode.AddNode(nodes);

            var categoryNode = new TreeListCategoryNode
            {
                IsVisible = true,
                Size      = new(width, 28f),
                String    = title
            };
            categoryNode.AddNode(contentNode);
            categoryNode.IsCollapsed = true;

            return categoryNode;
        }

        protected static HorizontalFlexNode CreateOverlayButtonRow(Action startAction, Action stopAction, float width)
        {
            var row = new HorizontalFlexNode
            {
                IsVisible      = true,
                Size           = new(width, 28f),
                AlignmentFlags = FlexFlags.FitContentHeight | FlexFlags.FitWidth,
                FitPadding     = 4f
            };
            row.AddNode
            (
                [
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = Lang.Get("Start"),
                        OnClick   = startAction
                    },
                    new TextButtonNode
                    {
                        IsVisible = true,
                        IsEnabled = true,
                        Size      = new(100f, 28f),
                        String    = Lang.Get("Stop"),
                        OnClick   = stopAction
                    }
                ]
            );

            return row;
        }

        protected static CheckboxNode CreateOverlayCheckbox
        (
            string       title,
            bool         isChecked,
            Action<bool> onClick,
            float        width,
            string?      tooltip = null
        )
        {
            var node = new CheckboxNode
            {
                IsVisible = true,
                IsEnabled = true,
                Size      = new(width, 24f),
                IsChecked = isChecked,
                String    = title,
                OnClick   = onClick
            };

            if (!string.IsNullOrWhiteSpace(tooltip))
                node.TextTooltip = tooltip;

            return node;
        }

        protected static TextNode CreateOverlayText(string text, float width)
        {
            var node = new TextNode
            {
                IsVisible     = true,
                Size          = new(width, 24f),
                FontSize      = 14,
                String        = text,
                AlignmentType = AlignmentType.Left
            };
            node.AutoAdjustTextSize();

            return node;
        }
    }

    private class PriceCheckCondition
    (
        AbortCondition                           condition,
        Func<ItemConfig, uint, uint, uint, bool> predicate
    )
    {
        public AbortCondition                           Condition { get; } = condition;
        public Func<ItemConfig, uint, uint, uint, bool> Predicate { get; } = predicate;
    }

    private static class PriceCheckConditions
    {
        private static readonly PriceCheckCondition[] Conditions =
        [
            new
            (
                AbortCondition.高于最大值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceMaximum
            ),

            new
            (
                AbortCondition.高于预期值,
                (cfg, _, modified, _) =>
                    modified > cfg.PriceExpected
            ),

            new
            (
                AbortCondition.大于可接受降价值,
                (cfg, orig, modified, _) =>
                    cfg.PriceMaxReduction != 0         &&
                    orig                  != 999999999 &&
                    orig - modified       > 0          &&
                    orig - modified       > cfg.PriceMaxReduction
            ),

            new
            (
                AbortCondition.低于收购价,
                (cfg, _, modified, _) =>
                    LuminaGetter.TryGetRow<Item>(cfg.ItemID, out var itemRow) &&
                    modified <= itemRow.PriceMid
            ),

            new
            (
                AbortCondition.低于最小值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceMinimum
            ),

            new
            (
                AbortCondition.低于预期值,
                (cfg, _, modified, _) =>
                    modified < cfg.PriceExpected
            )
        ];

        /// <summary>
        ///     获取所有价格检查条件
        /// </summary>
        public static IEnumerable<PriceCheckCondition> GetAll() => Conditions;

        /// <summary>
        ///     根据条件类型获取特定的检查条件
        /// </summary>
        public static PriceCheckCondition Get(AbortCondition condition) =>
            Conditions.FirstOrDefault(x => x.Condition == condition);
    }

    private class Config : ModuleConfig
    {
        public bool AutoPriceAdjustWhenNewOnSale = true;

        public bool AutoRetainerCollect = true;

        public int GilsShareMethod;

        public Dictionary<string, ItemConfig> ItemConfigs = new()
        {
            { new ItemKey(0, false).ToString(), new ItemConfig(0, false) },
            { new ItemKey(0, true).ToString(), new ItemConfig(0,  true) }
        };

        public SortOrder MarketItemsSortOrder       = SortOrder.上架顺序;
        public float     MarketItemsWindowFontScale = 0.8f;

        public bool SendPriceAdjustProcessMessage = true;
    }

    private class ItemKey : IEquatable<ItemKey>
    {
        public ItemKey() { }

        public ItemKey(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ   = isHQ;
        }

        public uint ItemID { get; set; }
        public bool IsHQ   { get; set; }

        public bool Equals(ItemKey? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override string ToString() => $"{ItemID}_{(IsHQ ? "HQ" : "NQ")}";

        public override bool Equals(object? obj) => Equals(obj as ItemKey);

        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemKey? lhs, ItemKey? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemKey lhs, ItemKey rhs) => !(lhs == rhs);
    }

    private class ItemConfig : IEquatable<ItemConfig>
    {
        public ItemConfig() { }

        public ItemConfig(uint itemID, bool isHQ)
        {
            ItemID = itemID;
            IsHQ   = isHQ;
            ItemName = itemID == 0
                           ? Lang.Get("AutoRetainerWork-PriceAdjust-CommonItemPreset")
                           : LuminaGetter.GetRow<Item>(ItemID)?.Name.ToString() ?? string.Empty;
        }

        public uint   ItemID   { get; set; }
        public bool   IsHQ     { get; set; }
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        ///     改价行为
        /// </summary>
        public AdjustBehavior AdjustBehavior { get; set; } = AdjustBehavior.固定值;

        /// <summary>
        ///     改价具体值
        /// </summary>
        public Dictionary<AdjustBehavior, int> AdjustValues { get; set; } = new()
        {
            { AdjustBehavior.固定值, 1 },
            { AdjustBehavior.百分比, 10 }
        };

        /// <summary>
        ///     最低可接受价格 (最小值: 1)
        /// </summary>
        public int PriceMinimum { get; set; } = 100;

        /// <summary>
        ///     最大可接受价格
        /// </summary>
        public int PriceMaximum { get; set; } = 100000000;

        /// <summary>
        ///     预期价格 (最小值: PriceMinimum + 1)
        /// </summary>
        public int PriceExpected { get; set; } = 200;

        /// <summary>
        ///     最大可接受降价值 (设置为 0 以禁用)
        /// </summary>
        public int PriceMaxReduction { get; set; }

        /// <summary>
        ///     单次上架数量 (设置为 0 以禁用)
        /// </summary>
        public int UpshelfCount { get; set; }

        /// <summary>
        ///     意外情况逻辑
        /// </summary>
        public Dictionary<AbortCondition, AbortBehavior> AbortLogic { get; set; } = [];

        public bool Equals(ItemConfig? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return ItemID == other.ItemID && IsHQ == other.IsHQ;
        }

        public override bool Equals(object? obj) => Equals(obj as ItemConfig);

        public override int GetHashCode() => HashCode.Combine(ItemID, IsHQ);

        public static bool operator ==(ItemConfig? lhs, ItemConfig? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(ItemConfig lhs, ItemConfig rhs) => !(lhs == rhs);
    }

    #endregion
}
