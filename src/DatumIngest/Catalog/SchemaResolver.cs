namespace DatumIngest.Catalog;

/// <summary>
/// Resolves SQL table references — <c>schema.table</c> or unqualified
/// <c>table</c> — into a canonical <see cref="QualifiedName"/> against a
/// <see cref="TableCatalog"/> and a captured <c>search_path</c>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution rules:
/// <list type="bullet">
///   <item><description><b>Explicit schema</b>: exact lookup against the
///     named schema; failure is reported as either "schema does not
///     exist" or "table does not exist in schema".</description></item>
///   <item><description><b>Unqualified</b>: walk the captured
///     <c>search_path</c> in order. First hit wins. Failure lists every
///     schema attempted so the user sees what was searched.</description></item>
/// </list>
/// </para>
/// <para>
/// The <c>search_path</c> snapshot is captured at construction so
/// concurrent <c>SET search_path = …</c> mutations don't affect an
/// in-flight resolver. Each new query plan builds a fresh resolver
/// from the catalog's then-current path.
/// </para>
/// </remarks>
public sealed class SchemaResolver
{
    private readonly TableCatalog _catalog;
    private readonly IReadOnlyList<string> _searchPath;

    /// <summary>
    /// Creates a resolver bound to <paramref name="catalog"/> and the
    /// supplied <paramref name="searchPath"/> snapshot. The caller is
    /// responsible for passing the catalog's current
    /// <see cref="TableCatalog.SearchPath"/>; the resolver does not
    /// re-read it.
    /// </summary>
    public SchemaResolver(TableCatalog catalog, IReadOnlyList<string> searchPath)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _searchPath = searchPath ?? throw new ArgumentNullException(nameof(searchPath));
    }

    /// <summary>The snapshot of <c>search_path</c> captured at construction.</summary>
    public IReadOnlyList<string> SearchPath => _searchPath;

    /// <summary>
    /// Resolves a (possibly-qualified) table reference to its canonical
    /// <see cref="QualifiedName"/>. Throws
    /// <see cref="SchemaResolutionException"/> when the table can't be
    /// found.
    /// </summary>
    /// <param name="explicitSchema">
    /// The schema qualifier from <c>schema.table</c>, or
    /// <see langword="null"/> for an unqualified reference.
    /// </param>
    /// <param name="tableName">The unqualified table name.</param>
    public QualifiedName Resolve(string? explicitSchema, string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        if (explicitSchema is not null)
        {
            QualifiedName qn = new(explicitSchema, tableName);
            if (_catalog.TryGetTable(qn, out _))
            {
                return qn;
            }

            // Distinguish "schema doesn't exist" from "table not in schema"
            // — both are user errors but with different remedies.
            if (!_catalog.TryFindBackend(explicitSchema, out _))
            {
                throw new SchemaResolutionException(
                    $"Schema '{explicitSchema}' does not exist.",
                    explicitSchema, tableName, _searchPath);
            }
            throw new SchemaResolutionException(
                $"Table '{tableName}' does not exist in schema '{explicitSchema}'.",
                explicitSchema, tableName, _searchPath);
        }

        foreach (string schema in _searchPath)
        {
            QualifiedName candidate = new(schema, tableName);
            if (_catalog.TryGetTable(candidate, out _))
            {
                return candidate;
            }
        }

        string pathDisplay = _searchPath.Count == 0
            ? "(empty search_path)"
            : "[" + string.Join(", ", _searchPath) + "]";
        throw new SchemaResolutionException(
            $"Table '{tableName}' not found in any schema on search_path {pathDisplay}.",
            ExplicitSchema: null, tableName, _searchPath);
    }

    /// <summary>
    /// Non-throwing variant of <see cref="Resolve"/>. Returns
    /// <see langword="true"/> when the table is found and yields the
    /// resolved <see cref="QualifiedName"/>; returns <see langword="false"/>
    /// when the table doesn't exist anywhere on the search_path (the
    /// out parameter still receives a "best guess" name — the explicit
    /// schema if supplied, or the first search_path entry otherwise —
    /// so callers building error messages can show what was attempted).
    /// </summary>
    /// <remarks>
    /// Used by DDL appliers that support <c>IF EXISTS</c> — they need
    /// to know "did this table exist?" without paying the cost of an
    /// exception when it didn't.
    /// </remarks>
    public bool TryResolve(string? explicitSchema, string tableName, out QualifiedName resolved)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        if (explicitSchema is not null)
        {
            resolved = new QualifiedName(explicitSchema, tableName);
            return _catalog.TryGetTable(resolved, out _);
        }

        foreach (string schema in _searchPath)
        {
            QualifiedName candidate = new(schema, tableName);
            if (_catalog.TryGetTable(candidate, out _))
            {
                resolved = candidate;
                return true;
            }
        }

        // Not found anywhere. Return (first_search_path_schema, name) as
        // the "best guess" so error paths have something to print.
        resolved = new QualifiedName(
            _searchPath.Count > 0 ? _searchPath[0] : "public",
            tableName);
        return false;
    }

    /// <summary>
    /// Resolves the target of a <c>CREATE TABLE</c> statement. Explicit
    /// schemas land in that schema (if it accepts DDL); unqualified
    /// names land in the first DDL-capable schema on
    /// <c>search_path</c>.
    /// </summary>
    /// <exception cref="SchemaResolutionException">
    /// The explicit schema is read-only (e.g. <c>system</c>), or no
    /// schema on the search_path supports DDL.
    /// </exception>
    public QualifiedName ResolveForCreate(string? explicitSchema, string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);

        if (explicitSchema is not null)
        {
            if (!_catalog.TryFindBackend(explicitSchema, out ITableCatalog? backend))
            {
                throw new SchemaResolutionException(
                    $"Schema '{explicitSchema}' does not exist.",
                    explicitSchema, tableName, _searchPath);
            }
            if (!backend.SupportsDdl)
            {
                throw new SchemaResolutionException(
                    $"Schema '{explicitSchema}' is read-only; CREATE TABLE is not supported there.",
                    explicitSchema, tableName, _searchPath);
            }
            return new QualifiedName(explicitSchema, tableName);
        }

        foreach (string schema in _searchPath)
        {
            if (_catalog.TryFindBackend(schema, out ITableCatalog? backend) && backend.SupportsDdl)
            {
                return new QualifiedName(schema, tableName);
            }
        }

        string pathDisplay = _searchPath.Count == 0
            ? "(empty search_path)"
            : "[" + string.Join(", ", _searchPath) + "]";
        throw new SchemaResolutionException(
            $"No DDL-capable schema on search_path {pathDisplay}; cannot CREATE TABLE without an explicit schema.",
            ExplicitSchema: null, tableName, _searchPath);
    }
}

/// <summary>
/// Thrown by <see cref="SchemaResolver"/> when a table reference can't
/// be resolved. Carries enough context (the parsed schema/table and
/// the search_path snapshot) for callers to render helpful diagnostics
/// without re-walking the catalog. Derives from
/// <see cref="InvalidOperationException"/> so callers that just want a
/// generic "this query can't succeed" catch handler don't have to know
/// the schema-resolution type.
/// </summary>
public sealed class SchemaResolutionException : InvalidOperationException
{
    /// <summary>The explicit schema from <c>schema.table</c>, or <see langword="null"/> when unqualified.</summary>
    public string? ExplicitSchema { get; }

    /// <summary>The unqualified table name.</summary>
    public string TableName { get; }

    /// <summary>The search_path that was walked (snapshot at resolve time).</summary>
    public IReadOnlyList<string> SearchPath { get; }

    /// <inheritdoc/>
    public SchemaResolutionException(
        string message,
        string? ExplicitSchema,
        string TableName,
        IReadOnlyList<string> SearchPath) : base(message)
    {
        this.ExplicitSchema = ExplicitSchema;
        this.TableName = TableName;
        this.SearchPath = SearchPath;
    }
}
