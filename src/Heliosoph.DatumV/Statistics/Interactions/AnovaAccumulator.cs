namespace Heliosoph.DatumV.Statistics.Interactions;

using Heliosoph.DatumV.Model;

/// <summary>
/// Computes the one-way ANOVA F-statistic for testing whether the means of a numeric
/// column differ significantly across groups defined by a categorical column.
/// Uses Welford's online algorithm per group. Groups are capped at
/// <see cref="MaxGroups"/>.
/// </summary>
public sealed class AnovaAccumulator
{
    /// <summary>Maximum number of groups to track.</summary>
    public const int MaxGroups = 1_000;

    private readonly bool _firstIsCategorical;
    private readonly Dictionary<string, GroupState> _groups = new();
    private long _totalCount;
    private double _grandMean;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnovaAccumulator"/> class.
    /// </summary>
    /// <param name="firstIsCategorical">
    /// True if valueA is the categorical column and valueB is numeric.
    /// False if valueA is numeric and valueB is categorical.
    /// </param>
    public AnovaAccumulator(bool firstIsCategorical)
    {
        _firstIsCategorical = firstIsCategorical;
    }

    /// <summary>
    /// Adds a pair of values. One must be categorical, the other numeric.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        if (valueA.IsNull || valueB.IsNull)
        {
            return;
        }

        DataValue catValue = _firstIsCategorical ? valueA : valueB;
        DataValue numValue = _firstIsCategorical ? valueB : valueA;

        string? group = CramerVAccumulator.ToCategorical(catValue);
        double number = numValue.Kind switch
        {
            DataKind.Float32 => numValue.AsFloat32(),
            DataKind.UInt8 => numValue.AsUInt8(),
            _ => double.NaN
        };

        if (group is null || double.IsNaN(number))
        {
            return;
        }

        _totalCount++;

        double delta = number - _grandMean;
        _grandMean += delta / _totalCount;

        if (_groups.TryGetValue(group, out GroupState? state))
        {
            state.Count++;
            double groupDelta = number - state.Mean;
            state.Mean += groupDelta / state.Count;
            double groupDelta2 = number - state.Mean;
            state.M2 += groupDelta * groupDelta2;
        }
        else if (_groups.Count < MaxGroups)
        {
            _groups[group] = new GroupState { Count = 1, Mean = number, M2 = 0 };
        }
        else
        {
            // Pool into <other> group
            string otherKey = "<other>";

            if (_groups.TryGetValue(otherKey, out GroupState? otherState))
            {
                otherState.Count++;
                double otherDelta = number - otherState.Mean;
                otherState.Mean += otherDelta / otherState.Count;
                double otherDelta2 = number - otherState.Mean;
                otherState.M2 += otherDelta * otherDelta2;
            }
            else
            {
                _groups[otherKey] = new GroupState { Count = 1, Mean = number, M2 = 0 };
            }
        }
    }

    /// <summary>
    /// Returns the ANOVA F-statistic, or NaN if insufficient data (fewer than 2 groups
    /// or not enough observations).
    /// </summary>
    public double GetValue()
    {
        int k = _groups.Count;

        if (k < 2 || _totalCount <= k)
        {
            return double.NaN;
        }

        double ssBetween = 0;
        double ssWithin = 0;

        foreach (GroupState state in _groups.Values)
        {
            ssBetween += state.Count * (state.Mean - _grandMean) * (state.Mean - _grandMean);
            ssWithin += state.M2;
        }

        double msBetween = ssBetween / (k - 1);
        double msWithin = ssWithin / (_totalCount - k);

        if (msWithin < double.Epsilon)
        {
            return double.NaN;
        }

        return msBetween / msWithin;
    }

    private sealed class GroupState
    {
        public long Count;
        public double Mean;
        public double M2;
    }
}
