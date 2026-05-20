using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Fts;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Streams rows whose indexed text column matches every token of a query
/// string. Replaces the <c>ScanOperator + FilterOperator(col @@ q)</c>
/// pair when the planner detects that the searched column has an FTS
/// index. The planner injection itself lands in PR-FTS-A4; this PR ships
/// the operator class so it's constructable, testable, and ready to be
/// picked up.
/// </summary>
/// <remarks>
/// <para><b>v1 semantics:</b> AND of all surviving tokens after
/// tokenization through the index's analyzer. No scoring, no top-k —
/// matching rows stream out in source-row order. An empty query (all
/// stop words, or only short tokens) yields zero rows; the
/// <see cref="Heliosoph.DatumV.Functions.Scalar.Fulltext.TsqueryMatchFunction"/>
/// equivalent returns true for any haystack, so the planner is
/// responsible for not injecting this operator for an empty-token
/// query.</para>
///
/// <para><b>Intersection strategy:</b> v1 uses a HashSet over the
/// shortest posting list and probes the rest. O(total postings) — fine
/// at chat scale. PR-FTS-D's posting-heap shape will pull in sorted-list
/// merge with skip pointers when posting counts grow.</para>
///
/// <para><b>Row materialisation:</b> postings are <c>(chunkIdx, rowOff)</c>
/// pairs that get translated to absolute row positions via the source
/// index's chunk directory, then seeked one-by-one through the table
/// provider's seek session. Same pattern as <see cref="ScanOperator"/>'s
/// exact-seek path.</para>
/// </remarks>
public sealed class FullTextSearchOperator : QueryOperator
{
    private readonly ITableProvider _provider;
    private readonly string _columnName;
    private readonly string _queryText;
    private readonly IReadOnlySet<string>? _requiredColumns;

    /// <summary>
    /// Constructs an FTS operator. Caller has already determined that
    /// <paramref name="provider"/> exposes a <see cref="ITextSearchIndex"/>
    /// on <paramref name="columnName"/> — the operator throws at execute
    /// time if that turns out to be false.
    /// </summary>
    public FullTextSearchOperator(
        ITableProvider provider,
        string columnName,
        string queryText,
        IReadOnlySet<string>? requiredColumns)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(columnName);
        ArgumentNullException.ThrowIfNull(queryText);

        _provider = provider;
        _columnName = columnName;
        _queryText = queryText;
        _requiredColumns = requiredColumns;
    }

    /// <summary>The table being searched.</summary>
    public ITableProvider TableProvider => _provider;

    /// <summary>The column the FTS predicate targets.</summary>
    public string ColumnName => _columnName;

    /// <summary>The raw query text before tokenization.</summary>
    public string QueryText => _queryText;

    /// <summary>Columns requested by downstream consumers, or <c>null</c> for all.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>Number of postings (per-term × per-document) consulted in the most recent run.</summary>
    public int PostingsConsulted { get; private set; }

    /// <summary>Number of rows that survived the AND intersection in the most recent run.</summary>
    public int MatchingRows { get; private set; }

    /// <inheritdoc />
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("FullTextSearch")
        {
            Properties = new Dictionary<string, string>
            {
                ["table"] = _provider.QualifiedName.ToString(),
                ["column"] = _columnName,
                ["query"] = _queryText,
            },
        };
    }

    /// <inheritdoc />
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter)
        => this; // No expressions to rewrite — the query text is a literal.

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CancellationToken cancellationToken = context.CancellationToken;

        if (!_provider.TryGetTextSearchIndex(_columnName, out ITextSearchIndex? index))
        {
            throw new InvalidOperationException(
                $"FullTextSearchOperator: table '{_provider.QualifiedName}' has no FTS index on column '{_columnName}'.");
        }

        // Collect unique post-analyzer tokens from the query.
        HashSet<string> queryTerms = new(StringComparer.Ordinal);
        foreach (Token t in index.Analyzer.Tokenize(_queryText))
        {
            queryTerms.Add(t.Term);
        }

        if (queryTerms.Count == 0)
        {
            // No surviving terms — caller (planner) should have shortcircuited.
            // Yield nothing rather than fall through to a full scan.
            yield break;
        }

        // Pull posting lists per term. Sort by size so we intersect smallest-first
        // (cheapest hash-set construction).
        List<IReadOnlyList<TextPosting>> postingLists = new(queryTerms.Count);
        foreach (string term in queryTerms)
        {
            IReadOnlyList<TextPosting> postings = index.FindPostings(term);
            if (postings.Count == 0)
            {
                // One term has zero postings → AND result is empty.
                PostingsConsulted = 0;
                MatchingRows = 0;
                yield break;
            }
            postingLists.Add(postings);
        }
        postingLists.Sort((a, b) => a.Count.CompareTo(b.Count));

        int totalConsulted = 0;
        foreach (IReadOnlyList<TextPosting> list in postingLists)
        {
            totalConsulted += list.Count;
        }
        PostingsConsulted = totalConsulted;

        HashSet<(int Chunk, long Row)> survivors = new(postingLists[0].Count);
        foreach (TextPosting p in postingLists[0])
        {
            survivors.Add((p.ChunkIndex, p.RowOffsetInChunk));
        }
        for (int i = 1; i < postingLists.Count; i++)
        {
            HashSet<(int Chunk, long Row)> next = new(postingLists[i].Count);
            foreach (TextPosting p in postingLists[i])
            {
                (int, long) key = (p.ChunkIndex, p.RowOffsetInChunk);
                if (survivors.Contains(key))
                {
                    next.Add(key);
                }
            }
            survivors = next;
            if (survivors.Count == 0) break;
        }

        MatchingRows = survivors.Count;
        if (survivors.Count == 0) yield break;

        // Resolve (chunk, row) → absolute row position via the source index's
        // chunk directory. The provider's source index is the same one the
        // FTS backfill consulted, so the mapping is in lockstep.
        SourceIndex? sourceIndex = _provider.GetSourceIndex();
        IReadOnlyList<IndexChunk>? chunks = sourceIndex?.Chunks;
        int defaultChunkSize = IndexConstants.DefaultChunkSize;

        long[] positions = new long[survivors.Count];
        int idx = 0;
        foreach ((int chunkIdx, long rowOff) in survivors)
        {
            long basePos = (chunks is not null && chunkIdx < chunks.Count)
                ? chunks[chunkIdx].RowOffset
                : (long)chunkIdx * defaultChunkSize;
            positions[idx++] = basePos + rowOff;
        }
        Array.Sort(positions);

        // Stream matching rows through a seek session. Mirrors ScanOperator's
        // exact-seek path.
        using ISeekSession seekSession = _provider.OpenSeekSession(_requiredColumns, context.Store);
        RowCopyOutputWriter writer = new(context);

        try
        {
            foreach (long rowPosition in positions)
            {
                await foreach (RowBatch inputBatch in seekSession.SeekAsync(
                    rowPosition, 1, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        for (int i = 0; i < inputBatch.Count; i++)
                        {
                            RowBatch? full = writer.Add(inputBatch, i);
                            if (full is not null) yield return full;
                        }
                    }
                    finally
                    {
                        context.ReturnRowBatch(inputBatch);
                    }
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }
}
