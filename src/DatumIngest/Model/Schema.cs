namespace DatumIngest.Model;

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
        : this(columns, primaryKeyColumnIndices: null)
    {
    }

    /// <summary>
    /// Creates a schema with an explicit ordered list of PRIMARY KEY column
    /// indices. Order matches the user's PK declaration; duplicates and
    /// out-of-range indices throw.
    /// </summary>
    public Schema(
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<int>? primaryKeyColumnIndices)
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

        if (primaryKeyColumnIndices is { Count: > 0 })
        {
            HashSet<int> seen = new();
            foreach (int idx in primaryKeyColumnIndices)
            {
                if (idx < 0 || idx >= columns.Count)
                {
                    throw new ArgumentException(
                        $"PRIMARY KEY column index {idx} is out of range [0, {columns.Count}).");
                }
                if (!seen.Add(idx))
                {
                    throw new ArgumentException(
                        $"PRIMARY KEY column index {idx} appears more than once.");
                }
            }
            PrimaryKeyColumnIndices = primaryKeyColumnIndices;
        }
        else
        {
            PrimaryKeyColumnIndices = Array.Empty<int>();
        }
    }

    /// <summary>The ordered list of column definitions.</summary>
    public IReadOnlyList<ColumnInfo> Columns => _columns;

    /// <summary>
    /// Ordered list of column indices forming the table's PRIMARY KEY.
    /// Empty when the schema has no PK. Order matches the PK declaration
    /// (table-level <c>PRIMARY KEY (b, a)</c> keeps <c>b</c> first
    /// regardless of column-declaration order).
    /// </summary>
    public IReadOnlyList<int> PrimaryKeyColumnIndices { get; }

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
