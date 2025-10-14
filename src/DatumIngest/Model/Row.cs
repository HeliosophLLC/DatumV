using DatumIngest.Execution;

namespace DatumIngest.Model;

/// <summary>
/// A single row of named <see cref="DataValue"/> entries. This is a lightweight value type
/// that stores references to shared column-name and name-index arrays, avoiding per-row
/// heap allocations in hot paths such as hash join build phases.
/// </summary>
/// <remarks>
/// <para>
/// Schema arrays (<c>names</c> and <c>nameIndex</c>) are typically shared across all rows
/// in a <see cref="RowBatch"/> — each struct holds the same two references, so the actual
/// per-row overhead is just three managed references (24 bytes on x64), stored inline in
/// the containing array or <see cref="List{T}"/>. This eliminates millions of individual
/// heap objects that previously caused expensive gen2 card-table scanning during ephemeral
/// GC collections.
/// </para>
/// </remarks>
public readonly struct Row
{
    private readonly IReadOnlyList<string> _names;
    private readonly DataValue[] _values;
    private readonly Dictionary<string, int> _nameIndex;

    /// <summary>
    /// Creates a row from parallel arrays of column names and values, building a
    /// case-insensitive name-to-ordinal index. Suitable for tests and one-off construction.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the name and value arrays have different lengths.
    /// </exception>
    public Row(string[] names, DataValue[] values)
    {
        if (names.Length != values.Length)
        {
            throw new ArgumentException(
                $"Name count ({names.Length}) must equal value count ({values.Length}).");
        }

        _names = names;
        _values = values;

        _nameIndex = new Dictionary<string, int>(names.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < names.Length; index++)
        {
            _nameIndex[names[index]] = index;
        }
    }

    /// <summary>
    /// Creates a row that shares a pre-built column name array and name-index dictionary
    /// with other rows of the same schema. This avoids re-allocating the names array and
    /// rebuilding the dictionary for every row in hot paths such as joins.
    /// </summary>
    /// <remarks>
    /// Callers must guarantee that <paramref name="names"/> and <paramref name="nameIndex"/>
    /// are never mutated after construction.
    /// </remarks>
    public Row(IReadOnlyList<string> names, DataValue[] values, Dictionary<string, int> nameIndex)
    {
        _names = names;
        _values = values;
        _nameIndex = nameIndex;
    }

    /// <summary>The number of fields in this row.</summary>
    public int FieldCount => _values.Length;

    /// <summary>The ordered column names.</summary>
    public IReadOnlyList<string> ColumnNames => _names;

    /// <summary>
    /// The raw backing array of values. Used internally by pooling infrastructure
    /// to return rented buffers without exposing mutation to public consumers.
    /// </summary>
    internal DataValue[] RawValues
    {
        get
        {
#if POOL_DIAGNOSTICS
            DatumIngest.Pooling.PoolBacking.AssertNotReturned(_values, "Row.RawValues");
#endif
            return _values;
        }
    }

    /// <summary>
    /// The raw column name array. Used internally by flat-buffer probe storage
    /// in <see cref="Execution.SpillPartition"/> to share schema across reconstructed rows.
    /// </summary>
    internal IReadOnlyList<string> RawNames => _names;

    /// <summary>
    /// The raw name-to-ordinal dictionary. Used internally by flat-buffer probe storage
    /// in <see cref="Execution.SpillPartition"/> to share schema across reconstructed rows.
    /// </summary>
    internal Dictionary<string, int> RawNameIndex => _nameIndex;

    /// <summary>
    /// <c>true</c> when this instance is the <c>default</c> (uninitialized) value.
    /// Useful for nullable-replacement patterns where <c>Row?</c> (<see cref="Nullable{Row}"/>)
    /// is avoided.
    /// </summary>
    public bool IsEmpty => _values is null;

    /// <summary>
    /// Creates a deep copy of this row with its own <see cref="DataValue"/> array.
    /// The schema arrays (names and name index) are shared with the original.
    /// Use this when the row must outlive the pooled buffer that backs it — for
    /// example, when collecting rows from a query stream whose
    /// <see cref="Execution.LocalBufferPool"/> is disposed after iteration.
    /// </summary>
    public Row Clone()
    {
        DataValue[] copy = new DataValue[_values.Length];
        Array.Copy(_values, copy, _values.Length);
        return new Row(_names, copy, _nameIndex);
    }

    /// <summary>
    /// Retrieves a value by column name (case-insensitive).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No column with the given name exists.</exception>
    public DataValue this[string name]
    {
        get
        {
            if (_nameIndex.TryGetValue(name, out int index))
            {
                return _values[index];
            }

            throw new KeyNotFoundException($"Column '{name}' not found.");
        }
    }

    /// <summary>
    /// Retrieves a value by zero-based ordinal position.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Ordinal is out of range.</exception>
    public DataValue this[int ordinal]
    {
        get
        {
            if ((uint)ordinal >= (uint)_values.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal),
                    ordinal,
                    $"Ordinal must be between 0 and {_values.Length - 1}.");
            }

            return _values[ordinal];
        }
    }

    /// <summary>
    /// Attempts to retrieve a value by column name without throwing.
    /// </summary>
    /// <returns><c>true</c> if the column exists; otherwise <c>false</c>.</returns>
    public bool TryGetValue(string name, out DataValue result)
    {
        if (_nameIndex.TryGetValue(name, out int index))
        {
            result = _values[index];
            return true;
        }

        result = default;
        return false;
    }
}
