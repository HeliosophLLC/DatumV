namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Computes Shannon entropy H = -Σ p_i log₂(p_i) from the value distribution.
/// Maintains a frequency map of distinct values, capped at <see cref="MaxDistinctValues"/>
/// to bound memory. When the cap is reached, new unseen values are pooled into
/// an untracked bucket and entropy is flagged as approximate.
/// </summary>
/// <remarks>
/// For <see cref="DataKind.Float32"/>, <see cref="DataKind.UInt8"/>, and other fixed-width
/// numeric columns, frequencies are tracked using integer keys (bit patterns or raw values)
/// to avoid per-row string allocations on the hot path. Types wider than 32 bits use
/// 64-bit integer keys.
/// </remarks>
public sealed class EntropyAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum number of distinct values to track exactly.</summary>
    public const int MaxDistinctValues = 100_000;

    /// <summary>
    /// Initial capacity for numeric frequency dictionaries. Sized to keep the first
    /// allocation under the Large Object Heap threshold while skipping the early resize chain.
    /// </summary>
    private const int NumericInitialCapacity = 4_096;

    private readonly Dictionary<string, long>? _stringFrequencies;
    private readonly Dictionary<int, long>? _numericFrequencies;
    private readonly Dictionary<long, long>? _wideNumericFrequencies;
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
    /// The <see cref="DataKind"/> of the column. <see cref="DataKind.Float32"/> and
    /// <see cref="DataKind.UInt8"/> use integer-keyed dictionaries to avoid per-row
    /// string allocations.
    /// </param>
    public EntropyAccumulator(DataKind kind)
    {
        _kind = kind;

        if (kind is DataKind.Int64 or DataKind.UInt64 or DataKind.Float64)
        {
            _wideNumericFrequencies = new(NumericInitialCapacity);
        }
        else if (kind is DataKind.Float32 or DataKind.UInt8
            or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32)
        {
            _numericFrequencies = new(NumericInitialCapacity);
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

        if (_wideNumericFrequencies is not null)
        {
            long key = _kind switch
            {
                DataKind.Int64 => value.AsInt64(),
                DataKind.UInt64 => unchecked((long)value.AsUInt64()),
                _ => BitConverter.DoubleToInt64Bits(value.AsFloat64())
            };

            _totalCount++;

            if (_wideNumericFrequencies.TryGetValue(key, out long count))
            {
                _wideNumericFrequencies[key] = count + 1;
            }
            else if (!_capped)
            {
                _wideNumericFrequencies[key] = 1;

                if (_wideNumericFrequencies.Count >= MaxDistinctValues)
                {
                    _capped = true;
                }
            }
            else
            {
                _untrackedCount++;
            }
        }
        else if (_numericFrequencies is not null)
        {
            int key = _kind switch
            {
                DataKind.UInt8 => value.AsUInt8(),
                DataKind.Int8 => value.AsInt8(),
                DataKind.Int16 => value.AsInt16(),
                DataKind.UInt16 => value.AsUInt16(),
                DataKind.Int32 => value.AsInt32(),
                DataKind.UInt32 => unchecked((int)value.AsUInt32()),
                _ => BitConverter.SingleToInt32Bits(value.AsFloat32())
            };

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

        if (_wideNumericFrequencies is not null && otherEntropy._wideNumericFrequencies is not null)
        {
            foreach (KeyValuePair<long, long> entry in otherEntropy._wideNumericFrequencies)
            {
                if (_wideNumericFrequencies.TryGetValue(entry.Key, out long existing))
                {
                    _wideNumericFrequencies[entry.Key] = existing + entry.Value;
                }
                else if (_wideNumericFrequencies.Count < MaxDistinctValues)
                {
                    _wideNumericFrequencies[entry.Key] = entry.Value;
                }
                else
                {
                    _untrackedCount += entry.Value;
                    _capped = true;
                }
            }

            if (_wideNumericFrequencies.Count >= MaxDistinctValues)
            {
                _capped = true;
            }
        }
        else if (_numericFrequencies is not null && otherEntropy._numericFrequencies is not null)
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

        if (_wideNumericFrequencies is not null)
        {
            foreach (long frequency in _wideNumericFrequencies.Values)
            {
                if (frequency > 0)
                {
                    double p = (double)frequency / _totalCount;
                    entropy -= p * Math.Log2(p);
                }
            }
        }
        else if (_numericFrequencies is not null)
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
            DataKind.Float32 => value.AsFloat32().ToString("R"),
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
