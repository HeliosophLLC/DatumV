using Heliosoph.DatumV.Indexing.Fts;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests for <see cref="FullTextSearchIndex"/>: insert + lookup, posting
/// order, prefix non-confusion (cat vs cats), persistence round-trip,
/// duplicate-posting tolerance, dispose contract.
/// </summary>
public sealed class FullTextSearchIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fts-test-{Guid.NewGuid():N}.datum-fts");
    private readonly IFullTextAnalyzer _analyzer = new SimpleEnglishAnalyzer();

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Create_EmptyIndex_HasNoPostings()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Equal(0, index.PostingCount);
    }

    [Fact]
    public void Create_RecordsColumnAndAnalyzer()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Equal("body", index.ColumnName);
        Assert.Same(_analyzer, index.Analyzer);
        Assert.Equal("simple_en", index.Analyzer.Name);
    }

    [Fact]
    public void InsertPosting_SingleTerm_FindablePosting()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.InsertPosting("cat", chunkIndex: 0, rowOffsetInChunk: 42);

        IReadOnlyList<TextPosting> postings = index.FindPostings("cat");

        Assert.Single(postings);
        Assert.Equal(new TextPosting(0, 42), postings[0]);
    }

    [Fact]
    public void FindPostings_UnknownTerm_ReturnsEmpty()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.InsertPosting("cat", 0, 5);

        Assert.Empty(index.FindPostings("dog"));
    }

    [Fact]
    public void FindPostings_MultiplePostingsForOneTerm_ReturnsAllInDocumentOrder()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");

        // Insert out of order; tree's dup-key tie-breaker must sort them by (chunk, row).
        index.InsertPosting("cat", 2, 0);
        index.InsertPosting("cat", 0, 5);
        index.InsertPosting("cat", 1, 100);
        index.InsertPosting("cat", 0, 1);

        IReadOnlyList<TextPosting> postings = index.FindPostings("cat");

        Assert.Equal(4, postings.Count);
        Assert.Equal(new TextPosting(0, 1), postings[0]);
        Assert.Equal(new TextPosting(0, 5), postings[1]);
        Assert.Equal(new TextPosting(1, 100), postings[2]);
        Assert.Equal(new TextPosting(2, 0), postings[3]);
    }

    [Fact]
    public void FindPostings_CatDoesNotMatchCatsPostings()
    {
        // The prefix-disambiguation contract from FtsPostingKeyEncoder:
        // searching for "cat" returns only "cat" postings, not "cats".
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");

        index.InsertPosting("cat", 0, 1);
        index.InsertPosting("cats", 0, 2);
        index.InsertPosting("cat", 0, 3);

        IReadOnlyList<TextPosting> cat = index.FindPostings("cat");
        IReadOnlyList<TextPosting> cats = index.FindPostings("cats");

        Assert.Equal(2, cat.Count);
        Assert.Equal(new TextPosting(0, 1), cat[0]);
        Assert.Equal(new TextPosting(0, 3), cat[1]);

        Assert.Single(cats);
        Assert.Equal(new TextPosting(0, 2), cats[0]);
    }

    [Fact]
    public void InsertPosting_DuplicateTriple_StoredAsTwoPostings_V1Behaviour()
    {
        // v1 docstring on InsertPosting says duplicate (term, chunk, row) is
        // the caller's problem. PR-FTS-C will dedupe via a higher-level
        // IndexDocument call. Lock in v1 behaviour explicitly.
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");

        index.InsertPosting("cat", 0, 5);
        index.InsertPosting("cat", 0, 5);

        Assert.Equal(2, index.PostingCount);
        Assert.Equal(2, index.FindPostings("cat").Count);
    }

    [Fact]
    public void InsertPosting_PostingCountReflectsInserts()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.InsertPosting("a", 0, 0);
        index.InsertPosting("b", 0, 1);
        index.InsertPosting("a", 0, 2);

        Assert.Equal(3, index.PostingCount);
    }

    [Fact]
    public void Open_AfterCreateAndInsert_PreservesPostings()
    {
        using (FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body"))
        {
            index.InsertPosting("cat", 0, 5);
            index.InsertPosting("dog", 1, 10);
            index.InsertPosting("cat", 2, 0);
        }

        using FullTextSearchIndex reopened = FullTextSearchIndex.Open(_path, _analyzer, "body");

        Assert.Equal(3, reopened.PostingCount);
        Assert.Equal(2, reopened.FindPostings("cat").Count);
        Assert.Single(reopened.FindPostings("dog"));
    }

    [Fact]
    public void InsertPosting_NegativeChunk_Throws()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Throws<ArgumentOutOfRangeException>(() => index.InsertPosting("cat", -1, 0));
    }

    [Fact]
    public void InsertPosting_NegativeRow_Throws()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Throws<ArgumentOutOfRangeException>(() => index.InsertPosting("cat", 0, -1));
    }

    [Fact]
    public void InsertPosting_NullTerm_Throws()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Throws<ArgumentNullException>(() => index.InsertPosting(null!, 0, 0));
    }

    [Fact]
    public void FindPostings_NullTerm_Throws()
    {
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        Assert.Throws<ArgumentNullException>(() => index.FindPostings(null!));
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.Dispose();
        index.Dispose(); // must not throw
    }

    [Fact]
    public void InsertPosting_AfterDispose_Throws()
    {
        FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.InsertPosting("cat", 0, 0));
    }

    [Fact]
    public void FindPostings_AfterDispose_Throws()
    {
        FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.FindPostings("cat"));
    }

    [Fact]
    public void ManyTermsManyPostings_NoCrosstalk()
    {
        // Light fuzz: 50 terms × 20 postings each, verify each term retrieves exactly its own.
        using FullTextSearchIndex index = FullTextSearchIndex.Create(_path, _analyzer, "body");

        const int termCount = 50;
        const int postingsPerTerm = 20;

        for (int t = 0; t < termCount; t++)
        {
            string term = $"term{t:D4}";
            for (int p = 0; p < postingsPerTerm; p++)
            {
                index.InsertPosting(term, chunkIndex: p % 5, rowOffsetInChunk: (t * 1000) + p);
            }
        }

        Assert.Equal(termCount * postingsPerTerm, index.PostingCount);

        for (int t = 0; t < termCount; t++)
        {
            string term = $"term{t:D4}";
            IReadOnlyList<TextPosting> postings = index.FindPostings(term);

            Assert.Equal(postingsPerTerm, postings.Count);
            foreach (TextPosting p in postings)
            {
                Assert.InRange(p.RowOffsetInChunk, t * 1000, (t * 1000) + postingsPerTerm - 1);
            }
        }
    }
}
