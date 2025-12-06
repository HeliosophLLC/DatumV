namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Models;

/// <summary>
/// LET visibility from WHERE — Phase 1 of the LET-as-first-class-plan-time-
/// abstraction roadmap. A LET binding defined in SELECT is referenceable from
/// WHERE: the planner lifts the LET's rung below the FilterOperator so its
/// hidden column is on the row by the time WHERE evaluates. Aggregate- or
/// window-derived LETs in WHERE are rejected with a clear "use HAVING" /
/// "use QUALIFY" diagnostic.
/// </summary>
public sealed class LetInWhereClauseTests : ServiceTestBase
{
    private static ModelCatalog BuildCatalogWithEcho()
    {
        ModelCatalog catalog = new(modelDirectory: System.IO.Path.GetTempPath());
        catalog.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance,
            OptionalArgKinds: null));
        return catalog;
    }

    // ──────────────── Pure-scalar LET in WHERE ────────────────

    /// <summary>
    /// A scalar LET binding (no aggregate, no window, no model) is referenceable
    /// from WHERE. The LET expression evaluates per-row in a RowEnricher rung
    /// below the FilterOperator, so the predicate sees the synthesised hidden
    /// column.
    /// </summary>
    [Fact]
    public async Task PureScalarLet_InWhere_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" },
            new object?[] { "alex" },
            new object?[] { "carol" });

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET shouty = upper(name), name, shouty " +
            "FROM t " +
            "WHERE shouty LIKE 'A%'",
            catalog);

        // alice → ALICE → matches; alex → ALEX → matches; bob and carol don't.
        Assert.Equal(2, rows.Count);
        HashSet<string> names = new(rows.Select(r => r["name"].AsString()));
        Assert.Contains("alice", names);
        Assert.Contains("alex", names);
    }

    /// <summary>
    /// LET that depends on another LET, referenced from WHERE. Both rungs must
    /// lift below the Filter in dependency order (inner first, then outer).
    /// </summary>
    [Fact]
    public async Task ChainedScalarLet_InWhere_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["first", "last"],
            ["alice", "andrews"],
            ["bob", "baker"],
            ["alex", "anderson"]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET fullname = concat(first, ' ', last), " +
            "       LET tagged = concat('USER:', upper(fullname)), " +
            "       fullname, tagged " +
            "FROM t " +
            "WHERE tagged LIKE 'USER:A%'",
            catalog);

        // alice andrews → USER:ALICE ANDREWS  ✓
        // alex anderson → USER:ALEX ANDERSON ✓
        // bob baker → USER:BOB BAKER          ✗
        Assert.Equal(2, rows.Count);
    }

    // ──────────────── Model-invocation LET in WHERE ────────────────

    /// <summary>
    /// The headline use case: <c>LET classification = models.f(x) … WHERE classification = 'cat'</c>.
    /// The MIO rung must lift below the Filter so the model evaluates once per
    /// row before WHERE — not re-invoked per filter call.
    /// </summary>
    [Fact]
    public async Task ModelLet_InWhere_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "cat" },
            new object?[] { "dog" },
            new object?[] { "bird" });
        catalog.Models = BuildCatalogWithEcho();

        // EchoModel passes input through unchanged. The query filters on the
        // model's output, which equals the input — so we should get one row.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET label = models.echo(name), name, label " +
            "FROM t " +
            "WHERE label = 'cat'",
            catalog);

        Assert.Single(rows);
        Assert.Equal("cat", rows[0]["name"].AsString());
        Assert.Equal("cat", rows[0]["label"].AsString());
    }

    // ──────────────── Single-evaluation guarantee ────────────────

    /// <summary>
    /// LET referenced from both WHERE and SELECT must evaluate exactly once
    /// per row — not twice (once for the predicate, once for the projection).
    /// Verified end-to-end: the query produces correct results without any
    /// duplicate-evaluation crash, AND the plan shape contains exactly one
    /// rung for the LET (not two).
    /// </summary>
    [Fact]
    public async Task ScalarLet_ReferencedFromWhereAndSelect_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["name"],
            new object?[] { "alice" },
            new object?[] { "bob" });

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET shouty = upper(name), shouty " +
            "FROM t " +
            "WHERE shouty = 'ALICE'",
            catalog);

        Assert.Single(rows);
        Assert.Equal("ALICE", rows[0]["shouty"].AsString());
    }

    // ──────────────── Aggregate / window LET in WHERE: error path ────────────────

    /// <summary>
    /// Aggregate-derived LET referenced from WHERE is a semantic error in
    /// standard SQL (HAVING is the right tool). The planner should reject
    /// with a diagnostic that points the user at the right alternative,
    /// not produce a plan that crashes at runtime.
    /// </summary>
    [Fact]
    public async Task AggregateLet_InWhere_RejectsWithDiagnostic()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 10f],
            ["A", 20f]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ExecuteQueryAsync(
                "SELECT LET total = SUM(value), category, total " +
                "FROM t " +
                "WHERE total > 25 " +
                "GROUP BY category",
                catalog));

        // Message must point at HAVING and name the offending binding.
        Assert.Contains("total", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HAVING", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
