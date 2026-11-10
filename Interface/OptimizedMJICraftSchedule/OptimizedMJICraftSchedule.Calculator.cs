using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Threading.TaskHelper.Models;
using CurrencyManager = FFXIVClientStructs.FFXIV.Client.Game.CurrencyManager;
using ScheduleAgent = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;
using NativeScheduleAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJICraftSchedule;

namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

internal partial class OptimizedMJICraftSchedule
{
    private sealed unsafe class Calculator
    (
        OptimizedMJICraftSchedule module
    )
    {
        public CalculationInput?  Input  { get; private set; }
        public CalculationResult? Result { get; private set; }
        public bool               IsBusy => operation != Operation.Idle;

        public bool HasTimeChanged
        {
            get
            {
                var serverTime = Framework.GetServerTime();
                return serverTime >= Input.CycleEndsAt ||
                       (Input.FirstCycle == Input.CurrentCycle && serverTime >= Input.HourEndsAt);
            }
        }

        private readonly Queue<byte>           pendingCycles    = [];
        private readonly List<ScheduledCraft>  expectedSchedule = [];
        private readonly Queue<ScheduledCraft> pendingCrafts    = [];
        private          Operation             operation;
        private          byte                  requestedCycle;
        private          byte                  originalCycle;
        private          bool                  awaitingCycle;
        private          int                   generation;

        public void Reset()
        {
            StopOperation();
            Input  = null;
            Result = null;
        }

        public void Start(int week, bool includeFavors)
        {
            if (operation != Operation.Idle)
                return;

            module.CancelPreview();
            var agent   = ScheduleAgent.Instance();
            var manager = MJIManager.Instance();

            if (agent       == null ||
                agent->Data == null ||
                manager     == null)
            {
                module.panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                return;
            }

            originalCycle = agent->Data->CycleDisplayed;
            operation     = Operation.Preparing;
            Result        = null;
            module.panel.Refresh();
            module.TaskHelper.Enqueue
            (
                () =>
                {
                    var currentAgent   = ScheduleAgent.Instance();
                    var currentManager = MJIManager.Instance();

                    if (currentAgent       == null ||
                        currentAgent->Data == null ||
                        currentManager     == null)
                    {
                        StopOperation();
                        return true;
                    }

                    if ((currentAgent->Data->Flags2 &
                         (NativeScheduleAgent.DataFlags2.ScheduleDataAvailable | NativeScheduleAgent.DataFlags2.DisableInteraction)) !=
                        NativeScheduleAgent.DataFlags2.ScheduleDataAvailable ||
                        currentManager->DemandDirty                          ||
                        (includeFavors && (currentManager->FavorState == null || currentManager->FavorState->UpdateState != 2)))
                        return false;

                    ReadCalculationInput(week, includeFavors);
                    return true;
                },
                timeoutAction: () =>
                {
                    StopOperation();
                    module.panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                }
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
            var cycleTime         = LuminaGetter.GetRow<CycleTime>(2);

            if (currentPopularity == null ||
                nextPopularity    == null ||
                cycleTime         == null)
            {
                StopOperation();
                module.panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                return;
            }

            var serverTime  = Framework.GetServerTime();
            var elapsed     = (serverTime - cycleTime.Value.FirstCycle) % cycleTime.Value.Cycle;
            var currentHour = (byte)(elapsed / 3600 % 24);

            Input = new()
            {
                CycleEndsAt  = serverTime - (elapsed % 86400) + 86400,
                HourEndsAt   = serverTime - (elapsed % 3600)  + 3600,
                CurrentCycle = data->CycleInProgress,
                CurrentHour  = currentHour,
                FirstCycle   = (byte)Math.Max(data->CycleInProgress, week * 7),
                EndCycle     = (byte)((week + 1) * 7),
                RestCycles   = data->RestCycles,
                Groove       = (int)data->Groove
            };
            Result = null;
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
                    module.panel.SetStatus("OptimizedMJICraftSchedule-NotReady");
                    return;
                }

                Input.Workshops.Add(new(index, ratio.Value.Ratio));
            }

            var landmarks = 0;

            foreach (var landmark in manager->IslandState.LandmarkIds)
            {
                if (landmark != 0)
                    landmarks++;
            }

            Input.MaxGroove = LuminaGetter.GetRow<MJICraftworksTension>((uint)landmarks)?.Unknown0 ?? 0;

            foreach (var material in LuminaGetter.Get<MJIItemPouch>())
            {
                if (material.RowId      >= Input.Stock.Length ||
                    material.Item.RowId == 0)
                    continue;

                Input.Stock[material.RowId] = (int)CurrencyManager.Instance()->GetItemCount(material.Item.RowId);
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
                Input.Recipes.Add(recipe.ID, recipe);
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
                    Input.Favors[((index / 3) - 1, favor->CraftObjectIds[index])] =
                        required - favor->NumDelivered[index] - favor->NumScheduled[index];
                }
            }

            var workingDays = 0;

            for (var cycle = Input.FirstCycle; cycle < Input.EndCycle; cycle++)
            {
                if ((data->RestCycles & (1u << cycle)) != 0)
                    continue;

                pendingCycles.Enqueue(cycle);
                workingDays++;
            }

            if (workingDays           == 0 ||
                Input.Recipes.Count   == 0 ||
                Input.Workshops.Count == 0)
            {
                StopOperation();
                module.panel.SetStatus("OptimizedMJICraftSchedule-NoWork");
                return;
            }

            for (var cycle = Input.CurrentCycle; cycle < 14; cycle++)
            {
                if ((cycle >= Input.FirstCycle && cycle < Input.EndCycle) ||
                    (data->RestCycles & (1u << cycle)) != 0)
                    continue;

                pendingCycles.Enqueue(cycle);
            }

            operation = Operation.Reading;
            module.panel.Refresh();
            RequestCycle(pendingCycles.Dequeue());
        }

        private void Calculate()
        {
            operation = Operation.Calculating;
            ScheduleAgent.Instance()->SetDisplayedCycle(originalCycle);
            var               snapshot          = Input;
            var               currentGeneration = generation;
            CalculationResult answer            = null;
            module.panel.Refresh();
            Task<CalculationResult> computation = null;
            var calculation = new TaskHelperTask
            (
                token =>
                {
                    computation ??= Task.Run(() => Solve(snapshot, token), token);
                    if (!computation.IsCompleted)
                        return Task.FromResult(false);

                    if (computation.IsFaulted)
                        return Task.FromException<bool>(computation.Exception);

                    if (computation.IsCanceled)
                        return Task.FromCanceled<bool>(token);

                    answer = computation.Result;
                    return Task.FromResult(true);
                },
                timeoutMS: 30_000,
                timeoutAction: () =>
                {
                    StopOperation();
                    module.panel.SetStatus("OptimizedMJICraftSchedule-SolveFailed");
                },
                exceptionAction: () =>
                {
                    StopOperation();
                    module.panel.SetStatus("OptimizedMJICraftSchedule-SolveFailed");
                }
            );
            module.TaskHelper.Enqueue(calculation);
            module.TaskHelper.Enqueue
            (() =>
                {
                    if (currentGeneration != generation)
                        return;

                    StopOperation();
                    Result = answer;
                    module.panel.Refresh();
                }
            );
        }

        public void Apply()
        {
            if (operation != Operation.Idle ||
                Result is not { Complete: true })
                return;

            module.CancelPreview();
            var agent = ScheduleAgent.Instance();

            if (agent                        == null               ||
                agent->Data                  == null               ||
                agent->Data->CycleInProgress != Input.CurrentCycle ||
                agent->Data->RestCycles      != Input.RestCycles   ||
                HasTimeChanged)
            {
                module.panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                return;
            }

            foreach (var (material, amount) in Result.Materials)
            {
                var pouch = LuminaGetter.GetRow<MJIItemPouch>(material);

                if (pouch                                                            == null ||
                    CurrencyManager.Instance()->GetItemCount(pouch.Value.Item.RowId) < amount + Input.Reserved[material])
                {
                    module.panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                    return;
                }
            }

            originalCycle = agent->Data->CycleDisplayed;
            foreach (var cycle in Result.Days.Keys.Order())
                pendingCycles.Enqueue(cycle);

            operation = Operation.Checking;
            module.panel.Refresh();
            RequestCycle(pendingCycles.Dequeue());
        }

        private void StopOperation()
        {
            generation++;
            module.TaskHelper?.Abort();
            pendingCycles.Clear();
            pendingCrafts.Clear();
            expectedSchedule.Clear();
            awaitingCycle = false;
            operation     = Operation.Idle;
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
            awaitingCycle = false;
            var token = ++generation;
            module.TaskHelper.Enqueue
            (() =>
                {
                    if (token != generation)
                        return;

                    var agent = ScheduleAgent.Instance();
                    requestedCycle      =  cycle;
                    awaitingCycle       =  true;
                    agent->Data->Flags2 |= NativeScheduleAgent.DataFlags2.DerivedDataDirty;
                    agent->SetDisplayedCycle(cycle);
                }
            );
            module.TaskHelper.Enqueue
            (
                () => token != generation,
                timeoutAction: () =>
                {
                    if (token != generation)
                        return;

                    var applying = operation is Operation.Clearing or Operation.Verifying;
                    StopOperation();
                    module.panel?.SetStatus
                    (
                        applying ?
                            "OptimizedMJICraftSchedule-ApplyFailed" :
                            "OptimizedMJICraftSchedule-Timeout"
                    );
                }
            );
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
                foreach (var entry in Result.Days[requestedCycle].OrderBy(entry => entry.Workshop).ThenBy(entry => entry.Hour))
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
            Result = null;
            ScheduleAgent.Instance()->SetDisplayedCycle(originalCycle);
            module.panel.SetStatus("OptimizedMJICraftSchedule-Applied");
        }

        private static bool SchedulesMatch(List<ScheduledCraft> actual, List<ScheduledCraft> expected) =>
            actual.Count == expected.Count &&
            actual.Select(entry => (entry.Workshop, entry.Hour, entry.ID)).Order()
                  .SequenceEqual(expected.Select(entry => (entry.Workshop, entry.Hour, entry.ID)).Order());

        public void UpdateScheduleData(ScheduleAgent* agent)
        {
            if (!awaitingCycle                                                              ||
                operation is Operation.Idle or Operation.Preparing or Operation.Calculating ||
                agent->Data                 == null                                         ||
                agent->Data->CycleDisplayed != requestedCycle)
                return;

            var data = agent->Data;

            if (data->CycleInProgress != Input.CurrentCycle ||
                data->RestCycles      != Input.RestCycles   ||
                HasTimeChanged)
            {
                StopOperation();
                module.panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                return;
            }

            List<ScheduledCraft> existing = [];

            foreach (var workshop in Input.Workshops)
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

            awaitingCycle = false;
            generation++;

            switch (operation)
            {
                case Operation.Reading:
                    var selected = requestedCycle >= Input.FirstCycle && requestedCycle < Input.EndCycle;

                    if (selected)
                    {
                        Input.Existing.Add(requestedCycle, existing);
                        Input.Fixed.Add(requestedCycle, []);
                    }

                    foreach (var entry in existing)
                    {
                        var started = requestedCycle == Input.CurrentCycle && entry.Hour <= Input.CurrentHour;

                        if (started)
                        {
                            if (selected)
                                Input.Fixed[requestedCycle].Add(entry);

                            continue;
                        }

                        if (!Input.Recipes.TryGetValue(entry.ID, out var recipe))
                            continue;

                        if (!selected)
                        {
                            for (var index = 0; index < recipe.Materials.Length; index++)
                            {
                                Input.Stock[recipe.Materials[index]]    -= recipe.Costs[index];
                                Input.Reserved[recipe.Materials[index]] += recipe.Costs[index];
                            }

                            continue;
                        }

                        var key = (requestedCycle / 7, (int)entry.ID);
                        if (Input.Favors.ContainsKey(key))
                            Input.Favors[key] += entry.Efficient ?
                                                     2 :
                                                     1;
                    }

                    if (pendingCycles.TryDequeue(out var next))
                        RequestCycle(next);
                    else
                        Calculate();
                    break;

                case Operation.Checking:
                    if (!SchedulesMatch(existing, Input.Existing[requestedCycle]))
                    {
                        StopOperation();
                        module.panel.SetStatus("OptimizedMJICraftSchedule-Stale");
                        return;
                    }

                    data->Flags1 |= NativeScheduleAgent.DataFlags1.MaterialsUpdated;
                    expectedSchedule.AddRange(existing);
                    foreach (var entry in existing.Except(Input.Fixed[requestedCycle]).OrderBy(entry => entry.Workshop).ThenByDescending(entry => entry.Hour))
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

        private static CalculationResult Solve(CalculationInput source, CancellationToken token)
        {
            var answer    = new CalculationResult();
            var stock     = (int[])source.Stock.Clone();
            var produced  = new int[91];
            var remaining = new int[182];
            foreach (var (key, amount) in source.Favors)
                remaining[(key.Week * 91) + key.ID] = Math.Max(0, amount);

            List<SearchState> beam =
            [
                new()
                {
                    Stock     = stock,
                    Produced  = produced,
                    Remaining = remaining,
                    Groove    = source.Groove
                }
            ];
            beam[0].Priority = new(GetFavorPotential(source, beam[0], null, -1, 0, 0), 0, 0);
            var previousWeek = 0;

            foreach (var cycle in source.Existing.Keys.Order())
            {
                token.ThrowIfCancellationRequested();
                var week = cycle / 7;

                if (week != previousWeek)
                {
                    foreach (var state in beam)
                    {
                        state.Groove = 0;
                        Array.Clear(state.Produced);
                        state.Priority = new(state.FavorCoverage + GetFavorPotential(source, state, null, -1, 0, week), state.FavorCoverage, state.Value);
                    }

                    previousWeek = week;
                }

                foreach (var state in beam)
                {
                    state.Produced = (int[])state.Produced.Clone();

                    foreach (var entry in source.Fixed[cycle])
                    {
                        var recipe = source.Recipes[entry.ID];
                        if (entry.Hour + recipe.Duration <= source.CurrentHour)
                            continue;

                        state.Produced[entry.ID] += entry.Efficient ?
                                                        2 :
                                                        1;
                        if (entry.Efficient)
                            state.Groove = Math.Min(source.MaxGroove, state.Groove + 1);
                    }
                }

                foreach (var workshop in source.Workshops)
                {
                    var fixedEntries = source.Fixed[cycle].Where(entry => entry.Workshop == workshop.Index).OrderBy(entry => entry.Hour).ToArray();
                    var firstHour = cycle == source.CurrentCycle ?
                                        source.CurrentHour + 1 :
                                        0;
                    ushort lastID = 0;

                    foreach (var entry in fixedEntries)
                    {
                        var endHour = entry.Hour + source.Recipes[entry.ID].Duration;
                        if (endHour < firstHour)
                            continue;

                        firstHour = endHour;
                        lastID    = entry.ID;
                    }

                    foreach (var state in beam)
                    {
                        state.Hour   = firstHour;
                        state.LastID = lastID;
                    }

                    for (var slot = fixedEntries.Length; slot < 6; slot++)
                    {
                        token.ThrowIfCancellationRequested();
                        var candidates = new PriorityQueue<SearchState, SearchPriority>();

                        foreach (var state in beam)
                        {
                            candidates.Enqueue
                            (
                                new()
                                {
                                    Stock         = state.Stock,
                                    Produced      = state.Produced,
                                    Remaining     = state.Remaining,
                                    Groove        = state.Groove,
                                    Hour          = 24,
                                    LastID        = state.LastID,
                                    FavorCoverage = state.FavorCoverage,
                                    Value         = state.Value,
                                    Tail          = state.Tail,
                                    Priority      = state.Priority
                                },
                                state.Priority
                            );
                            if (candidates.Count > SEARCH_WIDTH)
                                candidates.Dequeue();

                            foreach (var recipe in source.Recipes.Values)
                            {
                                if (recipe.Popularity[week]      == 0 ||
                                    state.Hour + recipe.Duration > 24 ||
                                    !CanAfford(recipe, state.Stock))
                                    continue;

                                var efficient = state.LastID != recipe.ID                                  &&
                                                source.Recipes.TryGetValue(state.LastID, out var previous) &&
                                                recipe.Themes.Any(theme => theme != 0 && previous.Themes.Contains(theme));
                                var quantity = efficient ?
                                                   2 :
                                                   1;
                                var favorIndex = (week * 91) + recipe.ID;
                                var coverage   = Math.Min(quantity, state.Remaining[favorIndex]);
                                var nextGroove = Math.Min
                                (
                                    source.MaxGroove,
                                    state.Groove +
                                    (efficient ?
                                         1 :
                                         0)
                                );
                                var demand    = EstimateDemand(recipe, cycle, source.CurrentCycle, state.Produced[recipe.ID]);
                                var baseValue = Math.Floor(recipe.Value * workshop.Ratio / 100d * (1 + (nextGroove / 100d)));
                                var value     = Math.Floor(baseValue * PopularityFactors[recipe.Popularity[cycle / 7]] / 100d * demand / 100d) * quantity;
                                var covered   = state.FavorCoverage + coverage;
                                var potential = GetFavorPotential(source, state, recipe, favorIndex, coverage, week);
                                var priority  = new SearchPriority(covered + potential, covered, state.Value + value);
                                if (candidates.Count                               >= SEARCH_WIDTH &&
                                    priority.CompareTo(candidates.Peek().Priority) <= 0)
                                    continue;

                                var candidate = new SearchState
                                {
                                    Stock         = (int[])state.Stock.Clone(),
                                    Produced      = (int[])state.Produced.Clone(),
                                    Remaining     = (int[])state.Remaining.Clone(),
                                    Groove        = nextGroove,
                                    Hour          = state.Hour + recipe.Duration,
                                    LastID        = recipe.ID,
                                    FavorCoverage = priority.FavorCoverage,
                                    Value         = priority.Value,
                                    Tail          = new(cycle, new(workshop.Index, (byte)state.Hour, recipe.ID, efficient), state.Tail),
                                    Priority      = priority
                                };
                                for (var material = 0; material < recipe.Materials.Length; material++)
                                    candidate.Stock[recipe.Materials[material]] -= recipe.Costs[material];

                                candidate.Produced[recipe.ID]   += quantity;
                                candidate.Remaining[favorIndex] -= coverage;
                                candidates.Enqueue(candidate, priority);
                                if (candidates.Count > SEARCH_WIDTH)
                                    candidates.Dequeue();
                            }

                        }

                        beam = [.. candidates.UnorderedItems.Select(item => item.Element)];
                    }
                }

                answer.Days.Add(cycle, []);
            }

            var best = beam.MaxBy(state => state.Priority);
            for (var node = best.Tail; node != null; node = node.Previous)
                answer.Days[node.Cycle].Add(node.Entry);

            foreach (var entries in answer.Days.Values)
                entries.Reverse();

            foreach (var entries in answer.Days.Values)
            {
                foreach (var entry in entries)
                {
                    var recipe = source.Recipes[entry.ID];

                    for (var index = 0; index < recipe.Materials.Length; index++)
                    {
                        if (recipe.Costs[index] == 0)
                            continue;

                        var material = recipe.Materials[index];
                        answer.Materials[material] = answer.Materials.GetValueOrDefault(material) + recipe.Costs[index];
                    }
                }
            }

            answer.Complete = answer.Days.Values.Any(entries => entries.Count != 0);
            answer.Value    = Evaluate(source, answer);
            return answer;
        }

        private static bool CanAfford(Recipe recipe, int[] stock)
        {
            for (var index = 0; index < recipe.Materials.Length; index++)
            {
                if (recipe.Costs[index]            > 0 &&
                    stock[recipe.Materials[index]] < recipe.Costs[index])
                    return false;
            }

            return true;
        }

        private static int GetFavorPotential(CalculationInput source, SearchState state, Recipe selected, int favorIndex, int coverage, int week)
        {
            var potential = 0;

            foreach (var (key, _) in source.Favors)
            {
                if (key.Week < week)
                    continue;

                var index = (key.Week * 91) + key.ID;
                var needed = state.Remaining[index] -
                             (index == favorIndex ?
                                  coverage :
                                  0);
                if (needed <= 0 ||
                    !source.Recipes.TryGetValue((ushort)key.ID, out var recipe))
                    continue;

                var available = needed;

                for (var material = 0; material < recipe.Materials.Length; material++)
                {
                    var cost = recipe.Costs[material];
                    if (cost == 0)
                        continue;

                    var pouch = recipe.Materials[material];
                    var selectedMaterial = selected == null ?
                                               -1 :
                                               Array.IndexOf(selected.Materials, pouch);
                    var used = selectedMaterial >= 0 ?
                                   selected.Costs[selectedMaterial] :
                                   0;
                    var quantity = key.Week > week ?
                                       1 :
                                       2;
                    var crafts = Math.Max(0, state.Stock[pouch] - used) / cost;
                    available = Math.Min(available, crafts * quantity);
                }

                potential += available;
            }

            return potential;
        }

        private static int EstimateDemand(Recipe recipe, int cycle, int currentCycle, int produced)
        {
            var demand = cycle >= 7 ?
                             5 :
                             SupplyDemandValues[recipe.Supply];
            if (cycle < 7)
                demand += Math.Min(2, cycle - currentCycle) * DemandShiftValues[recipe.DemandShift];

            demand -= produced;
            return demand switch
            {
                >= 18 => 160,
                >= 10 => 130,
                >= 2  => 100,
                >= -6 => 80,
                _     => 60
            };
        }

        private static long Evaluate(CalculationInput source, CalculationResult answer)
        {
            long value        = 0;
            var  produced     = new int[91];
            var  groove       = source.Groove;
            var  previousWeek = 0;

            foreach (var (cycle, entries) in answer.Days)
            {
                if (cycle / 7 != previousWeek)
                {
                    groove = 0;
                    Array.Clear(produced);
                    previousWeek = cycle / 7;
                }

                var fixedEntries = source.Fixed[cycle].Where(entry => entry.Hour + source.Recipes[entry.ID].Duration > source.CurrentHour).ToArray();

                foreach (var group in entries.Concat(fixedEntries).GroupBy(entry => entry.Hour + source.Recipes[entry.ID].Duration).OrderBy(group => group.Key))
                {
                    var nextGroove = Math.Min(source.MaxGroove, groove + group.Count(entry => entry.Efficient));

                    foreach (var entry in group)
                    {
                        if (fixedEntries.Contains(entry))
                            continue;

                        var recipe    = source.Recipes[entry.ID];
                        var workshop  = source.Workshops.First(item => item.Index == entry.Workshop);
                        var demand    = EstimateDemand(recipe, cycle, source.CurrentCycle, produced[entry.ID]);
                        var baseValue = Math.Floor(recipe.Value * workshop.Ratio / 100d * (1 + (nextGroove / 100d)));
                        value += (long)Math.Floor(baseValue * PopularityFactors[recipe.Popularity[cycle / 7]] / 100d * demand / 100d) *
                                 (entry.Efficient ?
                                      2 :
                                      1);
                    }

                    foreach (var entry in group)
                        produced[entry.ID] += entry.Efficient ?
                                                  2 :
                                                  1;

                    groove = nextGroove;
                }
            }

            return value;
        }

        public sealed class CalculationInput
        {
            public long                                   CycleEndsAt;
            public long                                   HourEndsAt;
            public byte                                   CurrentCycle;
            public byte                                   CurrentHour;
            public byte                                   FirstCycle;
            public byte                                   EndCycle;
            public uint                                   RestCycles;
            public int                                    Groove;
            public int                                    MaxGroove;
            public int[]                                  Stock     { get; } = new int[109];
            public int[]                                  Reserved  { get; } = new int[109];
            public List<Workshop>                         Workshops { get; } = [];
            public Dictionary<ushort, Recipe>             Recipes   { get; } = [];
            public Dictionary<(int Week, int ID), int>    Favors    { get; } = [];
            public Dictionary<byte, List<ScheduledCraft>> Existing  { get; } = [];
            public Dictionary<byte, List<ScheduledCraft>> Fixed     { get; } = [];
        }

        public sealed class CalculationResult
        {
            public Dictionary<byte, List<ScheduledCraft>> Days      { get; } = [];
            public Dictionary<ushort, int>                Materials { get; } = [];
            public long                                   Value;
            public bool                                   Complete;
        }

        public sealed class Recipe
        {
            public ushort   ID;
            public int      Duration;
            public int      Value;
            public ushort[] Themes;
            public ushort[] Materials;
            public ushort[] Costs;
            public byte[]   Popularity;
            public int      Supply;
            public int      DemandShift;
        }

        private sealed class SearchState
        {
            public int[]          Stock;
            public int[]          Produced;
            public int[]          Remaining;
            public int            Groove;
            public int            Hour;
            public ushort         LastID;
            public int            FavorCoverage;
            public double         Value;
            public SearchNode     Tail;
            public SearchPriority Priority;
        }

        private sealed record SearchNode
        (
            byte           Cycle,
            ScheduledCraft Entry,
            SearchNode     Previous
        );

        public readonly record struct Workshop
        (
            byte Index,
            int  Ratio
        );

        public readonly record struct ScheduledCraft
        (
            byte   Workshop,
            byte   Hour,
            ushort ID,
            bool   Efficient
        );

        private readonly record struct SearchPriority
        (
            int    PotentialCoverage,
            int    FavorCoverage,
            double Value
        ) : IComparable<SearchPriority>
        {
            public int CompareTo(SearchPriority other)
            {
                var potential = PotentialCoverage.CompareTo(other.PotentialCoverage);
                if (potential != 0)
                    return potential;

                var favor = FavorCoverage.CompareTo(other.FavorCoverage);
                return favor != 0 ?
                           favor :
                           Value.CompareTo(other.Value);
            }
        }

        #region 常量

        private const int SEARCH_WIDTH = 64;

        private static readonly int[] PopularityFactors  = [0, 140, 120, 100, 80];
        private static readonly int[] SupplyDemandValues = [21, 13, 5, -3, -11];
        private static readonly int[] DemandShiftValues  = [6, 3, 0, -3, -6];

        #endregion
    }
}
