namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Computes Shannon entropy H = -Σ p_i log₂(p_i) from the value distribution.
/// Maintains a frequency map of distinct values, capped at <see cref="MaxDistinctValues"/>
/// to bound memory. When the cap is reached, new unseen values are pooled into
/// an untracked bucket and entropy is flagged as approximate.
/// </summary>
public sealed class EntropyAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum number of distinct values to track exactly.</summary>
    public const int MaxDistinctValues = 100_000;

    private readonly Dictionary<string, long> _frequencies = new();
    private long _totalCount;
    private long _untrackedCount;
    private bool _capped;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        string? key = ToKey(value);

        if (key is null)
        {
            return;
        }

        _totalCount++;

        if (_frequencies.TryGetValue(key, out long count))
        {
            _frequencies[key] = count + 1;
        }
        else if (!_capped)
        {
            _frequencies[key] = 1;

            if (_frequencies.Count >= MaxDistinctValues)
            {
                _capped = true;
            }
        }
        else
        {
            _untrackedCount++;
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not EntropyAccumulator otherEntropy || otherEntropy._totalCount == 0)
        {
            return;
        }

        _totalCount += otherEntropy._totalCount;
        _untrackedCount += otherEntropy._untrackedCount;

        foreach (KeyValuePair<string, long> entry in otherEntropy._frequencies)
        {
            if (_frequencies.TryGetValue(entry.Key, out long existing))
            {
                _frequencies[entry.Key] = existing + entry.Value;
            }
            else if (_frequencies.Count < MaxDistinctValues)
            {
                _frequencies[entry.Key] = entry.Value;
            }
            else
            {
                _untrackedCount += entry.Value;
                _capped = true;
            }
        }

        if (_frequencies.Count >= MaxDistinctValues)
        {
            _capped = true;
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_totalCount == 0)
        {
            return new StatisticResult("entropy", new EntropyResult(0.0, false));
        }

        double entropy = 0.0;

        foreach (long frequency in _frequencies.Values)
        {
            if (frequency > 0)
            {
                double p = (double)frequency / _totalCount;
                entropy -= p * Math.Log2(p);
            }
        }

        // If capped, untracked values exist. Treat them as a single "other" category
        // for a conservative lower-bound estimate.
        if (_untrackedCount > 0)
        {
            double p = (double)_untrackedCount / _totalCount;
            entropy -= p * Math.Log2(p);
        }

        return new StatisticResult("entropy", new EntropyResult(entropy, _capped));
    }

    private static string? ToKey(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar().ToString("R"),
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.String => value.AsString(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.JsonValue => value.AsJsonValue(),
            _ => null
        };
    }
}

/// <summary>
/// Contains Shannon entropy result.
/// </summary>
/// <param name="Value">Shannon entropy in bits. Higher values indicate more disorder/information.</param>
/// <param name="Approximate">True if the frequency map was capped and entropy is a lower-bound estimate.</param>
public sealed record EntropyResult(double Value, bool Approximate);
