namespace DatumQuery.Statistics.Interactions;

using DatumQuery.Model;

/// <summary>
/// Computes Cramér's V statistic for association between two categorical columns.
/// Maintains a bounded contingency table with a maximum of <see cref="MaxCategories"/>
/// distinct values per column. Values beyond the cap are mapped to a synthetic
/// &lt;other&gt; category.
/// </summary>
public sealed class CramerVAccumulator
{
    /// <summary>Maximum distinct categories tracked per column.</summary>
    public const int MaxCategories = 1_000;

    private readonly Dictionary<string, long> _frequencyA = new();
    private readonly Dictionary<string, long> _frequencyB = new();
    private readonly Dictionary<(string, string), long> _joint = new();
    private long _totalCount;

    /// <summary>
    /// Adds a pair of categorical values. Both must be String, JsonValue, Date, or DateTime.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        if (valueA.IsNull || valueB.IsNull)
        {
            return;
        }

        string? a = ToCategorical(valueA);
        string? b = ToCategorical(valueB);

        if (a is null || b is null)
        {
            return;
        }

        // Cap categories
        if (!_frequencyA.ContainsKey(a) && _frequencyA.Count >= MaxCategories)
        {
            a = "<other>";
        }

        if (!_frequencyB.ContainsKey(b) && _frequencyB.Count >= MaxCategories)
        {
            b = "<other>";
        }

        _totalCount++;

        _frequencyA[a] = _frequencyA.GetValueOrDefault(a) + 1;
        _frequencyB[b] = _frequencyB.GetValueOrDefault(b) + 1;

        (string, string) key = (a, b);
        _joint[key] = _joint.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// Returns Cramér's V ∈ [0, 1], or NaN if insufficient data.
    /// V = sqrt(χ² / (n × (min(r, c) − 1)))
    /// </summary>
    public double GetValue()
    {
        if (_totalCount == 0)
        {
            return double.NaN;
        }

        int r = _frequencyA.Count;
        int c = _frequencyB.Count;

        if (r <= 1 || c <= 1)
        {
            return 0.0;
        }

        double chiSquared = 0;

        foreach (string a in _frequencyA.Keys)
        {
            foreach (string b in _frequencyB.Keys)
            {
                long observed = _joint.GetValueOrDefault((a, b));
                double expected = (double)_frequencyA[a] * _frequencyB[b] / _totalCount;

                if (expected > 0)
                {
                    double diff = observed - expected;
                    chiSquared += diff * diff / expected;
                }
            }
        }

        int minDimension = Math.Min(r, c) - 1;

        if (minDimension <= 0)
        {
            return 0.0;
        }

        return Math.Sqrt(chiSquared / (_totalCount * minDimension));
    }

    internal static string? ToCategorical(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.String => value.AsString(),
            DataKind.JsonValue => value.AsJsonValue(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            _ => null
        };
    }
}
