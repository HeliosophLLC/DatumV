using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Phase 6: scan-side filter type-coercion behavior. SQL literals
/// parse as <c>String</c> for date/time/uuid forms (the parser doesn't
/// know the target column kind), so a predicate like
/// <c>WHERE date_col = '2026-01-02'</c> arrives at the runtime
/// comparison with one Date operand and one String operand. The
/// runtime must coerce the literal to the column's kind so equality
/// returns the right answer. These tests pin the post-fix behavior.
/// </summary>
public sealed class FilterCoercionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_fc_{Guid.NewGuid():N}");
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
    public async Task DateColumn_EqualsStringLiteral_MatchesRow()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (d Date, v Int32)");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-01', 100), ('2026-01-02', 200), ('2026-01-03', 300)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE d = '2026-01-02'");
        List<int> values = await CollectInts(plan);

        Assert.Single(values);
        Assert.Equal(200, values[0]);
    }

    [Fact]
    public async Task DateColumn_StringLiteralOnLeft_MatchesRow()
    {
        // Operand order should not matter: `'…' = d` and `d = '…'` must
        // both produce the same coercion result.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (d Date, v Int32)");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-01', 100), ('2026-01-02', 200)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE '2026-01-02' = d");
        List<int> values = await CollectInts(plan);

        Assert.Single(values);
        Assert.Equal(200, values[0]);
    }

    [Fact]
    public async Task DateColumn_LessThanStringLiteral_MatchesEarlierRows()
    {
        // Range comparisons need the same coercion treatment as equality.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (d Date, v Int32)");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-01', 100), ('2026-01-05', 500), ('2026-01-10', 1000)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE d < '2026-01-05'");
        List<int> values = await CollectInts(plan);

        Assert.Single(values);
        Assert.Equal(100, values[0]);
    }

    [Fact]
    public async Task UuidColumn_EqualsStringLiteral_MatchesRow()
    {
        // Same shape applies to Uuid columns and string-form Uuid literals.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Uuid, v Int32)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "('11111111-1111-1111-1111-111111111111', 1), " +
            "('22222222-2222-2222-2222-222222222222', 2)");

        IQueryPlan plan = catalog.Plan(
            "SELECT v FROM t WHERE id = '22222222-2222-2222-2222-222222222222'");
        List<int> values = await CollectInts(plan);

        Assert.Single(values);
        Assert.Equal(2, values[0]);
    }

    [Fact]
    public async Task DateColumn_StringLiteralNonParseable_NoMatch_NoThrow()
    {
        // A literal that can't be coerced into the column's kind should
        // produce zero matches, not crash the query. Matches Postgres
        // implicit-cast behavior at runtime: invalid date strings
        // compare as unequal.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (d Date, v Int32)");
        catalog.Plan("INSERT INTO t VALUES ('2026-01-01', 100)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE d = 'not-a-date'");
        List<int> values = await CollectInts(plan);

        Assert.Empty(values);
    }

    private static async Task<List<int>> CollectInts(IQueryPlan plan)
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
}
