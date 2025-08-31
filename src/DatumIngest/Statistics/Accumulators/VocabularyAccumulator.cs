namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Collects all distinct values in a column up to a configurable cap.
/// When the cap is reached, collection stops and the result is flagged as capped
/// (meaning the vocabulary is incomplete and should not be used for exact set operations).
/// </summary>
/// <remarks>
/// For fixed-width numeric columns, values are tracked using integer keys to avoid
/// per-row string allocations on the hot path. String conversion happens only at
/// result time. The resulting values are sorted by <see cref="StringComparer.Ordinal"/>
/// for O(|A| + |B|) merge-based intersection with other vocabularies.
/// </remarks>
public sealed class VocabularyAccumulator : IStatisticAccumulator
{
    /// <summary>Default maximum number of distinct values to collect.</summary>
    public const int DefaultMaxDistinctValues = 10_000_000;

    /// <summary>
    /// Initial capacity for numeric hash sets. Sized to keep the first allocation under the
    /// Large Object Heap threshold (~85 KB) while skipping the early resize chain that would
    /// otherwise produce many short-lived intermediate arrays.
    /// </summary>
    private const int NumericInitialCapacity = 4_096;

    private readonly int _maxDistinctValues;
    private readonly DataKind _kind;
    private readonly HashSet<string>? _stringValues;
    private readonly HashSet<int>? _numericValues;
    private readonly HashSet<long>? _wideNumericValues;
    private bool _capped;

    /// <summary>
    /// Initializes a new instance of the <see cref="VocabularyAccumulator"/> class
    /// using string-keyed tracking.
    /// </summary>
    /// <param name="maxDistinctValues">Maximum distinct values to collect before capping.</param>
    public VocabularyAccumulator(int maxDistinctValues = DefaultMaxDistinctValues)
        : this(DataKind.String, maxDistinctValues)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VocabularyAccumulator"/> class,
    /// selecting numeric or string tracking based on column kind.
    /// </summary>
    /// <param name="kind">The <see cref="DataKind"/> of the column.</param>
    /// <param name="maxDistinctValues">Maximum distinct values to collect before capping.</param>
    public VocabularyAccumulator(DataKind kind, int maxDistinctValues = DefaultMaxDistinctValues)
    {
        _kind = kind;
        _maxDistinctValues = maxDistinctValues;

        if (kind is DataKind.Int64 or DataKind.UInt64 or DataKind.Float64)
        {
            _wideNumericValues = new(NumericInitialCapacity);
        }
        else if (kind is DataKind.Float32 or DataKind.UInt8
            or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32)
        {
            _numericValues = new(NumericInitialCapacity);
        }
        else
        {
            _stringValues = new();
        }
    }

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull || _capped)
        {
            return;
        }

        if (_wideNumericValues is not null)
        {
            long key = _kind switch
            {
                DataKind.Int64 => value.AsInt64(),
                DataKind.UInt64 => unchecked((long)value.AsUInt64()),
                _ => BitConverter.DoubleToInt64Bits(value.AsFloat64())
            };

            _wideNumericValues.Add(key);

            if (_wideNumericValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
        else if (_numericValues is not null)
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

            _numericValues.Add(key);

            if (_numericValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
        else
        {
            string key = value.Kind switch
            {
                DataKind.String => value.AsString(),
                DataKind.Uuid => value.AsUuid().ToString(),
                DataKind.Date => value.AsDate().ToString("O"),
                DataKind.DateTime => value.AsDateTime().ToString("O"),
                DataKind.JsonValue => value.AsJsonValue(),
                _ => value.ToString() ?? ""
            };

            _stringValues!.Add(key);

            if (_stringValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not VocabularyAccumulator otherVocabulary || _capped)
        {
            return;
        }

        if (_wideNumericValues is not null && otherVocabulary._wideNumericValues is not null)
        {
            foreach (long key in otherVocabulary._wideNumericValues)
            {
                _wideNumericValues.Add(key);
            }

            if (otherVocabulary._capped || _wideNumericValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
        else if (_numericValues is not null && otherVocabulary._numericValues is not null)
        {
            foreach (int key in otherVocabulary._numericValues)
            {
                _numericValues.Add(key);
            }

            if (otherVocabulary._capped || _numericValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
        else if (_stringValues is not null)
        {
            IEnumerable<string> otherKeys;

            if (otherVocabulary._stringValues is not null)
            {
                otherKeys = otherVocabulary._stringValues;
            }
            else if (otherVocabulary._numericValues is not null)
            {
                otherKeys = ConvertNumericKeysToStrings(otherVocabulary._numericValues, otherVocabulary._kind);
            }
            else if (otherVocabulary._wideNumericValues is not null)
            {
                otherKeys = ConvertWideNumericKeysToStrings(otherVocabulary._wideNumericValues, otherVocabulary._kind);
            }
            else
            {
                return;
            }

            foreach (string key in otherKeys)
            {
                _stringValues.Add(key);
            }

            if (otherVocabulary._capped || _stringValues.Count >= _maxDistinctValues)
            {
                _capped = true;
            }
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        string[] sorted;

        if (_wideNumericValues is not null)
        {
            sorted = new string[_wideNumericValues.Count];
            int index = 0;

            foreach (long key in _wideNumericValues)
            {
                sorted[index++] = WideNumericKeyToString(key);
            }
        }
        else if (_numericValues is not null)
        {
            sorted = new string[_numericValues.Count];
            int index = 0;

            foreach (int key in _numericValues)
            {
                sorted[index++] = NumericKeyToString(key);
            }
        }
        else
        {
            sorted = new string[_stringValues!.Count];
            _stringValues.CopyTo(sorted);
        }

        Array.Sort(sorted, StringComparer.Ordinal);

        return new StatisticResult("vocabulary", new VocabularyResult(sorted, _capped));
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

    private static IEnumerable<string> ConvertNumericKeysToStrings(HashSet<int> keys, DataKind kind)
    {
        foreach (int key in keys)
        {
            yield return kind switch
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
    }

    private static IEnumerable<string> ConvertWideNumericKeysToStrings(HashSet<long> keys, DataKind kind)
    {
        foreach (long key in keys)
        {
            yield return kind switch
            {
                DataKind.Int64 => key.ToString(),
                DataKind.UInt64 => ((ulong)key).ToString(),
                DataKind.Float64 => BitConverter.Int64BitsToDouble(key).ToString("G"),
                _ => key.ToString()
            };
        }
    }
}

/// <summary>
/// Contains the vocabulary accumulation result: all distinct values (sorted ordinal)
/// with a flag indicating whether the cap was reached before all values were collected.
/// </summary>
/// <param name="SortedValues">Distinct values sorted by <see cref="StringComparer.Ordinal"/>.</param>
/// <param name="Capped">True if the accumulator hit its distinct value cap (vocabulary is incomplete).</param>
public sealed record VocabularyResult(IReadOnlyList<string> SortedValues, bool Capped);
