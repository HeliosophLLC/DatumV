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
    /// <summary>
    /// Append session backed by a held-open <see cref="DatumFileWriterV2"/>.
    /// Constructed under the parent provider's <c>_mutationLock</c>;
    /// owns the permit until disposed. Writes stream straight to the
    /// data file (visible past the existing tail but unreferenced
    /// until commit); commit calls <c>FinalizeWriter</c> to write the
    /// new tail and rebuild the parent's snapshot. Disposing without
    /// committing closes the writer without finalizing — partial bytes
    /// are reclaimed by the next writer's torn-tail recovery.
    /// </summary>
    private sealed class DatumAppendSession : IAppendSession
    {
        private readonly DatumFileTableProviderV2 _provider;
        private DatumFileWriterV2? _writer;
        private bool _committed;
        private bool _disposed;
        private bool _anyWrites;

        // Captured at construction; unchanged across the session's
        // lifetime (column index + spec are CREATE-time properties).
        // null when the table has no IDENTITY column.
        private readonly IdentityState? _initialIdentityState;
        private long _identityNextValue;
        private bool _identityReserved;

        // Composite-encoded PK key bytes queued per WriteAsync row when
        // the parent provider has an open PK index. Flushed into the
        // bytes tree on CommitAsync after the data commit succeeds.
        // <see langword="null"/> when there's no on-disk PK index —
        // InsertExecutor's scan-based fallback handles uniqueness for
        // those tables (TEMP / InMemoryProvider).
        private readonly List<byte[]>? _pendingPkBytes;
        private readonly IReadOnlyList<int> _pkColumnIndices;

        // User-defined composite indexes (CREATE INDEX). One queue per
        // index, keyed by index name. Each entry carries the encoded key
        // plus the (chunkIndex, rowOffsetInChunk) where the row lands —
        // captured pre-AddRow so future point/range probes can seek back.
        // Empty (null) when the table has no composite indexes; otherwise
        // mirrors the snapshot of <c>_compositeIndexTrees</c> at session
        // start. Re-resolution against the live provider state happens
        // at flush time in CommitAsync.
        private readonly Dictionary<string, CompositeIndexState>? _compositeIndexStates;

        // Per-row inverted-index postings queued during WriteAsync, one
        // queue per FTS index (keyed by index name). Each pending posting
        // carries the deduped term + the (chunkIndex, rowOffsetInChunk) the
        // row lands at — same chunk-directory arithmetic the composite
        // queue uses. Flushed into the open FullTextSearchIndex on
        // CommitAsync after the data commit succeeds. Empty (null) when
        // the table has no FTS indexes. If a row carries a sidecar-bound
        // string (no access to the source registry here), we set
        // <see cref="_ftsRequiresFullRebuild"/> instead so the commit
        // falls back to a full FTS rebuild rather than silently dropping
        // postings.
        private readonly Dictionary<string, FtsIndexState>? _ftsIndexStates;

        // Set when WriteAsync encounters a sidecar-bound text value it
        // can't resolve via the incoming batch's arena. CommitAsync
        // detects this and calls RebuildFtsIndexesNoLockAsync (full
        // table scan) instead of flushing the per-row queues, trading
        // throughput for correctness on the rare INSERT...SELECT-from-
        // a-sidecar-source path.
        private bool _ftsRequiresFullRebuild;

        // Phase 3a-wide: per-row in-line index build. Eliminates the
        // post-commit scan that ExtendAsync used to do — the append session
        // already has the rows in hand, so we mirror them into an
        // IncrementalIndexBuilder (bloom + bitmap + zone maps) and queue
        // per-column tree entries during WriteAsync. CommitAsync flushes
        // the queues into the provider's open trees and writes the merged
        // sidecar atomically.
        //
        // Lazily initialized on first WriteAsync — schema is captured then.
        private Indexing.IncrementalIndexBuilder? _indexBuilder;
        private SourceIndex? _existingForMerge;
        private Dictionary<string, List<ValueIndexEntry>>? _pendingColumnEntries;
        private string[]? _indexableColumnNames;

        public DatumAppendSession(DatumFileTableProviderV2 provider)
        {
            _provider = provider;
            // Writer opened lazily on first WriteAsync — empty sessions
            // (Begin → Commit with no writes) are no-ops with no file
            // mutation, matching the in-memory provider's semantics.

            // Snapshot IDENTITY state once. Reservations advance the
            // session-local counter; commit pushes it back through the
            // writer's prologue.
            Snapshot snapshot = provider.AcquireSnapshot();
            try
            {
                IdentityState? state = ResolveIdentityStateLocked(snapshot);
                _initialIdentityState = state;
                _identityNextValue = state?.NextValue ?? 0;
            }
            finally
            {
                provider.ReleaseSnapshot(snapshot);
            }

            // PK index state. Captured once; the column indices can't
            // move because ALTER DROP COLUMN of a PK column is rejected.
            if (provider._pkIndexBytes is not null)
            {
                _pendingPkBytes = new List<byte[]>();
                _pkColumnIndices = provider._pkColumnIndices;
            }
            else
            {
                _pkColumnIndices = Array.Empty<int>();
            }

            // Composite index state. Snapshot the provider's open indexes
            // so the session has stable per-index queues + column ordinals.
            // ALTER DROP COLUMN that affects a covered column drops the
            // dependent index, so the ordinals are guaranteed stable for the
            // session's lifetime. Capturing the Tree handle here too lets
            // unique-index pre-flight probe the existing keys without
            // re-looking-up the dict on every row.
            if (provider._compositeIndexTrees.Count > 0)
            {
                _compositeIndexStates = new Dictionary<string, CompositeIndexState>(
                    StringComparer.OrdinalIgnoreCase);
                foreach ((string indexName, Indexing.BTree.MutableBytes.MutableBPlusTreeBytes tree) in provider._compositeIndexTrees)
                {
                    int[] ordinals = provider._compositeIndexColumnIndices[indexName];
                    IndexDescriptor descriptor = provider._compositeIndexDescriptors[indexName];
                    HashSet<byte[]>? seenUniqueKeys = descriptor.IsUnique
                        ? new HashSet<byte[]>(ByteArraySequenceEqualityComparer.Instance)
                        : null;
                    _compositeIndexStates[indexName] = new CompositeIndexState(
                        ColumnIndices: ordinals,
                        IsUnique: descriptor.IsUnique,
                        Tree: tree,
                        PendingEntries: new List<Indexing.BTree.MutableBytes.BytesIndexEntry>(),
                        SeenUniqueKeys: seenUniqueKeys);
                }
            }

            // FTS-index state snapshot. Walk the live runtime dicts directly
            // rather than _descriptor.Indexes — the descriptor reflects the
            // catalog manifest at provider-open, missing CREATE INDEX entries
            // added later in the same process. CREATE/DROP holds
            // _mutationLock for the whole publish step, so reading the dicts
            // here gives a consistent snapshot.
            if (provider._ftsIndexes.Count > 0)
            {
                Schema schema = provider._snapshot.Schema;
                _ftsIndexStates = new Dictionary<string, FtsIndexState>(StringComparer.OrdinalIgnoreCase);
                lock (provider._ftsIndexSync)
                {
                    foreach ((string indexName, string column) in provider._ftsIndexNameToColumn)
                    {
                        if (!provider._ftsIndexes.TryGetValue(column, out FullTextSearchIndex? index)) continue;
                        int ordinal = ResolveColumnOrdinalCaseInsensitive(schema, column);
                        if (ordinal < 0) continue;
                        _ftsIndexStates[indexName] = new FtsIndexState(
                            IndexName: indexName,
                            ColumnOrdinal: ordinal,
                            Analyzer: index.Analyzer,
                            Index: index,
                            PendingPostings: new List<PendingFtsPosting>());
                    }
                }
            }
        }

        /// <summary>
        /// Per-index state captured at the start of an append session: the
        /// schema column indices to extract per row, the open tree handle,
        /// the queue of encoded entries waiting for CommitAsync to flush,
        /// and (for UNIQUE indexes) the within-session "seen" set used by
        /// the pre-flight uniqueness check to catch duplicates BEFORE the
        /// data commit. The set survives the lifetime of one
        /// <c>AppendSession</c> so a single INSERT batch can't smuggle a
        /// pair of colliding rows past the tree's per-row check.
        /// </summary>
        private sealed record CompositeIndexState(
            int[] ColumnIndices,
            bool IsUnique,
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes Tree,
            List<Indexing.BTree.MutableBytes.BytesIndexEntry> PendingEntries,
            HashSet<byte[]>? SeenUniqueKeys);

        /// <summary>
        /// Per-FTS-index state captured at session start. Carries the column
        /// ordinal to read per row, the analyzer used to tokenize (captured
        /// to avoid a registry lookup per row), the open index handle, and
        /// the per-row queue of postings that <see cref="CommitAsync"/>
        /// flushes after the data commit succeeds.
        /// </summary>
        private sealed record FtsIndexState(
            string IndexName,
            int ColumnOrdinal,
            IFullTextAnalyzer Analyzer,
            FullTextSearchIndex Index,
            List<PendingFtsPosting> PendingPostings);

        /// <summary>
        /// A queued (term, document-position) pair waiting for commit-time
        /// insertion into a <see cref="FullTextSearchIndex"/>. Term strings
        /// are managed allocations from <see cref="IFullTextAnalyzer.Tokenize"/>
        /// (independent of any arena), so they're safe to hold across the
        /// source batch's pool return.
        /// </summary>
        private readonly record struct PendingFtsPosting(
            string Term, int ChunkIndex, long RowOffsetInChunk);

        /// <summary>
        /// Value-equality + content-derived hash for byte arrays. Used by
        /// the UNIQUE-index pre-flight <c>HashSet&lt;byte[]&gt;</c>; the
        /// default reference-equality semantics would let two distinct
        /// arrays with identical content slip past.
        /// </summary>
        private sealed class ByteArraySequenceEqualityComparer : IEqualityComparer<byte[]>
        {
            internal static readonly ByteArraySequenceEqualityComparer Instance = new();
            public bool Equals(byte[]? x, byte[]? y) =>
                x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
            public int GetHashCode(byte[] obj)
            {
                // Simple FNV-1a 32-bit. Encoded composite keys are short
                // (typically ≤ 40 bytes) and collisions only cost a few
                // SequenceEqual probes inside the HashSet.
                uint hash = 2166136261;
                foreach (byte b in obj)
                {
                    hash ^= b;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        public IdentityState? IdentityState => _initialIdentityState is null
            ? null
            : _initialIdentityState with { NextValue = _identityNextValue };

        public long ReserveNextIdentityValue()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot reserve IDENTITY values after CommitAsync.");
            if (_initialIdentityState is null)
            {
                throw new InvalidOperationException(
                    $"Table '{_provider.QualifiedName}' has no IDENTITY column.");
            }

            long reserved = _identityNextValue;
            _identityNextValue = checked(_identityNextValue + _initialIdentityState.Spec.Step);
            _identityReserved = true;
            return reserved;
        }

        private static IdentityState? ResolveIdentityStateLocked(Snapshot snapshot)
        {
            // Snapshot.Schema's tombstone-skipping is symmetric with the
            // schema-to-footer index map; walk live schema columns and
            // pick the one whose ColumnInfo.Identity is non-null.
            Schema schema = snapshot.Schema;
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                ColumnInfo c = schema.Columns[i];
                if (c.Identity is { } spec)
                {
                    return new IdentityState(
                        ColumnIndex: i,
                        ColumnKind: c.Kind,
                        Spec: spec,
                        NextValue: snapshot.Reader.Footer.Prologue.IdentityNextValue);
                }
            }
            return null;
        }

        public Task WriteAsync(RowBatch batch, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot write after CommitAsync.");
            ArgumentNullException.ThrowIfNull(batch);
            cancellationToken.ThrowIfCancellationRequested();

            if (batch.Count == 0) return Task.CompletedTask;

            ValidateBatchSchema(batch);

            // Capture PK keys per row, encoding them to bytes before the
            // batch potentially returns to the pool. The encoded bytes
            // are fresh byte[] arrays so they survive the batch's
            // arena lifecycle.
            if (_pendingPkBytes is not null)
            {
                DataValue[] tuple = new DataValue[_pkColumnIndices.Count];
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    for (int p = 0; p < _pkColumnIndices.Count; p++)
                    {
                        tuple[p] = row[_pkColumnIndices[p]];
                    }
                    byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, batch.Arena);
                    _pendingPkBytes.Add(encoded);
                }
            }

            // Phase 3a-wide: build the unified-sidecar delta + per-column
            // tree entry queues in lockstep with the data write. Lazy init
            // on first batch — _indexBuilder needs a fingerprint at
            // construction (will be swapped at Finalize), and the indexable
            // column set is captured from the live schema once.
            if (_indexBuilder is null)
            {
                EnsureIndexBuildersInitialized();
            }

            // Offset the session-local chunk index by the number of chunks the
            // existing source index already carries. SourceIndex.Merge appends
            // delta chunks AFTER existing ones (delta chunkIndex 0 becomes
            // merged chunkIndex N where N = existing chunk count); without
            // this shift, entries queued for the new rows would resolve via
            // chunks[0] in the planner and seek to pre-existing rows.
            int existingChunkCount = _existingForMerge?.Chunks.Count ?? 0;

            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];

                // Capture (chunkIndex, rowOffsetInChunk) BEFORE AddRow so the
                // values match where IncrementalIndexBuilder will place this
                // row internally. AddRow may roll the chunk forward inside
                // its own bookkeeping; we want the pre-add slot.
                int chunkIndex = _indexBuilder!.CurrentChunkIndex + existingChunkCount;
                int rowOffsetInChunk = _indexBuilder.RowsInCurrentChunk;

                _indexBuilder.AddRow(row, batch.Arena);

                if (_indexableColumnNames is { } cols)
                {
                    for (int c = 0; c < cols.Length; c++)
                    {
                        string columnName = cols[c];
                        DataValue value = row[columnName];
                        if (value.IsNull) continue;
                        // Sidecar-bound values can't survive the source
                        // batch's pool return (the DataValue copy points at
                        // an offset that's about to be reused). Skip — the
                        // column degrades to scan for those rows.
                        if (value.IsInSidecar) continue;
                        _pendingColumnEntries![columnName].Add(
                            new ValueIndexEntry(value, chunkIndex, rowOffsetInChunk));
                    }
                }

                // Composite indexes: encode this row's tuple once per index.
                // Encoded bytes outlive the source batch's arena (Encode
                // returns a fresh byte[]), so the queue is safe to hold.
                // Skip indexing any row that has a NULL in a covered
                // column — Postgres-compatible equality semantics
                // (`= NULL` never matches, so the missing entry can
                // never cause a missed seek hit). `IS NULL` falls
                // through to scan regardless. NULLS DISTINCT also exempts
                // these rows from the UNIQUE check.
                //
                // For UNIQUE indexes, pre-flight check before queueing:
                // probe the tree (existing rows) and the session-local
                // seen-set (rows queued earlier in THIS batch). Throwing
                // here, before the row reaches the writer, keeps INSERT
                // all-or-nothing — a failed unique violation never
                // commits half a batch.
                if (_compositeIndexStates is { } compositeStates)
                {
                    foreach ((string indexName, CompositeIndexState state) in compositeStates)
                    {
                        DataValue[] tuple = new DataValue[state.ColumnIndices.Length];
                        bool hasNull = false;
                        for (int p = 0; p < state.ColumnIndices.Length; p++)
                        {
                            DataValue v = row[state.ColumnIndices[p]];
                            if (v.IsNull) { hasNull = true; break; }
                            tuple[p] = v;
                        }
                        if (hasNull) continue;

                        byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, batch.Arena);

                        if (state.IsUnique)
                        {
                            // Probe existing entries on disk.
                            if (state.Tree.TryFind(encoded, out _))
                            {
                                throw new UniqueIndexViolationException(
                                    $"INSERT into '{_provider._descriptor.Name}' would violate UNIQUE INDEX '{indexName}': a row with the same key already exists.");
                            }
                            // Probe within-batch rows queued earlier.
                            if (!state.SeenUniqueKeys!.Add(encoded))
                            {
                                throw new UniqueIndexViolationException(
                                    $"INSERT into '{_provider._descriptor.Name}' would violate UNIQUE INDEX '{indexName}': two rows in this batch share the same key.");
                            }
                        }

                        state.PendingEntries.Add(
                            new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                encoded, chunkIndex, rowOffsetInChunk));
                    }
                }

                // FTS indexes: tokenize the indexed column's text and queue
                // one posting per unique term per row. Postings flushed in
                // CommitAsync. Sidecar-bound values can't be resolved here
                // (we don't have the source batch's SidecarRegistry), so we
                // fall back to a post-commit full rebuild — slower, but
                // avoids silently dropping rows from the index.
                if (_ftsIndexStates is { } ftsStates && !_ftsRequiresFullRebuild)
                {
                    foreach ((string _, FtsIndexState state) in ftsStates)
                    {
                        DataValue v = row[state.ColumnOrdinal];
                        if (v.IsNull) continue;
                        if (v.IsInSidecar)
                        {
                            _ftsRequiresFullRebuild = true;
                            break;
                        }
                        string text = v.AsString(batch.Arena);
                        // Per-row term dedup matches BackfillFtsIndexAsync —
                        // one posting per (term, document), not per token.
                        HashSet<string> uniqueTerms = new(StringComparer.Ordinal);
                        foreach (Token token in state.Analyzer.Tokenize(text))
                        {
                            if (uniqueTerms.Add(token.Term))
                            {
                                state.PendingPostings.Add(new PendingFtsPosting(
                                    token.Term, chunkIndex, rowOffsetInChunk));
                            }
                        }
                    }
                }
            }

            _writer ??= _provider.OpenAppendWriter();
            _writer.WriteRowBatch(batch);
            _anyWrites = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Lazy initializer for the in-line index build. Captures the live
        /// schema's indexable-column set, asks the provider to open/create
        /// trees for them, and creates an <see cref="Indexing.IncrementalIndexBuilder"/>
        /// rooted at a placeholder fingerprint (overridden at finalize with
        /// the real post-commit fingerprint). Called from the first WriteAsync.
        /// </summary>
        private void EnsureIndexBuildersInitialized()
        {
            // Capture the existing in-memory index for the merge step.
            // Same lifetime story as the original RebuildIndexNoLock(existingForExtend)
            // path: RebuildSnapshotAfterMutation no longer disposes the
            // cached mmap, so this reference's accessor stays alive
            // through CommitAsync's merge.
            _existingForMerge = _provider._sourceIndex;

            Schema schema = _provider._snapshot.Schema;

            // Indexable columns: kind-based eligibility, no array columns
            // (parallel to the rules SourceIndexBuilder.CreateBitmapAccumulators
            // and Indexer.IndexAsync use).
            List<string> indexable = new();
            foreach (ColumnInfo column in schema.Columns)
            {
                if (Indexing.SourceIndexBuilder.IsAutoIndexableKind(column.Kind) && !column.IsArray)
                {
                    indexable.Add(column.Name);
                }
            }
            _indexableColumnNames = indexable.ToArray();

            // Have the provider open/create trees for any indexable columns
            // that don't yet have one open. After this call, every column
            // in _indexableColumnNames has an open MutableBPlusTree we can
            // GetColumnTreeForAppendSession() on at commit time.
            _provider.EnsureColumnTreesForIndexableColumnsNoLock();

            _pendingColumnEntries = new Dictionary<string, List<ValueIndexEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (string columnName in _indexableColumnNames)
            {
                _pendingColumnEntries[columnName] = new List<ValueIndexEntry>();
            }

            // Builder needs a fingerprint at construction; it's overridden
            // at Finalize() with the real post-commit one. Bloom + bitmap
            // for every column ("Auto" mode parity with Indexer.IndexAsync's
            // default IndexOptions).
            SourceIndexBuilder builder = new(
                bloomAllColumns: true,
                chunkSize: Indexing.IndexConstants.EffectiveChunkSize,
                computeCardinality: true);
            SourceFingerprint placeholder = new(0, new byte[32]);
            _indexBuilder = builder.CreateIncrementalBuilder(placeholder);
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("CommitAsync was already called.");
            cancellationToken.ThrowIfCancellationRequested();

            // Capture the pre-mutation in-memory index BEFORE we touch
            // the file. PR13b's chunk-splice extend path needs this to
            // carry forward the prefix's bloom + bitmap + zone-map
            // bytes without rescanning. RebuildSnapshotAfterMutation no
            // longer disposes the cached mmap, so the captured
            // reference's accessor stays alive until
            // RebuildIndexNoLock's final InvalidateSourceIndexCache.
            SourceIndex? existingForExtend = _provider._sourceIndex;

            try
            {
                // If the session reserved IDENTITY values but never
                // wrote a batch, we still need to bump the prologue's
                // counter so the wasted reservations aren't handed out
                // again on a future session. Open the writer in append
                // mode — it carries forward existing state, including
                // the IDENTITY counter, and rewrites the prologue.
                if (_writer is null && _identityReserved)
                {
                    _writer = _provider.OpenAppendWriter();
                }

                if (_writer is not null)
                {
                    if (_initialIdentityState is not null && _identityReserved)
                    {
                        _writer.UpdateIdentityNextValue(_identityNextValue);
                    }
                    _writer.FinalizeWriter();
                    _writer.Dispose();
                    _writer = null;

                    // Rebuild the parent's snapshot under the lock we
                    // already hold (the session has the semaphore
                    // permit). Sidecar may have grown if any of the
                    // appended rows spilled non-inline payloads.
                    // Snapshot rebuild leaves _sourceIndex untouched —
                    // RebuildIndexNoLock below handles the swap.
                    _provider.RebuildSnapshotAfterMutation(sidecarMayHaveGrown: _anyWrites);
                }

                // Flush queued PK keys into the on-disk B+Tree. Done after
                // the .datum file commit succeeds so a writer crash between
                // the two leaves the index either consistent (no data, no
                // index update) or slightly stale (rows present, missing
                // from index). The latter only widens the window for a
                // duplicate to slip past on the next INSERT — caught at
                // REINDEX (PR12) until we add a 2-phase commit.
                if (_pendingPkBytes is not null && _provider._pkIndexBytes is not null)
                {
                    foreach (byte[] encoded in _pendingPkBytes)
                    {
                        // ChunkIndex / RowOffset are placeholder zeros —
                        // the lookup is uniqueness-only; the actual
                        // (chunk, row) addressing for "find me the row"
                        // is a follow-up PR. Storing 0/0 keeps the entry
                        // shape compatible.
                        _provider._pkIndexBytes.Insert(
                            new Indexing.BTree.MutableBytes.BytesIndexEntry(
                                encoded, ChunkIndex: 0, RowOffsetInChunk: 0L));
                    }
                    _pendingPkBytes.Clear();
                }

                // Flush queued composite-index entries. Same crash-window
                // contract as the PK queue: if the process dies between
                // this loop and the next commit, the affected indexes go
                // slightly stale — rebuild via DROP / CREATE INDEX, or
                // (future) REINDEX once it learns about composite trees.
                //
                // For UNIQUE indexes, the underlying tree throws
                // DuplicateKeyException when an insert would create a
                // second entry with the same encoded key. Translate into
                // UniqueIndexViolationException so the INSERT statement
                // surfaces a clean error.
                if (_compositeIndexStates is { } compositeStates)
                {
                    foreach ((string indexName, CompositeIndexState state) in compositeStates)
                    {
                        if (!_provider._compositeIndexTrees.TryGetValue(
                                indexName, out Indexing.BTree.MutableBytes.MutableBPlusTreeBytes? tree))
                        {
                            // Index was dropped mid-session — nothing to flush.
                            state.PendingEntries.Clear();
                            continue;
                        }
                        foreach (Indexing.BTree.MutableBytes.BytesIndexEntry entry in state.PendingEntries)
                        {
                            try
                            {
                                tree.Insert(entry);
                            }
                            catch (Indexing.BTree.MutableBytes.DuplicateKeyException)
                            {
                                throw new UniqueIndexViolationException(
                                    $"INSERT violated UNIQUE INDEX '{indexName}' on table '{_provider._descriptor.Name}': " +
                                    "a row with the same key already exists.");
                            }
                        }
                        state.PendingEntries.Clear();
                    }
                }

                // Flush queued FTS postings. Mirrors the composite-index
                // flush: same post-data-commit timing means a writer crash
                // between the two leaves the FTS sidecar slightly stale
                // (rows committed, postings missing) — recoverable via
                // REINDEX. If WriteAsync flagged a sidecar-bound source
                // value mid-session, do a full rebuild instead so the
                // index sees every row regardless of source-store
                // accessibility.
                if (_ftsIndexStates is { } ftsStates)
                {
                    if (_ftsRequiresFullRebuild)
                    {
                        await _provider.RebuildFtsIndexesNoLockAsync(cancellationToken).ConfigureAwait(false);
                        foreach ((string _, FtsIndexState s) in ftsStates) s.PendingPostings.Clear();
                    }
                    else
                    {
                        foreach ((string indexName, FtsIndexState state) in ftsStates)
                        {
                            // Index may have been dropped mid-session — the
                            // descriptor still names it but the open dict
                            // has cleared. Skip; nothing to flush.
                            if (!_provider._ftsIndexes.TryGetValue(state.Index.ColumnName, out FullTextSearchIndex? live)
                                || !ReferenceEquals(live, state.Index))
                            {
                                state.PendingPostings.Clear();
                                continue;
                            }
                            foreach (PendingFtsPosting p in state.PendingPostings)
                            {
                                state.Index.InsertPosting(p.Term, p.ChunkIndex, p.RowOffsetInChunk);
                            }
                            state.PendingPostings.Clear();
                        }
                    }
                }

                _committed = true;
            }
            catch
            {
                // Commit failed — drop the writer without finalizing.
                // Subsequent torn-tail recovery cleans up.
                _writer?.Dispose();
                _writer = null;
                throw;
            }

            // Phase 3a-wide: refresh the unified .datum-index sidecar from
            // the in-line build that ran alongside WriteAsync. The append
            // session already has the per-row bloom / bitmap / zone-map
            // accumulators populated and per-column tree entries queued —
            // we don't need Indexer to re-scan the file the way the old
            // PR13b extend path did.
            //
            // Failure-mode contract (locked in PR13's design): if any of
            // the steps below fails, the data commit stands. The cache is
            // invalidated and the on-disk .datum-index becomes Stale (per
            // IndexValidity) — queries fall back to scan until a manual
            // REINDEX recovers. We swallow the exception so the user's
            // INSERT statement doesn't appear to fail when the data is
            // actually committed.
            //
            // IDENTITY-only commits (no _anyWrites) bump the prologue and
            // therefore the fingerprint, but no rows changed; the existing
            // index is functionally stale on fingerprint mismatch alone.
            // Match PR13a-2 behaviour and just invalidate.
            if (_anyWrites && _indexBuilder is not null)
            {
                try
                {
                    FlushIndexBuildersToProvider(existingForExtend);
                }
                catch
                {
                    _provider.InvalidateSourceIndexCache();
                }
            }
            else
            {
                _provider.InvalidateSourceIndexCache();
            }
        }

        /// <summary>
        /// Phase 3a-wide commit-time flush: drains the per-column tree entry
        /// queues into the provider's open <see cref="MutableBPlusTree"/>s,
        /// finalizes the in-line index build using the post-commit data-file
        /// fingerprint, merges with <paramref name="existing"/>, and writes
        /// the result via <see cref="DatumFileTableProviderV2.WriteUnifiedSidecarNoLock"/>.
        /// </summary>
        private void FlushIndexBuildersToProvider(SourceIndex? existing)
        {
            // Per-column tree inserts. Trees stay open across the call —
            // the append session's mutation lock keeps concurrent readers
            // from observing torn state through the open dictionary.
            if (_pendingColumnEntries is not null)
            {
                foreach (KeyValuePair<string, List<ValueIndexEntry>> entry in _pendingColumnEntries)
                {
                    MutableBPlusTree? tree = _provider.GetColumnTreeForAppendSession(entry.Key);
                    if (tree is null) continue;
                    foreach (ValueIndexEntry e in entry.Value)
                    {
                        tree.Insert(e);
                    }
                }
            }

            // Compute the post-commit fingerprint of the .datum file.
            // The data writer just rewrote the prologue, so the fingerprint
            // necessarily changed; the in-line builder needs the live one
            // so the on-disk sidecar matches.
            SourceFingerprint freshFingerprint;
            using (FileStream stream = new(
                _provider._descriptor.FilePath,
                FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, useAsync: false))
            {
                freshFingerprint = SourceFingerprint.Compute(stream);
            }

            SourceIndex delta = _indexBuilder!.Finalize(freshFingerprint);

            SourceIndex merged = existing is null
                ? delta
                : SourceIndex.Merge(existing, delta);

            string tableName = PathDetector.DeriveTableName(_provider._descriptor.FilePath);
            _provider.WriteUnifiedSidecarNoLock(merged, tableName);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;

            try
            {
                if (!_committed && _writer is not null)
                {
                    // Abort: close the writer without FinalizeWriter so
                    // no new tail is emitted. Trailing partial bytes
                    // become unreachable garbage cleaned up by the next
                    // writer's RecoverIfTorn pass.
                    _writer.Dispose();
                    _writer = null;
                }
                _indexBuilder?.Dispose();
                _indexBuilder = null;
            }
            finally
            {
                _provider._mutationLock.Release();
            }

            return ValueTask.CompletedTask;
        }

        private void ValidateBatchSchema(RowBatch batch)
        {
            // Capture current schema under snapshot lock — light touch,
            // doesn't bump the refcount because we don't need to retain
            // anything across the call.
            Schema schema;
            lock (_provider._snapshotLock)
            {
                schema = _provider._snapshot.Schema;
            }

            if (batch.ColumnLookup.Count != schema.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"Append batch has {batch.ColumnLookup.Count} columns but table " +
                    $"'{_provider.QualifiedName}' has {schema.Columns.Count}.");
            }
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                string expected = schema.Columns[i].Name;
                string actual = batch.ColumnLookup.GetColumnName(i);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Append batch column {i} is named '{actual}' but table " +
                        $"'{_provider.QualifiedName}' expects '{expected}'.");
                }
            }
        }
    }
}
