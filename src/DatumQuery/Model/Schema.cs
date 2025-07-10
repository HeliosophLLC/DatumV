namespace DatumQuery.Model;

/// <summary>
/// An immutable ordered collection of <see cref="ColumnInfo"/> entries that describes
/// the shape of a row or batch flowing through the query pipeline.
/// </summary>
public sealed class Schema
{
    private readonly IReadOnlyList<ColumnInfo> _columns;

    // Case-insensitive index for fast name lookups.
    private readonly Dictionary<string, int> _nameIndex;

    /// <summary>
    /// Creates a schema from the given columns.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="columns"/> is empty or contains duplicate names (case-insensitive).
    /// </exception>
    public Schema(IReadOnlyList<ColumnInfo> columns)
    {
        if (columns.Count == 0)
        {
            throw new ArgumentException("A schema must contain at least one column.");
        }

        _nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < columns.Count; index++)
        {
            if (!_nameIndex.TryAdd(columns[index].Name, index))
            {
                throw new ArgumentException(
                    $"Duplicate column name '{columns[index].Name}' (case-insensitive).");
            }
        }

        _columns = columns;
    }

    /// <summary>The ordered list of column definitions.</summary>
    public IReadOnlyList<ColumnInfo> Columns => _columns;

    /// <summary>
    /// Finds a column by name using case-insensitive comparison.
    /// </summary>
    /// <returns>The matching <see cref="ColumnInfo"/>, or <c>null</c> if not found.</returns>
    public ColumnInfo? FindColumn(string name)
    {
        if (_nameIndex.TryGetValue(name, out int index))
        {
            return _columns[index];
        }

        return null;
    }
}
