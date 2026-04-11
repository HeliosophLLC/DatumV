using DatumIngest.Catalog;
using DatumIngest.Execution;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <c>ScanOperator</c>'s top-level predicate extraction in
/// <c>CollectExactSeekPositions</c>: the extractors
/// (<c>ExtractTopLevelEqualities</c> / <c>Betweens</c> / <c>Ins</c>) only
/// descend through AND chains; OR branches halt extraction. Without these
/// tests, the AND-only invariant is exercised only transitively through
/// composite-index tests — slice S4 (predicate-walker consolidation) needs a
/// direct guard so a refactor can't silently regress the invariant.
/// </summary>
public sealed class ScanOperatorPredicateExtractionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_extract_{Guid.NewGuid():N}");
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

    /// <summary>
    /// <c>WHERE a = 1 OR b = 20</c> — top-level operator is OR, so
    /// <c>ExtractTopLevelEqualities</c> yields nothing.
    /// <c>CollectExactSeekPositions</c> returns null and the exact-seek code
    /// path doesn't fire. ExactSeekRowsFetched stays unset.
    /// </summary>
    [Fact]
    public async Task TopLevelOr_DoesNotExtractEqualities_FallsBackToScan()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_b ON t (b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 11), (2, 20, 22), (3, 30, 33)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 OR b = 20");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        Assert.Null(exactSeek);
    }

    /// <summary>
    /// <c>WHERE a = 1 AND (b = 20 OR c = 300)</c> — top-level is AND. Left
    /// side contributes <c>(a, 1)</c>. Right side is OR, so its components
    /// <c>(b, 20)</c> and <c>(c, 300)</c> are NOT extracted. The seek
    /// planner therefore only probes the <c>a</c> index.
    /// <para>
    /// Data is shaped so <c>a=1</c> has more matches than <c>b=20</c> or
    /// <c>c=300</c>; if the OR were wrongly extracted, the fewest-positions
    /// tiebreak in <c>CollectExactSeekPositions</c> would pick the 1-row
    /// match (from b or c). With correct OR-blocking,
    /// <c>ExactSeekRowsFetched</c> reflects <c>a=1</c>'s count of 3.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AndWithEmbeddedOr_ExtractsOnlyAndedEqualities()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_b ON t (b)");
        catalog.Plan("CREATE INDEX idx_t_c ON t (c)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 100, 110), " +
            "(1, 20, 200, 120), " +
            "(1, 30, 300, 130), " +
            "(2, 40, 400, 240), " +
            "(3, 50, 500, 350)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND (b = 20 OR c = 300)");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        // a=1 → 3 rows. If OR were wrongly extracted, the picker would settle
        // on either b=20 (1 row) or c=300 (1 row) instead of a=1's 3.
        Assert.Equal(3, exactSeek);
    }

    private static async Task<int?> GetScanExactSeekRowsAsync(IQueryPlan plan)
    {
        ExplainPlanNode root = await plan.AnalyzeAsync(CancellationToken.None);
        return FindScanNode(root)?.ExactSeekRowsFetched;
    }

    private static ExplainPlanNode? FindScanNode(ExplainPlanNode node)
    {
        if (node.OperatorName == "Scan") return node;
        foreach (ExplainPlanNode child in node.Children)
        {
            ExplainPlanNode? found = FindScanNode(child);
            if (found is not null) return found;
        }
        return null;
    }
}
