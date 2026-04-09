using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoMiniCactpot : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoMiniCactpotTitle"),
        Description = Lang.Get("AutoMiniCactpotDescription"),
        Category    = ModuleCategory.GoldSaucer
    };
    
    private readonly MiniCactpotSolver solver = new();

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 5_000 };

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   "LotteryDaily", OnAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LotteryDaily", OnAddon);
        if (LotteryDaily != null)
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnAddon);
        FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                TaskHelper.Abort();
                FrameworkManager.Instance().Reg(OnUpdate);
                break;
            case AddonEvent.PreFinalize:
                FrameworkManager.Instance().Unreg(OnUpdate);
                TaskHelper.Enqueue(() => AddonSelectYesnoEvent.ClickYes());
                break;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        var addon = (AddonLotteryDaily*)LotteryDaily;
        var agent = AgentLotteryDaily.Instance();
        if (addon == null || agent == null) return;

        Span<byte> state = stackalloc byte[MiniCactpotSolver.TOTAL_NUMBERS];
        for (var i = 0; i < MiniCactpotSolver.TOTAL_NUMBERS; i++)
            state[i] = agent->Numbers[i];

        switch (agent->Status)
        {
            // 选数字
            case 1:
            {
                var solution = solver.Solve(state);

                for (var i = 0; i < MiniCactpotSolver.TOTAL_NUMBERS; i++)
                    if (solution[i])
                    {
                        ClickGameNode(addon, i);
                        break;
                    }

                break;
            }

            // 选线
            case 2:
            {
                var               solution = solver.Solve(state);
                ReadOnlySpan<int> map      = [6, 3, 4, 5, 7, 0, 1, 2];

                for (var i = 0; i < MiniCactpotSolver.TOTAL_LANES; i++)
                    if (solution[map[i]])
                    {
                        ClickLaneNode(addon, i);
                        break;
                    }

                break;
            }

            // 结束
            case 4:
                addon->AtkUnitBase.Callback(-1);
                addon->Close(true);
                break;
        }
    }

    private static void ClickGameNode(AddonLotteryDaily* addon, int i)
    {
        var nodeID = addon->GameBoard[i]->AtkComponentButton.AtkComponentBase.OwnerNode->AtkResNode.NodeId;
        if (nodeID is < 30 or > 38) return;

        addon->AtkUnitBase.Callback(1, (int)(nodeID - 30));
    }

    private static void ClickLaneNode(AddonLotteryDaily* addon, int i)
    {
        if (i is < 0 or > 8) return;

        var nodeID = addon->LaneSelector[i]->OwnerNode->NodeId;
        if (nodeID is < 21 or > 28) return;

        var unkNumber3D4 = (int)(nodeID - 21);

        var ptr = (int*)((nint)addon + 1004);
        *ptr = unkNumber3D4;

        addon->AtkUnitBase.Callback(2, unkNumber3D4);
    }

    internal sealed class MiniCactpotSolver
    {
        public const int TOTAL_NUMBERS = 9;
        public const int TOTAL_LANES   = 8;

        private static readonly ushort[] Payouts =
        [
            0, 0, 0, 0, 0, 0,
            10000, 36, 720, 360, 80, 252, 108, 72, 54, 180, 72, 180, 119, 36, 306, 1080, 144, 1800, 3600
        ];

        private static readonly byte[][] Lines =
        [
            [0, 1, 2], [3, 4, 5], [6, 7, 8],
            [0, 3, 6], [1, 4, 7], [2, 5, 8],
            [0, 4, 8], [2, 4, 6]
        ];

        private readonly Dictionary<ulong, float> memo = new();

        public bool[] Solve(ReadOnlySpan<byte> state)
        {
            ulong board         = 0;
            var   revealedCount = 0;
            var   usedMask      = 0;

            for (var i = 0; i < 9; i++)
            {
                var val = state[i];
                if (val <= 0) continue;
                board    |= (ulong)val << i * 4;
                usedMask |= 1          << val - 1;
                revealedCount++;
            }

            if (revealedCount == 4)
            {
                var result = new bool[8];
                var maxEv  = -1.0;

                Span<byte> available = stackalloc byte[9];
                var        avCount   = 0;
                for (var i = 1; i <= 9; i++)
                    if ((usedMask & 1 << i - 1) == 0)
                        available[avCount++] = (byte)i;
                var availableSpan = available[..avCount];

                for (var i = 0; i < 8; i++)
                {
                    var ev = CalculateLineEV(board, i, availableSpan);

                    if (ev > maxEv + 0.001)
                    {
                        maxEv = ev;
                        Array.Clear(result, 0, result.Length);
                        result[i] = true;
                    }
                    else if (Math.Abs(ev - maxEv) < 0.001)
                        result[i] = true;
                }

                return result;
            }
            else
            {
                memo.Clear();
                var result = new bool[9];
                var maxEv  = -1.0;

                for (var i = 0; i < 9; i++)
                {
                    if ((board >> i * 4 & 0xF) != 0) continue;

                    var ev = EvaluateCell(board, usedMask, revealedCount, i);

                    if (ev > maxEv + 0.001)
                    {
                        maxEv = ev;
                        Array.Clear(result, 0, result.Length);
                        result[i] = true;
                    }
                    else if (Math.Abs(ev - maxEv) < 0.001)
                        result[i] = true;
                }

                return result;
            }
        }

        private float GetMaxEV(ulong board, int usedMask, int revealedCount)
        {
            if (revealedCount == 4)
            {
                if (memo.TryGetValue(board, out var val)) return val;

                var        maxLineEv = 0f;
                Span<byte> available = stackalloc byte[9];
                var        avCount   = 0;
                for (var i = 1; i <= 9; i++)
                    if ((usedMask & 1 << i - 1) == 0)
                        available[avCount++] = (byte)i;

                var availableSpan = available[..avCount];

                for (var i = 0; i < 8; i++)
                {
                    var ev = CalculateLineEV(board, i, availableSpan);
                    if (ev > maxLineEv)
                        maxLineEv = ev;
                }

                memo[board] = maxLineEv;
                return maxLineEv;
            }
            else
            {
                if (memo.TryGetValue(board, out var val)) return val;

                var maxCellEv = 0f;

                for (var i = 0; i < 9; i++)
                {
                    if ((board >> i * 4 & 0xF) != 0) continue;

                    var ev = EvaluateCell(board, usedMask, revealedCount, i);
                    if (ev > maxCellEv)
                        maxCellEv = ev;
                }

                memo[board] = maxCellEv;
                return maxCellEv;
            }
        }

        private float EvaluateCell(ulong board, int usedMask, int revealedCount, int cellIndex)
        {
            var sumEv         = 0f;
            var count         = 0;
            var availableMask = ~usedMask & 0x1FF;

            for (var v = 1; v <= 9; v++)
            {
                if ((availableMask & 1 << v - 1) == 0) continue;

                var nextBoard = board | (ulong)v << cellIndex * 4;
                sumEv += GetMaxEV(nextBoard, usedMask | 1 << v - 1, revealedCount + 1);
                count++;
            }

            return count == 0 ? 0 : sumEv / count;
        }

        private static float CalculateLineEV(ulong board, int lineIdx, ReadOnlySpan<byte> available)
        {
            var lineIndices = Lines[lineIdx];
            var currentSum  = 0;
            var hiddenCount = 0;

            Span<byte> hiddenInLine = stackalloc byte[3];

            for (var k = 0; k < 3; k++)
            {
                var idx = lineIndices[k];
                var val = (int)(board >> idx * 4 & 0xF);
                if (val > 0)
                    currentSum += val;
                else
                    hiddenInLine[hiddenCount++] = idx;
            }

            if (hiddenCount == 0) return Payouts[currentSum];

            var totalPayout = 0;
            var perms       = 0;

            switch (hiddenCount)
            {
                case 1:
                {
                    foreach (var t in available)
                        totalPayout += Payouts[currentSum + t];

                    perms = available.Length;
                    break;
                }
                case 2:
                {
                    for (var i = 0; i < available.Length; i++)
                    for (var j = 0; j < available.Length; j++)
                    {
                        if (i == j) continue;
                        totalPayout += Payouts[currentSum + available[i] + available[j]];
                        perms++;
                    }

                    break;
                }
                default:
                {
                    for (var i = 0; i < available.Length; i++)
                    for (var j = 0; j < available.Length; j++)
                    {
                        if (i == j) continue;

                        for (var k = 0; k < available.Length; k++)
                        {
                            if (k == i || k == j) continue;
                            totalPayout += Payouts[currentSum + available[i] + available[j] + available[k]];
                            perms++;
                        }
                    }

                    break;
                }
            }

            return (float)totalPayout / perms;
        }
    }
}
