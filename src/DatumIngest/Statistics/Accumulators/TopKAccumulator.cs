namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates top-K most frequent values using a bounded frequency map.
/// When more than K distinct values are observed, the least frequent are evicted.
/// </summary>
/// <remarks>
/// For <see cref="DataKind.Scalar"/> and <see cref="DataKind.UInt8"/> columns, frequencies
/// are tracked using integer keys (float bit patterns or byte values) to avoid per-row
/// string allocations on the hot path. String keys are produced on demand for results.
/// </remarks>
public sealed class TopKAccumulator : IStatisticAccumulator
{
    private readonly int _k;
    private readonly DataKind _kind;
    private readonly Dictionary<string, long>? _stringFrequencies;
    private readonly Dictionary<int, long>? _numericFrequencies;

    /// <summary>
    /// Initializes a new instance of the <see cref="TopKAccumulator"/> class
    /// using string-keyed frequency tracking.
    /// </summary>
    /// <param name="k">Maximum number of top values to track. Defaults to 10.</param>
    public TopKAccumulator(int k = 10) : this(k, DataKind.String)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TopKAccumulator"/> class,
    /// selecting numeric or string frequency tracking based on column kind.
    /// </summary>
    /// <param name="k">Maximum number of top values to track.</param>
    /// <param name="kind">
    /// The <see cref="DataKind"/> of the column. <see cref="DataKind.Scalar"/> and
    /// <see cref="DataKind.UInt8"/> use integer-keyed dictionaries to avoid per-row
    /// string allocations.
    /// </param>
    public TopKAccumulator(int k, DataKind kind)
    {
        _k = k;
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

    /// <summary>Gets the current frequency map.</summary>
    public IReadOnlyDictionary<string, long> Frequencies
    {
        get
        {
            if (_stringFrequencies is not null)
            {
                return _stringFrequencies;
            }

            // Convert numeric keys to strings on demand (not on the hot path).
            Dictionary<string, long> result = new(_numericFrequencies!.Count);
            foreach (KeyValuePair<int, long> entry in _numericFrequencies)
            {
                result[NumericKeyToString(entry.Key)] = entry.Value;
            }

            return result;
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

            if (_numericFrequencies.TryGetValue(key, out long currentCount))
            {
                _numericFrequencies[key] = currentCount + 1;
            }
            else
            {
                _numericFrequencies[key] = 1;

                if (_numericFrequencies.Count > _k * 2)
                {
                    TrimNumeric();
                }
            }
        }
        else
        {
            string key = ValueToString(value);

            if (_stringFrequencies!.TryGetValue(key, out long currentCount))
            {
                _stringFrequencies[key] = currentCount + 1;
            }
            else
            {
                _stringFrequencies[key] = 1;

                if (_stringFrequencies.Count > _k * 2)
                {
                    TrimString();
                }
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

        if (_numericFrequencies is not null && otherTopK._numericFrequencies is not null)
        {
            foreach (KeyValuePair<int, long> entry in otherTopK._numericFrequencies)
            {
                _numericFrequencies.TryGetValue(entry.Key, out long currentCount);
                _numericFrequencies[entry.Key] = currentCount + entry.Value;
            }

            if (_numericFrequencies.Count > _k * 2)
            {
                TrimNumeric();
            }
        }
        else if (_stringFrequencies is not null)
        {
            if (otherTopK._stringFrequencies is not null)
            {
                foreach (KeyValuePair<string, long> entry in otherTopK._stringFrequencies)
                {
                    _stringFrequencies.TryGetValue(entry.Key, out long currentCount);
                    _stringFrequencies[entry.Key] = currentCount + entry.Value;
                }
            }
            else
            {
                // Cross-mode merge: convert other's numeric keys to strings.
                foreach (KeyValuePair<int, long> entry in otherTopK._numericFrequencies!)
                {
                    string key = otherTopK.NumericKeyToString(entry.Key);
                    _stringFrequencies.TryGetValue(key, out long currentCount);
                    _stringFrequencies[key] = currentCount + entry.Value;
                }
            }

            if (_stringFrequencies.Count > _k * 2)
            {
                TrimString();
            }
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        List<KeyValuePair<string, long>> sorted;

        if (_numericFrequencies is not null)
        {
            TrimNumeric();
            sorted = new(_numericFrequencies.Count);
            foreach (KeyValuePair<int, long> entry in _numericFrequencies)
            {
                sorted.Add(new KeyValuePair<string, long>(NumericKeyToString(entry.Key), entry.Value));
            }
        }
        else
        {
            TrimString();
            sorted = [.. _stringFrequencies!];
        }

        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        return new StatisticResult("top_k", new TopKResult(sorted.AsReadOnly()));
    }

    private void TrimString()
    {
        if (_stringFrequencies!.Count <= _k)
        {
            return;
        }

        List<KeyValuePair<string, long>> sorted = [.. _stringFrequencies];
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        _stringFrequencies.Clear();
        int keepCount = Math.Min(_k, sorted.Count);
        for (int i = 0; i < keepCount; i++)
        {
            _stringFrequencies[sorted[i].Key] = sorted[i].Value;
        }
    }

    private void TrimNumeric()
    {
        if (_numericFrequencies!.Count <= _k)
        {
            return;
        }

        List<KeyValuePair<int, long>> sorted = [.. _numericFrequencies];
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        _numericFrequencies.Clear();
        int keepCount = Math.Min(_k, sorted.Count);
        for (int i = 0; i < keepCount; i++)
        {
            _numericFrequencies[sorted[i].Key] = sorted[i].Value;
        }
    }

    private string NumericKeyToString(int key)
    {
        return _kind switch
        {
            DataKind.Scalar => BitConverter.Int32BitsToSingle(key).ToString("G"),
            DataKind.UInt8 => ((byte)key).ToString(),
            _ => key.ToString()
        };
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
