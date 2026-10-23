using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.Expansion.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using AddonWeeklyBingo = FFXIVClientStructs.FFXIV.Client.UI.AddonWeeklyBingo;
using AgentWeeklyBingo = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentWeeklyBingo;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class OptimizedWeeklyBingo : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedWeeklyBingoTitle"),
        Description = Lang.Get("OptimizedWeeklyBingoDescription"),
        Category    = ModuleCategory.Interface,
        Author      = ["Due"],
        PreviewImageURL =
        [
            "https://gh.atmoomen.top/raw.githubusercontent.com/Dalamud-DailyRoutines/DailyRoutines/main/Resources/Modules/OptimizedWeeklyBingo/preview-1.webp"
        ]
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<DutySlot.Delegates.ReceiveEvent>? DutySlotReceiveEventHook;

    private readonly IAddonEventHandle[] eventHandles = new IAddonEventHandle[DUTY_SLOT_COUNT];

    private ProbabilityCalculator probabilityCalculator = null!;

    protected override void Init()
    {
        probabilityCalculator = new();

        DutySlotReceiveEventHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(DutySlot.MemberFunctionPointers),
            nameof(DutySlot.MemberFunctionPointers.ReceiveEvent),
            (DutySlot.Delegates.ReceiveEvent)DutySlotReceiveEventDetour
        );
        DutySlotReceiveEventHook.Enable();

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "WeeklyBingo", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostRefresh, "WeeklyBingo", OnAddon);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "WeeklyBingo", OnAddon);

        if (WeeklyBingo->IsAddonAndNodesReady())
            OnAddon(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        IAddonLifecycle.Instance().UnregisterListener(OnAddon);
        RemoveEvents();
    }

    private void OnAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        switch (type)
        {
            case AddonEvent.PostSetup:
                RemoveEvents();
                SetupDutySlotEvents();
                UpdateProbabilityText();
                break;
            case AddonEvent.PostRefresh:
                UpdateProbabilityText();
                break;
            case AddonEvent.PreFinalize:
                RemoveEvents();
                break;
        }
    }

    private void SetupDutySlotEvents()
    {
        var addon = (AddonWeeklyBingo*)WeeklyBingo;
        if (addon == null) return;

        for (var index = 0; index < DUTY_SLOT_COUNT; index++)
        {
            var button = addon->DutySlotList[index].DutyButton;
            if (button == null || button->OwnerNode == null) continue;

            var slotIndex = index;
            eventHandles[index] = IAddonEventManager.Instance().AddEvent
            (
                (nint)addon,
                (nint)button->OwnerNode,
                AddonEventType.ButtonClick,
                (_, _) => OnDutySlotClick(slotIndex)
            );
        }
    }

    private void UpdateProbabilityText()
    {
        var addon = (AddonWeeklyBingo*)WeeklyBingo;
        if (addon == null) return;

        var probabilities = probabilityCalculator.GetLineProbabilities();
        if (double.IsNaN(probabilities.OneLine)) return;

        var lineOneNode = addon->GetTextNodeById(78);
        if (lineOneNode == null) return;

        var lineTwoNode = addon->GetTextNodeById(89);
        if (lineTwoNode == null) return;

        var lineThreeNode = addon->GetTextNodeById(100);
        if (lineThreeNode == null) return;

        lineOneNode->SetText($"{LuminaWrapper.GetAddonText(5611)}（{probabilities.OneLine:P2}）");
        lineTwoNode->SetText($"{LuminaWrapper.GetAddonText(5612)}（{probabilities.TwoLines:P2}）");
        lineThreeNode->SetText($"{LuminaWrapper.GetAddonText(5613)}（{probabilities.ThreeLines:P2}）");
    }

    private void RemoveEvents()
    {
        for (var index = 0; index < eventHandles.Length; index++)
        {
            if (eventHandles[index] is not { } handle) continue;

            IAddonEventManager.Instance().RemoveEvent(handle);
            eventHandles[index] = null;
        }
    }

    private void DutySlotReceiveEventDetour
    (
        DutySlot*     dutySlot,
        AtkEventType  eventType,
        int           eventParam,
        AtkEvent*     atkEvent,
        AtkEventData* atkEventData
    )
    {
        if (eventType                        == AtkEventType.ButtonClick &&
            eventParam                       == 0                        &&
            atkEventData                     != null                     &&
            atkEventData->MouseData.ButtonId == 1)
            return;

        DutySlotReceiveEventHook.Original(dutySlot, eventType, eventParam, atkEvent, atkEventData);
    }

    private static void OnDutySlotClick
    (
        int index
    )
    {
        if ((uint)index >= DUTY_SLOT_COUNT ||
            ICondition.Instance().IsBoundByDuty)
            return;

        var player = PlayerState.Instance();
        if (player == null                 ||
            !player->HasWeeklyBingoJournal ||
            player->IsWeeklyBingoExpired() ||
            player->GetWeeklyBingoTaskStatus(index) != PlayerState.WeeklyBingoTaskStatus.Open)
            return;

        var agent = AgentWeeklyBingo.Instance();
        if (agent == null || agent->ContentListAddonID != 0) return;

        agent->SelectedDutySlotIndex = (uint)index;

        if (ShouldUseNativeOpen(player->WeeklyBingoOrderData[index]))
        {
            agent->OpenDutyOrContentList();
            return;
        }

        agent->ContentListAddonID = 1;
        agent->OpenDutyOrContentList();
        agent->ContentListAddonID = 0;

        switch (agent->ContentList.Count)
        {
            case 0:
                break;
            case 1:
                var contentsFinder = AgentContentsFinder.Instance();
                if (contentsFinder != null)
                    contentsFinder->OpenRegularDuty(agent->ContentList[0].ContentFinderConditionID);
                break;
            default:
                agent->OpenContentList();
                break;
        }
    }

    private static bool ShouldUseNativeOpen
    (
        uint orderID
    ) =>
        !LuminaGetter.TryGetRow<WeeklyBingoOrderData>(orderID, out var order) ||
        order.Type is 0 or 3 or > 9;

    private sealed class ProbabilityCalculator
    {
        private readonly long[]?[] probabilityBoards = new long[]?[1 << BOARD_CELL_COUNT];

        private int    lastProbabilityMask = -1;
        private double lastOneLineProbability;
        private double lastTwoLineProbability;
        private double lastThreeLineProbability;

        public (double OneLine, double TwoLines, double ThreeLines) GetLineProbabilities()
        {
            var player = PlayerState.Instance();

            if (player == null || !player->HasWeeklyBingoJournal)
                return (double.NaN, double.NaN, double.NaN);

            var mask = 0;

            for (var index = 0; index < BOARD_CELL_COUNT; index++)
            {
                if (player->IsWeeklyBingoStickerPlaced(index))
                    mask |= 1 << index;
            }

            if (mask == lastProbabilityMask)
                return (lastOneLineProbability, lastTwoLineProbability, lastThreeLineProbability);

            if (BitOperations.PopCount((uint)mask) > STICKER_COUNT)
                return (double.NaN, double.NaN, double.NaN);

            var counts = CountBoards(mask);

            lastProbabilityMask      = mask;
            lastOneLineProbability   = (double)counts[1] / counts[0];
            lastTwoLineProbability   = (double)counts[2] / counts[0];
            lastThreeLineProbability = (double)counts[3] / counts[0];

            return (lastOneLineProbability, lastTwoLineProbability, lastThreeLineProbability);
        }

        private long[] CountBoards
        (
            int mask
        )
        {
            if (probabilityBoards[mask] is { } cached)
                return cached;

            if (BitOperations.PopCount((uint)mask) >= STICKER_COUNT)
            {
                var lines = CountLines(mask);

                return probabilityBoards[mask] =
                [
                    1,
                    lines >= 1 ?
                        1 :
                        0,
                    lines >= 2 ?
                        1 :
                        0,
                    lines >= 3 ?
                        1 :
                        0
                ];
            }

            var counts = new long[4];

            for (var cell = 0; cell < BOARD_CELL_COUNT; cell++)
            {
                if ((mask & (1 << cell)) != 0) continue;

                var childCounts = CountBoards(mask | (1 << cell));

                for (var index = 0; index < counts.Length; index++)
                    counts[index] += childCounts[index];
            }

            return probabilityBoards[mask] = counts;
        }

        private static int CountLines
        (
            int mask
        )
        {
            var lines     = 0;
            var diagonal1 = 0;
            var diagonal2 = 0;

            for (var index = 0; index < BOARD_SIZE; index++)
            {
                var rowMask    = 0;
                var columnMask = 0;

                for (var offset = 0; offset < BOARD_SIZE; offset++)
                {
                    rowMask    |= 1 << ((index  * BOARD_SIZE) + offset);
                    columnMask |= 1 << ((offset * BOARD_SIZE) + index);
                }

                if ((mask & rowMask) == rowMask)
                    lines++;

                if ((mask & columnMask) == columnMask)
                    lines++;

                diagonal1 |= 1 << ((index * BOARD_SIZE) + index);
                diagonal2 |= 1 << ((index * BOARD_SIZE) + (BOARD_SIZE - 1 - index));
            }

            if ((mask & diagonal1) == diagonal1)
                lines++;

            if ((mask & diagonal2) == diagonal2)
                lines++;

            return lines;
        }

        #region 常量

        private const int BOARD_SIZE       = 4;
        private const int BOARD_CELL_COUNT = BOARD_SIZE * BOARD_SIZE;
        private const int STICKER_COUNT    = 9;

        #endregion
    }

    #region 常量

    private const int DUTY_SLOT_COUNT = 16;

    #endregion
}
