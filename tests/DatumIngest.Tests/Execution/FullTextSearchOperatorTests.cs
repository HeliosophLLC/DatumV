using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing.Fts;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="FullTextSearchOperator"/>: AND-of-terms matching,
/// stop-word filtering parity with the index, posting-list intersection,
/// empty-query short-circuit, no-match short-circuit, projection
/// pushdown.
/// </summary>
public sealed class FullTextSearchOperatorTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_ftsop_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

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

    [Fact]
    public async Task Execute_SingleTerm_MatchesEveryRowContainingTerm()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'lazy dog under the bench'), " +
            "(3, 'fox jumped over')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "fox");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        int[] ids = rows.Select(r => r["id"].AsInt32()).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1, 3 }, ids);
        Assert.Equal(2, op.MatchingRows);
    }

    [Fact]
    public async Task Execute_TwoTerms_AndIntersection()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'quick brown fox'), " +     // fox + quick → match
            "(2, 'lazy brown bench'), " +    // no fox
            "(3, 'quick fox')");             // fox + quick → match
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "fox quick");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        int[] ids = rows.Select(r => r["id"].AsInt32()).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public async Task Execute_NoMatchingTerm_YieldsNoRows()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'quick brown fox'), (2, 'lazy dog')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "elephant");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        Assert.Empty(rows);
        Assert.Equal(0, op.MatchingRows);
    }

    [Fact]
    public async Task Execute_OneTermAbsent_AndShortCircuitsToEmpty()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'quick brown fox')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "fox elephant");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Execute_EmptyQuery_YieldsNoRows()
    {
        // Query with only stop words tokenizes to nothing. Operator yields zero
        // rows rather than degrading to full scan — planner is expected to not
        // route empty queries through here.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'fox'), (2, 'cat')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "the and or");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Execute_StopWordsInQuery_FilteredViaAnalyzer()
    {
        // Query "the fox" → tokenizes to ["fox"]; should match rows containing fox.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'a fox'), (2, 'a cat'), (3, 'the fox')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "the fox");
        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        int[] ids = rows.Select(r => r["id"].AsInt32()).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public async Task Execute_NoFtsIndexOnColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'fox')");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "fox");
        ExecutionContext ctx = CreateExecutionContext(catalog: catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await op.CollectRowsAsync(ctx));
    }

    [Fact]
    public async Task Execute_RequiredColumnsRestrictsProjection()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String, author String)");
        catalog.Plan("INSERT INTO messages VALUES (1, 'fox', 'alice'), (2, 'cat', 'bob')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(
            catalog, "messages", "body", queryText: "fox",
            requiredColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" });

        List<Row> rows = await op.CollectRowsAsync(CreateExecutionContext(catalog: catalog));

        Assert.Single(rows);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        // The operator forwards required-columns into the seek session;
        // unselected columns are absent from the emitted row schema.
        Assert.DoesNotContain(rows[0].ColumnNames, c => c.Equals("body", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rows[0].ColumnNames, c => c.Equals("author", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DescribeForExplain_ReportsKeyProperties()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        FullTextSearchOperator op = BuildOp(catalog, "messages", "body", queryText: "anything");
        OperatorPlanDescription describe = op.DescribeForExplain();

        Assert.Equal("FullTextSearch", describe.OperatorName);
        Assert.NotNull(describe.Properties);
        Assert.Equal("messages", describe.Properties!["table"]);
        Assert.Equal("body", describe.Properties["column"]);
        Assert.Equal("anything", describe.Properties["query"]);
    }

    private static FullTextSearchOperator BuildOp(
        TableCatalog catalog,
        string table,
        string column,
        string queryText,
        IReadOnlySet<string>? requiredColumns = null)
    {
        Assert.True(catalog.TryGetTable(table, out ITableProvider? provider));
        return new FullTextSearchOperator(provider!, column, queryText, requiredColumns);
    }
}
