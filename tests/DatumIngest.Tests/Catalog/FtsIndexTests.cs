using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing.Fts;

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
        Path.Combine(_tempDir, $"{tableName}.datum-fts-{column}");

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
}
