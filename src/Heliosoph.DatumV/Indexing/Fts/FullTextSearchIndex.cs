using Heliosoph.DatumV.Indexing.BTree.MutableBytes;

namespace Heliosoph.DatumV.Indexing.Fts;

/// <summary>
/// Shape-A FTS index: a thin <see cref="ITextSearchIndex"/> facade over a
/// dup-key <see cref="MutableBPlusTreeBytes"/>. Postings for a single term
/// share an encoded byte key and are sorted as duplicates by the tree's
/// natural (ChunkIndex, RowOffsetInChunk) tie-breakers — prefix-scan
/// retrieval is in ascending document order.
/// </summary>
/// <remarks>
/// <para>The on-disk file is the bytes-tree alone — no FTS-specific header.
/// The analyzer used to build the index is supplied at <see cref="Create"/>
/// / <see cref="Open"/> time by the caller (typically the table provider,
/// resolving the analyzer name from a catalog-level index descriptor).
/// PR-FTS-A2 doesn't yet know how that descriptor is stored; PR-FTS-A3 owns
/// the catalog wiring.</para>
///
/// <para>Single-writer model inherited from <see cref="MutableBPlusTreeBytes"/>
/// — callers (the table provider's mutation path) serialize writes
/// themselves.</para>
/// </remarks>
internal sealed class FullTextSearchIndex : ITextSearchIndex
{
    private readonly MutableBPlusTreeBytes _tree;
    private readonly IFullTextAnalyzer _analyzer;
    private readonly string _columnName;
    private bool _disposed;

    private FullTextSearchIndex(MutableBPlusTreeBytes tree, IFullTextAnalyzer analyzer, string columnName)
    {
        _tree = tree;
        _analyzer = analyzer;
        _columnName = columnName;
    }

    public string ColumnName => _columnName;

    public IFullTextAnalyzer Analyzer => _analyzer;

    public long PostingCount => _tree.EntryCount;

    /// <summary>
    /// Creates a new FTS sidecar at <paramref name="path"/>. Fails if the
    /// file already exists (same contract as <see cref="MutableBPlusTreeBytes.Create"/>).
    /// </summary>
    internal static FullTextSearchIndex Create(string path, IFullTextAnalyzer analyzer, string columnName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(columnName);

        MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Create(path, allowDuplicates: true);
        return new FullTextSearchIndex(tree, analyzer, columnName);
    }

    /// <summary>
    /// Opens an existing FTS sidecar at <paramref name="path"/>. The caller
    /// supplies the analyzer (resolved from the catalog-level descriptor);
    /// the index file itself doesn't carry analyzer metadata.
    /// </summary>
    internal static FullTextSearchIndex Open(string path, IFullTextAnalyzer analyzer, string columnName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(columnName);

        MutableBPlusTreeBytes tree = MutableBPlusTreeBytes.Open(path);
        return new FullTextSearchIndex(tree, analyzer, columnName);
    }

    public IReadOnlyList<TextPosting> FindPostings(string term)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(term);

        byte[] key = FtsPostingKeyEncoder.Encode(term);
        IReadOnlyList<BytesIndexEntry> entries = _tree.FindPrefix(key);

        if (entries.Count == 0)
        {
            return Array.Empty<TextPosting>();
        }

        TextPosting[] result = new TextPosting[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            result[i] = new TextPosting(entries[i].ChunkIndex, entries[i].RowOffsetInChunk);
        }
        return result;
    }

    public void InsertPosting(string term, int chunkIndex, long rowOffsetInChunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(term);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffsetInChunk);

        byte[] key = FtsPostingKeyEncoder.Encode(term);
        _tree.Insert(new BytesIndexEntry(key, chunkIndex, rowOffsetInChunk));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _tree.Dispose();
        _disposed = true;
    }
}
