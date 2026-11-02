using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using Timer = System.Threading.Timer;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.KamiToolKit.Styles;
using OmenTools.KamiToolKit.Styles.Island;
using ScheduleAgent = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;
using RestAddon = FFXIVClientStructs.Expansion.FFXIV.Client.UI.AddonMJICraftScheduleMaintenance;
using NativeScheduleAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;

namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

public unsafe partial class OptimizedMJICraftSchedule : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OptimizedMJICraftScheduleTitle"),
        Description = Lang.Get("OptimizedMJICraftScheduleDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Hook<ScheduleAgent.Delegates.UpdateScheduleData>        UpdateScheduleDataHook;
    private Hook<ScheduleAgent.Delegates.HandleEventSetRestConfirm> HandleEventSetRestConfirmHook;
    private Hook<RestAddon.Delegates.UpdateConfirmButton>           UpdateConfirmButtonHook;
    private Hook<ScheduleAgent.Delegates.AskSetRestConfirm>         AskSetRestConfirmHook;
    private Hook<ScheduleAgent.Delegates.UpdateMainAddon>           UpdateMainAddonHook;

    private CalculationInput?  input;
    private CalculationResult? result;
    
    private          CancellationTokenSource cancellation;
    private          Timer                   timeout;
    private readonly Queue<byte>             pendingCycles    = [];
    private readonly List<ScheduledCraft>    expectedSchedule = [];
    private readonly Queue<ScheduledCraft>   pendingCrafts    = [];
    private          Operation               operation;
    private          byte                    requestedCycle;
    private          byte                    originalCycle;
    private          int                     generation;
    private          bool                    enabled;
    private          bool                    isPreviewing;

    protected override void Init()
    {
        enabled = true;

        uiStyle = new IslandUIStyle();
        
        UpdateScheduleDataHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.UpdateScheduleData),
            (ScheduleAgent.Delegates.UpdateScheduleData)UpdateScheduleDataDetour
        );
        HandleEventSetRestConfirmHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.HandleEventSetRestConfirm),
            (ScheduleAgent.Delegates.HandleEventSetRestConfirm)HandleEventSetRestConfirmDetour
        );
        UpdateConfirmButtonHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(RestAddon.MemberFunctionPointers),
            nameof(RestAddon.UpdateConfirmButton),
            (RestAddon.Delegates.UpdateConfirmButton)UpdateConfirmButtonDetour
        );
        AskSetRestConfirmHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.AskSetRestConfirm),
            (ScheduleAgent.Delegates.AskSetRestConfirm)AskSetRestConfirmDetour
        );
        UpdateMainAddonHook ??= IGameInteropProvider.Instance().HookFromMemberFunction
        (
            typeof(ScheduleAgent.MemberFunctionPointers),
            nameof(ScheduleAgent.UpdateMainAddon),
            (ScheduleAgent.Delegates.UpdateMainAddon)UpdateMainAddonDetour
        );
        UpdateMainAddonHook.Enable();
        AskSetRestConfirmHook.Enable();
        UpdateScheduleDataHook.Enable();
        HandleEventSetRestConfirmHook.Enable();
        UpdateConfirmButtonHook.Enable();

        panel = new(this)
        {
            InternalName   = "DROptimizedMJICraftSchedule",
            Title          = Info.Title,
            Style          = uiStyle,
            ContentPadding = Vector2.Zero,
            Size           = new(480, 384)
        };

        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PostSetup,   "MJICraftSchedule", OnSchedule);
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MJICraftSchedule", OnSchedule);
        if (MJICraftSchedule->IsAddonAndNodesReady())
            OnSchedule(AddonEvent.PostSetup, null);
    }

    protected override void Uninit()
    {
        CancelPreview();
        enabled = false;
        StopOperation();
        
        IAddonLifecycle.Instance().UnregisterListener(OnSchedule);
        
        previewIndicator?.Dispose();
        previewIndicator = null;
        
        panel?.Dispose();
        panel = null;
    }

    private void StartCalculation(int week, bool includeFavors)
    {
        if (operation != Operation.Idle)
            return;

        CancelPreview();
        var agent   = ScheduleAgent.Instance();
        var manager = MJIManager.Instance();

        if (agent       == null ||
            agent->Data == null ||
            manager     == null)
        {
            panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
            return;
        }

        originalCycle = agent->Data->CycleDisplayed;
        operation     = Operation.Preparing;
        result        = null;
        panel.Refresh();
        var currentGeneration = ++generation;
        var deadline          = DateTime.UtcNow.AddSeconds(15);
        timeout = new
        (
            _ =>
            {
                IFramework.Instance().Run
                (
                    () =>
                    {
                        if (!enabled                        ||
                            currentGeneration != generation ||
                            operation         != Operation.Preparing)
                            return;

                        var currentAgent   = ScheduleAgent.Instance();
                        var currentManager = MJIManager.Instance();

                        if (currentAgent       == null ||
                            currentAgent->Data == null ||
                            currentManager     == null)
                        {
                            StopOperation();
                            return;
                        }

                        if ((currentAgent->Data->Flags2 &
                             (NativeScheduleAgent.DataFlags2.ScheduleDataAvailable | NativeScheduleAgent.DataFlags2.DisableInteraction)) !=
                            NativeScheduleAgent.DataFlags2.ScheduleDataAvailable ||
                            currentManager->DemandDirty                          ||
                            (includeFavors && (currentManager->FavorState == null || currentManager->FavorState->UpdateState != 2)))
                        {
                            if (DateTime.UtcNow >= deadline)
                            {
                                StopOperation();
                                panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                            }

                            return;
                        }

                        timeout?.Dispose();
                        timeout = null;
                        ReadCalculationInput(week, includeFavors);
                    },
                    CancellationToken.None
                );
            },
            null,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200)
        );
        manager->RequestDemandFull();
        if (includeFavors)
            manager->RequestFavorData();
        agent->SetDisplayedCycle(originalCycle);
    }

    private void ReadCalculationInput(int week, bool includeFavors)
    {
        var agent             = ScheduleAgent.Instance();
        var manager           = MJIManager.Instance();
        var data              = agent->Data;
        var currentPopularity = LuminaGetter.GetRow<MJICraftworksPopularity>(manager->CurrentPopularity);
        var nextPopularity    = LuminaGetter.GetRow<MJICraftworksPopularity>(manager->NextPopularity);

        if (currentPopularity == null ||
            nextPopularity    == null)
        {
            StopOperation();
            panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
            return;
        }

        input = new()
        {
            CurrentCycle = data->CycleInProgress,
            FirstCycle   = (byte)Math.Max(data->CycleInProgress + 1, week * 7),
            EndCycle     = (byte)((week                         + 1) * 7),
            RestCycles   = data->RestCycles,
            Groove       = (int)data->Groove
        };
        result = null;
        pendingCycles.Clear();

        for (byte index = 0; index < 4; index++)
        {
            if (manager->IslandState.Workshops.PlaceId[index]           == 0 ||
                manager->IslandState.Workshops.UnderConstruction[index] != 0)
                continue;

            var ratio = LuminaGetter.GetRow<MJICraftworksRankRatio>((uint)manager->IslandState.Workshops.BuildingLevel[index] + 1);

            if (ratio == null)
            {
                StopOperation();
                panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                return;
            }

            input.Workshops.Add(new(index, ratio.Value.Ratio));
        }

        var landmarks = 0;

        foreach (var landmark in manager->IslandState.LandmarkIds)
        {
            if (landmark != 0)
                landmarks++;
        }

        input.MaxGroove = LuminaGetter.GetRow<MJICraftworksTension>((uint)landmarks)?.Unknown0 ?? 0;

        foreach (var material in LuminaGetter.Get<MJIItemPouch>())
        {
            if (material.RowId      >= input.Stock.Length ||
                material.Item.RowId == 0)
                continue;

            input.Reserved[material.RowId] = data->MaterialUse.Entries[2].UsedAmounts[(int)material.RowId];
            input.Stock[material.RowId] = (int)CurrencyManager.Instance()->GetItemCount(material.Item.RowId) -
                                          input.Reserved[material.RowId];
        }

        foreach (var craft in data->Crafts)
        {
            if (craft.CraftObjectId == 0                ||
                craft.LevelReq      > data->IslandLevel ||
                craft.CraftingTime is not (4 or 6 or 8))
                continue;

            var recipe = new Recipe
            {
                ID        = craft.CraftObjectId,
                Name      = LuminaGetter.GetRow<Item>(craft.ItemId)?.Name.ToString() ?? string.Empty,
                Duration  = craft.CraftingTime,
                Value     = craft.Value,
                Themes    = [.. craft.ThemeIds],
                Materials = [.. craft.MaterialItemPouchRowIds],
                Costs     = [.. craft.MaterialCosts],
                Popularity =
                [
                    (byte)currentPopularity.Value.Popularity[craft.CraftObjectId].RowId,
                    (byte)nextPopularity.Value.Popularity[craft.CraftObjectId].RowId
                ],
                Supply      = (int)manager->GetSupplyForCraftwork(craft.CraftObjectId),
                DemandShift = (int)manager->GetDemandShiftForCraftwork(craft.CraftObjectId)
            };
            input.Recipes.Add(recipe.ID, recipe);
        }

        if (includeFavors)
        {
            for (var index = (week + 1) * 3; index < (week + 2) * 3; index++)
            {
                var favor = manager->FavorState;
                if (favor->CraftObjectIds[index] == 0 ||
                    favor->Shipped(index))
                    continue;

                var request = LuminaGetter.GetRow<MJINekomimiRequest>((uint)((index % 3) + 1));
                if (request == null)
                    continue;

                var required = favor->Bonus(index) ?
                                   request.Value.Unknown1 :
                                   request.Value.Unknown0;
                input.Favors[((index / 3) - 1, favor->CraftObjectIds[index])] =
                    required - favor->NumDelivered[index] - favor->NumScheduled[index];
            }
        }

        for (var cycle = input.FirstCycle; cycle < input.EndCycle; cycle++)
        {
            if ((data->RestCycles & (1u << cycle)) == 0)
                pendingCycles.Enqueue(cycle);
        }

        if (pendingCycles.Count   == 0 ||
            input.Recipes.Count   == 0 ||
            input.Workshops.Count == 0)
        {
            StopOperation();
            panel.SetStatus("OptimizedMJICraftSchedule-NoWork");
            return;
        }

        operation = Operation.Reading;
        panel.Refresh();
        RequestCycle(pendingCycles.Dequeue());
    }

    private void Calculate()
    {
        operation = Operation.Calculating;
        ScheduleAgent.Instance()->SetDisplayedCycle(originalCycle);
        cancellation = new(TimeSpan.FromSeconds(30));
        var token             = cancellation.Token;
        var currentGeneration = generation;
        var snapshot          = input;
        panel.Refresh();
        _ = Task.Run(() => Solve(snapshot, token), token).ContinueWith
        (
            task =>
            {
                _ = IFramework.Instance().Run
                (
                    () =>
                    {
                        if (!enabled ||
                            currentGeneration != generation)
                            return;

                        StopOperation();

                        if (task.IsCompletedSuccessfully)
                        {
                            result = task.Result;
                            panel.Refresh();
                        }
                        else
                            panel.SetStatus("OptimizedMJICraftSchedule-SolveFailed");
                    },
                    CancellationToken.None
                );
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void Apply()
    {
        if (operation != Operation.Idle ||
            result is not { Complete: true })
            return;

        CancelPreview();
        var agent = ScheduleAgent.Instance();

        if (agent                        == null               ||
            agent->Data                  == null               ||
            agent->Data->CycleInProgress != input.CurrentCycle ||
            agent->Data->RestCycles      != input.RestCycles)
        {
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return;
        }

        foreach (var (material, amount) in result.Materials)
        {
            var pouch = LuminaGetter.GetRow<MJIItemPouch>(material);

            if (pouch                                                            == null ||
                CurrencyManager.Instance()->GetItemCount(pouch.Value.Item.RowId) < amount + input.Reserved[material])
            {
                panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                return;
            }
        }

        originalCycle = agent->Data->CycleDisplayed;
        foreach (var cycle in result.Days.Keys.Order())
            pendingCycles.Enqueue(cycle);

        operation = Operation.Checking;
        panel.Refresh();
        RequestCycle(pendingCycles.Dequeue());
    }

    private void TogglePreview()
    {
        if (operation != Operation.Idle ||
            result    == null)
            return;

        if (isPreviewing)
        {
            CancelPreview();
            panel.Refresh();
            return;
        }

        var agent = ScheduleAgent.Instance();
        if (agent       == null ||
            agent->Data == null)
            return;

        if (agent->Data->CycleInProgress != input.CurrentCycle ||
            agent->Data->RestCycles      != input.RestCycles)
        {
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return;
        }

        isPreviewing = true;
        if (result.Days.ContainsKey(agent->Data->CycleDisplayed))
            agent->UpdateMainAddon();
        else
            agent->SetDisplayedCycle(result.Days.Keys.Min());
        panel.Refresh();
    }

    private void CancelPreview()
    {
        if (!isPreviewing)
            return;

        isPreviewing                = false;
        previewIndicator?.IsVisible = false;
        var agent = ScheduleAgent.Instance();
        if (agent       != null                      &&
            agent->Data != null                      &&
            MJICraftSchedule->IsAddonAndNodesReady() &&
            (agent->Data->Flags2 & NativeScheduleAgent.DataFlags2.ScheduleDataAvailable) != 0)
            agent->UpdateMainAddon();
    }

    private bool UpdateMainAddonDetour(ScheduleAgent* agent)
    {
        previewIndicator?.IsVisible = false;
        var refreshed = UpdateMainAddonHook.Original(agent);
        if (!refreshed          ||
            !isPreviewing       ||
            result      == null ||
            agent->Data == null)
            return refreshed;

        var data = agent->Data;

        if (data->CycleInProgress != input.CurrentCycle ||
            data->RestCycles      != input.RestCycles)
        {
            isPreviewing = false;
            result       = null;
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return refreshed;
        }

        if (!result.Days.TryGetValue(data->CycleDisplayed, out var entries))
            return refreshed;

        previewIndicator?.IsVisible = true;
        Span<NativeScheduleAgent.WorkshopData> schedules = stackalloc NativeScheduleAgent.WorkshopData[4];
        data->WorkshopSchedules.CopyTo(schedules);
        var derivedFlag = data->Flags2 & NativeScheduleAgent.DataFlags2.DerivedDataDirty;

        try
        {
            data->WorkshopSchedules.Clear();

            foreach (var entry in entries)
            {
                ref var workshop = ref data->WorkshopSchedules[entry.Workshop];
                workshop.EntryData[workshop.NumScheduleEntries++] = new()
                {
                    CraftObjectId = entry.ID,
                    StartingSlot  = entry.Hour,
                    Duration      = (byte)input.Recipes[entry.ID].Duration,
                    Efficient     = entry.Efficient
                };
                if (entry.Efficient)
                    workshop.NumEfficientCrafts++;
            }

            return UpdateMainAddonHook.Original(agent);
        }
        finally
        {
            schedules.CopyTo(data->WorkshopSchedules);
            data->Flags2 = (data->Flags2 & ~NativeScheduleAgent.DataFlags2.DerivedDataDirty) | derivedFlag;
        }
    }

    private void StopOperation()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        timeout?.Dispose();
        timeout = null;
        pendingCycles.Clear();
        pendingCrafts.Clear();
        expectedSchedule.Clear();
        operation = Operation.Idle;
    }

    private static void UpdateConfirmButtonDetour(RestAddon* addon)
    {
        uint mask = 0;

        for (var index = 0; index < 14; index++)
        {
            var node = addon->GetNodeById
            (
                (uint)(index < 7 ?
                           index + 6 :
                           index + 9)
            );
            if (node != null &&
                node->GetAsAtkComponentCheckBox()->IsChecked)
                mask |= 1u << index;
        }

        var count = BitOperations.PopCount(mask);
        addon->GetComponentButtonById(24)->SetEnabledState(count is >= 1 and <= 4);
        var text = addon->GetTextNodeById(2);
        if (text != null)
            text->SetText(Lang.Get("OptimizedMJICraftSchedule-RestHint"));
    }

    private uint AskSetRestConfirmDetour(ScheduleAgent* agent, uint restCycles)
    {
        var addonID      = AskSetRestConfirmHook.Original(agent, restCycles);
        var confirmation = (AddonSelectYesno*)SelectYesno;
        if (confirmation == null)
            return addonID;

        var weeks = new string[2];

        for (var week = 0; week < 2; week++)
        {
            var days = Enumerable.Range(0, 7)
                                 .Where(day => (restCycles & (1u << ((week * 7) + day))) != 0)
                                 .Select(day => (day + 1).ToString())
                                 .ToArray();
            weeks[week] = Lang.Get
            (
                "OptimizedMJICraftSchedule-RestCycle",
                new Dictionary<string, object>
                {
                    ["week"] = Lang.Get
                    (
                        week == 0 ?
                            "OptimizedMJICraftSchedule-ThisWeek" :
                            "OptimizedMJICraftSchedule-NextWeek"
                    ),
                    ["text"] = days.Length == 0 ?
                                   Lang.Get("OptimizedMJICraftSchedule-NoRest") :
                                   string.Join("、", days)
                }
            );
        }

        confirmation->PromptText->SetText
        (
            Lang.Get
            (
                "OptimizedMJICraftSchedule-RestConfirm",
                new Dictionary<string, object>
                {
                    ["text"] = string.Join("\n", weeks)
                }
            )
        );
        return addonID;
    }

    private bool HandleEventSetRestConfirmDetour(ScheduleAgent* agent, AtkValue* values)
    {
        if (values->Int != 0)
            return HandleEventSetRestConfirmHook.Original(agent, values);

        var mask = agent->Data->NewRestCycles & 0x3FFF;
        if (BitOperations.PopCount(mask) is < 1 or > 4)
            return true;

        Span<uint> days  = stackalloc uint[4];
        var        count = 0;

        for (var index = 0; index < 14; index++)
        {
            if ((mask & (1u << index)) != 0)
                days[count++] = (uint)index;
        }

        days[count..].Fill(days[0]);

        CancelPreview();
        StopOperation();
        result                          = null;
        agent->Data->RestCycles         = mask;
        agent->Data->ConfirmAddonHandle = 0;
        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.SetMJIWorkshopRest, days[0], days[1], days[2], days[3]);
        if (MJICraftScheduleMaintenance != null)
            MJICraftScheduleMaintenance->Close(true);
        agent->Data->OpenedModalAddonHandle = 0;
        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        panel.Refresh();
        return true;
    }

    private enum Operation
    {
        Idle,
        Preparing,
        Reading,
        Calculating,
        Checking,
        Clearing,
        Verifying
    }

    private void RequestCycle(byte cycle)
    {
        requestedCycle = cycle;
        var token = ++generation;
        timeout?.Dispose();
        timeout = new
        (
            _ =>
            {
                IFramework.Instance().Run
                (() =>
                    {
                        if (!enabled                ||
                            token     != generation ||
                            operation == Operation.Idle)
                            return;

                        var applying = operation is Operation.Clearing or Operation.Verifying;
                        StopOperation();
                        panel?.SetStatus
                        (
                            applying ?
                                "OptimizedMJICraftSchedule-ApplyFailed" :
                                "OptimizedMJICraftSchedule-Timeout"
                        );
                    }
                );
            },
            null,
            TimeSpan.FromSeconds(15),
            Timeout.InfiniteTimeSpan
        );
        ScheduleAgent.Instance()->SetDisplayedCycle(cycle);
    }

    private void AdvanceApplication()
    {
        if (operation == Operation.Clearing)
        {
            if (pendingCrafts.TryDequeue(out var removed))
            {
                expectedSchedule.Remove(removed);
                ExecuteCommandManager.Instance().ExecuteCommand
                (
                    ExecuteCommandFlag.AssignMJIWorkshop,
                    (uint)(8 * (((removed.Hour + 17) % 24) | (32 * removed.ID))),
                    requestedCycle,
                    removed.Workshop,
                    1
                );
                RequestCycle(requestedCycle);
                return;
            }

            operation = Operation.Verifying;
            foreach (var entry in result.Days[requestedCycle].OrderBy(entry => entry.Workshop).ThenBy(entry => entry.Hour))
                pendingCrafts.Enqueue(entry);
        }

        if (pendingCrafts.TryDequeue(out var added))
        {
            expectedSchedule.Add(added);
            MJIManager.Instance()->ScheduleCraft(added.ID, (byte)((added.Hour + 17) % 24), requestedCycle, added.Workshop);
            RequestCycle(requestedCycle);
            return;
        }

        expectedSchedule.Clear();

        if (pendingCycles.TryDequeue(out var following))
        {
            operation = Operation.Checking;
            RequestCycle(following);
            return;
        }

        StopOperation();
        result = null;
        ScheduleAgent.Instance()->SetDisplayedCycle(originalCycle);
        panel.SetStatus("OptimizedMJICraftSchedule-Applied");
    }

    private static bool SchedulesMatch(List<ScheduledCraft> actual, List<ScheduledCraft> expected) =>
        actual.Count == expected.Count &&
        actual.Select(entry => (entry.Workshop, entry.Hour, entry.ID)).Order()
              .SequenceEqual(expected.Select(entry => (entry.Workshop, entry.Hour, entry.ID)).Order());

    private void UpdateScheduleDataDetour
    (
        ScheduleAgent* agent,
        byte           cycleInProgress,
        byte*          durations,
        byte*          startingHours,
        byte*          craftObjectIDs,
        byte*          currentRestDays,
        byte*          nextRestDays,
        byte*          efficiencies,
        uint           groove
    )
    {
        UpdateScheduleDataHook.Original
        (
            agent,
            cycleInProgress,
            durations,
            startingHours,
            craftObjectIDs,
            currentRestDays,
            nextRestDays,
            efficiencies,
            groove
        );
        if (operation is Operation.Idle or Operation.Preparing or Operation.Calculating ||
            agent->Data                 == null                                         ||
            agent->Data->CycleDisplayed != requestedCycle)
            return;

        var data = agent->Data;

        if (data->CycleInProgress != input.CurrentCycle ||
            data->RestCycles      != input.RestCycles)
        {
            StopOperation();
            panel.SetStatus("OptimizedMJICraftSchedule-Stale");
            return;
        }

        List<ScheduledCraft> existing = [];

        foreach (var workshop in input.Workshops)
        {
            var schedule = data->WorkshopSchedules[workshop.Index];

            for (var index = 0; index < schedule.NumScheduleEntries; index++)
            {
                var entry     = schedule.EntryData[index];
                var efficient = entry.Efficient;
                existing.Add(new(workshop.Index, entry.StartingSlot, (ushort)entry.CraftObjectId, efficient));
            }
        }

        if (operation is Operation.Clearing or Operation.Verifying &&
            !SchedulesMatch(existing, expectedSchedule))
            return;

        timeout?.Dispose();
        generation++;

        switch (operation)
        {
            case Operation.Reading:
                input.Existing.Add(requestedCycle, existing);

                foreach (var entry in existing)
                {
                    if (!input.Recipes.TryGetValue(entry.ID, out var recipe))
                        continue;

                    for (var index = 0; index < recipe.Materials.Length; index++)
                    {
                        input.Stock[recipe.Materials[index]]    += recipe.Costs[index];
                        input.Reserved[recipe.Materials[index]] -= recipe.Costs[index];
                    }

                    var key = (requestedCycle / 7, (int)entry.ID);
                    if (input.Favors.ContainsKey(key))
                        input.Favors[key] += entry.Efficient ?
                                                 2 :
                                                 1;
                }

                if (pendingCycles.TryDequeue(out var next))
                    RequestCycle(next);
                else
                    Calculate();
                break;

            case Operation.Checking:
                if (!SchedulesMatch(existing, input.Existing[requestedCycle]))
                {
                    StopOperation();
                    panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                    return;
                }

                data->Flags1 |= NativeScheduleAgent.DataFlags1.MaterialsUpdated;
                expectedSchedule.AddRange(existing);
                foreach (var entry in existing.OrderBy(entry => entry.Workshop).ThenByDescending(entry => entry.Hour))
                    pendingCrafts.Enqueue(entry);
                operation = Operation.Clearing;
                AdvanceApplication();
                break;

            case Operation.Clearing:
            case Operation.Verifying:
                AdvanceApplication();
                break;
        }
    }
}
