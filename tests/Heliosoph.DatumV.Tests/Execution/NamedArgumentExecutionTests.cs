using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for PG-style named function-call arguments.
/// Exercises the parser → <c>NamedArgPermuter</c> planner pass → scalar
/// dispatch pipeline against built-in scalar functions and procedural
/// UDFs registered via <c>CREATE FUNCTION</c>.
/// </summary>
public sealed class NamedArgumentExecutionTests : ServiceTestBase
{
    private static async Task<List<DataValue>> CollectFirstColumnAsync(StatementPlan plan)
    {
        List<DataValue> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        return values;
    }

    /// <summary>
    /// Built-in <c>assert_true</c> declares <c>(condition, message)</c>
    /// where the second slot is optional. Calling with the message
    /// supplied by name validates the simplest pass-through case.
    /// </summary>
    [Fact]
    public async Task AssertTrue_NamedMessage_PassesThroughTruthyValue()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(true, message := 'ok') AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Fat-arrow equivalent of the named-message case. Same call
    /// semantics; the parser tolerates both operators.
    /// </summary>
    [Fact]
    public async Task AssertTrue_FatArrowMessage_PassesThrough()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(true, message => 'ok') AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Both arguments supplied by name. Permutation must place
    /// <c>condition</c> in slot 0 and <c>message</c> in slot 1 regardless
    /// of source-order — the call site below intentionally lists
    /// <c>message</c> first.
    /// </summary>
    [Fact]
    public async Task AssertTrue_AllNamed_ReordersToPositional()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        StatementPlan plan = catalog.Plan(
            "SELECT assert_true(message := 'ok', condition := true) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Single(values);
        Assert.True(values[0].AsBoolean());
    }

    /// <summary>
    /// Unknown parameter name surfaces the planner-pass diagnostic with
    /// the call site name, not an opaque "Unknown function" or runtime
    /// arity error.
    /// </summary>
    [Fact]
    public void AssertTrue_UnknownParameterName_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT assert_true(true, unknown_param := 'x') AS r FROM t"));

        Assert.Contains("assert_true", ex.Message);
        Assert.Contains("unknown_param", ex.Message);
    }

    /// <summary>
    /// Positional arguments must precede every named argument. The
    /// planner pass rejects the inverse ordering — surfacing a precise
    /// "positional after named" message rather than relying on
    /// downstream dispatch to misinterpret the call.
    /// </summary>
    [Fact]
    public void AssertTrue_PositionalAfterNamed_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT assert_true(message := 'x', true) AS r FROM t"));

        Assert.Contains("positional", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Procedural UDFs reach the runtime via the synthetic descriptor
    /// registered by <see cref="Heliosoph.DatumV.Catalog.RoutineRegistrar"/>;
    /// the permuter must recognise that descriptor and reorder named
    /// args accordingly.
    /// </summary>
    [Fact]
    public async Task ProceduralUdf_AllNamed_ReordersAccordingToParameterNames()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });
        catalog.Plan(
            "CREATE FUNCTION subtract(a INT32, b INT32) RETURNS INT32 BEGIN RETURN a - b END");

        StatementPlan positional = catalog.Plan("SELECT subtract(10, 3) AS r FROM t");
        List<DataValue> positionalValues = await CollectFirstColumnAsync(positional);
        Assert.Equal(7, positionalValues[0].AsInt32());

        StatementPlan reordered = catalog.Plan("SELECT subtract(b := 3, a := 10) AS r FROM t");
        List<DataValue> reorderedValues = await CollectFirstColumnAsync(reordered);
        Assert.Equal(7, reorderedValues[0].AsInt32());
    }

    /// <summary>
    /// When a procedural UDF parameter has a <c>DEFAULT</c> and the
    /// caller skips it via named-arg ordering, the planner pass must
    /// inject the default's AST fragment — not a NULL literal — into
    /// the omitted slot so the body sees the declared fallback value.
    /// </summary>
    [Fact]
    public async Task ProceduralUdf_NamedSkipUsesParameterDefault()
    {
        TableCatalog catalog = CreateCatalog("t", columns: ["id"], new object?[] { 1 });
        catalog.Plan(
            "CREATE FUNCTION add_with_default(a INT32, b INT32 = 100) RETURNS INT32 BEGIN RETURN a + b END");

        StatementPlan plan = catalog.Plan("SELECT add_with_default(a := 5) AS r FROM t");
        List<DataValue> values = await CollectFirstColumnAsync(plan);

        Assert.Equal(105, values[0].AsInt32());
    }

    // ─── TVF named arguments in FROM ──────────────────────────────────
    //
    // Same parser → NamedArgPermuter → dispatch pipeline as scalar
    // calls, except the call site is a TableSource (FunctionSource)
    // rather than a FunctionCallExpression. Exercised against
    // generate_series — a numeric three-arg TVF with an optional step
    // and stable PG-compatible parameter names (start, stop, step).

    private static async Task<List<DataValue>> CollectColumnAsync(
        TableCatalog catalog, string sql, string column)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<DataValue> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][column]);
            }
        }
        return values;
    }

    /// <summary>
    /// Two named args, source-order matching declared parameter order.
    /// The TVF must receive (0, 4) and yield 0..4 inclusive.
    /// </summary>
    [Fact]
    public async Task Tvf_AllNamedInDeclaredOrder_YieldsPositionalRange()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(start := 0, stop := 4)", "value");

        Assert.Equal(5, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            Assert.Equal(i, values[i].AsInt32());
        }
    }

    /// <summary>
    /// Same call, names supplied in reverse source order. The permuter
    /// must place stop in slot 1 and start in slot 0 regardless of
    /// textual ordering — otherwise generate_series would interpret
    /// (4, 0) as an empty range.
    /// </summary>
    [Fact]
    public async Task Tvf_AllNamedReversed_PermutesToDeclaredOrder()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(stop := 4, start := 0)", "value");

        Assert.Equal(5, values.Count);
        Assert.Equal(0, values[0].AsInt32());
        Assert.Equal(4, values[4].AsInt32());
    }

    /// <summary>
    /// Positional prefix + trailing named arg. Skipping the optional
    /// middle slot is the case the MS MARCO recipe needs — call
    /// open_csv_typed(path, header := FALSE) without supplying
    /// skip_lines/comment/null_token in between. generate_series stands
    /// in here with the same shape: (start, stop, step) and step
    /// optional.
    /// </summary>
    [Fact]
    public async Task Tvf_PositionalPlusTrailingNamed_RangeUsesStep()
    {
        TableCatalog catalog = CreateCatalog();
        List<DataValue> values = await CollectColumnAsync(catalog,
            "SELECT value FROM generate_series(0, 6, step := 2)", "value");

        Assert.Equal(4, values.Count);
        Assert.Equal(0, values[0].AsInt32());
        Assert.Equal(2, values[1].AsInt32());
        Assert.Equal(4, values[2].AsInt32());
        Assert.Equal(6, values[3].AsInt32());
    }

    /// <summary>
    /// Unknown parameter name surfaces the permuter's diagnostic with
    /// the call-site function name and the offending parameter, same
    /// shape as the scalar path.
    /// </summary>
    [Fact]
    public void Tvf_UnknownParameterName_ThrowsWithSpecificMessage()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SELECT * FROM generate_series(start := 0, nope := 4)"));

        Assert.Contains("generate_series", ex.Message);
        Assert.Contains("nope", ex.Message);
    }
}
