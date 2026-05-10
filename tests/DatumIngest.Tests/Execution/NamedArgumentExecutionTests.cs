using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

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
    /// registered by <see cref="DatumIngest.Catalog.RoutineRegistrar"/>;
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
}
