namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Computes categorical diagnostics: top-K coverage ratio and rare category ratio.
/// Coverage indicates whether a small number of categories dominate the distribution.
/// Rare ratio measures the fraction of categories with very few observations,
/// useful for deciding between one-hot encoding and embeddings, and for detecting dirty data.
/// Maintains a frequency map of distinct values, capped at <see cref="MaxDistinctValues"/>
/// to bound memory. When the cap is reached, new unseen values are still counted toward
/// the total but not tracked individually, and the result is flagged as approximate.
/// </summary>
/// <remarks>
/// For <see cref="DataKind.Float32"/> and <see cref="DataKind.UInt8"/> columns, frequencies
/// are tracked using integer keys (float bit patterns or byte values) to avoid per-row
/// string allocations on the hot path.
/// </remarks>
public sealed class CategoricalDiagnosticsAccumulator : IStatisticAccumulator
{
    /// <summary>
    /// Categories observed fewer than this many times are classified as rare.
    /// This threshold is a fixed heuristic — low enough to avoid flagging moderately
    /// infrequent values, high enough to catch singletons and near-singletons that
    /// often indicate data entry errors or extreme long-tail categories.
    /// </summary>
    public const int RareThreshold = 5;

    /// <summary>Maximum number of distinct values to track exactly.</summary>
    public const int MaxDistinctValues = 100_000;

    private readonly int _k;
    private readonly DataKind _kind;
    private readonly Dictionary<string, long>? _stringFrequencies;
    private readonly Dictionary<int, long>? _numericFrequencies;
    private long _totalCount;
    private long _untrackedCount;
    private bool _capped;

    /// <summary>
    /// Initializes a new instance of the <see cref="CategoricalDiagnosticsAccumulator"/> class
    /// using string-keyed frequency tracking.
    /// </summary>
    /// <param name="k">Number of top categories used to compute coverage. Should match the top-K parameter.</param>
    public CategoricalDiagnosticsAccumulator(int k = 10) : this(k, DataKind.String)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CategoricalDiagnosticsAccumulator"/> class,
    /// selecting numeric or string frequency tracking based on column kind.
    /// </summary>
    /// <param name="k">Number of top categories used to compute coverage.</param>
    /// <param name="kind">
    /// The <see cref="DataKind"/> of the column. <see cref="DataKind.Float32"/> and
    /// <see cref="DataKind.UInt8"/> use integer-keyed dictionaries to avoid per-row
    /// string allocations.
    /// </param>
    public CategoricalDiagnosticsAccumulator(int k, DataKind kind)
    {
        _k = k;
        _kind = kind;

        if (kind is DataKind.Float32 or DataKind.UInt8)
        {
            _numericFrequencies = new();
        }
        else
        {
            _stringFrequencies = new();
        }
    }

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        _totalCount++;

        if (_numericFrequencies is not null)
        {
            int key = _kind == DataKind.UInt8
                ? value.AsUInt8()
                : BitConverter.SingleToInt32Bits(value.AsFloat32());

            if (_numericFrequencies.TryGetValue(key, out long currentCount))
            {
                _numericFrequencies[key] = currentCount + 1;
            }
            else if (!_capped)
            {
                _numericFrequencies[key] = 1;

                if (_numericFrequencies.Count >= MaxDistinctValues)
                {
                    _capped = true;
                }
            }
            else
            {
                _untrackedCount++;
            }
        }
        else
        {
            string key = ValueToString(value);

            if (_stringFrequencies!.TryGetValue(key, out long currentCount))
            {
                _stringFrequencies[key] = currentCount + 1;
            }
            else if (!_capped)
            {
                _stringFrequencies[key] = 1;

                if (_stringFrequencies.Count >= MaxDistinctValues)
                {
                    _capped = true;
                }
            }
            else
            {
                _untrackedCount++;
            }
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
        _untrackedCount += otherDiagnostics._untrackedCount;

        if (_numericFrequencies is not null && otherDiagnostics._numericFrequencies is not null)
        {
            foreach (KeyValuePair<int, long> entry in otherDiagnostics._numericFrequencies)
            {
                if (_numericFrequencies.TryGetValue(entry.Key, out long currentCount))
                {
                    _numericFrequencies[entry.Key] = currentCount + entry.Value;
                }
                else if (_numericFrequencies.Count < MaxDistinctValues)
                {
                    _numericFrequencies[entry.Key] = entry.Value;
                }
                else
                {
                    _untrackedCount += entry.Value;
                    _capped = true;
                }
            }

            if (_numericFrequencies.Count >= MaxDistinctValues)
            {
                _capped = true;
            }
        }
        else if (_stringFrequencies is not null && otherDiagnostics._stringFrequencies is not null)
        {
            foreach (KeyValuePair<string, long> entry in otherDiagnostics._stringFrequencies)
            {
                if (_stringFrequencies.TryGetValue(entry.Key, out long currentCount))
                {
                    _stringFrequencies[entry.Key] = currentCount + entry.Value;
                }
                else if (_stringFrequencies.Count < MaxDistinctValues)
                {
                    _stringFrequencies[entry.Key] = entry.Value;
                }
                else
                {
                    _untrackedCount += entry.Value;
                    _capped = true;
                }
            }

            if (_stringFrequencies.Count >= MaxDistinctValues)
            {
                _capped = true;
            }
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_totalCount == 0)
        {
            return new StatisticResult("categorical_diagnostics", new CategoricalDiagnosticsResult(0.0, 0.0, 0, 0, false));
        }

        // Coverage: sum of top-K category counts / total non-null count
        List<long> counts;

        if (_numericFrequencies is not null)
        {
            counts = [.. _numericFrequencies.Values];
        }
        else
        {
            counts = [.. _stringFrequencies!.Values];
        }

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
        foreach (long count in counts)
        {
            if (count < RareThreshold)
            {
                rareCategoryCount++;
            }
        }

        long totalCategoryCount = counts.Count;
        double rareRatio = (double)rareCategoryCount / totalCategoryCount;

        return new StatisticResult(
            "categorical_diagnostics",
            new CategoricalDiagnosticsResult(coverageTopK, rareRatio, rareCategoryCount, totalCategoryCount, _capped));
    }

    private static string ValueToString(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Float32 => value.AsFloat32().ToString("G"),
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
/// <param name="Approximate">True if the frequency map was capped and diagnostics are based on a subset of distinct values.</param>
public sealed record CategoricalDiagnosticsResult(
    double CoverageTopK,
    double RareRatio,
    long RareCategoryCount,
    long TotalCategoryCount,
    bool Approximate);
