using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for PR-FTS-A4: the planner rewrite that replaces
/// <c>ScanOperator + FilterOperator(tsquery_match(col, q))</c> with a
/// <c>FullTextSearchOperator</c> when the column has an FTS index.
/// Covers the happy path, the safety fallbacks (no index → keep filter;
/// non-constant query → keep filter; empty query → keep filter to preserve
/// match-all semantics), residual-predicate handling, and end-to-end SELECT
/// execution through the rewrite.
/// </summary>
public sealed class FullTextSearchPlannerTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_ftsplan_{Guid.NewGuid():N}");
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

    // ──────────────────── Rewrite happens / falls through ────────────────────

    [Fact]
    public void Plan_WithIndexAndLiteralQuery_InjectsFullTextSearchOperator()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");

        Assert.True(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.False(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    [Fact]
    public void Plan_WithIndexAndPlainToTsqueryWrapper_InjectsFullTextSearchOperator()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan(
            "SELECT id FROM messages WHERE body @@ plainto_tsquery('fox')");

        Assert.True(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
    }

    [Fact]
    public void Plan_NoIndex_FallsBackToScanPlusFilter()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        // No CREATE INDEX — planner must not inject the FTS op.

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");

        Assert.False(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.True(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    [Fact]
    public void Plan_NonConstantRhs_FallsBackToScanPlusFilter()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String, q String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        // RHS is a column reference, not a literal — can't determine at plan time.
        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ q");

        Assert.False(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.True(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    [Fact]
    public void Plan_EmptyQueryString_FallsBackToScanPlusFilter()
    {
        // Empty query: tsquery_match returns true (match-all), FTS operator
        // returns nothing. Planner keeps the filter path for semantic parity.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ ''");

        Assert.False(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.True(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    [Fact]
    public void Plan_StopWordOnlyQuery_FallsBackToScanPlusFilter()
    {
        // Stop-word-only query tokenizes to zero terms → same match-all case.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'the and or'");

        Assert.False(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.True(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    [Fact]
    public void Plan_FtsPlusResidualPredicate_KeepsResidualAsFilter()
    {
        // FTS predicate + non-FTS predicate ANDed. The FTS predicate should
        // be pushed into the operator; the residual stays as a FilterOperator.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan(
            "SELECT id FROM messages WHERE body @@ 'fox' AND id > 5");

        Assert.True(ContainsOperator(plan.ExplainTree, "FullTextSearch"));
        Assert.True(ContainsOperator(plan.ExplainTree, "Filter"));
    }

    // ──────────────────── End-to-end execution through the rewrite ────────────────────

    [Fact]
    public async Task Execute_FtsAcceleratedQuery_MatchesExpectedRows()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick brown fox'), " +
            "(2, 'lazy dog under the bench'), " +
            "(3, 'fox jumped over')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");

        int[] ids = (await CollectFirstColumnInts(plan)).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    [Fact]
    public async Task Execute_FtsPlusResidualPredicate_FiltersBoth()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'fox one'), " +
            "(2, 'fox two'), " +
            "(7, 'fox seven'), " +
            "(8, 'cat eight')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan(
            "SELECT id FROM messages WHERE body @@ 'fox' AND id > 5");

        int[] ids = (await CollectFirstColumnInts(plan)).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 7 }, ids);
    }

    [Fact]
    public async Task Execute_QueryThroughPlainToTsquery_MatchesSameRows()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'fox quick brown'), " +
            "(2, 'fox lazy'), " +
            "(3, 'cat brown')");
        catalog.Plan("CREATE INDEX idx_msg_body ON messages (body) USING FTS");

        IQueryPlan plan = catalog.Plan(
            "SELECT id FROM messages WHERE body @@ plainto_tsquery('fox brown')");

        int[] ids = (await CollectFirstColumnInts(plan)).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 1 }, ids);
    }

    [Fact]
    public async Task Execute_WithoutIndex_StillWorksViaFilterPath()
    {
        // Sanity check: without an FTS index, the function-evaluation path
        // still returns correct results (proving the rewrite is an
        // optimisation, not a correctness requirement).
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE messages (id Int32, body String)");
        catalog.Plan(
            "INSERT INTO messages VALUES " +
            "(1, 'the quick fox'), " +
            "(2, 'cat sat on mat')");
        // Deliberately no CREATE INDEX.

        IQueryPlan plan = catalog.Plan("SELECT id FROM messages WHERE body @@ 'fox'");

        List<int> ids = await CollectFirstColumnInts(plan);
        Assert.Single(ids);
        Assert.Equal(1, ids[0]);
    }

    // ──────────────────── Helpers ────────────────────

    private static async Task<List<int>> CollectFirstColumnInts(IQueryPlan plan)
    {
        List<int> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsInt32());
            }
        }
        return values;
    }

    private static bool ContainsOperator(ExplainPlanNode root, string operatorName)
    {
        Stack<ExplainPlanNode> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ExplainPlanNode node = stack.Pop();
            if (node.OperatorName.Equals(operatorName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            foreach (ExplainPlanNode child in node.Children)
            {
                stack.Push(child);
            }
        }
        return false;
    }
}
