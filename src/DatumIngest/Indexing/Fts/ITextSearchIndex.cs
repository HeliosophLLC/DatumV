namespace DatumIngest.Indexing.Fts;

/// <summary>
/// Per-column inverted index for full-text search. Maps tokenized terms to
/// the documents (chunk + row offset) where they appear. Acceleration only;
/// never part of a PRIMARY KEY or composite index.
/// </summary>
/// <remarks>
/// <para>v1 (PR-FTS-A) is boolean-only: postings carry (chunk, rowOffset)
/// and nothing else. Term frequency lands in PR-FTS-B alongside BM25 — see
/// <c>docs/deferred-decisions.md</c> for the deferred storage decision.</para>
///
/// <para>Tokenization at query time must match tokenization at insert time
/// — both sides go through <see cref="Analyzer"/>. Callers that build queries
/// from raw user text run <c>Analyzer.Tokenize</c> first and call
/// <see cref="FindPostings(string)"/> per surviving term.</para>
/// </remarks>
public interface ITextSearchIndex : IDisposable
{
    /// <summary>Name of the indexed column. Case-preserved.</summary>
    string ColumnName { get; }

    /// <summary>
    /// Analyzer used at index-build time. Query callers must use the same
    /// analyzer to tokenize their query text — that's why it's surfaced on
    /// the index rather than re-looked-up from the registry by query code.
    /// </summary>
    IFullTextAnalyzer Analyzer { get; }

    /// <summary>
    /// Total number of postings (term, chunk, row) triples in the index.
    /// Multiple postings per (term, document) pair are allowed in v1 (PR-A2
    /// doesn't dedupe at insert time); PR-FTS-C maintenance will collapse
    /// these to one posting per (term, document) with a frequency count.
    /// </summary>
    long PostingCount { get; }

    /// <summary>
    /// Returns every posting for <paramref name="term"/> in
    /// (chunkIndex, rowOffsetInChunk) ascending order. Returns an empty list
    /// when the term has no postings. <paramref name="term"/> must already
    /// be tokenized (lowercased, stripped); the index does not re-run the
    /// analyzer.
    /// </summary>
    IReadOnlyList<TextPosting> FindPostings(string term);

    /// <summary>
    /// Records that <paramref name="term"/> occurs in the document at
    /// (<paramref name="chunkIndex"/>, <paramref name="rowOffsetInChunk"/>).
    /// Caller is responsible for tokenization and within-document deduping
    /// (the index will happily store duplicate (term, chunk, row) triples
    /// as separate postings).
    /// </summary>
    void InsertPosting(string term, int chunkIndex, long rowOffsetInChunk);
}

/// <summary>
/// A single (chunk, row) posting from an <see cref="ITextSearchIndex"/>.
/// PR-FTS-B grows this with a term-frequency field once BM25 lands.
/// </summary>
public readonly record struct TextPosting(int ChunkIndex, long RowOffsetInChunk);
