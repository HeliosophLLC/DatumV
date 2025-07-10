namespace DatumQuery.Statistics.Accumulators;

using DatumQuery.Model;

/// <summary>
/// Accumulates top-K most frequent values using a bounded frequency map.
/// When more than K distinct values are observed, the least frequent are evicted.
/// </summary>
public sealed class TopKAccumulator : IStatisticAccumulator
{
    private readonly int _k;
    private readonly Dictionary<string, long> _frequencies = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TopKAccumulator"/> class.
    /// </summary>
    /// <param name="k">Maximum number of top values to track. Defaults to 10.</param>
    public TopKAccumulator(int k = 10)
    {
        _k = k;
    }

    /// <summary>Gets the current frequency map.</summary>
    public IReadOnlyDictionary<string, long> Frequencies => _frequencies;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        string key = ValueToString(value);

        if (_frequencies.TryGetValue(key, out long currentCount))
        {
            _frequencies[key] = currentCount + 1;
        }
        else
        {
            _frequencies[key] = 1;

            // If we've exceeded capacity, evict the least frequent entry
            if (_frequencies.Count > _k * 2)
            {
                Trim();
            }
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not TopKAccumulator otherTopK)
        {
            return;
        }

        foreach (KeyValuePair<string, long> entry in otherTopK._frequencies)
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

        if (_frequencies.Count > _k * 2)
        {
            Trim();
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        Trim();

        List<KeyValuePair<string, long>> sorted = [.. _frequencies];
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        return new StatisticResult("top_k", new TopKResult(sorted.AsReadOnly()));
    }

    private void Trim()
    {
        if (_frequencies.Count <= _k)
        {
            return;
        }

        List<KeyValuePair<string, long>> sorted = [.. _frequencies];
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        _frequencies.Clear();
        int keepCount = Math.Min(_k, sorted.Count);
        for (int i = 0; i < keepCount; i++)
        {
            _frequencies[sorted[i].Key] = sorted[i].Value;
        }
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
/// Contains the top-K frequency results.
/// </summary>
/// <param name="Entries">Value-frequency pairs sorted by frequency descending.</param>
public sealed record TopKResult(IReadOnlyList<KeyValuePair<string, long>> Entries);
