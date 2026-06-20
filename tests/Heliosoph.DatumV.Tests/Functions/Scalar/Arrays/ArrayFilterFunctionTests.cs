using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Arrays;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Arrays;

/// <summary>
/// Tests for <see cref="ArrayFilterFunction"/>: the
/// <c>array_filter(arr, x -&gt; predicate)</c> higher-order function. Mirrors
/// the <see cref="ArrayTransformFunctionTests"/> layout — direct invocation
/// against a constructed frame plus end-to-end SQL execution to verify the
/// operator-pipeline LambdaInvoker auto-attach reaches this function too.
/// </summary>
public sealed class ArrayFilterFunctionTests : ServiceTestBase
{
    // ----- direct invocation -----

    [Fact]
    public async Task ArrayFilter_PrimitiveIntArray_KeepsMatching()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x > 2");
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3, 4, 5 }, DataKind.Int32);

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(3, result.GetArrayLength());

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(3, elements[0].AsInt32());
        Assert.Equal(4, elements[1].AsInt32());
        Assert.Equal(5, elements[2].AsInt32());
    }

    [Fact]
    public async Task ArrayFilter_AllRejected_ReturnsEmptyArrayOfSameKind()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x > 100");
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3 }, DataKind.Int32);

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public async Task ArrayFilter_AllKept_ReturnsSameLengthAndOrder()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> true");
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 10, 20, 30 }, DataKind.Int32);

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.Equal(3, result.GetArrayLength());
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(10, elements[0].AsInt32());
        Assert.Equal(20, elements[1].AsInt32());
        Assert.Equal(30, elements[2].AsInt32());
    }

    [Fact]
    public async Task ArrayFilter_NullArray_ReturnsNullArrayOfSameKind()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x > 0");
        ValueRef nullArray = ValueRef.NullArray(DataKind.Int32);

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { nullArray, lambda }, frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task ArrayFilter_NullLambda_Throws()
    {
        var (_, frame) = MakeEvaluatorAndFrame();
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2 }, DataKind.Int32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ArrayFilterFunction().ExecuteAsync(
                new[] { input, ValueRef.Null(DataKind.Lambda) }, frame, default));
        Assert.Contains("lambda", ex.Message);
    }

    [Fact]
    public async Task ArrayFilter_NonBooleanPredicate_Throws()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x");        // Int32, not Boolean
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3 }, DataKind.Int32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ArrayFilterFunction().ExecuteAsync(
                new[] { input, lambda }, frame, default));
        Assert.Contains("Boolean", ex.Message);
    }

    [Fact]
    public async Task ArrayFilter_NullPredicateResult_DropsElement()
    {
        // x = 2 returns NULL because the input element [1] is NULL. NULL should drop.
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x = 2");
        ValueRef input = ValueRef.FromArray(
            DataKind.Int32,
            new[]
            {
                ValueRef.FromInt32(1),
                ValueRef.Null(DataKind.Int32),
                ValueRef.FromInt32(2),
                ValueRef.FromInt32(3),
            });

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal(2, result.GetArrayElements()[0].AsInt32());
    }

    [Fact]
    public async Task ArrayFilter_StringArray_KeepsByPredicate()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> length(x) > 1");
        ValueRef input = ValueRef.FromArray(
            DataKind.String,
            new[]
            {
                ValueRef.FromString("a"),
                ValueRef.FromString("bb"),
                ValueRef.FromString("c"),
                ValueRef.FromString("dddd"),
            });

        ValueRef result = await new ArrayFilterFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.Equal(DataKind.String, result.Kind);
        Assert.Equal(2, result.GetArrayLength());
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal("bb", elements[0].AsString());
        Assert.Equal("dddd", elements[1].AsString());
    }

    // ----- end-to-end through SQL execution -----

    [Fact]
    public async Task EndToEnd_ArrayFilterThroughSQL_AppliesPredicatePerRow()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["x"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_filter([10, 20, 30, 40, 50], x -> x > 25) AS kept FROM t",
            catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["kept"];
        Assert.True(value.IsArray);
        // Small-integer literals infer as Int8, which the filter preserves.
        Assert.Equal(DataKind.Int8, value.Kind);
    }

    [Fact]
    public async Task EndToEnd_ArrayFilterAndJoin_BothNamesWork()
    {
        // array_join is registered as an alias of array_to_string in the
        // function registry. Verifies the alias resolves end-to-end.
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["x"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_join(array_filter(['alpha', 'beta', 'gamma'], s -> length(s) > 4), ',') AS r FROM t",
            catalog);

        Assert.Equal("alpha,gamma", rows[0]["r"].AsString());
    }

    // ----- helpers -----

    private (ExpressionEvaluator Evaluator, EvaluationFrame Frame) MakeEvaluatorAndFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        Heliosoph.DatumV.Execution.ExecutionContext context = CreateExecutionContext(
            store: arena, accountant: accountant);
        Heliosoph.DatumV.Execution.ExecutionContext scoped = context.Derive(
            variableScope: scope, variableStore: arena);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
        return (evaluator, frame);
    }

    private static ValueRef MakeLambda(
        ExpressionEvaluator evaluator, EvaluationFrame frame, string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_filter(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call =
            (FunctionCallExpression)select.Statement.Columns[0].Expression;
        LambdaExpression ast = (LambdaExpression)call.Arguments[1];
        return ValueRef.FromLambda(LambdaValue.Capture(ast, frame.Row));
    }
}
