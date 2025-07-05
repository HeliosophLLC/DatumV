namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

/// <summary>
/// Computes categorical diagnostics: top-K coverage ratio and rare category ratio.
/// Coverage indicates whether a small number of categories dominate the distribution.
/// Rare ratio measures the fraction of categories with very few observations,
/// useful for deciding between one-hot encoding and embeddings, and for detecting dirty data.
/// </summary>
public sealed class CategoricalDiagnosticsAccumulator : IStatisticAccumulator
{
    /// <summary>
    /// Categories observed fewer than this many times are classified as rare.
    /// This threshold is a fixed heuristic — low enough to avoid flagging moderately
    /// infrequent values, high enough to catch singletons and near-singletons that
    /// often indicate data entry errors or extreme long-tail categories.
    /// </summary>
    public const int RareThreshold = 5;

    private readonly int _k;
    private readonly Dictionary<string, long> _frequencies = new();
    private long _totalCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="CategoricalDiagnosticsAccumulator"/> class.
    /// </summary>
    /// <param name="k">Number of top categories used to compute coverage. Should match the top-K parameter.</param>
    public CategoricalDiagnosticsAccumulator(int k = 10)
    {
        _k = k;
    }

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        string key = ValueToString(value);

        _totalCount++;

        if (_frequencies.TryGetValue(key, out long currentCount))
        {
            _frequencies[key] = currentCount + 1;
        }
        else
        {
            _frequencies[key] = 1;
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not CategoricalDiagnosticsAccumulator otherDiagnostics)
        {
            return;
        }

        _totalCount += otherDiagnostics._totalCount;

        foreach (KeyValuePair<string, long> entry in otherDiagnostics._frequencies)
        {
            if (_frequencies.TryGetValue(entry.Key, out long currentCount))
            {
                _frequencies[entry.Key] = currentCount + entry.Value;
            }
            else
            {
                _frequencies[entry.Key] = entry.Value;
            }
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_totalCount == 0 || _frequencies.Count == 0)
        {
            return new StatisticResult("categorical_diagnostics", new CategoricalDiagnosticsResult(0.0, 0.0, 0, 0));
        }

        // Coverage: sum of top-K category counts / total non-null count
        List<long> counts = [.. _frequencies.Values];
        counts.Sort((a, b) => b.CompareTo(a));

        long topKSum = 0;
        int topKCount = Math.Min(_k, counts.Count);
        for (int i = 0; i < topKCount; i++)
        {
            topKSum += counts[i];
        }

        double coverageTopK = (double)topKSum / _totalCount;

        // Rare ratio: categories with count < RareThreshold / total distinct categories
        long rareCategoryCount = 0;
        foreach (long count in _frequencies.Values)
        {
            if (count < RareThreshold)
            {
                rareCategoryCount++;
            }
        }

        long totalCategoryCount = _frequencies.Count;
        double rareRatio = (double)rareCategoryCount / totalCategoryCount;

        return new StatisticResult(
            "categorical_diagnostics",
            new CategoricalDiagnosticsResult(coverageTopK, rareRatio, rareCategoryCount, totalCategoryCount));
    }

    private static string ValueToString(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar().ToString("G"),
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.String => value.AsString(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.JsonValue => value.AsJsonValue(),
            _ => value.ToString() ?? ""
        };
    }
}

/// <summary>
/// Contains categorical diagnostic results: top-K coverage and rare category ratio.
/// </summary>
/// <param name="CoverageTopK">Fraction of total observations covered by the K most frequent categories. Values near 1.0 indicate low cardinality.</param>
/// <param name="RareRatio">Fraction of distinct categories with fewer than 5 observations. High values suggest dirty data or extreme long-tail distributions.</param>
/// <param name="RareCategoryCount">Number of distinct categories with fewer than 5 observations.</param>
/// <param name="TotalCategoryCount">Total number of distinct categories observed.</param>
public sealed record CategoricalDiagnosticsResult(
    double CoverageTopK,
    double RareRatio,
    long RareCategoryCount,
    long TotalCategoryCount);
