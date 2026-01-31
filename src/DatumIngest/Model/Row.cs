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
    /// <summary>
    /// Am empty row with no columns. Useful as a placeholder for operators that require a non-null
    /// row but have no actual data to provide, such as the initial frame for a source operator's
    /// argument evaluation.
    /// </summary>
    public readonly static Row Empty = new(ColumnLookup.Empty, Array.Empty<DataValue>());

    private readonly DataValue[] _values;
    private readonly ColumnLookup _columnLookup;

    /// <summary>
    /// Initializes a new <see cref="Row"/> with the given column lookup and values.
    /// </summary>
    /// <param name="columnLookup">The column lookup containing column names and indices.</param>
    /// <param name="values">The array of data values for this row.</param>
    public Row(ColumnLookup columnLookup, DataValue[] values)
    {
        _columnLookup = columnLookup;
        _values = values;
    }

    /// <summary>The number of fields in this row.</summary>
    public int FieldCount => _values.Length;

    /// <summary>The ordered column names.</summary>
    public IReadOnlyList<string> ColumnNames => _columnLookup.ColumnNames;

    /// <summary>
    /// Gets the column lookup associated with this row, which contains column names and indices.
    /// </summary>
    public ColumnLookup ColumnLookup => _columnLookup;

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
    internal IReadOnlyList<string> RawNames => _columnLookup.ColumnNames;

    /// <summary>
    /// The raw name-to-ordinal dictionary. Used internally by flat-buffer probe storage
    /// in <see cref="Execution.SpillPartition"/> to share schema across reconstructed rows.
    /// </summary>
    internal IReadOnlyDictionary<string, int> RawNameIndex => _columnLookup.NameIndex;

    /// <summary>
    /// Retrieves a value by column name (case-insensitive).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No column with the given name exists.</exception>
    public DataValue this[string name]
    {
        get
        {
            if (_columnLookup.TryGetColumnOrdinal(name, out int index))
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
        if (_columnLookup.TryGetColumnOrdinal(name, out int index))
        {
            result = _values[index];
            return true;
        }

        result = default;
        return false;
    }
}
