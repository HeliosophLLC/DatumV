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
    /// Returns a per-column acceleration index (B+Tree-backed) for
    /// <paramref name="columnName"/>, when the provider maintains one.
    /// PR13d migrates B+Tree / sorted-index lookup off
    /// <see cref="Indexing.SourceIndex"/> (which lives inside the
    /// whole-file-COW <c>.datum-index</c> sidecar) and onto per-column
    /// <c>.datum-bptree-{col}</c> page-COW files owned by the provider —
    /// callers that previously called <c>SourceIndex.TryGetColumnIndex</c>
    /// should call this instead so the lookup picks up the right backing
    /// store regardless of whether trees still live in the unified
    /// sidecar (transition state) or alongside it (final state).
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The column index, or <c>null</c> if no index exists for this column.</param>
    /// <returns><c>true</c> if an index exists for the specified column.</returns>
    /// <remarks>
    /// Default implementation returns <c>false</c>. Providers that own
    /// per-column B+Tree files (the persistent <c>.datum</c> provider
    /// after PR13d) override and resolve through their per-column tree
    /// dictionary.
    /// </remarks>
    bool TryGetColumnIndex(string columnName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Indexing.IColumnIndex? index)
    {
        index = null;
        return false;
    }

    /// <summary>
    /// Returns the full-text search index for <paramref name="columnName"/>
    /// if the provider maintains one (CREATE INDEX ... USING FTS). The
    /// planner consults this when matching <c>col @@ tsquery</c> predicates
    /// against an indexed column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The FTS index, or <see langword="null"/> if none exists.</param>
    /// <returns><see langword="true"/> if an FTS index exists for the column.</returns>
    /// <remarks>
    /// Default implementation returns <see langword="false"/>. Providers
    /// that own <c>.datum-fts-{col}</c> sidecars override and resolve
    /// through their per-column FTS dictionary. The catalog-level
    /// descriptor wiring that drives discovery lands in PR-FTS-A3; A2
    /// ships the interface contract and the storage primitive.
    /// </remarks>
    bool TryGetTextSearchIndex(string columnName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Indexing.Fts.ITextSearchIndex? index)
    {
        index = null;
        return false;
    }

    /// <summary>
    /// Returns the user-defined composite indexes (created via
    /// <c>CREATE INDEX</c>) registered on this provider. The query planner
    /// consults this list when matching AND-chained equality predicates
    /// against composite-index column tuples.
    /// </summary>
    /// <remarks>
    /// Default implementation returns an empty list. Providers that
    /// maintain <c>.datum-cindex-*</c> sidecars (the persistent
    /// <c>.datum</c> provider) override and return adapters around the
    /// open trees.
    /// </remarks>
    IReadOnlyList<Indexing.ICompositeIndex> GetCompositeIndexes()
    {
        return Array.Empty<Indexing.ICompositeIndex>();
    }

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
    /// <param name="typeIdTranslations">
    /// Optional per-query translator from on-disk struct type-ids to the calling
    /// query's <see cref="Model.TypeRegistry"/> ids. Format-aware providers
    /// (<see cref="Providers.IDatumFileTableProvider"/>) thread this through the
    /// page-decoder open so per-row Struct values stamp the right runtime id
    /// without needing a per-provider mutable cache. Other providers should
    /// ignore the parameter.
    /// </param>
    /// <returns>An async enumerable of row batches from the data source.</returns>
    IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        CancellationToken cancellationToken,
        Model.TypeIdTranslationTable? typeIdTranslations = null);

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

    // ──────────────────── Mutation (catalog-level ALTER TABLE) ────────────────────

    /// <summary>
    /// True when this provider supports schema mutations via
    /// <see cref="AddColumn"/> and <see cref="DropColumn"/>. Default
    /// <see langword="false"/>; mutable providers (e.g. .datum file,
    /// in-memory) override to <see langword="true"/>.
    /// </summary>
    bool CanAlterColumns => false;

    /// <summary>
    /// True when this provider supports row appends via
    /// <see cref="AppendRowsAsync"/>. Default <see langword="false"/>.
    /// </summary>
    bool CanAppendRows => false;

    /// <summary>
    /// True when this provider supports row deletes via
    /// <see cref="DeleteRows"/>. Default <see langword="false"/>.
    /// </summary>
    bool CanDeleteRows => false;

    /// <summary>
    /// True when this provider supports per-cell row updates via
    /// <see cref="UpdateRowsAsync"/>. Default <see langword="false"/>;
    /// providers that opt in (.datum file via page-COW rewrite, in-memory)
    /// override.
    /// </summary>
    bool CanUpdateRows => false;

    /// <summary>
    /// Adds a new column to the table. The new column is populated with
    /// nulls for every existing row and so must be nullable.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/> —
    /// override on providers that opt in via <see cref="CanAlterColumns"/>.
    /// Schema changes are not visible to scans / seek-sessions that were
    /// already opened against this provider; close and reopen to observe
    /// the new column.
    /// </remarks>
    void AddColumn(Model.ColumnInfo column) =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support AddColumn (CanAlterColumns is false).");

    /// <summary>
    /// Soft-drops a column from the table by name. The column block is
    /// retained in the underlying store for compaction-time reclamation,
    /// but is hidden from <see cref="GetSchema"/> and from subsequent
    /// scans.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// </remarks>
    void DropColumn(string columnName) =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support DropColumn (CanAlterColumns is false).");

    /// <summary>
    /// Promotes the column at <paramref name="columnIndex"/> to be the
    /// table's PRIMARY KEY. Implementations are expected to enforce
    /// uniqueness across existing rows (failing with a duplicate-key
    /// violation if not satisfied) and to persist the PK metadata so it
    /// survives a reopen.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/> —
    /// override on providers that opt in via <see cref="CanAlterColumns"/>.
    /// Called by the catalog as the second half of
    /// <c>ALTER TABLE … ADD COLUMN … PRIMARY KEY</c> after the column
    /// itself has been added and (optionally) backfilled.
    /// </remarks>
    Task EnablePrimaryKeyAsync(int columnIndex, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support EnablePrimaryKey (CanAlterColumns is false).");

    /// <summary>
    /// Opens a caller-owned <see cref="IAppendSession"/> for streaming
    /// inserts. The session holds a writer for its lifetime; rows
    /// become visible only after <see cref="IAppendSession.CommitAsync"/>.
    /// One session per provider at a time — concurrent callers wait
    /// for the active session to dispose.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// Mutable providers override.
    /// </remarks>
    IAppendSession BeginAppend() =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support BeginAppend (CanAppendRows is false).");

    /// <summary>
    /// Appends every <see cref="RowBatch"/> in <paramref name="batches"/>
    /// to the table in a single committed unit. Convenience wrapper over
    /// <see cref="BeginAppend"/> — opens a session, drains the
    /// enumerable, and commits.
    /// </summary>
    /// <remarks>
    /// Provider implementations should rely on this default implementation
    /// rather than re-implementing the drain-and-commit loop themselves.
    /// </remarks>
    async Task AppendRowsAsync(IAsyncEnumerable<RowBatch> batches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batches);
        await using IAppendSession session = BeginAppend();
        await foreach (RowBatch batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await session.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-deletes the rows at the given linear (zero-based) row
    /// indices. Subsequent scans skip these rows; storage reclamation
    /// happens at compaction.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// Indices are linear over the live row sequence (post-tombstone
    /// from previous deletes) — the same numbering a fresh
    /// <c>SELECT * FROM table</c> would yield.
    /// </remarks>
    void DeleteRows(IReadOnlyList<long> rowIndices) =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support DeleteRows (CanDeleteRows is false).");

    /// <summary>
    /// Replaces specific cell values in specific rows. Each
    /// <see cref="RowUpdateRequest"/> names a row by its 0-based linear
    /// index over the live row sequence (post-tombstone — the same
    /// numbering a fresh <c>SELECT * FROM table</c> would yield) and
    /// supplies a sparse map of column index → new value.
    /// </summary>
    /// <param name="requests">
    /// Update requests. Order is irrelevant; the provider may reorder
    /// internally (e.g. group by page for the .datum page-COW path).
    /// Empty list is a no-op.
    /// </param>
    /// <param name="sourceStore">
    /// Backing store for any non-inline <see cref="DataValue"/> in the
    /// per-row <see cref="RowUpdateRequest.NewValues"/> maps. Pass
    /// <see langword="null"/> when every supplied value is inline. The
    /// store must remain valid for the duration of the call.
    /// </param>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// Providers that maintain a writable backing store override and opt
    /// in via <see cref="CanUpdateRows"/>.
    /// </remarks>
    Task UpdateRowsAsync(IReadOnlyList<RowUpdateRequest> requests, IValueStore? sourceStore = null) =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support UpdateRowsAsync (CanUpdateRows is false).");

    /// <summary>
    /// True when this provider supports rebuilding its <c>.datum-index</c>
    /// acceleration sidecar via <see cref="RebuildIndexAsync"/>. Default
    /// <see langword="false"/>; only the persistent <c>.datum</c> file
    /// provider opts in. In-memory tables have no index sidecar.
    /// </summary>
    bool CanRebuildIndex => false;

    /// <summary>
    /// Rebuilds the table's <c>.datum-index</c> sidecar from the current
    /// data file. Replaces any previously cached or stale index — after
    /// the call returns, <see cref="GetSourceIndex"/> reflects the
    /// freshly-built file with a fingerprint that matches the live
    /// <c>.datum</c> contents.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>;
    /// providers that opt in via <see cref="CanRebuildIndex"/> override.
    /// </remarks>
    Task RebuildIndexAsync() =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support RebuildIndexAsync (CanRebuildIndex is false).");

    /// <summary>
    /// True when this provider supports refreshing the cached half of its
    /// <c>.datum-manifest</c> sidecar (top-K, quantiles, histogram, entropy,
    /// kind-specific summaries) via <see cref="RebuildManifestAsync"/>. Default
    /// <see langword="false"/>; only the persistent <c>.datum</c> file
    /// provider opts in.
    /// </summary>
    bool CanRebuildManifest => false;

    /// <summary>
    /// Refreshes the cached half of the <c>.datum-manifest</c> sidecar by
    /// scanning the current data and rebuilding all per-column statistics.
    /// After the call returns, <see cref="GetManifest"/> reflects the
    /// fresh cached values composed with the live overlay, and the
    /// <see cref="Manifest.FeatureManifest.CachedStatsValid"/> flag is
    /// <see langword="true"/> for every column.
    /// </summary>
    /// <remarks>
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// Providers override and opt in via <see cref="CanRebuildManifest"/>.
    /// </remarks>
    Task RebuildManifestAsync() =>
        throw new NotSupportedException(
            $"Table '{Name}' does not support RebuildManifestAsync (CanRebuildManifest is false).");

    /// <summary>
    /// Returns the current validity state of this table's
    /// <c>.datum-index</c> sidecar — surfaced to users via the
    /// <c>is_valid</c> column on <c>datum_catalog.indexes</c> so they
    /// can see when an index needs rebuilding without poking at the
    /// file system.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see cref="IndexValidity.Missing"/>:
    /// providers that don't carry an acceleration sidecar (in-memory
    /// tables, system / virtual schemas) will surface as having no
    /// index at all. The persistent <c>.datum</c> provider overrides
    /// to report <see cref="IndexValidity.Valid"/>,
    /// <see cref="IndexValidity.Stale"/>, or
    /// <see cref="IndexValidity.Missing"/> based on whether the file
    /// exists, the fingerprint matches, and any post-mutation
    /// invalidation has occurred.
    /// </remarks>
    IndexValidity GetIndexValidity() => IndexValidity.Missing;

    /// <summary>
    /// Returns an on-disk PRIMARY KEY lookup if the provider maintains one,
    /// or <see langword="null"/> if the executor should fall back to the
    /// scan-based PK uniqueness check (PR10f's HashSet path).
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see langword="null"/>. Providers that
    /// back single-column PRIMARY KEYs with the mutable B+Tree
    /// (<c>.datum-pkindex</c>) override to expose the lookup so
    /// <c>InsertExecutor</c> can probe in O(log table-size) instead of
    /// pre-loading every existing row into a <c>HashSet</c>. Composite PKs
    /// stay on the scan path until a follow-up extends the encoder.
    /// </remarks>
    IPrimaryKeyLookup? GetPrimaryKeyLookup() => null;
}
