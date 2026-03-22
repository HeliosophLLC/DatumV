using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Describes a single column resolved from a query's table sources,
/// enriched with the originating table name or alias for qualified access.
/// </summary>
/// <param name="ColumnName">The column name as it appears in the source schema.</param>
/// <param name="Kind">
/// The data kind of the column. For typed-array columns this is the
/// per-element kind; combine with <paramref name="IsArray"/>=true to
/// recognise the array shape.
/// </param>
/// <param name="Nullable">Whether the column may contain null values.</param>
/// <param name="SourceTableOrAlias">
/// The table name or alias this column originates from,
/// or <c>null</c> for computed columns without a clear source.
/// </param>
/// <param name="IsArray">
/// True when this column carries the <c>IsArray</c> flag — i.e. each value
/// is a typed array of <see cref="Kind"/> elements rather than a single
/// scalar.
/// </param>
/// <param name="IsMultiDim">
/// True when each value is a multi-dimensional typed array (carries an
/// explicit shape — <see cref="DataValue.IsMultiDim"/> on every row).
/// Propagates from source columns whose <see cref="ColumnInfo.FixedShape"/>
/// has ndim ≥ 2 and from projection expressions (e.g. <c>infer()</c>) that
/// produce multi-dim values dynamically. Used by downstream signature
/// dispatch so chained calls in subqueries see the correct shape.
/// </param>
public sealed record ResolvedColumn(
    string ColumnName,
    DataKind Kind,
    bool Nullable,
    string? SourceTableOrAlias,
    bool IsArray = false,
    bool IsMultiDim = false);

/// <summary>
/// The combined column schema resolved from all table sources in a query's
/// FROM and JOIN clauses. Supports both qualified (<c>alias.column</c>) and
/// unqualified (<c>column</c>) lookups for editor autocomplete scenarios.
/// </summary>
public sealed class ResolvedQuerySchema
{
    private readonly IReadOnlyList<ResolvedColumn> _columns;
    private readonly Dictionary<string, ResolvedColumn> _qualifiedIndex;
    private readonly Dictionary<string, ResolvedColumn> _unqualifiedIndex;

    /// <summary>
    /// Creates a resolved query schema from the given columns.
    /// </summary>
    /// <param name="columns">The complete list of resolved columns from all sources.</param>
    public ResolvedQuerySchema(IReadOnlyList<ResolvedColumn> columns)
    {
        _columns = columns;
        _qualifiedIndex = new Dictionary<string, ResolvedColumn>(StringComparer.OrdinalIgnoreCase);
        _unqualifiedIndex = new Dictionary<string, ResolvedColumn>(StringComparer.OrdinalIgnoreCase);

        foreach (ResolvedColumn column in columns)
        {
            // Register qualified name (e.g. "alias.column") if a source is known.
            if (column.SourceTableOrAlias is not null)
            {
                string qualifiedName = $"{column.SourceTableOrAlias}.{column.ColumnName}";
                _qualifiedIndex.TryAdd(qualifiedName, column);
            }

            // Register unqualified name. First occurrence wins when columns from
            // different tables share a name — callers should use qualified references
            // to disambiguate.
            _unqualifiedIndex.TryAdd(column.ColumnName, column);
        }
    }

    /// <summary>The complete ordered list of resolved columns.</summary>
    public IReadOnlyList<ResolvedColumn> Columns => _columns;

    /// <summary>
    /// Returns the distinct table names or aliases that contributed columns.
    /// </summary>
    public IEnumerable<string> TableNames =>
        _columns
            .Select(column => column.SourceTableOrAlias)
            .Where(source => source is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)!;

    /// <summary>
    /// Finds a column by name, supporting both qualified (<c>alias.column</c>)
    /// and unqualified (<c>column</c>) references with case-insensitive matching.
    /// </summary>
    /// <param name="name">The column name to look up.</param>
    /// <returns>The matching column, or <c>null</c> if not found.</returns>
    public ResolvedColumn? FindColumn(string name)
    {
        if (_qualifiedIndex.TryGetValue(name, out ResolvedColumn? qualified))
        {
            return qualified;
        }

        if (_unqualifiedIndex.TryGetValue(name, out ResolvedColumn? unqualified))
        {
            return unqualified;
        }

        return null;
    }

    /// <summary>
    /// Returns all columns originating from the specified table name or alias.
    /// </summary>
    /// <param name="tableNameOrAlias">The table name or alias to filter by.</param>
    /// <returns>The matching columns, which may be empty.</returns>
    public IReadOnlyList<ResolvedColumn> FindColumns(string tableNameOrAlias)
    {
        List<ResolvedColumn> result = new();

        foreach (ResolvedColumn column in _columns)
        {
            if (string.Equals(column.SourceTableOrAlias, tableNameOrAlias, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(column);
            }
        }

        return result;
    }
}
