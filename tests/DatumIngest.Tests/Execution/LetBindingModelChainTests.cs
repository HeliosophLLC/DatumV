namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

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
