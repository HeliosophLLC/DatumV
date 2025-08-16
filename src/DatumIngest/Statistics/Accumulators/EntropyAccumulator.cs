namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Computes Shannon entropy H = -Σ p_i log₂(p_i) from the value distribution.
/// Maintains a frequency map of distinct values, capped at <see cref="MaxDistinctValues"/>
/// to bound memory. When the cap is reached, new unseen values are pooled into
/// an untracked bucket and entropy is flagged as approximate.
/// </summary>
/// <remarks>
/// For <see cref="DataKind.Scalar"/> and <see cref="DataKind.UInt8"/> columns, frequencies
/// are tracked using integer keys (float bit patterns or byte values) to avoid per-row
/// string allocations on the hot path.
/// </remarks>
public sealed class EntropyAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum number of distinct values to track exactly.</summary>
    public const int MaxDistinctValues = 100_000;

    private readonly Dictionary<string, long>? _stringFrequencies;
    private readonly Dictionary<int, long>? _numericFrequencies;
    private readonly DataKind _kind;
    private long _totalCount;
    private long _untrackedCount;
    private bool _capped;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntropyAccumulator"/> class
    /// using string-keyed frequency tracking.
    /// </summary>
    public EntropyAccumulator() : this(DataKind.String)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EntropyAccumulator"/> class,
    /// selecting numeric or string frequency tracking based on column kind.
    /// </summary>
    /// <param name="kind">
    /// The <see cref="DataKind"/> of the column. <see cref="DataKind.Scalar"/> and
    /// <see cref="DataKind.UInt8"/> use integer-keyed dictionaries to avoid per-row
    /// string allocations.
    /// </param>
    public EntropyAccumulator(DataKind kind)
    {
        _kind = kind;

        if (kind is DataKind.Scalar or DataKind.UInt8)
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

        if (_numericFrequencies is not null)
        {
            int key = _kind == DataKind.UInt8
                ? value.AsUInt8()
                : BitConverter.SingleToInt32Bits(value.AsScalar());

            _totalCount++;

            if (_numericFrequencies.TryGetValue(key, out long count))
            {
                _numericFrequencies[key] = count + 1;
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
            string? key = ToKey(value);

            if (key is null)
            {
                return;
            }

            _totalCount++;

            if (_stringFrequencies!.TryGetValue(key, out long count))
            {
                _stringFrequencies[key] = count + 1;
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
        if (other is not EntropyAccumulator otherEntropy || otherEntropy._totalCount == 0)
        {
            return;
        }

        _totalCount += otherEntropy._totalCount;
        _untrackedCount += otherEntropy._untrackedCount;

        if (_numericFrequencies is not null && otherEntropy._numericFrequencies is not null)
        {
            foreach (KeyValuePair<int, long> entry in otherEntropy._numericFrequencies)
            {
                if (_numericFrequencies.TryGetValue(entry.Key, out long existing))
                {
                    _numericFrequencies[entry.Key] = existing + entry.Value;
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
        else if (_stringFrequencies is not null && otherEntropy._stringFrequencies is not null)
        {
            foreach (KeyValuePair<string, long> entry in otherEntropy._stringFrequencies)
            {
                if (_stringFrequencies.TryGetValue(entry.Key, out long existing))
                {
                    _stringFrequencies[entry.Key] = existing + entry.Value;
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
            return new StatisticResult("entropy", new EntropyResult(0.0, false));
        }

        double entropy = 0.0;

        if (_numericFrequencies is not null)
        {
            foreach (long frequency in _numericFrequencies.Values)
            {
                if (frequency > 0)
                {
                    double p = (double)frequency / _totalCount;
                    entropy -= p * Math.Log2(p);
                }
            }
        }
        else
        {
            foreach (long frequency in _stringFrequencies!.Values)
            {
                if (frequency > 0)
                {
                    double p = (double)frequency / _totalCount;
                    entropy -= p * Math.Log2(p);
                }
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
