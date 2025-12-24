using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMiniCactpot : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoMiniCactpotTitle"),
        Description = GetLoc("AutoMiniCactpotDescription"),
        Category    = ModuleCategories.GoldSaucer,
    };

    private const  int TotalNumbers = PerfectCactpot.TotalNumbers;
    private const  int TotalLanes   = PerfectCactpot.TotalLanes;
    private static int SelectedLineNumber3D4;

    private static readonly PerfectCactpot perfectCactpot = new();
    private static          int[]          gameState      = new int[TotalNumbers];

    protected override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 5_000 };

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LotteryDaily", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LotteryDaily", OnAddon);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                DService.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LotteryDaily", OnAddon);
                TaskHelper.Abort();
                OnAddon(AddonEvent.PostRefresh, args);
                break;
            case AddonEvent.PostRefresh:
                TaskHelper.DelayNext(100);
                TaskHelper.Enqueue(() => IsAddonAndNodesReady(LotteryDaily));
                TaskHelper.Enqueue(() => GameUpdater((nint)LotteryDaily));
                break;
            case AddonEvent.PreFinalize:
                DService.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "LotteryDaily", OnAddon);
                TaskHelper.Enqueue(() => ClickSelectYesnoYes());
                break;
        }
    }

    private void GameUpdater(nint addonPtr)
    {
        var addon = (AddonLotteryDaily*)addonPtr;
        gameState = GetGameState(addon);

        // 游戏结束
        if (!gameState.Contains(0))
        {
            TaskHelper.Abort();
            TaskHelper.Enqueue(ClickExit);
        }
        else
        {
            var solution = perfectCactpot.Solve(gameState);

            if (solution.Length == 8)
            {
                solution =
                [
                    solution[6], // 左对角线
                    solution[3], // 第一列
                    solution[4], // 第二列
                    solution[5], // 第三列
                    solution[7], // 右对角线
                    solution[0], // 第一行
                    solution[1], // 第二行
                    solution[2], // 第三行
                ];

                // 线
                for (var i = 0; i < TotalLanes; i++)
                {
                    if (solution[i])
                        ClickLaneNode(addon, i);
                }
            }
            else
            {
                // 点
                for (var i = 0; i < TotalNumbers; i++)
                {
                    if (solution[i])
                    {
                        ClickGameNode(addon, i);
                        break;
                    }
                }
            }
        }
    }

    private static int[] GetGameState(AddonLotteryDaily* addon) => 
        Enumerable.Range(0, TotalNumbers).Select(i => addon->GameNumbers[i]).ToArray();

    private static void ClickGameNode(AddonLotteryDaily* addon, int i)
    {
        var nodeID = addon->GameBoard[i]->AtkComponentButton.AtkComponentBase.OwnerNode->AtkResNode.NodeId;
        ClickLotteryDaily.Using(addon).Block(nodeID);
    }

    private static void ClickLaneNode(AddonLotteryDaily* addon, int i)
    {
        if (i is < 0 or > 8) return;

        var nodeID = addon->LaneSelector[i]->OwnerNode->NodeId;
        
        var unkNumber3D4 = ClickLotteryDaily.Using(addon).Line(nodeID);
        if (unkNumber3D4 == -1) return;
        
        SelectedLineNumber3D4 = unkNumber3D4;

        ClickConfirm();
    }

    private static void ClickConfirm() => ClickLotteryDaily.Using(LotteryDaily).Confirm(SelectedLineNumber3D4);

    private static void ClickExit()
    {
        ClickLotteryDaily.Using(LotteryDaily).Exit();
        LotteryDaily->Close(true);
    }

    protected override void Uninit() => 
        DService.AddonLifecycle.UnregisterListener(OnAddon);

    private sealed class ClickLotteryDaily(AtkUnitBase* Addon)
    {
        public static ClickLotteryDaily Using(AtkUnitBase* addon) => new(addon);
        
        public static ClickLotteryDaily Using(AddonLotteryDaily* addon) => new((AtkUnitBase*)addon);
        
        private static readonly Dictionary<uint, uint> BlockIDToCallbackIndex = new()
        {
            { 30, 0 }, // (0, 0)
            { 31, 1 }, // (0, 1)
            { 32, 2 }, // (0, 2)
            { 33, 3 }, // (1, 0)
            { 34, 4 }, // (1, 1)
            { 35, 5 }, // (1, 2)
            { 36, 6 }, // (2, 0)
            { 37, 7 }, // (2, 1)
            { 38, 8 }  // (2, 2)
        };

        public static readonly Dictionary<uint, int> LineNodeIDToUnkNumber3D4 = new()
        {
            { 22, 1 }, // 第一列 (从左到右)
            { 23, 2 }, // 第二列
            { 24, 3 }, // 第二列
            { 26, 5 }, // 第一行 (从上到下) 
            { 27, 6 }, // 第二行
            { 28, 7 }, // 第二行
            { 21, 0 }, // 左侧对角线
            { 25, 4 }  // 右侧对角线
        };

        public void Block(uint nodeID)
        {
            if (!BlockIDToCallbackIndex.TryGetValue(nodeID, out var index)) return;
            Callback(Addon, true, 1, index);
        }

        public int Line(uint nodeID)
        {
            var unkNumber3D4 = LineNodeIDToUnkNumber3D4[nodeID];
            var ptr          = (int*)((nint)Addon + 1004);
            *ptr = unkNumber3D4;

            return unkNumber3D4;
        }

        public void Confirm(int index) => Callback(Addon, true, 2, index);

        public void Exit() => Callback(Addon, true, -1);
    }

    /// <summary>
    ///     https://super-aardvark.github.io/yuryu/
    /// </summary>
    internal sealed class PerfectCactpot
    {
        public const int TotalNumbers = 9;
        public const int TotalLanes = 8;

        private const double EPS = 0.00001;

        private readonly Dictionary<string, (double Value, bool[] Tiles)> PrecalculatedOpenings = new()
        {
            { "100000000", (1677.7854166666664, [false, false, true, false, false, false, true, false, false]) },
            { "200000000", (1665.8127976190476, [false, false, true, false, false, false, true, false, false]) },
            { "300000000", (1662.5047619047620, [false, false, true, false, false, false, true, false, false]) },
            { "400000000", (1365.0047619047618, [false, false, false, false, true, false, false, false, false]) },
            { "500000000", (1359.5589285714286, [false, false, false, false, true, false, false, false, false]) },
            { "600000000", (1364.3044642857142, [false, false, false, false, true, false, false, false, false]) },
            { "700000000", (1454.5455357142855, [false, false, false, false, true, false, false, false, false]) },
            { "800000000", (1527.0875000000000, [false, false, true, false, true, false, true, false, false]) },
            { "900000000", (1517.7214285714285, [false, false, true, false, true, false, true, false, false]) },
            { "010000000", (1411.3541666666665, [false, false, false, false, true, false, false, false, false]) },
            { "020000000", (1414.9401785714288, [false, false, false, false, true, false, false, false, false]) },
            { "030000000", (1406.4190476190477, [false, false, false, false, true, false, false, false, false]) },
            { "040000000", (1443.3062499999999, [false, false, false, false, false, false, true, false, true]) },
            { "050000000", (1444.3172619047618, [false, false, false, false, true, false, true, false, true]) },
            { "060000000", (1441.3663690476192, [false, false, false, false, true, false, false, false, false]) },
            { "070000000", (1485.6839285714286, [false, false, false, false, true, false, false, false, false]) },
            { "080000000", (1512.9279761904760, [true, false, true, false, false, false, false, false, false]) },
            { "090000000", (1518.4663690476190, [true, false, true, false, false, false, false, false, false]) },
            { "001000000", (1677.7854166666664, [true, false, false, false, false, false, false, false, true]) },
            { "002000000", (1665.8127976190476, [true, false, false, false, false, false, false, false, true]) },
            { "003000000", (1662.5047619047620, [true, false, false, false, false, false, false, false, true]) },
            { "004000000", (1365.0047619047618, [false, false, false, false, true, false, false, false, false]) },
            { "005000000", (1359.5589285714286, [false, false, false, false, true, false, false, false, false]) },
            { "006000000", (1364.3044642857142, [false, false, false, false, true, false, false, false, false]) },
            { "007000000", (1454.5455357142855, [false, false, false, false, true, false, false, false, false]) },
            { "008000000", (1527.0875000000000, [true, false, false, false, true, false, false, false, true]) },
            { "009000000", (1517.7214285714285, [true, false, false, false, true, false, false, false, true]) },
            { "000100000", (1411.3541666666665, [false, false, false, false, true, false, false, false, false]) },
            { "000200000", (1414.9401785714288, [false, false, false, false, true, false, false, false, false]) },
            { "000300000", (1406.4190476190477, [false, false, false, false, true, false, false, false, false]) },
            { "000400000", (1443.3062499999999, [false, false, true, false, false, false, false, false, true]) },
            { "000500000", (1444.3172619047618, [false, false, true, false, true, false, false, false, true]) },
            { "000600000", (1441.3663690476192, [false, false, false, false, true, false, false, false, false]) },
            { "000700000", (1485.6839285714286, [false, false, false, false, true, false, false, false, false]) },
            { "000800000", (1512.9279761904760, [true, false, false, false, false, false, true, false, false]) },
            { "000900000", (1518.4663690476190, [true, false, false, false, false, false, true, false, false]) },
            { "000010000", (1860.4401785714285, [true, false, true, false, false, false, true, false, true]) },
            { "000020000", (1832.5413690476191, [true, false, true, false, false, false, true, false, true]) },
            { "000030000", (1834.1797619047620, [true, false, true, false, false, false, true, false, true]) },
            { "000040000", (1171.9669642857143, [true, false, true, false, false, false, true, false, true]) },
            { "000050000", (1176.2047619047619, [true, false, true, false, false, false, true, false, true]) },
            { "000060000", (1234.6142857142856, [true, false, true, false, false, false, true, false, true]) },
            { "000070000", (1427.3583333333331, [true, false, true, false, false, false, true, false, true]) },
            { "000080000", (1544.7607142857144, [true, false, true, false, false, false, true, false, true]) },
            { "000090000", (1509.1976190476190, [true, false, true, false, false, false, true, false, true]) },
            { "000001000", (1411.3541666666665, [false, false, false, false, true, false, false, false, false]) },
            { "000002000", (1414.9401785714288, [false, false, false, false, true, false, false, false, false]) },
            { "000003000", (1406.4190476190477, [false, false, false, false, true, false, false, false, false]) },
            { "000004000", (1443.3062499999999, [true, false, false, false, false, false, true, false, false]) },
            { "000005000", (1444.3172619047618, [true, false, true, false, false, false, true, false, false]) },
            { "000006000", (1441.3663690476192, [false, false, false, false, true, false, false, false, false]) },
            { "000007000", (1485.6839285714286, [false, false, false, false, true, false, false, false, false]) },
            { "000008000", (1512.9279761904760, [false, false, true, false, false, false, false, false, true]) },
            { "000009000", (1518.4663690476190, [false, false, true, false, false, false, false, false, true]) },
            { "000000100", (1677.7854166666664, [true, false, false, false, false, false, false, false, true]) },
            { "000000200", (1665.8127976190476, [true, false, false, false, false, false, false, false, true]) },
            { "000000300", (1662.5047619047620, [true, false, false, false, false, false, false, false, true]) },
            { "000000400", (1365.0047619047618, [false, false, false, false, true, false, false, false, false]) },
            { "000000500", (1359.5589285714286, [false, false, false, false, true, false, false, false, false]) },
            { "000000600", (1364.3044642857142, [false, false, false, false, true, false, false, false, false]) },
            { "000000700", (1454.5455357142855, [false, false, false, false, true, false, false, false, false]) },
            { "000000800", (1527.0875000000000, [true, false, false, false, true, false, false, false, true]) },
            { "000000900", (1517.7214285714285, [true, false, false, false, true, false, false, false, true]) },
            { "000000010", (1411.3541666666665, [false, false, false, false, true, false, false, false, false]) },
            { "000000020", (1414.9401785714288, [false, false, false, false, true, false, false, false, false]) },
            { "000000030", (1406.4190476190477, [false, false, false, false, true, false, false, false, false]) },
            { "000000040", (1443.3062499999999, [true, false, true, false, false, false, false, false, false]) },
            { "000000050", (1444.3172619047618, [true, false, true, false, true, false, false, false, false]) },
            { "000000060", (1441.3663690476192, [false, false, false, false, true, false, false, false, false]) },
            { "000000070", (1485.6839285714286, [false, false, false, false, true, false, false, false, false]) },
            { "000000080", (1512.9279761904760, [false, false, false, false, false, false, true, false, true]) },
            { "000000090", (1518.4663690476190, [false, false, false, false, false, false, true, false, true]) },
            { "000000001", (1677.7854166666664, [false, false, true, false, false, false, true, false, false]) },
            { "000000002", (1665.8127976190476, [false, false, true, false, false, false, true, false, false]) },
            { "000000003", (1662.5047619047620, [false, false, true, false, false, false, true, false, false]) },
            { "000000004", (1365.0047619047618, [false, false, false, false, true, false, false, false, false]) },
            { "000000005", (1359.5589285714286, [false, false, false, false, true, false, false, false, false]) },
            { "000000006", (1364.3044642857142, [false, false, false, false, true, false, false, false, false]) },
            { "000000007", (1454.5455357142855, [false, false, false, false, true, false, false, false, false]) },
            { "000000008", (1527.0875000000000, [false, false, true, false, true, false, true, false, false]) },
            { "000000009", (1517.7214285714285, [false, false, true, false, true, false, true, false, false]) },
        };

        private static int[] Payouts =>
        [
            0, 0, 0, 0, 0, 0, 10000, 36, 720, 360, 80, 252, 108, 72, 54, 180, 72, 180, 119, 36, 306, 1080, 144, 1800,
            3600,
        ];

        internal bool[] Solve(int[] state)
        {
            var num_revealed = state.Count(x => x > 0);

            var num_options = 9;
            if (num_revealed == 4)
                num_options = 8;

            var which_to_flip = new bool[num_options];

            switch (num_revealed)
            {
                case 0:
                    return [true, false, true, false, false, false, true, false, true];

                case 1:
                {
                    var stateStr = string.Join("", state);
                    (_, which_to_flip) = PrecalculatedOpenings[stateStr];
                    break;
                }

                default:
                    SolveAny(ref state, ref which_to_flip);
                    break;
            }

            return which_to_flip;
        }

        private static double SolveAny(ref int[] state, ref bool[] options)
        {
            var dummy_array = new bool[options.Length];
            var hiddenNumbers = new List<int>();
            var ids = new List<int>();
            var has = new int[10];
            var tot_win = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                if (state[i] == 0)
                {
                    ids.Add(i);
                    tot_win.Add(0);
                }
                else
                    has[state[i]] = 1;
            }

            var num_hidden = tot_win.Count;
            var num_revealed = 9 - num_hidden;

            for (var i = 1; i <= 9; i++)
            {
                if (has[i] == 0)
                    hiddenNumbers.Add(i);
            }

            if (num_revealed >= 4)
            {
                var permutations = 0;
                tot_win = [0, 0, 0, 0, 0, 0, 0, 0];

                do
                {
                    permutations++;
                    for (var i = 0; i < ids.Count; i++) 
                        state[ids[i]] = hiddenNumbers[i];

                    tot_win[0] += Payouts[state[0] + state[1] + state[2]];
                    tot_win[1] += Payouts[state[3] + state[4] + state[5]];
                    tot_win[2] += Payouts[state[6] + state[7] + state[8]];
                    tot_win[3] += Payouts[state[0] + state[3] + state[6]];
                    tot_win[4] += Payouts[state[1] + state[4] + state[7]];
                    tot_win[5] += Payouts[state[2] + state[5] + state[8]];
                    tot_win[6] += Payouts[state[0] + state[4] + state[8]];
                    tot_win[7] += Payouts[state[2] + state[4] + state[6]];
                }
                while (NextPermutation(hiddenNumbers));

                var currentMax = tot_win[0];
                options[0] = true;
                for (var i = 1; i < 8; i++)
                {
                    if (tot_win[i] > currentMax)
                    {
                        currentMax = tot_win[i];

                        for (var j = 0; j < i; j++)
                            options[j] = false;

                        options[i] = true;
                    }
                    else if (Math.Abs(tot_win[i] - currentMax) < 0.1f)
                        options[i] = true;
                }

                return currentMax / permutations;
            }
            else
            {
                for (var i = 0; i < num_hidden; i++)
                {
                    for (var j = 0; j < num_hidden; j++)
                    {
                        state[ids[i]] =  hiddenNumbers[j];
                        tot_win[i]    += SolveAny(ref state, ref dummy_array);
                        for (var k = 0; k < num_hidden; k++)
                            state[ids[k]] = 0;
                    }
                }

                var currentMax = tot_win[0];
                options[ids[0]] = true;
                for (var i = 1; i < tot_win.Count; i++)
                {
                    if (tot_win[i] > currentMax + EPS)
                    {
                        currentMax = tot_win[i];
                        for (var j = 0; j < i; j++)
                            options[ids[j]] = false;

                        options[ids[i]] = true;
                    }
                    else if (tot_win[i] > currentMax - EPS)
                        options[ids[i]] = true;
                }

                return currentMax / num_hidden;
            }
        }

        private static bool NextPermutation(List<int> list)
        {
            const int begin = 0;
            var end = list.Count;

            if (list.Count <= 1)
                return false;

            var i = list.Count - 1;

            while (true)
            {
                var j = i;
                i--;

                if (list[i] < list[j])
                {
                    var k = end;

                    while (list[i] >= list[--k]) 
                    { }

                    Swap(list, i, k);
                    Reverse(list, j, end);
                    return true;
                }

                if (i == begin)
                {
                    Reverse(list, begin, end);
                    return false;
                }
            }
        }

        private static void Reverse<T>(List<T> list, int begin, int end)
        {
            var count = end - begin;

            var reversedSlice = list.GetRange(begin, count);
            reversedSlice.Reverse();

            for (var i = 0; i < reversedSlice.Count; i++) 
                list[begin + i] = reversedSlice[i];
        }

        private static void Swap<T>(List<T> list, int i1, int i2) => (list[i1], list[i2]) = (list[i2], list[i1]);
    }
}
