namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates top-K most frequent values using a bounded frequency map.
/// When more than K distinct values are observed, the least frequent are evicted.
/// </summary>
/// <remarks>
/// For <see cref="DataKind.Float32"/>, <see cref="DataKind.UInt8"/>, and other fixed-width
/// numeric columns, frequencies are tracked using integer keys (bit patterns or raw values)
/// to avoid per-row string allocations on the hot path. Types wider than 32 bits use
/// 64-bit integer keys. String keys are produced on demand for results.
/// </remarks>
public sealed class TopKAccumulator : IStatisticAccumulator
{
    /// <summary>
    /// Initial capacity for numeric frequency dictionaries. Sized to keep the first
    /// allocation under the Large Object Heap threshold while skipping the early resize chain.
    /// </summary>
    private const int NumericInitialCapacity = 4_096;

    private readonly int _k;
    private readonly DataKind _kind;
    private readonly Dictionary<string, long>? _stringFrequencies;
    private readonly Dictionary<int, long>? _numericFrequencies;
    private readonly Dictionary<long, long>? _wideNumericFrequencies;

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
    /// The <see cref="DataKind"/> of the column. <see cref="DataKind.Float32"/>,
    /// <see cref="DataKind.UInt8"/>, and other fixed-width numeric types use
    /// integer-keyed dictionaries to avoid per-row string allocations.
    /// </param>
    public TopKAccumulator(int k, DataKind kind)
    {
        _k = k;
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

    /// <summary>Gets the current frequency map.</summary>
    public IReadOnlyDictionary<string, long> Frequencies
    {
        get
        {
            if (_stringFrequencies is not null)
            {
                return _stringFrequencies;
            }

            if (_wideNumericFrequencies is not null)
            {
                Dictionary<string, long> result = new(_wideNumericFrequencies.Count);
                foreach (KeyValuePair<long, long> entry in _wideNumericFrequencies)
                {
                    result[WideNumericKeyToString(entry.Key)] = entry.Value;
                }

                return result;
            }

            // Convert numeric keys to strings on demand (not on the hot path).
            Dictionary<string, long> narrowResult = new(_numericFrequencies!.Count);
            foreach (KeyValuePair<int, long> entry in _numericFrequencies)
            {
                narrowResult[NumericKeyToString(entry.Key)] = entry.Value;
            }

            return narrowResult;
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
            long key = ToWideNumericKey(value);

            if (_wideNumericFrequencies.TryGetValue(key, out long currentCount))
            {
                _wideNumericFrequencies[key] = currentCount + 1;
            }
            else
            {
                _wideNumericFrequencies[key] = 1;

                if (_wideNumericFrequencies.Count > _k * 2)
                {
                    TrimWideNumeric();
                }
            }
        }
        else if (_numericFrequencies is not null)
        {
            int key = ToNumericKey(value);

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

        if (_wideNumericFrequencies is not null && otherTopK._wideNumericFrequencies is not null)
        {
            foreach (KeyValuePair<long, long> entry in otherTopK._wideNumericFrequencies)
            {
                _wideNumericFrequencies.TryGetValue(entry.Key, out long currentCount);
                _wideNumericFrequencies[entry.Key] = currentCount + entry.Value;
            }

            if (_wideNumericFrequencies.Count > _k * 2)
            {
                TrimWideNumeric();
            }
        }
        else if (_numericFrequencies is not null && otherTopK._numericFrequencies is not null)
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
            else if (otherTopK._numericFrequencies is not null)
            {
                // Cross-mode merge: convert other's numeric keys to strings.
                foreach (KeyValuePair<int, long> entry in otherTopK._numericFrequencies)
                {
                    string key = otherTopK.NumericKeyToString(entry.Key);
                    _stringFrequencies.TryGetValue(key, out long currentCount);
                    _stringFrequencies[key] = currentCount + entry.Value;
                }
            }
            else if (otherTopK._wideNumericFrequencies is not null)
            {
                // Cross-mode merge: convert other's wide numeric keys to strings.
                foreach (KeyValuePair<long, long> entry in otherTopK._wideNumericFrequencies)
                {
                    string key = otherTopK.WideNumericKeyToString(entry.Key);
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

        if (_wideNumericFrequencies is not null)
        {
            TrimWideNumeric();
            sorted = new(_wideNumericFrequencies.Count);
            foreach (KeyValuePair<long, long> entry in _wideNumericFrequencies)
            {
                sorted.Add(new KeyValuePair<string, long>(WideNumericKeyToString(entry.Key), entry.Value));
            }
        }
        else if (_numericFrequencies is not null)
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
            DataKind.Float32 => BitConverter.Int32BitsToSingle(key).ToString("G"),
            DataKind.UInt8 => ((byte)key).ToString(),
            DataKind.Int8 => ((sbyte)key).ToString(),
            DataKind.Int16 => ((short)key).ToString(),
            DataKind.UInt16 => ((ushort)key).ToString(),
            DataKind.Int32 => key.ToString(),
            DataKind.UInt32 => ((uint)key).ToString(),
            _ => key.ToString()
        };
    }

    private string WideNumericKeyToString(long key)
    {
        return _kind switch
        {
            DataKind.Int64 => key.ToString(),
            DataKind.UInt64 => ((ulong)key).ToString(),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(key).ToString("G"),
            _ => key.ToString()
        };
    }

    private int ToNumericKey(DataValue value)
    {
        return _kind switch
        {
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => unchecked((int)value.AsUInt32()),
            _ => BitConverter.SingleToInt32Bits(value.AsFloat32())
        };
    }

    private long ToWideNumericKey(DataValue value)
    {
        return _kind switch
        {
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => unchecked((long)value.AsUInt64()),
            _ => BitConverter.DoubleToInt64Bits(value.AsFloat64())
        };
    }

    private void TrimWideNumeric()
    {
        if (_wideNumericFrequencies!.Count <= _k)
        {
            return;
        }

        List<KeyValuePair<long, long>> sorted = [.. _wideNumericFrequencies];
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

        _wideNumericFrequencies.Clear();
        int keepCount = Math.Min(_k, sorted.Count);
        for (int i = 0; i < keepCount; i++)
        {
            _wideNumericFrequencies[sorted[i].Key] = sorted[i].Value;
        }
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
/// Contains the top-K frequency results.
/// </summary>
/// <param name="Entries">Value-frequency pairs sorted by frequency descending.</param>
public sealed record TopKResult(IReadOnlyList<KeyValuePair<string, long>> Entries);
