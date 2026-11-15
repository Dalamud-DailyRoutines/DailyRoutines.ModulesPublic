using System.Collections.Frozen;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using GatheringPointEventHandler = FFXIVClientStructs.Expansion.FFXIV.Client.Game.Event.GatheringPointEventHandler;
using PlayerController = FFXIVClientStructs.Expansion.FFXIV.Client.Game.Control.PlayerController;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

internal unsafe partial class AutoGatherDiadem
{
    private const uint ZONE_FIRMAMENT = 886;
    private const uint ZONE_DIADEM    = 939;

    private static readonly Dictionary<GatheringMode, (string Title, string Description)> GatheringModeLoc = new()
    {
        [GatheringMode.练级] = (Lang.Get("AutoGatherDiadem-Mode-Leveling-Title"), Lang.Get("AutoGatherDiadem-Mode-Leveling-Description")),
        [GatheringMode.冲分] = (Lang.Get("AutoGatherDiadem-Mode-SkillPT-Title"), Lang.Get("AutoGatherDiadem-Mode-SkillPT-Description")),
        [GatheringMode.绿材] = (Lang.Get("AutoGatherDiadem-Mode-Clouded-Title"), Lang.Get("AutoGatherDiadem-Mode-Clouded-Description"))
    };

    // 商店可兑换的物品
    private static readonly List<ShopExchangeItem> ExchangeItems =
        ShopExchangeItem.Generate();

    private static readonly Dictionary<uint, GatherJobInfo> JobInfos = new()
    {
        // 采矿工
        [16] = new()
        {

            // 富矿 1 / 2, 纳尔札尔福音, 莫非王土 2
            ActionsAbove850     = [21177, 25589, 21203, 241],
            // 高产 2 - 富矿 2 - 高产 - 富矿
            ActionsSingle       = [272, 25589, 4073, 21177],
            DetectAction        = 228,
            DetectStatus        = 234,
            DetectPremiumAction = 291,

            BaseDetectAction = 227,
            BaseDetectStatus = 225,
            Achievement500K  = 2515,
            // 矿脉, 石场
            GatherPointNames = new[]
            {
                LuminaWrapper.GetGatheringPointName(1), LuminaWrapper.GetGatheringPointName(2),
                LuminaWrapper.GetGatheringPointName(5), LuminaWrapper.GetGatheringPointName(6),
                LuminaWrapper.GetGatheringPointName(9), LuminaWrapper.GetGatheringPointName(10),
                LuminaWrapper.GetGatheringPointName(13), LuminaWrapper.GetGatheringPointName(14),
                LuminaWrapper.GetGatheringPointName(17), LuminaWrapper.GetGatheringPointName(18),
                LuminaWrapper.GetGatheringPointName(23), LuminaWrapper.GetGatheringPointName(24)
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        },
        // 园艺工
        [17] = new()
        {

            // 沃土 1 / 2, 诺菲卡福音, 天赐收成 2
            ActionsAbove850     = [21178, 25590, 21204, 224],
            // 丰收 2 - 沃土 2 - 丰收 - 沃土
            ActionsSingle       = [273, 25590, 4087, 21178],
            DetectAction        = 211,
            DetectStatus        = 233,
            DetectPremiumAction = 290,

            BaseDetectAction = 210,
            BaseDetectStatus = 217,
            Achievement500K  = 2518,
            // 良材, 草场
            GatherPointNames = new[]
            {
                LuminaWrapper.GetGatheringPointName(3), LuminaWrapper.GetGatheringPointName(4),
                LuminaWrapper.GetGatheringPointName(7), LuminaWrapper.GetGatheringPointName(8),
                LuminaWrapper.GetGatheringPointName(11), LuminaWrapper.GetGatheringPointName(12),
                LuminaWrapper.GetGatheringPointName(15), LuminaWrapper.GetGatheringPointName(16),
                LuminaWrapper.GetGatheringPointName(19), LuminaWrapper.GetGatheringPointName(20),
                LuminaWrapper.GetGatheringPointName(25), LuminaWrapper.GetGatheringPointName(26)
            }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        }
    };

    // 起始点顺时针
    private static readonly WindmireTeleporter[] Teleporters =
    [
        new(new(-628.6f, 281f, -191.2f), new(-592.1f, 34f, -229.7f)),
        new(new(137.4f, -49.5f, -477.0f), new(174.8f, 295f, -737.5f)),
        new(new(434.9f, 290.4f, -735.5f), new(465.5f, 193f, -666.0f)),
        new(new(527.0f, 193f, -492.6f), new(602.4f, 252f, -411.1f)),
        new(new(634.6f, 252.1f, -388.3f), new(714.8f, 217.2f, -335.2f)),
        new(new(659.7f, 223.6f, -33.9f), new(466.4f, -24f, 185.3f)),
        new(new(339.1f, -4f, 471.9f), new(276.0f, 87.0f, 468.1f)),
        new(new(-145.5f, 113.8f, 519.8f), new(-288f, 318f, 559f)),
        new(new(-560.2f, 319f, 310.0f), new(-727.7f, 272.5f, -28.0f))
    ];

    // ItemID - 可以恢复的基础 GP
    private static readonly Dictionary<uint, uint> CordialItems = new()
    {
        [12669] = 400,
        [6141]  = 300,
        [16911] = 150
    };

    // 各种绿材天气 (Weather ID - Job ID)
    private static readonly Dictionary<uint, uint> SpecialWeathers = new()
    {
        // 灵烈火 - 第四期重建用的幻火石 - 采矿工
        [133] = 16,
        // 灵飘尘 - 第四期重建用的幻土草 - 园艺工
        [134] = 17,
        // 灵飞电 - 第四期重建用的幻雷矿砂 - 采矿工
        [135] = 16,
        // 灵罡风 - 第四期重建用的幻风树枝 - 园艺工
        [136] = 17
    };

    // 很不幸, 我们又挖到天钢工具那里了
    private static readonly List<uint> SpecialItems = [32001, 32045, 32002, 32046, 32003, 32047, 32004, 32048];

    private static readonly FrozenSet<string> CloudedGatherPointNames =
    [
        with(StringComparer.OrdinalIgnoreCase),
        LuminaWrapper.GetGatheringPointName(23),
        LuminaWrapper.GetGatheringPointName(24),
        LuminaWrapper.GetGatheringPointName(25),
        LuminaWrapper.GetGatheringPointName(26)
    ];

    private static readonly Vector3 RedirectPosition    = new(468, -23.7f, 213);
    private static readonly Vector3 ZoneDefaultPosition = new(-656.5f, 285.3f, -157.4f);

    private GatheringNodeState ReadGatheringState()
    {
        var target = TargetManager.Target;
        if (target is not { ObjectKind: ObjectKind.GatheringPoint })
            return new(0, -1, GatheringState.等待加载);

        var point = (GatheringPointObject*)target.ToStruct();
        if (point->Impl == null)
            return new(0, -1, GatheringState.等待加载);

        var handler = (GatheringPointEventHandler*)point->Impl->EventHandler;
        if (handler == null || handler->Count == 0 || handler->Items == null)
            return new(0, -1, GatheringState.等待加载);

        var count = handler->RemainingCount;
        if (count == 0)
            return new(0, -1, GatheringState.采集完毕);

        if (handler->IsGathering || handler->IsQuickGathering)
            return new(count, -1, GatheringState.等待加载);

        var bestIndex     = -1;
        var bestRate      = 0;
        var bestLevel     = 0;
        var bestItemID    = 0U;
        var bestPreferred = false;
        var hasItems      = false;

        for (var index = 0; index < 8; index++)
        {
            ref var slot = ref handler->Items[index];
            if (slot.GatheringItemID == 0)
                continue;

            hasItems = true;
            if (slot.SuccessRate == 0 || !LuminaGetter.TryGetRow<GatheringItem>(slot.GatheringItemID, out var item))
                continue;

            var level     = item.GatheringItemLevel.Value.GatheringItemLevel;
            var preferred = config.GatheringItems.Contains(slot.GatheringItemID);

            if (config.Mode == GatheringMode.练级)
            {
                if (!FOURTH_PHASE_GATHERING_ITEMS.Contains(slot.GatheringItemID) ||
                    level            >= 80                                       ||
                    slot.SuccessRate < bestRate                                  ||
                    (slot.SuccessRate == bestRate && level <= bestLevel))
                    continue;
            }
            else
            {
                if (!preferred && !FOURTH_PHASE_GATHERING_ITEMS.Contains(slot.GatheringItemID))
                    continue;

                if (bestIndex >= 0 && ((!preferred && bestPreferred) || (preferred == bestPreferred && slot.ItemID <= bestItemID)))
                    continue;
            }

            bestIndex     = index;
            bestRate      = slot.SuccessRate;
            bestLevel     = level;
            bestItemID    = slot.ItemID;
            bestPreferred = preferred;
        }

        if (!hasItems)
            return new(count, -1, GatheringState.等待加载);

        return new
        (
            count,
            bestIndex,
            bestIndex >= 0 ?
                GatheringState.可以采集 :
                GatheringState.无法采集,
            handler->QuickGatheringEnabled
        );
    }

    private readonly record struct GatheringNodeState
    (
        uint           Count,
        int            Index,
        GatheringState State,
        bool           QuickGatheringEnabled = false
    );

    private struct GatherJobInfo
    {
        /// <summary>
        ///     采集点名称
        /// </summary>
        public FrozenSet<string> GatherPointNames;

        /// <summary>
        ///     GP 大于等于 850 时使用的技能
        /// </summary>
        public List<uint> ActionsAbove850;

        /// <summary>
        ///     防止溢出 GP 时使用的技能
        /// </summary>
        public List<uint> ActionsSingle;

        /// <summary>
        ///     侦查技能
        /// </summary>
        public uint DetectAction;

        /// <summary>
        ///     侦查状态
        /// </summary>
        public uint DetectStatus;

        /// <summary>
        ///     最高级侦查技能
        /// </summary>
        public uint DetectPremiumAction;

        /// <summary>
        ///     基础侦查技能
        /// </summary>
        public uint BaseDetectAction;

        /// <summary>
        ///     基础侦查状态
        /// </summary>
        public uint BaseDetectStatus;

        /// <summary>
        ///     50 万分成就 ID
        /// </summary>
        public uint Achievement500K;
    }

    private record ShopExchangeItem
    (
        uint   ShopID,
        string ItemName,
        uint   ItemID,
        uint   CostItemCount
    )
    {
        public static List<ShopExchangeItem> Generate()
        {
            List<uint> shops = [1770041, 1770281, 1770301];

            List<ShopExchangeItem> result = [];

            foreach (var shop in shops)
            {
                var shopData = LuminaGetter.GetRow<SpecialShop>(shop).GetValueOrDefault();

                foreach (var item in shopData.Item)
                {
                    var itemID = item.ReceiveItems[0].Item.RowId;
                    if (itemID == 0) continue;

                    var itemCost = item.ItemCosts[0].CurrencyCost;
                    if (itemCost == 0) continue;

                    result.Add(new(shop, item.ReceiveItems[0].Item.Value.Name.ToString(), itemID, itemCost));
                }
            }

            return result;
        }

        public int GetShopIndex() => ShopID switch
        {
            // 天穹街振兴票
            1770041 => 0,
            // 天穹街振兴票（装备·家具）
            1770281 => 1,
            // 天穹街振兴票（素材·魔晶石·消耗品）
            1770301 => 2
        };

        public void Purchase()
        {
            var count = GetAvailablePurchaseCount();
            if (count == 0) return;

            var index = GetItemIndex();
            if (index == -1) return;

            AgentId.Shop.SendEvent(1, 0, index, count, 0);
        }

        public int GetItemIndex()
        {
            var agent = AgentShop.Instance();
            if (agent == null || !agent->IsAgentActive() || agent->ItemReceive == null) return -1;

            var counter = -1;

            foreach (var itemReceive in agent->ItemReceiveSpan)
            {
                counter++;
                if (itemReceive.ItemId == ItemID)
                    return counter;
            }

            return -1;
        }

        public int GetAvailablePurchaseCount()
        {
            var heldCurrency = InventoryManager.Instance()->GetInventoryItemCount(28063);
            return (int)(heldCurrency / CostItemCount);
        }
    }

    private class WindmireTeleporter
    (
        Vector3 pointA,
        Vector3 pointB
    )
    {
        private const float ACTIVATION_DISTANCE         = 150f;
        private const float ACTIVATION_DISTANCE_SQUARED = ACTIVATION_DISTANCE * ACTIVATION_DISTANCE;

        private const float   USAGE_TIME = 5f;
        public        Vector3 PointA { get; } = pointA;
        public        Vector3 PointB { get; } = pointB;

        /// <summary>
        ///     检测是否可以使用传送站
        /// </summary>
        /// <param name="currentPosition">当前位置</param>
        /// <param name="destination">目标位置（仅用于判断传送是否有助于接近目的地）</param>
        /// <param name="entryPoint">输出参数：玩家需要接近的传送站点位（A或B）</param>
        /// <param name="ignoreDistance">是否忽略激活距离限制，为 true 时认为始终可及</param>
        /// <returns>是否推荐使用此传送站</returns>
        public bool CanUseTeleporter
        (
            Vector3     currentPosition,
            Vector3     destination,
            out Vector3 entryPoint,
            bool        ignoreDistance = false
        )
        {
            entryPoint = Vector3.Zero;

            var distToASq = Vector3.DistanceSquared(currentPosition, PointA);
            var distToBSq = Vector3.DistanceSquared(currentPosition, PointB);

            var nearPointA = distToASq <= ACTIVATION_DISTANCE_SQUARED;
            var nearPointB = distToBSq <= ACTIVATION_DISTANCE_SQUARED;

            if (!ignoreDistance && !nearPointA && !nearPointB)
                return false;

            // 确定玩家靠近哪个传送点（优先A点如果同时靠近两个点）
            var nearbyPoint = nearPointA ?
                                  PointA :
                                  PointB;
            var oppositePoint = nearPointA ?
                                    PointB :
                                    PointA;

            var baseSpeed = PlayerController.Instance()->MoveControllerWalk.BaseMovementSpeed;

            // 计算普通移动所需时间（直线距离/速度）
            var directDistance     = Vector3.Distance(currentPosition, destination);
            var directTimeEstimate = directDistance / baseSpeed;

            // 计算使用传送站所需总时间
            // 1. 到达入口点的时间
            var distanceToEntryPoint = Vector3.Distance(currentPosition, nearbyPoint);
            var timeToEntryPoint     = distanceToEntryPoint / baseSpeed;

            // 2. 从出口点到目的地的时间
            var distanceFromExitToDestination = Vector3.Distance(oppositePoint, destination);
            var timeFromExitToDestination     = distanceFromExitToDestination / baseSpeed;

            // 总时间
            var totalTeleportTime = timeToEntryPoint + USAGE_TIME + timeFromExitToDestination;

            // 只有当使用传送站的总时间少于直接移动的时间时，才推荐使用
            if (totalTeleportTime < directTimeEstimate)
            {
                entryPoint = nearbyPoint;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     获取传送目标点（知道入口点后计算出口点）
        /// </summary>
        public Vector3 GetDestinationPoint
        (
            Vector3 entryPoint
        )
        {
            if (Vector3.DistanceSquared(entryPoint, PointA) < float.Epsilon)
                return PointB;

            return Vector3.DistanceSquared(entryPoint, PointB) < float.Epsilon ?
                       PointA :
                       throw new ArgumentException("输入的入口点不是此传送站的任一传送点");
        }

        /// <summary>
        ///     从多个传送站中查找最佳选择
        /// </summary>
        public static bool FindBestTeleporter
        (
            WindmireTeleporter[] teleporters,
            Vector3              currentPosition,
            Vector3              destination,
            out Vector3          entryPoint,
            bool                 ignoreDistance = true
        )
        {
            var found = false;
            entryPoint = Vector3.Zero;
            var bestTimeSaving = 0f;

            // 计算直接移动所需时间
            var directDistance     = Vector3.Distance(currentPosition, destination);
            var baseSpeed          = PlayerController.Instance()->MoveControllerWalk.BaseMovementSpeed;
            var directTimeEstimate = directDistance / baseSpeed;

            foreach (var teleporter in teleporters)
            {
                if (teleporter.CanUseTeleporter(currentPosition, destination, out var point, ignoreDistance))
                {
                    var exitPoint = teleporter.GetDestinationPoint(point);

                    // 计算使用传送站所需总时间
                    // 1. 到达入口点的时间
                    var distanceToEntryPoint = Vector3.Distance(currentPosition, point);
                    var timeToEntryPoint     = distanceToEntryPoint / baseSpeed;

                    // 2. 从出口点到目的地的时间
                    var distanceFromExitToDestination = Vector3.Distance(exitPoint, destination);
                    var timeFromExitToDestination     = distanceFromExitToDestination / baseSpeed;

                    // 总时间
                    var totalTeleportTime = timeToEntryPoint + USAGE_TIME + timeFromExitToDestination;

                    // 计算节省的时间
                    var timeSaving = directTimeEstimate - totalTeleportTime;

                    if (timeSaving > bestTimeSaving)
                    {
                        bestTimeSaving = timeSaving;
                        found          = true;
                        entryPoint     = point;
                    }
                }
            }

            return found;
        }
    }

    #region 常量

    private static readonly FrozenSet<uint> FOURTH_PHASE_GATHERING_ITEMS =
        Enumerable.Range(10642, 44).Select(uint.CreateChecked).ToFrozenSet();

    private static readonly FrozenSet<uint> GATHERING_ITEMS =
        Enumerable.Range(10642, 72).Select(uint.CreateChecked).ToFrozenSet();

    #endregion
}
