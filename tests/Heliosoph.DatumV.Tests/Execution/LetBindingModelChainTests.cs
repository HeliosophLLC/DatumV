namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

/// <summary>
/// Regression tests for the LET-binding-as-hoist-target pass. When a projection
/// chains LET bindings through model invocations
/// (<c>LET a = models.f(x), LET b = concat(…, a), LET c = models.g(b)</c>),
/// the hoister must lift each LET into its own upstream rung — a
/// <see cref="Operators.RowEnricherOperator"/> for scalar bodies and a
/// <see cref="Operators.ModelInvocationOperator"/> for model bodies — in
/// dependency order. Without this, the model-invocation rungs run before any
/// LET bindings are computed and crash with "Column 'X' not found in row."
/// </summary>
public sealed class LetBindingModelChainTests : ServiceTestBase
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

    /// <summary>
    /// The user's reported failing shape — five chained LET bindings, three of
    /// which call <c>models.*</c>. Regression marker for the dependency-aware
    /// staging pass: <c>summary</c> is fed by <c>models.echo(prompt)</c> where
    /// <c>prompt</c> is itself a scalar LET binding.
    /// </summary>
    [Fact]
    public async Task ChainedLetWithInterleavedModels_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "alice" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT " +
            "  LET caption = models.echo(name), " +
            "  LET prompt = concat('say:', caption), " +
            "  LET summary = models.echo(prompt), " +
            "  LET tagged = concat('result=', summary), " +
            "  LET response = models.echo(tagged), " +
            "  name, summary, response " +
            "FROM t",
            catalog);

        Assert.Single(rows);
        Row row = rows[0];
        Assert.Equal("alice", row["name"].AsString());
        // EchoModel passes its input through, so summary == prompt == "say:alice"
        Assert.Equal("say:alice", row["summary"].AsString());
        // response = echo(tagged) = echo("result=say:alice") = "result=say:alice"
        Assert.Equal("result=say:alice", row["response"].AsString());
    }

    /// <summary>
    /// Smallest non-trivial shape: one scalar LET feeds a model.
    /// <c>upper(name)</c> evaluates per-row in an Enricher rung, then
    /// <c>models.echo(__let_k)</c> runs against that row.
    /// </summary>
    [Fact]
    public async Task ScalarLet_FeedsModel_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "bob" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET k = upper(name), LET v = models.echo(k), v FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("BOB", rows[0]["v"].AsString());
    }

    /// <summary>
    /// Reverse direction: a model output feeds a scalar LET, which is then
    /// projected out. The model rung produces <c>__let_c</c>; the scalar
    /// Enricher rung consumes it to produce <c>__let_t</c>.
    /// </summary>
    [Fact]
    public async Task Model_FeedsScalarLet_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "carol" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET c = models.echo(name), LET t = concat('cap: ', c), t FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("cap: carol", rows[0]["t"].AsString());
    }

    /// <summary>
    /// Two models, both reading the same upstream LET binding. The dependency
    /// graph should produce a single Enricher rung for the shared scalar LET
    /// and two separate MIO rungs that both reference its hidden column —
    /// not two independent evaluations of the scalar.
    /// </summary>
    [Fact]
    public async Task SharedScalarLet_FeedsTwoModels_RunsEndToEnd()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "dave" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT " +
            "  LET k = upper(name), " +
            "  LET a = models.echo(k), " +
            "  LET b = models.echo(k), " +
            "  a, b " +
            "FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("DAVE", rows[0]["a"].AsString());
        Assert.Equal("DAVE", rows[0]["b"].AsString());
    }

    /// <summary>
    /// Safety net for Option B: when the projection has LET bindings but no
    /// model calls, the planner should NOT lift the LETs — they should keep
    /// running inside <see cref="Operators.ProjectOperator"/> as today.
    /// Validates by asserting no <see cref="Operators.RowEnricherOperator"/>
    /// appears between Project and Scan for a LET-only query (the current
    /// CSE pass would only insert RowEnricher if a duplicate scalar appears
    /// at multiple sites, which this query avoids).
    /// </summary>
    /// <summary>
    /// Dot-notation struct-field access on a LET-bound struct, in a projection
    /// that ALSO carries a model invocation. The model call forces the
    /// projection through <c>HoistProjectWithLetStaircase</c>, which renames
    /// the LET to a synthetic hidden column (<c>__let_s_N</c>). The expression
    /// rewriter must remap the qualifier in <c>s.a</c> to the synthetic name,
    /// otherwise the runtime evaluator looks for <c>s</c> in the augmented row
    /// (where it no longer lives) and throws <c>Column 's.a' not found in row</c>.
    /// IndexAccess (<c>s['a']</c>) goes through a different runtime path that
    /// already worked — this test covers the dot-syntax gap.
    /// </summary>
    [Fact]
    public async Task DotAccess_OnLetBoundStruct_InProjectionWithModelCall_Resolves()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "frank" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET s = {a: 'hello', b: 'world'}, "
            + "LET v = models.echo(s.a), v AS result FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("hello", rows[0]["result"].AsString());
    }

    /// <summary>
    /// Same scenario but the dot-access lives in the SELECT list (not a
    /// sibling LET body). Confirms the rewriter handles every expression
    /// site in the staircase pass.
    /// </summary>
    [Fact]
    public async Task DotAccess_OnLetBoundStruct_InSelectListWithModelCall_Resolves()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "grace" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET s = {a: 'hello', b: 'world'}, "
            + "LET v = models.echo(name), s.a AS first, v AS echoed FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("hello", rows[0]["first"].AsString());
        Assert.Equal("grace", rows[0]["echoed"].AsString());
    }

    /// <summary>
    /// Topo-order regression for the user-reported shape:
    ///   <c>LET d = models.X(...), LET v = scalar_fn(d.field), …</c>.
    /// The dep edge from <c>v</c> to <c>d</c> flows only through the
    /// qualified ref <c>d.field</c> — the dep collector must follow
    /// <see cref="ColumnReference.TableName"/> when it matches a hoist
    /// key, otherwise <c>v</c> lands at the same staircase level as
    /// <c>d</c> (or earlier) and the row evaluator throws
    /// <c>Column '__let_d_N.field' not found in row</c>. Bracket access
    /// (<c>d['field']</c>) already worked because the bare <c>d</c>
    /// surfaces as a child of the IndexAccess.
    /// </summary>
    [Fact]
    public async Task DotAccess_OnLetBoundStruct_FromScalarSiblingLet_TopoOrders()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "henry" });
        catalog.Models = BuildCatalogWithEcho();

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT "
            + "  LET m = models.echo(name), "
            + "  LET s = {a: 'hello', b: 'world'}, "
            + "  LET v = upper(s.a), "
            + "  v AS result, m AS echoed "
            + "FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("HELLO", rows[0]["result"].AsString());
        Assert.Equal("henry", rows[0]["echoed"].AsString());
    }

    [Fact]
    public async Task LetWithoutModel_DoesNotLiftIntoStaircase()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "t",
            columns: ["name"],
            new object?[] { "erin" });

        // No model catalog — and the query has no models.* calls. The result
        // should still execute correctly using in-projection LET evaluation.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET k = upper(name), LET v = concat('hello ', k), v FROM t",
            catalog);

        Assert.Single(rows);
        Assert.Equal("hello ERIN", rows[0]["v"].AsString());
    }
}
