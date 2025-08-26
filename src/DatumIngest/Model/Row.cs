namespace DatumIngest.Model;

/// <summary>
/// A single row of named <see cref="DataValue"/> entries.
/// Provides both name-based and ordinal-based access with case-insensitive name matching.
/// </summary>
public class Row
{
    private string[] _names;
    private readonly DataValue[] _values;

    // Case-insensitive index for fast name lookups.
    private Dictionary<string, int> _nameIndex;

    /// <summary>
    /// Creates a row from parallel arrays of column names and values.
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

        _nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
    /// are never mutated after construction. <see cref="Row"/> is immutable, so sharing is safe.
    /// </remarks>
    internal Row(string[] names, DataValue[] values, Dictionary<string, int> nameIndex)
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
    internal DataValue[] RawValues => _values;

    /// <summary>
    /// Replaces the column name array and name-index dictionary without allocating
    /// a new <see cref="Row"/>. Used by the pooling infrastructure when a rented
    /// row needs to adopt a different <see cref="Execution.Operators.JoinOperator.CombinedRowSchema"/>.
    /// </summary>
    internal void UpdateSchema(string[] names, Dictionary<string, int> nameIndex)
    {
        _names = names;
        _nameIndex = nameIndex;
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
