using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.DatumFile;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.DatumFile.V2.Decoding;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.BTree.Mutable;
using Heliosoph.DatumV.Indexing.Fts;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Statistics;

namespace Heliosoph.DatumV.Catalog.Providers;

public sealed partial class DatumFileTableProviderV2
{
    // ──────────────────── Mutation (catalog-level ALTER TABLE) ────────────────────

    /// <inheritdoc/>
    public bool CanAlterColumns => true;

    /// <inheritdoc/>
    public bool CanAppendRows => true;

    /// <inheritdoc/>
    public bool CanDeleteRows => true;

    /// <inheritdoc/>
    public bool CanUpdateRows => true;

    /// <inheritdoc/>
    public void AddColumn(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!column.Nullable)
        {
            throw new ArgumentException(
                $"AddColumn requires column '{column.Name}' to be nullable: existing rows are " +
                "back-filled with nulls and a non-nullable column would violate the schema.",
                nameof(column));
        }

        ColumnDescriptorV2 descriptor = new(
            Name: column.Name,
            Kind: column.Kind,
            Encoder: ColumnDescriptorV2.EncoderFor(column.Kind, column.IsArray),
            IsNullable: column.Nullable,
            IsArray: column.IsArray);

        // Translate the column's optional DEFAULT / GENERATED ALWAYS AS
        // expressions into SQL fragments the writer persists in the
        // prologue's defaults table / footer's computed-columns block.
        // Mutual exclusion is enforced upstream by the catalog.
        string? defaultFragment = column.DefaultExpression is null
            ? null
            : Execution.QueryExplainer.FormatExpression(column.DefaultExpression);
        string? computedFragment = column.ComputedExpression is null
            ? null
            : Execution.QueryExplainer.FormatExpression(column.ComputedExpression);

        // IDENTITY: the writer needs the new column's footer index, which
        // it computes itself (newColumnCount - 1 after the resize). We
        // pass a placeholder ColumnIndex; the writer overwrites it from
        // its own state at pump time.
        IdentityWriterSpec? identitySpec = column.Identity is null
            ? null
            : new IdentityWriterSpec(
                ColumnIndex: -1,  // writer assigns the real footer index
                Seed: column.Identity.Seed,
                Step: column.Identity.Step,
                AcceptUserValues: column.Identity.AcceptUserValues);

        _mutationLock.Wait();
        try
        {
            DatumFileWriterV2.AddColumn(_descriptor.FilePath, descriptor, defaultFragment, computedFragment, identitySpec);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DropColumnIdentityAsync(int columnIndex, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Schema schema = _snapshot.Schema;
            if (columnIndex < 0 || columnIndex >= schema.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex),
                    $"Column index {columnIndex} is out of range for schema with {schema.Columns.Count} columns.");
            }

            DatumFileWriterV2.ClearIdentity(_descriptor.FilePath);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SetColumnNotNullAsync(int columnIndex, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Schema schema = _snapshot.Schema;
            if (columnIndex < 0 || columnIndex >= schema.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex),
                    $"Column index {columnIndex} is out of range for schema with {schema.Columns.Count} columns.");
            }
            string columnName = schema.Columns[columnIndex].Name;

            // Idempotent when already NOT NULL (matches PG). The descriptor
            // flip + finalize would also be a no-op in the writer, but
            // returning here avoids the unnecessary scan AND the
            // generation bump.
            if (!schema.Columns[columnIndex].Nullable)
            {
                return;
            }

            // Validate: scan the column, reject if any row holds NULL.
            // Single sequential pass — same scan path as a query, so the
            // cost is one full column read. The mutation lock blocks
            // concurrent INSERTs for the duration of the scan, so the
            // result is a consistent snapshot.
            HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase) { columnName };
            await foreach (RowBatch batch in ScanAsync(
                requiredColumns: requiredColumns,
                filterHint: null,
                targetArena: null,
                cancellationToken: ct).ConfigureAwait(false))
            {
                try
                {
                    // The scan may project a different column ordering;
                    // resolve the index from the batch's lookup.
                    int localIndex = batch.ColumnLookup.GetColumnIndex(columnName);
                    if (localIndex < 0) continue;

                    for (int r = 0; r < batch.Count; r++)
                    {
                        if (batch[r][localIndex].IsNull)
                        {
                            throw new InvalidOperationException(
                                $"column \"{columnName}\" contains NULL values");
                        }
                    }
                }
                finally
                {
                    batch.Dispose();
                }
            }

            DatumFileWriterV2.SetColumnNotNull(_descriptor.FilePath, columnName);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DropColumnNotNullAsync(int columnIndex, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Schema schema = _snapshot.Schema;
            if (columnIndex < 0 || columnIndex >= schema.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex),
                    $"Column index {columnIndex} is out of range for schema with {schema.Columns.Count} columns.");
            }
            string columnName = schema.Columns[columnIndex].Name;

            DatumFileWriterV2.ClearColumnNotNull(_descriptor.FilePath, columnName);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DropColumnDefaultAsync(int columnIndex, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Schema schema = _snapshot.Schema;
            if (columnIndex < 0 || columnIndex >= schema.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex),
                    $"Column index {columnIndex} is out of range for schema with {schema.Columns.Count} columns.");
            }
            string columnName = schema.Columns[columnIndex].Name;

            DatumFileWriterV2.ClearColumnDefault(_descriptor.FilePath, columnName);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DisablePrimaryKeyAsync(CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // No-op shortcut — caller (the catalog) already handles the
            // "no PK to drop" error; this is defense in depth.
            if (_snapshot.Schema.PrimaryKeyColumnIndices.Count == 0)
            {
                return;
            }

            // Close + delete the sidecar FIRST so a crash between this and
            // the footer flip leaves the table without an orphan PK index
            // pointing at stale (chunk, row) entries. Subsequent footer
            // commits with the new (empty) PrimaryKeyColumnIndices then
            // also won't try to re-open the missing sidecar.
            _pkIndexBytes?.Dispose();
            _pkIndexBytes = null;
            _pkColumnIndices = Array.Empty<int>();

            string pkIndexPath = GetPrimaryKeyIndexPath(_descriptor.FilePath);
            if (File.Exists(pkIndexPath))
            {
                try { File.Delete(pkIndexPath); } catch { /* best-effort */ }
            }

            DatumFileWriterV2.ClearPrimaryKey(_descriptor.FilePath);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void DropColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        _mutationLock.Wait();
        try
        {
            DatumFileWriterV2.DropColumn(_descriptor.FilePath, columnName);
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Promotes <paramref name="columnIndex"/> to be the table's PRIMARY KEY.
    /// Builds the <c>.datum-pkindex</c> sidecar from a scan of existing rows,
    /// then flips the footer's <c>PrimaryKeyColumnIndices</c> to reference
    /// the column. On any failure (NULL in the column, duplicate value, IO
    /// error), the partial sidecar is deleted and the footer is left
    /// untouched.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="TableCatalog"/> as the second half of
    /// <c>ALTER TABLE … ADD COLUMN … PRIMARY KEY</c> — provider.AddColumn
    /// runs first (with IDENTITY backfill if specified) so the column is
    /// populated by the time this scan starts.
    /// </remarks>
    /// <inheritdoc/>
    public async Task EnablePrimaryKeyAsync(int columnIndex, CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Validate against the live snapshot.
            Schema schema = _snapshot.Schema;
            if (schema.PrimaryKeyColumnIndices.Count > 0)
            {
                throw new InvalidOperationException(
                    "EnablePrimaryKey: table already has a PRIMARY KEY. " +
                    "Only one PK per table is supported.");
            }
            if (columnIndex < 0 || columnIndex >= schema.Columns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(columnIndex),
                    $"Column index {columnIndex} is out of range for schema with {schema.Columns.Count} columns.");
            }
            string columnName = schema.Columns[columnIndex].Name;

            // Defensive cleanup — a stray sidecar from a prior aborted attempt
            // would corrupt this build's results.
            string pkIndexPath = GetPrimaryKeyIndexPath(_descriptor.FilePath);
            if (File.Exists(pkIndexPath))
            {
                File.Delete(pkIndexPath);
            }

            // Build the sidecar. We intentionally let the underlying tree's
            // uniqueness enforcement do the duplicate detection: Insert
            // throws DuplicateKeyException on a collision, which we translate
            // into a user-facing PrimaryKeyViolationException. On any failure
            // the file is deleted so a retry starts from a clean slate.
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree =
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(pkIndexPath, allowDuplicates: false);
            bool indexFinalized = false;
            try
            {
                await foreach (RowBatch batch in ScanAsync(
                    requiredColumns: null,
                    filterHint: null,
                    targetArena: null,
                    cancellationToken: ct).ConfigureAwait(false))
                {
                    try
                    {
                        for (int r = 0; r < batch.Count; r++)
                        {
                            Row row = batch[r];
                            DataValue value = row[columnIndex];
                            if (value.IsNull)
                            {
                                throw new Execution.PrimaryKeyViolationException(
                                    $"PRIMARY KEY column '{columnName}' has a NULL in an existing row; " +
                                    "every row must have a non-null value before the column can be PK.");
                            }

                            byte[] encoded = Indexing.CompositeKeyEncoder.Encode(
                                new[] { value }, batch.Arena);
                            try
                            {
                                tree.Insert(new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                    encoded, ChunkIndex: 0, RowOffsetInChunk: 0L));
                            }
                            catch (Indexing.BTree.MutableBytes.DuplicateKeyException)
                            {
                                throw new Execution.PrimaryKeyViolationException(
                                    $"PRIMARY KEY violation on column '{columnName}': duplicate value " +
                                    "in existing rows. The PK index cannot be built without unique values.");
                            }
                        }
                    }
                    finally
                    {
                        batch.Dispose();
                    }
                }
                indexFinalized = true;
            }
            finally
            {
                tree.Dispose();
                if (!indexFinalized && File.Exists(pkIndexPath))
                {
                    try { File.Delete(pkIndexPath); } catch { /* best-effort cleanup */ }
                }
            }

            // Sidecar built and closed — flip the footer to commit. If this
            // throws, the sidecar is orphaned but harmless (the footer still
            // says "no PK", so TryOpenPrimaryKeyIndex won't load it on the
            // next reopen). Delete it anyway so a retry starts clean.
            try
            {
                DatumFileWriterV2.SetPrimaryKey(_descriptor.FilePath, new ushort[] { (ushort)columnIndex });
            }
            catch
            {
                if (File.Exists(pkIndexPath))
                {
                    try { File.Delete(pkIndexPath); } catch { /* best-effort cleanup */ }
                }
                throw;
            }

            // Re-snapshot and re-open the sidecar handle so the in-memory
            // provider state matches the freshly-committed footer.
            RebuildSnapshotAfterMutation(sidecarMayHaveGrown: false);
            _pkIndexBytes?.Dispose();
            _pkIndexBytes = null;
            _pkColumnIndices = Array.Empty<int>();
            TryOpenPrimaryKeyIndex();
            InvalidateSourceIndexCache();
        }
        finally
        {
            _mutationLock.Release();
        }
    }
}
