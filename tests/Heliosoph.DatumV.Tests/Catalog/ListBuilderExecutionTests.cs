using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// End-to-end tests for the body-local <c>List&lt;T&gt;</c> accumulator: a
/// procedural UDF declares <c>List&lt;T&gt;</c>, grows it with <c>APPEND</c> /
/// <c>RESERVE</c>, and the list freezes to a flat <c>Array&lt;T&gt;</c> at its
/// promotion boundaries (a scalar-function argument and <c>RETURN</c>). This is
/// the SAM accumulator pattern in miniature, exercised through the same
/// statement executor.
/// </summary>
public class ListBuilderExecutionTests : ServiceTestBase
{
    // Array results are arena-backed; the DataValue must be read with the
    // batch's store *inside* the enumeration (the arena recycles between
    // batches), so these helpers materialise per-row before returning.
    // Span reads happen in synchronous helpers — a ReadOnlySpan local can't
    // live inside the async-foreach state machine.
    private static int[] ReadIntArray(RowBatch batch) =>
        batch[0][0].AsArraySpan<int>(batch.Arena).ToArray();

    private static float[] ReadFloatArray(RowBatch batch) =>
        batch[0][0].AsArraySpan<float>(batch.Arena).ToArray();

    private static async Task<int[]> FirstIntArrayAsync(StatementPlan plan)
    {
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            if (batch.Count > 0)
            {
                return ReadIntArray(batch);
            }
        }
        throw new InvalidOperationException("query produced no rows");
    }

    private static async Task<float[]> FirstFloatArrayAsync(StatementPlan plan)
    {
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            if (batch.Count > 0)
            {
                return ReadFloatArray(batch);
            }
        }
        throw new InvalidOperationException("query produced no rows");
    }

    private static async Task<int> FirstInt32Async(StatementPlan plan)
    {
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            if (batch.Count > 0)
            {
                return batch[0][0].AsInt32();
            }
        }
        throw new InvalidOperationException("query produced no rows");
    }

    private static async Task<float> FirstFloat32Async(StatementPlan plan)
    {
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            if (batch.Count > 0)
            {
                return batch[0][0].AsFloat32();
            }
        }
        throw new InvalidOperationException("query produced no rows");
    }

    private static async Task RunAsync(StatementPlan plan)
    {
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            _ = batch.Count;
        }
    }

    private TableCatalog SingleRowCatalog() =>
        CreateCatalog("data", columns: ["v"], new object?[] { 1 });

    // ───────────────── APPEND scalar in a loop, freeze at RETURN ─────────────────

    [Fact]
    public async Task AppendScalarsInLoop_FreezesToArrayAtReturn()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION build_seq(n INT32) RETURNS INT32[] BEGIN " +
                "DECLARE acc List<Int32>; " +
                "DECLARE i INT32 = 0; " +
                "WHILE i < n BEGIN APPEND i TO acc; SET i = i + 1 END; " +
                "RETURN acc " +
            "END");
        StatementPlan plan = catalog.Plan("SELECT build_seq(5) FROM data");

        int[] result = await FirstIntArrayAsync(plan);
        Assert.Equal([0, 1, 2, 3, 4], result);
    }

    // ───────────────── APPEND array (concatenation) ─────────────────

    [Fact]
    public async Task AppendArrays_ConcatenatesElements()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION cat_lists() RETURNS INT32[] BEGIN " +
                "DECLARE acc List<Int32>; " +
                "APPEND [10::Int32, 20::Int32] TO acc; " +
                "APPEND [30::Int32, 40::Int32, 50::Int32] TO acc; " +
                "RETURN acc " +
            "END");
        StatementPlan plan = catalog.Plan("SELECT cat_lists() FROM data");

        int[] result = await FirstIntArrayAsync(plan);
        Assert.Equal([10, 20, 30, 40, 50], result);
    }

    // ───────────────── Mixed scalar + array, Float32 (the SAM shape) ─────────────────

    [Fact]
    public async Task AppendScalarAndArray_Float32_SamShape()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION sam_like() RETURNS FLOAT32[] BEGIN " +
                "DECLARE acc List<Float32>; " +
                "APPEND CAST(1.5 AS Float32) TO acc; " +              // scalar
                "APPEND [CAST(2.5 AS Float32), CAST(3.5 AS Float32)] TO acc; " + // array concat
                "RETURN acc " +
            "END");
        StatementPlan plan = catalog.Plan("SELECT sam_like() FROM data");

        float[] result = await FirstFloatArrayAsync(plan);
        Assert.Equal([1.5f, 2.5f, 3.5f], result);
    }

    // ───────────────── Freeze at a scalar-function argument boundary ─────────────────

    [Fact]
    public async Task ListPassedToFunction_FreezesAtArgumentBoundary()
    {
        // cardinality() does not opt into receiving a live list, so the
        // dispatcher freezes acc to an Array<Int32> before the call.
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION count_seq(n INT32) RETURNS INT32 BEGIN " +
                "DECLARE acc List<Int32>; " +
                "DECLARE i INT32 = 0; " +
                "WHILE i < n BEGIN APPEND i TO acc; SET i = i + 1 END; " +
                "RETURN cardinality(acc) " +
            "END");
        StatementPlan plan = catalog.Plan("SELECT count_seq(7) FROM data");

        Assert.Equal(7, await FirstInt32Async(plan));
    }

    // ───────────── Static type = frozen Array<T> (strict-signature consumer) ─────────────

    [Fact]
    public async Task ListPassedToStrictlyTypedArrayFunction_RegistersAndRuns()
    {
        // Regression for the arity-gate static type: a List<Float32> variable's
        // STATIC type must resolve to Array<Float32> so a strict signature
        // (array_sum: Float32[]) matches at registration, not just at runtime.
        // Before the fix this failed CREATE with "no matching signature …
        // [Unknown]" — exactly the MobileSAM mask_nms_planes install error.
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION sum_list() RETURNS FLOAT32 BEGIN " +
                "DECLARE acc List<Float32>; " +
                "APPEND CAST(1.5 AS Float32) TO acc; " +
                "APPEND CAST(2.5 AS Float32) TO acc; " +
                "RETURN array_sum(acc) END");
        StatementPlan plan = catalog.Plan("SELECT sum_list() FROM data");

        Assert.Equal(4.0f, await FirstFloat32Async(plan), 3);
    }

    // ───────────────── RESERVE ─────────────────

    [Fact]
    public async Task Reserve_ThenAppend_ProducesSameResult()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION reserved_seq(n INT32) RETURNS INT32[] BEGIN " +
                "DECLARE acc List<Int32>; " +
                "RESERVE n FOR acc; " +
                "DECLARE i INT32 = 0; " +
                "WHILE i < n BEGIN APPEND i TO acc; SET i = i + 1 END; " +
                "RETURN acc " +
            "END");
        StatementPlan plan = catalog.Plan("SELECT reserved_seq(3) FROM data");

        int[] result = await FirstIntArrayAsync(plan);
        Assert.Equal([0, 1, 2], result);
    }

    [Fact]
    public async Task EmptyList_FreezesToEmptyArray()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION empty_list() RETURNS INT32[] BEGIN " +
                "DECLARE acc List<Int32>; RETURN acc END");
        StatementPlan plan = catalog.Plan("SELECT empty_list() FROM data");

        Assert.Empty(await FirstIntArrayAsync(plan));
    }

    // ───────────────── Negatives ─────────────────

    [Fact]
    public async Task DeclareListWithInitializer_Throws()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION bad_init() RETURNS INT32[] BEGIN " +
                "DECLARE acc List<Int32> = [1::Int32]; RETURN acc END");
        StatementPlan plan = catalog.Plan("SELECT bad_init() FROM data");

        // The body throws an ExecutionException; the evaluator wraps it with
        // source position, so assert on the family + preserved message.
        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() => RunAsync(plan));
        Assert.Contains("without an", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendToNonListVariable_Throws()
    {
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION bad_target() RETURNS INT32 BEGIN " +
                "DECLARE notalist INT32 = 1; APPEND 2 TO notalist; RETURN notalist END");
        StatementPlan plan = catalog.Plan("SELECT bad_target() FROM data");

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() => RunAsync(plan));
        Assert.Contains("not a List", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendScalar_CoercesNumericKind()
    {
        // SQL literals narrow (`7` is Int8); APPEND coerces to the element kind,
        // and Int32 → Float32 widens cleanly — no CAST needed.
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION coerced() RETURNS FLOAT32[] BEGIN " +
                "DECLARE acc List<Float32>; APPEND 7 TO acc; APPEND 8::Int32 TO acc; RETURN acc END");
        StatementPlan plan = catalog.Plan("SELECT coerced() FROM data");

        float[] result = await FirstFloatArrayAsync(plan);
        Assert.Equal([7f, 8f], result);
    }

    [Fact]
    public async Task AppendInconvertibleScalar_Throws()
    {
        // A value that has no numeric conversion to the element kind is rejected.
        TableCatalog catalog = SingleRowCatalog();
        catalog.Plan(
            "CREATE FUNCTION bad_kind() RETURNS FLOAT32[] BEGIN " +
                "DECLARE acc List<Float32>; APPEND 'notanumber' TO acc; RETURN acc END");
        StatementPlan plan = catalog.Plan("SELECT bad_kind() FROM data");

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() => RunAsync(plan));
        Assert.Contains("cannot be converted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListType_AsColumnType_IsRejected()
    {
        TableCatalog catalog = SingleRowCatalog();
        // List<T> is body-local; it is not a storable column type.
        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("CREATE TABLE bad_col (c List<Int32>)"));
    }
}
