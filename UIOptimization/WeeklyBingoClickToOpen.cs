using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public class WeeklyBingoClickToOpen : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("WeeklyBingoClickToOpenTitle"),
        Description = GetLoc("WeeklyBingoClickToOpenDescription"),
        Category    = ModuleCategories.UIOptimization,
        Author      = ["Due"]
    };

    private static readonly IAddonEventHandle?[] eventHandles = new IAddonEventHandle?[16];

    protected override unsafe void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "WeeklyBingo", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddon);

        if (IsAddonAndNodesReady(WeeklyBingo)) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddon);
        OnAddon(AddonEvent.PreFinalize, null);
    }

    private static unsafe void OnAddon(AddonEvent type, AddonArgs? args)
    {
        foreach (var index in Enumerable.Range(0, 16))
        {
            if (eventHandles[index] is { } handle)
            {
                DService.AddonEvent.RemoveEvent(handle);
                eventHandles[index] = null;
            }
        }

        if (type != AddonEvent.PostSetup) return;

        var addon = (AddonWeeklyBingo*)WeeklyBingo;
        if (addon == null) return;

        foreach (var index in Enumerable.Range(0, 16))
        {
            var dutySlot = addon->DutySlotList[index];
            var handle   = DService.AddonEvent.AddEvent((nint)addon, (nint)dutySlot.DutyButton->OwnerNode, AddonEventType.ButtonClick, OnDutySlotClick);
            eventHandles[index] = handle;
        }
    }

    private static unsafe void OnDutySlotClick(AddonEventType atkEventType, AddonEventData data)
    {
        var dutyButtonNode = (AtkResNode*)data.NodeTargetPointer;
        if (dutyButtonNode == null) return;

        var agent = AgentContentsFinder.Instance();
        if (agent == null) return;

        // 副本内无法打开
        if (BoundByDuty) return;
        
        var tileIndex      = (int)dutyButtonNode->NodeId - 12;
        var selectedTask = PlayerState.Instance()->GetWeeklyBingoTaskStatus(tileIndex);
        var bingoRowID   = PlayerState.Instance()->WeeklyBingoOrderData[tileIndex];

        if (selectedTask is PlayerState.WeeklyBingoTaskStatus.Open)
        {
            if (TryGetRouletteDutyByBingoData(bingoRowID, out var rouletteDuty))
                agent->OpenRouletteDuty(rouletteDuty);

            if (TryGetRegularDutyByBingoData(bingoRowID, out var regularDuty))
                agent->OpenRegularDuty(regularDuty);
        }
    }

    private static bool TryGetRouletteDutyByBingoData(uint bingoRowID, out byte rouletteRowID)
    {
        rouletteRowID = bingoRowID switch
        {
            // 纷争前线
            54 => 7,
            // 水晶冲突 (练习赛)
            52 => 40,
            _  => 0,
        };

        return rouletteRowID != 0;
    }

    private static bool TryGetRegularDutyByBingoData(uint bingoRowID, out uint dutyRowID)
    {
        dutyRowID = 0;
        if (!LuminaGetter.TryGetRow<WeeklyBingoOrderData>(bingoRowID, out var bingoDataRow)) return false;

        var bingoDataID = bingoDataRow.Data.RowId;
        dutyRowID = bingoDataRow.Type switch
        {
            // 具体副本
            0 => LuminaGetter.Get<ContentFinderCondition>()
                             .Where(_ => UIState.IsInstanceContentUnlocked(bingoDataID))
                             .Where(c => c.Content.RowId == bingoDataID)
                             .OrderBy(row => row.SortKey)
                             .FirstOrDefault().RowId,
            // 指定等级的副本
            1 => LuminaGetter.Get<ContentFinderCondition>()
                             .Where(m => m.ContentType.RowId is 2)
                             .Where(m => m.ClassJobLevelRequired == bingoDataID)
                             .OrderBy(row => row.SortKey)
                             .FirstOrDefault().RowId,
            // 指定等级区间的副本
            2 => bingoDataID == 49
                     ? 56
                     : LuminaGetter.Get<ContentFinderCondition>()
                                   .Where(m => m.ContentType.RowId is 2)
                                   .Where(m => m.ClassJobLevelRequired >= bingoDataID -
                                               (bingoDataID > 50 ? 9 : 49) &&
                                               m.ClassJobLevelRequired <= bingoDataID - 1)
                                   .OrderBy(row => row.SortKey)
                                   .FirstOrDefault().RowId,
            // 挖宝, PVP, 深宫
            3 => bingoRowID switch
            {
                46 => 0, // 宝物库
                52 => 0, // 水晶冲突 (上面处理过了)
                53 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 21)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId, // 深层迷宫
                54 => 0,                                   // 纷争前线 (上面处理过了)
                67 => 599,                                 // 烈羽争锋 (现在就等于隐塞)
                _  => 0
            },
            // 大型和团本
            4 => bingoDataID switch
            {
                // 巴哈邂逅
                2 => 93,
                // 巴哈入侵
                3 => 98,
                // 巴哈真源
                4 => 107,
                // 亚历山大启动
                5 => 112,
                // 亚历山大律动
                6 => 136,
                // 亚历山大天动
                7 => 186,
                // 欧米茄德尔塔
                8 => 252,
                // 欧米茄西格玛
                9 => 286,
                // 欧米茄阿尔法
                10 => 587,
                // 伊甸觉醒 1-2
                11 => 653,
                // 伊甸觉醒 3-4
                12 => 682,
                // 伊甸共鸣 1-2
                13 => 715,
                // 伊甸共鸣 3-4
                14 => 726,
                // 伊甸再生 1-2
                15 => 749,
                // 伊甸再生 3-4
                16 => 751,
                // 万魔殿边狱 1-2
                17 => 808,
                // 万魔殿边狱 3-4
                18 => 807,
                // 万魔殿炼狱 1-2
                19 => 872,
                // 万魔殿炼狱 3-4
                20 => 876,
                // 万魔殿天狱 1-2
                21 => 936,
                // 万魔殿天狱 3-4
                22 => 941,
                // 伊甸觉醒
                23 => 653,
                // 伊甸共鸣
                24 => 715,
                // 伊甸再生
                25 => 749,
                // 2.0 团本
                26 => 92,
                // 3.0 团本
                27 => 120,
                // 4.0 团本
                28 => 281,
                // 5.0 团本
                29 => 700,
                // 6.0 团本
                30 => 866,
                // 万魔殿边狱
                31 => 808,
                // 万魔殿炼狱
                32 => 872,
                // 万魔殿天狱
                33 => 936,
                // 阿卡狄亚轻量级 1-2
                34 => 985,
                // 阿卡狄亚轻量级 3-4
                35 => 989,
                // 阿卡狄亚轻量级 1-4
                36 => 985,
                // 阿卡狄亚中量级 1-2
                37 => 1019,
                // 阿卡狄亚中量级 3-4,
                38 => 1023,
                _  => 0
            },
            // 多等级区间
            5 => bingoDataID switch
            {
                // 1-49 级迷宫
                49 => 56,
                // 51-59/61-69/71-79 级迷宫
                79 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 2)
                                  .Where(m => m.ClassJobLevelRequired is >= 51 and <= 79 &&
                                              m.ClassJobLevelRequired % 10 != 0)
                                  .Where(m => m.ClassJobLevelRequired % 10 != 0)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                // 81-89/91-99 级迷宫
                99 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 2)
                                  .Where(m => m.ClassJobLevelRequired is >= 81 and <= 99 &&
                                              m.ClassJobLevelRequired % 10 != 0)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                _ => 0
            },
            // 整数级迷宫
            6 => bingoDataID switch
            {
                60 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 2)
                                  .Where(m => m.ClassJobLevelRequired is (50 or 60))
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                80 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 2)
                                  .Where(m => m.ClassJobLevelRequired is (70 or 80))
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                90 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 2)
                                  .Where(m => m.ClassJobLevelRequired is 90)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                _ => 0
            },
            // 歼灭战
            7 => bingoDataID switch
            {
                60 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 4)
                                  .Where(m => m.ClassJobLevelRequired is >= 50 and <= 60)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                100 => LuminaGetter.Get<ContentFinderCondition>()
                                   .Where(m => m.ContentType.RowId is 4)
                                   .Where(m => m.ClassJobLevelRequired is >= 70 and <= 100)
                                   .OrderBy(row => row.SortKey)
                                   .FirstOrDefault().RowId,
                _ => 0
            },
            // 团本
            8 => bingoDataID switch
            {
                60 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 5)
                                  .Where(m => m.ClassJobLevelRequired is >= 50 and <= 60)
                                  .Where(m => m.AllianceRoulette)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                90 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 5)
                                  .Where(m => m.ClassJobLevelRequired is >= 70 and <= 90)
                                  .Where(m => m.AllianceRoulette)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                _ => 0
            },
            // 大型
            9 => bingoDataID switch
            {
                60 => LuminaGetter.Get<ContentFinderCondition>()
                                  .Where(m => m.ContentType.RowId is 5)
                                  .Where(m => m.NormalRaidRoulette)
                                  .Where(m => m.ClassJobLevelRequired is >= 50 and <= 60)
                                  .OrderBy(row => row.SortKey)
                                  .FirstOrDefault().RowId,
                100 => LuminaGetter.Get<ContentFinderCondition>()
                                   .Where(m => m.ContentType.RowId is 5)
                                   .Where(m => m.NormalRaidRoulette)
                                   .Where(m => m.ClassJobLevelRequired is >= 70 and <= 100)
                                   .OrderBy(row => row.SortKey)
                                   .FirstOrDefault().RowId,
                _ => 0
            },
            _ => 0,
        };

        if (bingoDataRow.Type == 0 && !UIState.IsInstanceContentUnlocked(bingoDataID))
            NotificationError(GetLoc("WeeklyBingoClickToOpen-UnlockError")); // 还没有解锁该副本

        return dutyRowID != 0;
    }
}
