using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Models;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for procedural UDFs (<c>CREATE FUNCTION … BEGIN…END</c>):
/// register a UDF whose body is a sequence of <c>DECLARE</c> / <c>SET</c> /
/// <c>IF</c> / <c>RETURN</c> statements, plan a query that calls it, and
/// execute the plan against in-memory rows. Exercises the full path the
/// PR3 adapter introduces — UdfInliner pass-through, scalar dispatch into
/// the runtime adapter, per-call <see cref="VariableScope"/>, and cycle
/// detection for transitive recursion.
/// </summary>
public class ProceduralUdfExecutionTests : ServiceTestBase
{
    private static async Task<List<DataValue>> CollectFirstColumnAsync(IQueryPlan plan)
    {
        List<DataValue> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        return values;
    }

    // ───────────────────── Smallest viable procedural UDF ─────────────────────

    [Fact]
    public async Task SingleReturn_EvaluatesBodyExpression()
    {
        // Smallest legal procedural UDF: one statement, RETURN expr.
        // The adapter should dispatch the call, evaluate @x * @x against
        // the parameter binding, and return Int32.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 5 });

        catalog.Plan(
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");
        IQueryPlan plan = catalog.Plan("SELECT udf.sq(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Single(values);
        Assert.Equal(DataKind.Int32, values[0].Kind);
        Assert.Equal(25, values[0].AsInt32());
    }

    [Fact]
    public async Task DeclareThenReturn_EvaluatesIntermediateBinding()
    {
        // DECLARE evaluates once and the bound value is reused inside RETURN.
        // Without per-call evaluation semantics this would fail (the body
        // would only resolve @y the same way macro substitution would).
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 3 });

        catalog.Plan(
            "CREATE FUNCTION step(@x INT32) RETURNS INT32 BEGIN " +
                "DECLARE @y INT32 = @x + 1; " +
                "RETURN @y * 2 " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.step(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(8, values[0].AsInt32());
    }

    // ───────────────────── Per-call evaluation semantics ─────────────────────

    [Fact]
    public async Task DeclareWithRandom_EvaluatesOncePerCall()
    {
        // The whole motivation for procedural UDFs: a DECLARE whose RHS is
        // nondeterministic should roll *once* per invocation and reuse the
        // rolled value everywhere it's referenced.
        // RETURN concat(cast(@x AS STRING), '/', cast(@x AS STRING)) — the
        // two halves must match because @x was rolled once. A macro UDF
        // would re-roll on every reference. Strings are read against the
        // batch's arena while it's still live so non-inline payloads don't
        // dangle.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["seed"],
            new object?[] { 1 });

        catalog.Plan(
            "CREATE FUNCTION twin() RETURNS STRING BEGIN " +
                "DECLARE @x FLOAT32 = random(0.0, 1.0); " +
                "RETURN concat(CAST(@x AS STRING), '/', CAST(@x AS STRING)) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.twin() FROM data");

        List<string> rendered = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rendered.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Single(rendered);
        string[] halves = rendered[0].Split('/');
        Assert.Equal(2, halves.Length);
        Assert.Equal(halves[0], halves[1]);
    }

    // ───────────────────── Control flow ─────────────────────

    [Fact]
    public async Task IfElseBranches_BothPathsReturn_PicksMatchingBranch()
    {
        // Both Then and Else terminate in RETURN. The runtime should pick
        // the branch whose predicate matches and never reach the trailing
        // RETURN if the IF is itself a terminating control-flow node.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { -7 },
            new object?[] { 4 });

        catalog.Plan(
            "CREATE FUNCTION abs2(@x INT32) RETURNS INT32 BEGIN " +
                "IF @x < 0 RETURN -@x ELSE RETURN @x " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.abs2(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(2, values.Count);
        Assert.Equal(7, values[0].AsInt32());
        Assert.Equal(4, values[1].AsInt32());
    }

    // ───────────────────── Casting + IS NOT NULL ─────────────────────

    [Fact]
    public async Task ReturnsTypeMismatch_CoercesViaImplicitCast()
    {
        // Body produces a Float64; declared RETURNS INT32 should coerce.
        // Mirrors the macro-UDF behaviour but goes through the procedural
        // adapter's post-RETURN cast.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 9 });

        catalog.Plan(
            "CREATE FUNCTION half(@x INT32) RETURNS INT32 BEGIN " +
                "RETURN @x / 2.0 " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.half(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(DataKind.Int32, values[0].Kind);
        Assert.Equal(4, values[0].AsInt32());
    }

    [Fact]
    public async Task IsNotNullParam_NullArg_ThrowsAtCallBoundary()
    {
        // The IS NOT NULL check fires before the body runs, so the user
        // sees the null-arg error pinned to the specific parameter name.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { null });

        catalog.Plan(
            "CREATE FUNCTION sq(@x INT32 IS NOT NULL) RETURNS INT32 BEGIN RETURN @x * @x END");
        IQueryPlan plan = catalog.Plan("SELECT udf.sq(v) FROM data");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));
        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("@x", fullMessage);
        Assert.Contains("must not be null", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────── Composition ─────────────────────

    [Fact]
    public async Task ProceduralCallsMacro_ResolvesNestedDispatch()
    {
        // Procedural body references udf.macroX(...). The inner macro is
        // inlined into the body's RETURN expression at registration time
        // (the registrar runs the inliner over StatementBody expressions),
        // so the runtime adapter sees a fully-substituted body.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 3 });

        catalog.Plan("CREATE FUNCTION dbl(@x INT32) AS @x * 2");
        catalog.Plan(
            "CREATE FUNCTION quad(@x INT32) RETURNS INT32 BEGIN " +
                "RETURN udf.dbl(udf.dbl(@x)) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.quad(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(12, values[0].AsInt32());
    }

    [Fact]
    public async Task ProceduralCallsProcedural_DispatchesAtRuntime()
    {
        // Procedural-to-procedural calls aren't macro-substituted; they
        // resolve through the FunctionRegistry adapter at evaluation time.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 4 });

        catalog.Plan(
            "CREATE FUNCTION inc(@x INT32) RETURNS INT32 BEGIN RETURN @x + 1 END");
        catalog.Plan(
            "CREATE FUNCTION inc2(@x INT32) RETURNS INT32 BEGIN " +
                "RETURN udf.inc(udf.inc(@x)) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.inc2(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(6, values[0].AsInt32());
    }

    // ───────────────────── Cycle detection ─────────────────────

    [Fact]
    public async Task DirectRecursion_ThrowsCyclicError()
    {
        // The runtime cycle detector pushes each procedural-UDF name on an
        // AsyncLocal stack on entry; re-entering a name already on the
        // stack throws. v1 doesn't allow recursion in either direction.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 1 });

        catalog.Plan(
            "CREATE FUNCTION recurse(@x INT32) RETURNS INT32 BEGIN " +
                "RETURN udf.recurse(@x) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.recurse(v) FROM data");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));
        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("Cyclic", fullMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recurse", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransitiveRecursion_ThrowsCyclicError()
    {
        // a → b → a through the procedural dispatch path. Cycle detection
        // walks the per-async-context stack rather than the call AST so
        // transitive cycles are caught the same way as direct ones.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 1 });

        catalog.Plan(
            "CREATE FUNCTION a(@x INT32) RETURNS INT32 BEGIN RETURN udf.b(@x) END");
        catalog.Plan(
            "CREATE FUNCTION b(@x INT32) RETURNS INT32 BEGIN RETURN udf.a(@x) END");
        IQueryPlan plan = catalog.Plan("SELECT udf.a(v) FROM data");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));
        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("Cyclic", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────── Lifecycle ─────────────────────

    [Fact]
    public async Task DropFunction_RemovesProceduralAdapter()
    {
        // After DROP FUNCTION, the udf.X dispatch path should no longer
        // resolve. The error pins the name so the user can locate it.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 1 });

        catalog.Plan(
            "CREATE FUNCTION sq(@x INT32) RETURNS INT32 BEGIN RETURN @x * @x END");
        catalog.Plan("DROP FUNCTION sq");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
        {
            // Both planning paths must reject the call. Different
            // implementations might fail at plan time vs execute time;
            // either is acceptable as long as the user sees the name.
            IQueryPlan plan = catalog.Plan("SELECT udf.sq(v) FROM data");
            return CollectFirstColumnAsync(plan);
        });

        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("sq", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrReplace_SwapsProceduralAdapterImplementation()
    {
        // OR REPLACE should re-register the procedural adapter; the
        // subsequent call must see the new body.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 5 });

        catalog.Plan(
            "CREATE FUNCTION transform(@x INT32) RETURNS INT32 BEGIN RETURN @x + 1 END");
        catalog.Plan(
            "CREATE OR REPLACE FUNCTION transform(@x INT32) RETURNS INT32 BEGIN RETURN @x * 100 END");
        IQueryPlan plan = catalog.Plan("SELECT udf.transform(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(500, values[0].AsInt32());
    }

    [Fact]
    public async Task OrReplace_SwapsMacroForProcedural_DispatchSwitchesToAdapter()
    {
        // Edge case: a name first registered as a macro, then OR REPLACED as
        // procedural. The macro-form had no adapter in the FunctionRegistry;
        // after replace the adapter must exist and the call must dispatch
        // through it (rather than failing because the inliner saw the new
        // procedural descriptor and skipped substitution but no adapter
        // existed yet).
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 5 });

        catalog.Plan("CREATE FUNCTION transform(@x INT32) AS @x + 1");
        catalog.Plan(
            "CREATE OR REPLACE FUNCTION transform(@x INT32) RETURNS INT32 BEGIN RETURN @x * 100 END");
        IQueryPlan plan = catalog.Plan("SELECT udf.transform(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(500, values[0].AsInt32());
    }

    [Fact]
    public async Task OrReplace_SwapsProceduralForMacro_DispatchInlinesAgain()
    {
        // The reverse: procedural first, then OR REPLACE'd as a macro. The
        // adapter that was registered for the procedural form must be
        // dropped so the inliner takes over again — otherwise a stale
        // adapter would dispatch the old procedural body.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 5 });

        catalog.Plan(
            "CREATE FUNCTION transform(@x INT32) RETURNS INT32 BEGIN RETURN @x + 1 END");
        catalog.Plan(
            "CREATE OR REPLACE FUNCTION transform(@x INT32) AS @x * 100");
        IQueryPlan plan = catalog.Plan("SELECT udf.transform(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(500, values[0].AsInt32());
    }

    // ───────────────────── models.X dispatch from procedural body ─────────────────────

    private static ModelCatalog BuildEchoCatalog()
    {
        ModelCatalog models = new(modelDirectory: Path.GetTempPath());
        models.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance,
            OptionalArgKinds: null));
        return models;
    }

    [Fact]
    public async Task ProceduralBody_CallsModel_DispatchesPerRowThroughCatalog()
    {
        // The reason PR 2 exists: a procedural UDF body that calls
        // models.X(...) should dispatch per row through the model catalog.
        // The hoister doesn't see the call (the body is opaque to the
        // planner), so without the FunctionRegistry → ModelCatalog fallback,
        // the body's evaluator throws "Unknown function: models.echo".
        TableCatalog catalog = CreateCatalog("data",
            columns: ["caption"],
            new object?[] { "hello world" },
            new object?[] { "another row" });
        catalog.Models = BuildEchoCatalog();

        catalog.Plan(
            "CREATE FUNCTION wrap(@s STRING) RETURNS STRING BEGIN " +
                "RETURN models.echo(@s) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.wrap(caption) FROM data");

        List<string> rendered = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rendered.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Equal(2, rendered.Count);
        Assert.Equal("hello world", rendered[0]);
        Assert.Equal("another row", rendered[1]);
    }

    [Fact]
    public async Task ProceduralBody_CallsModelAfterDeclareBinding_EvaluatesOncePerRow()
    {
        // The motivating shape from the user's RewriteCaption use case:
        // bind some intermediate values via DECLARE, then pass them through
        // a model. Verifies the body's per-call VariableScope and the model
        // dispatch path coexist correctly inside the same evaluator.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["caption"],
            new object?[] { "describe a forest" });
        catalog.Models = BuildEchoCatalog();

        catalog.Plan(
            "CREATE FUNCTION rewrite(@caption STRING) RETURNS STRING BEGIN " +
                "DECLARE @prompt STRING = concat('rewrite: ', @caption); " +
                "RETURN models.echo(@prompt) " +
            "END");
        IQueryPlan plan = catalog.Plan("SELECT udf.rewrite(caption) FROM data");

        List<string> rendered = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rendered.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Single(rendered);
        Assert.Equal("rewrite: describe a forest", rendered[0]);
    }
}
