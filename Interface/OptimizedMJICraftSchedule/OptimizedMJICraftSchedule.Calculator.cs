namespace DailyRoutines.ModulesPublic.OptimizedMJICraftSchedule;

public partial class OptimizedMJICraftSchedule
{
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

            foreach (var workshop in source.Workshops)
            {
                foreach (var state in beam)
                {
                    state.Hour   = 0;
                    state.LastID = 0;
                }

                for (var slot = 0; slot < 6; slot++)
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

        answer.MissingFavors = best.Remaining.Sum();
        answer.Complete      = answer.Days.Values.Any(entries => entries.Count != 0);
        answer.Value         = Evaluate(source, answer);
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

            foreach (var group in entries.GroupBy(entry => entry.Hour + source.Recipes[entry.ID].Duration).OrderBy(group => group.Key))
            {
                var nextGroove = Math.Min(source.MaxGroove, groove + group.Count(entry => entry.Efficient));

                foreach (var entry in group)
                {
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

    private sealed class CalculationInput
    {
        public byte                                   CurrentCycle;
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
    }

    private sealed class CalculationResult
    {
        public Dictionary<byte, List<ScheduledCraft>> Days      { get; } = [];
        public Dictionary<ushort, int>                Materials { get; } = [];
        public long                                   Value;
        public int                                    MissingFavors;
        public bool                                   Complete;
    }

    private sealed class Recipe
    {
        public ushort   ID;
        public string   Name;
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

    private readonly record struct Workshop
    (
        byte Index,
        int  Ratio
    );

    private readonly record struct ScheduledCraft
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
