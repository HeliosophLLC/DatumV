using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DatumIngest.DatumFile;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Indexing.Fts;
using DatumIngest.Ingestion;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Statistics;

namespace DatumIngest.Catalog.Providers;

public sealed partial class DatumFileTableProviderV2
{
    // ──────────────────── Full-text index lifecycle ────────────────────

    /// <summary>
    /// Open FTS indexes for this table, keyed by indexed column name.
    /// One FTS index per column in v1 (deferred-decisions #5 covers the
    /// multi-analyzer-per-column extension).
    /// </summary>
    private readonly Dictionary<string, FullTextSearchIndex> _ftsIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Index name → column name reverse lookup. Required because DROP INDEX
    /// names the index, but the open dict is keyed by column. Kept in
    /// sync with <see cref="_ftsIndexes"/>.
    /// </summary>
    private readonly Dictionary<string, string> _ftsIndexNameToColumn =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fast lock guarding <see cref="_ftsIndexes"/> and
    /// <see cref="_ftsIndexNameToColumn"/>. Separate from
    /// <see cref="_mutationLock"/>: writers hold mutation-lock for the
    /// whole CREATE/DROP (build can be slow) and only need this fast lock
    /// around the final dict swap; readers (TryGetTextSearchIndex) take
    /// only this lock.
    /// </summary>
    private readonly object _ftsIndexSync = new();

    /// <summary>
    /// Returns the <c>.datum-fts-{column}</c> path companion for the given
    /// data file + column. Column name sanitized so non-alphanumeric chars
    /// don't collide with the path separator.
    /// </summary>
    internal static string GetFtsIndexPath(string datumPath, string columnName)
    {
        string sanitized = SanitizeColumnNameForPath(columnName);
        return Path.ChangeExtension(datumPath, $".datum-fts-{sanitized}");
    }

    /// <summary>
    /// Discovers and opens every FTS sidecar declared in
    /// <see cref="TableDescriptor.Indexes"/> with <see cref="IndexKind.FullText"/>.
    /// Indexes whose declared analyzer isn't registered in the running
    /// build are skipped silently and degrade to scan-based access until
    /// REINDEX or until the analyzer is registered.
    /// </summary>
    private void OpenFtsIndexes()
    {
        if (_descriptor.Indexes is not { Count: > 0 } declared) return;

        foreach (IndexDescriptor descriptor in declared)
        {
            if (descriptor.Kind != IndexKind.FullText) continue;
            if (descriptor.Columns.Count != 1) continue;
            if (descriptor.AnalyzerName is null) continue;
            if (!FtsAnalyzerRegistry.Default.TryGet(descriptor.AnalyzerName, out IFullTextAnalyzer? analyzer))
            {
                continue;
            }

            string column = descriptor.Columns[0];
            string path = GetFtsIndexPath(_descriptor.FilePath, column);
            if (!File.Exists(path)) continue;

            try
            {
                FullTextSearchIndex index = FullTextSearchIndex.Open(path, analyzer!, column);
                _ftsIndexes[column] = index;
                _ftsIndexNameToColumn[descriptor.Name] = column;
            }
            catch
            {
                // Torn write / version mismatch — degrade silently. REINDEX
                // (once it learns about FTS) will rebuild.
            }
        }
    }

    /// <summary>
    /// Closes every open FTS index and clears the dictionaries. Called from
    /// <see cref="Dispose"/>.
    /// </summary>
    private void CloseFtsIndexes()
    {
        foreach (FullTextSearchIndex idx in _ftsIndexes.Values)
        {
            try { idx.Dispose(); } catch { /* best-effort */ }
        }
        _ftsIndexes.Clear();
        _ftsIndexNameToColumn.Clear();
    }

    /// <inheritdoc />
    public bool TryGetTextSearchIndex(string columnName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITextSearchIndex? index)
    {
        lock (_ftsIndexSync)
        {
            if (_ftsIndexes.TryGetValue(columnName, out FullTextSearchIndex? fts))
            {
                index = fts;
                return true;
            }
        }
        index = null;
        return false;
    }

    /// <summary>
    /// Creates a new FTS sidecar (<c>.datum-fts-{column}</c>), backfills it
    /// from a full table scan, and registers it for read access. Holds
    /// <see cref="_mutationLock"/> for the duration so concurrent INSERT /
    /// UPDATE / DELETE / DROP serialize against the build. The index is
    /// published to the visible-state dictionaries only after backfill
    /// completes — a concurrent reader either misses the index entirely
    /// (and queries fall through to scan) or sees the fully-populated
    /// tree.
    /// </summary>
    internal async Task AddFtsIndexAsync(IndexDescriptor descriptor)
    {
        if (descriptor.Kind != IndexKind.FullText)
        {
            throw new ArgumentException(
                $"AddFtsIndexAsync requires a FullText descriptor; got {descriptor.Kind}.", nameof(descriptor));
        }
        if (descriptor.Columns.Count != 1)
        {
            throw new ArgumentException(
                "FTS index must cover exactly one column.", nameof(descriptor));
        }
        if (descriptor.AnalyzerName is null)
        {
            throw new ArgumentException(
                "FTS descriptor missing AnalyzerName.", nameof(descriptor));
        }

        string column = descriptor.Columns[0];

        await _mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ftsIndexes.ContainsKey(column))
            {
                throw new InvalidOperationException(
                    $"Column '{column}' already has a full-text index on table '{_descriptor.Name}'.");
            }

            if (!FtsAnalyzerRegistry.Default.TryGet(descriptor.AnalyzerName, out IFullTextAnalyzer? analyzer))
            {
                throw new InvalidOperationException(
                    $"FTS analyzer '{descriptor.AnalyzerName}' is not registered.");
            }

            int columnOrdinal = ResolveColumnOrdinalCaseInsensitive(_snapshot.Schema, column);
            if (columnOrdinal < 0)
            {
                throw new InvalidOperationException(
                    $"CREATE INDEX '{descriptor.Name}': column '{column}' no longer exists on the schema.");
            }

            string path = GetFtsIndexPath(_descriptor.FilePath, column);
            FullTextSearchIndex index = FullTextSearchIndex.Create(path, analyzer!, column);

            try
            {
                if (GetRowCount() > 0)
                {
                    await BackfillFtsIndexAsync(index, columnOrdinal, analyzer!, CancellationToken.None).ConfigureAwait(false);
                }

                lock (_ftsIndexSync)
                {
                    _ftsIndexes[column] = index;
                    _ftsIndexNameToColumn[descriptor.Name] = column;
                }
            }
            catch
            {
                try { index.Dispose(); } catch { /* best-effort */ }
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
                throw;
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Scans the table, tokenizes each row's column value with the FTS
    /// index's analyzer, and inserts one posting per (term, document) —
    /// duplicates within a single document are folded into one posting via
    /// a per-row <see cref="HashSet{T}"/>. NULL values are skipped.
    /// </summary>
    private async Task BackfillFtsIndexAsync(
        FullTextSearchIndex index,
        int columnOrdinal,
        IFullTextAnalyzer analyzer,
        CancellationToken ct)
    {
        IReadOnlyList<Indexing.IndexChunk>? chunks = _sourceIndex?.Chunks;
        int defaultChunkSize = Indexing.IndexConstants.DefaultChunkSize;
        long absoluteRow = 0;
        int currentChunk = 0;
        HashSet<string> uniqueTermsForRow = new(StringComparer.Ordinal);

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
                    int chunkIndex;
                    long rowOffsetInChunk;
                    if (chunks is not null && chunks.Count > 0)
                    {
                        while (currentChunk + 1 < chunks.Count &&
                               absoluteRow >= chunks[currentChunk].RowOffset + chunks[currentChunk].RowCount)
                        {
                            currentChunk++;
                        }
                        chunkIndex = currentChunk;
                        rowOffsetInChunk = absoluteRow - chunks[chunkIndex].RowOffset;
                    }
                    else
                    {
                        chunkIndex = (int)(absoluteRow / defaultChunkSize);
                        rowOffsetInChunk = absoluteRow % defaultChunkSize;
                    }

                    Row row = batch[r];
                    DataValue v = row[columnOrdinal];
                    if (!v.IsNull)
                    {
                        // Sidecar-bound strings (long values past the inline
                        // threshold) need the (store, registry) overload —
                        // the one-arg AsString(Arena) only handles inline +
                        // arena-backed.
                        string text = v.AsString(batch.Arena, SidecarRegistry);
                        uniqueTermsForRow.Clear();
                        foreach (Token token in analyzer.Tokenize(text))
                        {
                            if (uniqueTermsForRow.Add(token.Term))
                            {
                                index.InsertPosting(token.Term, chunkIndex, rowOffsetInChunk);
                            }
                        }
                    }

                    absoluteRow++;
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
    }

    private static int ResolveColumnOrdinalCaseInsensitive(Schema schema, string columnName)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Tears down every open FTS sidecar, recreates an empty tree per index,
    /// and replays the table's rows through each analyzer to repopulate. Used
    /// by <see cref="UpdateRowsAsync"/>: an UPDATE that rewrites the indexed
    /// column would leave stale postings pointing at rows whose text no
    /// longer matches. Mirrors <see cref="RebuildCompositeIndexesNoLockAsync"/>.
    /// Caller must hold <c>_mutationLock</c>.
    /// </summary>
    private async Task RebuildFtsIndexesNoLockAsync(CancellationToken ct)
    {
        // Snapshot live runtime state: (indexName, column, analyzer) for every
        // open FTS index. Reading `_descriptor.Indexes` here would be wrong —
        // that reflects the descriptor as of provider-open, missing CREATE
        // INDEX entries added later in the same process. The open dicts plus
        // each index's Analyzer property are the authoritative runtime view.
        List<(string IndexName, string Column, IFullTextAnalyzer Analyzer)> snapshot = new();
        lock (_ftsIndexSync)
        {
            foreach ((string indexName, string column) in _ftsIndexNameToColumn)
            {
                if (_ftsIndexes.TryGetValue(column, out FullTextSearchIndex? idx))
                {
                    snapshot.Add((indexName, column, idx.Analyzer));
                }
            }
        }
        if (snapshot.Count == 0) return;

        // Tear down existing trees + sidecars under the dict lock so a
        // concurrent reader sees either pre- or post-clear state, never
        // a half-populated rebuild.
        List<FullTextSearchIndex> oldHandles = new();
        lock (_ftsIndexSync)
        {
            foreach (FullTextSearchIndex idx in _ftsIndexes.Values) oldHandles.Add(idx);
            _ftsIndexes.Clear();
            _ftsIndexNameToColumn.Clear();
        }
        foreach (FullTextSearchIndex h in oldHandles)
        {
            try { h.Dispose(); } catch { /* best-effort */ }
        }
        foreach ((string _, string column, IFullTextAnalyzer _) in snapshot)
        {
            string path = GetFtsIndexPath(_descriptor.FilePath, column);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        // Recreate empty trees + backfill each. Schema drift (column gone)
        // drops the index for this rebuild round.
        Schema schema = _snapshot.Schema;
        foreach ((string indexName, string column, IFullTextAnalyzer analyzer) in snapshot)
        {
            int ordinal = ResolveColumnOrdinalCaseInsensitive(schema, column);
            if (ordinal < 0) continue;

            string path = GetFtsIndexPath(_descriptor.FilePath, column);
            FullTextSearchIndex index = FullTextSearchIndex.Create(path, analyzer, column);
            try
            {
                if (GetRowCount() > 0)
                {
                    await BackfillFtsIndexAsync(index, ordinal, analyzer, ct).ConfigureAwait(false);
                }
                lock (_ftsIndexSync)
                {
                    _ftsIndexes[column] = index;
                    _ftsIndexNameToColumn[indexName] = column;
                }
            }
            catch
            {
                try { index.Dispose(); } catch { /* best-effort */ }
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
                throw;
            }
        }
    }

    /// <summary>
    /// Drops the FTS index named <paramref name="indexName"/> — disposes
    /// the tree handle, deletes the <c>.datum-fts-{column}</c> sidecar,
    /// and removes it from the visible-state dictionaries. Idempotent on
    /// unknown names (matches the composite-index drop contract).
    /// </summary>
    internal void DropFtsIndex(string indexName)
    {
        _mutationLock.Wait();
        try
        {
            FullTextSearchIndex? toDispose = null;
            string? column = null;

            lock (_ftsIndexSync)
            {
                if (_ftsIndexNameToColumn.TryGetValue(indexName, out column))
                {
                    _ftsIndexNameToColumn.Remove(indexName);
                    if (_ftsIndexes.TryGetValue(column, out toDispose))
                    {
                        _ftsIndexes.Remove(column);
                    }
                }
            }

            if (toDispose is not null)
            {
                try { toDispose.Dispose(); } catch { /* best-effort */ }
            }

            if (column is not null)
            {
                string path = GetFtsIndexPath(_descriptor.FilePath, column);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }
}
