using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

/// <summary>
/// Interface for data source providers that stream rows from a backing store
/// </summary>
public interface ITableProvider : IDisposable
{
    /// <summary>
    /// Gets the logical table name from the descriptor for use in SQL queries.
    /// </summary>
    string Name { get;}

    /// <summary>
    /// Returns true if the provider supports seeking to specific row positions via
    /// <see cref="OpenSeekSession"/>, enabling operators like <c>ScanOperator</c> to
    /// perform index seeks for equality predicates.
    /// </summary>
    bool Seekable { get; }

    /// <summary>
    /// Returns the row count for the table.
    /// </summary>
    /// <returns>The row count for the table.</returns>
    long GetRowCount();

    /// <summary>
    /// Returns the schema of the table.
    /// </summary>
    /// <returns>The inferred or declared schema.</returns>
    Schema GetSchema();

    /// <summary>
    /// Returns metadata about the table's contents, such as column-level statistics, if available.
    /// </summary>
    /// <returns>The query results manifest, or <c>null</c> if not available.</returns>
    Manifest.QueryResultsManifest? GetManifest();

    /// <summary>
    /// If supported by the provider, returns an index that can be used for metadata-based pruning
    /// (e.g. zone maps) and/or index seeks. The index is advisory only
    /// </summary>
    /// <returns>The source index, or <c>null</c> if not available.</returns>
    Indexing.SourceIndex? GetSourceIndex();

    /// <summary>
    /// Streams all rows from the table, optionally applying an advisory filter hint for
    /// statistics-based partition pruning. The caller is responsible for applying the
    /// filter for correctness — the stream may still contain non-matching rows.
    /// </summary>
    /// <param name="requiredColumns">
    /// Columns needed downstream. When <c>null</c>, all columns are returned. Providers
    /// that support projection pushdown may skip producing columns outside this set.
    /// </param>
    /// <param name="filterHint">
    /// Optional predicate used for zone-map / chunk-level pruning. May be <c>null</c>.
    /// Must not be used to suppress individual rows — the caller applies the filter for
    /// correctness.
    /// </param>
    /// <param name="targetArena">
    /// Optional arena that emitted batches must be bound to. When supplied, the provider
    /// rents every output <see cref="RowBatch"/> against this arena (instead of a fresh
    /// per-batch arena), so all non-inline values land in a single per-query store.
    /// Required by the one-arena-per-query model — query callers pass
    /// <c>ExecutionContext.Store</c>; ingest-time / standalone callers may pass
    /// <c>null</c> to keep the legacy per-batch-arena behaviour.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of row batches from the data source.</returns>
    IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a caller-owned session for repeated seeks into this table. The session holds
    /// the reader, decode buffers, and projection metadata for its lifetime — dispose it
    /// when done to release those resources. Requires <see cref="Seekable"/> to be
    /// <c>true</c>.
    /// </summary>
    /// <param name="requiredColumns">
    /// Columns needed downstream. When <c>null</c>, all columns are returned. Cannot be
    /// changed after the session is opened — open a new session for a different projection.
    /// </param>
    /// <param name="targetArena">
    /// Optional arena that batches yielded by the session must be bound to. Same
    /// semantics as the <c>targetArena</c> parameter on <see cref="ScanAsync"/>:
    /// query callers pass <c>ExecutionContext.Store</c>; ingest-time / standalone
    /// callers may pass <c>null</c>.
    /// </param>
    /// <returns>A seek session bound to the required columns and target arena.</returns>
    ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns, Arena? targetArena = null);
}
