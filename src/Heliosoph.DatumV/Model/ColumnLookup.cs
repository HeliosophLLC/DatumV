using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Model;

/// <summary>
/// Provides efficient name-to-ordinal mapping for column lookup
/// </summary>
public sealed class ColumnLookup
{
    /// <summary>
    /// A shared empty ColumnLookup instance that can be used for operators with no columns (e.g., SingleEmptyRowOperator).
    /// </summary>
    public static readonly ColumnLookup Empty = new(Array.Empty<string>());

    private readonly Dictionary<string, int> _nameIndex;
    private Dictionary<string, int>? _schemaIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnLookup"/> class with
    /// the specified column names and name index.
    /// </summary>
    /// <param name="columns">Array of tuples containing column index and name.</param>
    public ColumnLookup((int index, int schemaIndex, string name)[] columns)
    {
        ColumnNames = Array.ConvertAll(columns, c => c.name);
        Count = ColumnNames.Count;
        _nameIndex = new Dictionary<string, int>(columns.Length, StringComparer.OrdinalIgnoreCase);
        _schemaIndex = new Dictionary<string, int>(columns.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (index, schemaIndex, name) in columns)
        {
            _nameIndex[name] = index;
            _schemaIndex[name] = schemaIndex;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnLookup"/> class with
    /// the specified column names and name index.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    /// <param name="nameIndex">Case-insensitive name-to-ordinal map.</param>
    public ColumnLookup(string[] columnNames, Dictionary<string, int> nameIndex)
    {
        ColumnNames = columnNames;
        Count = columnNames.Length;
        _nameIndex = nameIndex;
        _schemaIndex = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnLookup"/> class with
    /// the specified column names and name index.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    public ColumnLookup(string[] columnNames)
    {
        ColumnNames = columnNames;
        Count = columnNames.Length;
        _nameIndex = new Dictionary<string, int>(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        _schemaIndex = null;
        for (int index = 0; index < columnNames.Length; index++)
        {
            _nameIndex[columnNames[index]] = index;
        }
    }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnLookup"/> class with
    /// the specified column names and name index.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    public ColumnLookup(IReadOnlyList<string> columnNames)
        : this([..columnNames]) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnLookup"/> class with
    /// the specified column names and name index.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    public ColumnLookup(IReadOnlyList<ColumnInfo> columnNames)
    {
        ColumnNames = columnNames.Select(c => c.Name).ToArray();
        Count = ColumnNames.Count;
        _nameIndex = new Dictionary<string, int>(columnNames.Count, StringComparer.OrdinalIgnoreCase);
        _schemaIndex = new Dictionary<string, int>(columnNames.Count, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < columnNames.Count; index++)
        {
            string name = columnNames[index].Name;
            _nameIndex[name] = index;
            _schemaIndex[name] = index;
        }
    }

    /// <summary>
    /// Gets the ordered column names.
    /// </summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    /// Gets the case-insensitive name-to-ordinal map for column lookup.
    /// </summary>
    public IReadOnlyDictionary<string, int> NameIndex => _nameIndex;

    /// <summary>
    /// Gets the number of columns in the lookup.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the column name at the specified ordinal.
    /// </summary>
    /// <param name="column">The zero-based column index.</param>
    /// <returns>The column name.</returns>
    public string this[int column] => ColumnNames[column];

    /// <summary>
    /// Gets the zero-based column index for the specified column name.
    /// </summary>
    /// <param name="columnName">The name of the column.</param>
    /// <returns>The zero-based column index.</returns>
    /// <exception cref="ArgumentException">Thrown if the column does not exist.</exception>
    public int GetColumnIndex(string columnName)
    {
        if (!_nameIndex.TryGetValue(columnName, out int index))
        {
            throw new ArgumentException($"Column '{columnName}' does not exist in the batch.");
        }
        return index;
    }

    /// <summary>
    /// Gets the schema column index for the specified column name. This is the position within the original
    /// schema, which may differ from the column ordinal if the columns are projected.
    /// </summary>
    /// <param name="columnName">The name of the column.</param>
    /// <returns>The zero-based schema column index.</returns>
    /// <exception cref="ArgumentException">Thrown if the column does not exist.</exception>
    public int GetSchemaColumnIndex(string columnName)
    {
        if (_schemaIndex is null)
        {
            throw new InvalidOperationException(
                "This ColumnLookup was created without schema information. " +
                "Use the tuple-based constructor if you need schema ordinals.");
        }
        
        if (_schemaIndex.TryGetValue(columnName, out int index))
        {
            return index;
        }

        throw new ArgumentException($"Column '{columnName}' does not exist in the batch.");
    }

    /// <summary>
    /// Gets the schema column index for the specified column ordinal. This is the position within the original
    /// schema, which may differ from the column ordinal if the columns are projected.
    /// </summary>
    /// <param name="column">The zero-based column index.</param>
    /// <returns>The zero-based schema column index.</returns>
    public int GetSchemaColumnIndex(int column)
    {
        if (_schemaIndex is null)
        {
            throw new InvalidOperationException(
                "This ColumnLookup was created without schema information. " +
                "Use the tuple-based constructor if you need schema ordinals.");
        }

        if (column < 0 || column >= ColumnNames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, $"Column index must be between 0 and {ColumnNames.Count - 1}.");
        }

        string columnName = ColumnNames[column];

        return _schemaIndex[columnName];
    }

    /// <summary>
    /// Tries to get the schema column index for the specified column ordinal. This is the position within the original
    /// schema, which may differ from the column ordinal if the columns are projected.
    /// </summary>
    /// <param name="column">The zero-based column index.</param>
    /// <param name="schemaIndex">The zero-based schema column index if found.</param>
    /// <returns><c>true</c> if the column exists; otherwise, <c>false</c>.</returns>
    public bool TryGetSchemaColumnIndex(int column, [NotNullWhen(true)] out int? schemaIndex)
    {
        if (_schemaIndex is null)
        {
            schemaIndex = null;
            return false;
        }

        if (column < 0 || column >= ColumnNames.Count)
        {
            schemaIndex = null;
            return false;
        }

        string columnName = ColumnNames[column];

        if (_schemaIndex.TryGetValue(columnName, out int index))
        {
            schemaIndex = index;
            return true;
        }

        schemaIndex = null;
        return false;
    }

    /// <summary>
    /// Tries to get the schema column index for the specified column name. This is the position within the original
    /// schema, which may differ from the column ordinal if the columns are projected.
    /// </summary>
    /// <param name="columnName">The name of the column.</param>
    /// <param name="schemaIndex">The zero-based schema column index if found.</param>
    /// <returns><c>true</c> if the column exists; otherwise, <c>false</c>.</returns>
    public bool TryGetSchemaColumnIndex(string columnName, [NotNullWhen(true)] out int? schemaIndex)
    {
        if (_schemaIndex is null)
        {
            schemaIndex = null;
            return false;
        }

        if (_schemaIndex.TryGetValue(columnName, out int index))
        {
            schemaIndex = index;
            return true;
        }

        schemaIndex = null;
        return false;
    }

    /// <summary>
    /// Indicates whether this <see cref="ColumnLookup"/> contains schema index information.
    /// If <c>true</c>, the schema column index can be retrieved for each column; otherwise, only the batch column index is available.
    /// </summary>
    public bool HasSchemaIndices => _schemaIndex != null;

    /// <summary>
    /// Returns the column name at the specified ordinal.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The column name.</returns>
    public string GetColumnName(int column)
        => ColumnNames[column];

    /// <summary>
    /// Resolves a column name to its zero-based ordinal.
    /// </summary>
    /// <param name="name">Column name (case-insensitive).</param>
    /// <param name="ordinal">The column ordinal if found.</param>
    /// <returns><c>true</c> if the column exists.</returns>
    public bool TryGetColumnOrdinal(string name, out int ordinal)
        => _nameIndex.TryGetValue(name, out ordinal);

    /// <summary>
    /// Determines whether the batch contains a column with the specified name.
    /// </summary>
    /// <param name="columnName">The name of the column.</param>
    /// <returns><c>true</c> if the column exists; otherwise, <c>false</c>.</returns>
    public bool HasColumn(string columnName) => _nameIndex.ContainsKey(columnName);
}