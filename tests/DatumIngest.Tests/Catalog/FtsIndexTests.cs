using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Indexing.Fts;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Lifecycle tests for full-text indexes — <c>CREATE INDEX ... USING FTS</c>,
/// <c>DROP INDEX</c>, backfill on populated tables, catalog reopen,
/// validation errors. Mirrors <see cref="CompositeIndexTests"/> for the FTS
/// path so coverage of the two index kinds stays parallel.
/// </summary>
public sealed class FtsIndexTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_fts_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");
    private string FtsPath(string tableName, string column) =>
        Path.Combine(_tempDir, "data", "public", $"{tableName}.datum-fts-{column}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ──────────────────── File lifecycle ────────────────────

    [Fact]
    public void CreateIndex_UsingFts_CreatesSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        Assert.True(File.Exists(FtsPath("messages", "body")));
    }

    [Fact]
    public void CreateIndex_UsingFts_DefaultAnalyzerIsSimpleEn()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Equal("simple_en", index!.Analyzer.Name);
    }

    [Fact]
    public void CreateIndex_UsingFts_WithExplicitAnalyzer_Persisted()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS WITH (analyzer = 'simple_en')");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out _));
    }

    [Fact]
    public void DropIndex_UsingFts_RemovesSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        Assert.True(File.Exists(FtsPath("messages", "body")));

        catalog.Plan("DROP INDEX idx_msg_body");
        Assert.False(File.Exists(FtsPath("messages", "body")));
    }

    [Fact]
    public void DropIndex_UsingFts_UnregistersFromProvider()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        catalog.Plan("DROP INDEX idx_msg_body");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.False(provider!.TryGetTextSearchIndex("body", out _));
    }

    // ──────────────────── Backfill on populated tables ────────────────────

    [Fact]
    public void CreateIndex_UsingFts_OnPopulatedTable_BackfillsPostings()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'lazy dog under the bench'), " +
            "(3, 'fox jumped over')");

        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));

        // "fox" appears in rows 1 and 3; "lazy" only in row 2.
        Assert.Equal(2, index!.FindPostings("fox").Count);
        Assert.Single(index.FindPostings("lazy"));

        // Stop words filtered ("the", "over") — they should yield no postings.
        Assert.Empty(index.FindPostings("the"));
    }

    [Fact]
    public void CreateIndex_UsingFts_BackfillDedupesWithinDocument()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'fox fox fox fox')");

        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));

        // 4 occurrences of "fox" in the same document → 1 posting (PR-A2's
        // per-doc dedup at backfill time, ahead of PR-FTS-B term frequency).
        Assert.Single(index!.FindPostings("fox"));
    }

    [Fact]
    public void CreateIndex_UsingFts_BackfillSkipsNullValues()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'fox'), " +
            "(2, NULL), " +
            "(3, 'fox again')");

        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));

        Assert.Equal(2, index!.FindPostings("fox").Count);
    }

    // ──────────────────── Catalog persistence ────────────────────

    [Fact]
    public void CreateIndex_UsingFts_SurvivesCatalogReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE messages (id Int32, body String)");
            catalog.Plan("INSERT INTO messages VALUES (1, 'persistent fox')");
            catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Assert.True(reopened.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Equal("simple_en", index!.Analyzer.Name);
        Assert.Single(index.FindPostings("fox"));
    }

    // ──────────────────── Validation errors ────────────────────

    [Fact]
    public void CreateIndex_UsingFts_NonStringColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, n Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX bad ON t (n) USING FTS"));

        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public void CreateIndex_UsingFts_MultipleColumns_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a String, b String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX bad ON t (a, b) USING FTS"));

        Assert.Contains("exactly one column", ex.Message);
    }

    [Fact]
    public void CreateIndex_UsingFts_UniqueFlag_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE UNIQUE INDEX bad ON t (a) USING FTS"));

        Assert.Contains("UNIQUE", ex.Message);
    }

    [Fact]
    public void CreateIndex_UsingFts_DuplicateColumn_Throws()
    {
        // v1 restriction: one FTS index per column. Deferred-decisions #5
        // covers the multi-analyzer-per-column extension.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a String)");
        catalog.Plan("CREATE INDEX idx_one ON t (a) USING FTS");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_two ON t (a) USING FTS"));

        Assert.Contains("already has a full-text index", ex.Message);
    }

    [Fact]
    public void CreateIndex_UsingFts_UnknownAnalyzer_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX bad ON t (a) USING FTS WITH (analyzer = 'porter_en')"));

        Assert.Contains("porter_en", ex.Message);
    }

    [Fact]
    public void CreateIndex_UsingFts_UnknownOption_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX bad ON t (a) USING FTS WITH (bogus = 'x')"));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void CreateIndex_Composite_WithUnknownWithOption_Throws()
    {
        // WITH options aren't valid on composite indexes — typos shouldn't be
        // silently dropped.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX bad ON t (a) WITH (analyzer = 'simple_en')"));

        Assert.Contains("WITH options", ex.Message);
    }

    // ──────────────────── Incremental maintenance (H1') ────────────────────

    [Fact]
    public void Insert_AfterCreateIndex_PostingsReflectNewRows()
    {
        // Regression: pre-H1' an INSERT after CREATE INDEX would leave the
        // FTS sidecar empty, and the rewritten plan would silently return
        // zero results. Postings must be queued and flushed per WriteAsync.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'lazy dog'), " +
            "(3, 'fox jumped over')");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));

        Assert.Equal(2, index!.FindPostings("fox").Count);
        Assert.Single(index.FindPostings("lazy"));
        Assert.Empty(index.FindPostings("the"));
    }

    [Fact]
    public void Insert_AfterCreateIndex_DedupesTermsWithinDocument()
    {
        // Per-row dedup matches BackfillFtsIndexAsync: one posting per
        // (term, document) regardless of repeat-count.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan("INSERT INTO messages VALUES (1, 'fox fox fox fox')");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Single(index!.FindPostings("fox"));
    }

    [Fact]
    public void Insert_AfterCreateIndex_SkipsNullValues()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'fox'), " +
            "(2, NULL), " +
            "(3, 'fox again')");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Equal(2, index!.FindPostings("fox").Count);
    }

    [Fact]
    public void Insert_AfterCreateIndex_AcceleratedQueryFindsNewRows()
    {
        // End-to-end: the planner injects FullTextSearchOperator on `@@`, so
        // an INSERT after CREATE INDEX must keep the operator-path answer
        // consistent with the filter-path answer. Before H1' this returned
        // zero rows even though the data was committed.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'lazy dog'), " +
            "(3, 'fox jumped')");

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");
        List<int> ids = new();
        foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None).ToBlockingEnumerable())
        {
            for (int i = 0; i < batch.Count; i++) ids.Add(batch[i][0].AsInt32());
        }
        ids.Sort();
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public void Insert_MultipleBatches_AccumulatesPostingsAcrossInserts()
    {
        // Each INSERT runs its own AppendSession; the queue is per-session,
        // so a second INSERT must extend (not replace) the on-disk postings.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        catalog.Plan("INSERT INTO messages VALUES (1, 'fox one')");
        catalog.Plan("INSERT INTO messages VALUES (2, 'fox two')");
        catalog.Plan("INSERT INTO messages VALUES (3, 'cat three')");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Equal(2, index!.FindPostings("fox").Count);
        Assert.Single(index.FindPostings("cat"));
    }

    [Fact]
    public void Update_RewritesPostingsForChangedRows()
    {
        // Pre-H1' an UPDATE rewrote the indexed text but left old postings
        // pointing at rows whose body no longer matched. Full rebuild after
        // UPDATE makes the FTS sidecar consistent with the data.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'old fox'), " +
            "(2, 'second message')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan("UPDATE messages SET body = 'new wolf' WHERE id = 1");

        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        // Old term 'fox' no longer matches; new term 'wolf' does.
        Assert.Empty(index!.FindPostings("fox"));
        Assert.Single(index.FindPostings("wolf"));
    }

    [Fact]
    public void Update_AcceleratedQueryReflectsNewValues()
    {
        // End-to-end UPDATE check: querying the new term must return the
        // updated row, and the old term must not.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'old fox'), " +
            "(2, 'fox stays')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan("UPDATE messages SET body = 'new wolf' WHERE id = 1");

        IQueryPlan wolfPlan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'wolf'");
        List<int> wolfIds = CollectIds(wolfPlan);
        Assert.Equal(new[] { 1 }, wolfIds);

        IQueryPlan foxPlan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");
        List<int> foxIds = CollectIds(foxPlan);
        Assert.Equal(new[] { 2 }, foxIds);
    }

    private static List<int> CollectIds(IQueryPlan plan)
    {
        List<int> ids = new();
        foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None).ToBlockingEnumerable())
        {
            for (int i = 0; i < batch.Count; i++) ids.Add(batch[i][0].AsInt32());
        }
        ids.Sort();
        return ids;
    }

    // ──────────────────── DROP TABLE / ALTER DROP COLUMN cascade (H2 / H3) ────────────────────

    [Fact]
    public void DropTable_RemovesFtsSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        Assert.True(File.Exists(FtsPath("messages", "body")));

        catalog.Plan("DROP TABLE messages");

        Assert.False(File.Exists(FtsPath("messages", "body")));
    }

    [Fact]
    public void DropTable_SweepsBothCompositeAndFtsSidecars()
    {
        // Mixed-index table: DROP TABLE must sweep both .datum-cindex-* and
        // .datum-fts-* sidecars in one shot.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, title String, body String)");
        catalog.Plan("CREATE INDEX idx_title ON t (title)");
        catalog.Plan("CREATE INDEX idx_body ON t (body) USING FTS");
        Assert.True(File.Exists(FtsPath("t", "body")));

        catalog.Plan("DROP TABLE t");

        Assert.False(File.Exists(FtsPath("t", "body")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "data", "public", "t.datum-cindex-idx_title")));
    }

    [Fact]
    public void AlterDropColumn_DropsDependentFtsIndex()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");
        Assert.True(File.Exists(FtsPath("messages", "body")));

        catalog.Plan("ALTER TABLE messages DROP COLUMN body");

        // Sidecar file removed.
        Assert.False(File.Exists(FtsPath("messages", "body")));

        // Provider's open dict no longer carries the index.
        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.False(provider!.TryGetTextSearchIndex("body", out _));
    }

    [Fact]
    public void AlterDropColumn_OnUnindexedColumn_LeavesFtsIndexIntact()
    {
        // Dropping a column that's NOT covered by the FTS index shouldn't
        // touch it. Mirrors the composite-index "non-cascading drop" case.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String, note String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'searchable fox', 'aside')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        catalog.Plan("ALTER TABLE messages DROP COLUMN note");

        Assert.True(File.Exists(FtsPath("messages", "body")));
        Assert.True(catalog.TryGetTable("messages", out ITableProvider? provider));
        Assert.True(provider!.TryGetTextSearchIndex("body", out ITextSearchIndex? index));
        Assert.Single(index!.FindPostings("fox"));
    }

    // ──────────────────── DELETE smoke test ────────────────────

    [Fact]
    public void Delete_WithoutReindex_FilterPathStillReturnsCorrectRows()
    {
        // Documents the v1 limitation: DELETE soft-deletes via tombstones
        // but doesn't trigger an FTS rebuild (the public DeleteRows API is
        // synchronous; the rebuild is async-only). The FTS-accelerated
        // operator may resolve postings to wrong rows after a DELETE — the
        // user-visible recovery is REINDEX. The filter-path fallback
        // (without an index) always returns correct results, and that's
        // what we assert here to lock in the worst-case bound. The same
        // staleness affects composite indexes.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'fox one'), " +
            "(2, 'fox two'), " +
            "(3, 'cat three')");

        catalog.Plan("DELETE FROM messages WHERE id = 1");

        // Query without an FTS index (the filter path) — must be correct.
        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");
        List<int> ids = CollectIds(plan);
        Assert.Equal(new[] { 2 }, ids);
    }
}
