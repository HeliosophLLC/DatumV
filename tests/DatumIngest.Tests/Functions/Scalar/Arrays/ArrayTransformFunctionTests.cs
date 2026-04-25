using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Arrays;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Arrays;

/// <summary>
/// Tests for <see cref="ArrayTransformFunction"/>: the
/// <c>array_transform(arr, x -&gt; expr)</c> higher-order function. Mirrors
/// the AnimateFrames test layout — direct invocation against a constructed
/// frame plus end-to-end SQL execution to verify the operator-pipeline
/// LambdaInvoker auto-attach reaches this function too.
/// </summary>
public sealed class ArrayTransformFunctionTests : ServiceTestBase
{
    // ----- direct invocation -----

    [Fact]
    public async Task ArrayTransform_PrimitiveIntArray_AppliesLambda()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x * 2");
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2, 3 }, DataKind.Int32);

        ValueRef result = await new ArrayTransformFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(3, result.GetArrayLength());

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(2, elements[0].AsInt32());
        Assert.Equal(4, elements[1].AsInt32());
        Assert.Equal(6, elements[2].AsInt32());
    }

    [Fact]
    public async Task ArrayTransform_ChangesElementKind_WhenLambdaReturnsDifferentKind()
    {
        // Lambda body: x -> length(x). Input is String[], result should be Int32[].
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> length(x)");
        ValueRef input = ValueRef.FromArray(
            DataKind.String,
            new[]
            {
                ValueRef.FromString("a"),
                ValueRef.FromString("bb"),
                ValueRef.FromString("ccc"),
            });

        ValueRef result = await new ArrayTransformFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        Assert.True(result.IsArray);
        // Result element kind reflects the lambda body, not the input.
        Assert.Equal(DataKind.Int32, result.Kind);
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(1, elements[0].AsInt32());
        Assert.Equal(2, elements[1].AsInt32());
        Assert.Equal(3, elements[2].AsInt32());
    }

    [Fact]
    public async Task ArrayTransform_NullArray_ReturnsNullArrayOfSameKind()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x + 1");
        ValueRef nullArray = ValueRef.NullArray(DataKind.Int32);

        ValueRef result = await new ArrayTransformFunction().ExecuteAsync(
            new[] { nullArray, lambda }, frame, default);

        Assert.True(result.IsNull);
        Assert.True(result.IsArray);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task ArrayTransform_EmptyArray_ReturnsEmptyArray()
    {
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x * 2");
        ValueRef empty = ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Int32);

        ValueRef result = await new ArrayTransformFunction().ExecuteAsync(
            new[] { empty, lambda }, frame, default);

        Assert.True(result.IsArray);
        Assert.Equal(0, result.GetArrayLength());
        // No invocations happened — fallback to input element kind.
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task ArrayTransform_NullLambda_Throws()
    {
        var (_, frame) = MakeEvaluatorAndFrame();
        ValueRef input = ValueRef.FromPrimitiveArray(new[] { 1, 2 }, DataKind.Int32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new ArrayTransformFunction().ExecuteAsync(
                new[] { input, ValueRef.Null(DataKind.Lambda) }, frame, default));
        Assert.Contains("lambda", ex.Message);
    }

    [Fact]
    public async Task ArrayTransform_PreservesNullElements_AndPassesThemToLambda()
    {
        // ValueRef[] backing lets us include typed-null elements. The lambda
        // here returns its input unchanged, so the null element survives in
        // the output.
        var (evaluator, frame) = MakeEvaluatorAndFrame();
        ValueRef lambda = MakeLambda(evaluator, frame, "x -> x");
        ValueRef input = ValueRef.FromArray(
            DataKind.Int32,
            new[]
            {
                ValueRef.FromInt32(10),
                ValueRef.Null(DataKind.Int32),
                ValueRef.FromInt32(30),
            });

        ValueRef result = await new ArrayTransformFunction().ExecuteAsync(
            new[] { input, lambda }, frame, default);

        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        Assert.Equal(10, elements[0].AsInt32());
        Assert.True(elements[1].IsNull);
        Assert.Equal(30, elements[2].AsInt32());
    }

    // ----- end-to-end through SQL execution (verifies registry + auto-attach) -----

    [Fact]
    public async Task EndToEnd_ArrayTransformThroughSQL_AppliesLambdaPerRow()
    {
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["x"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_transform([10, 20, 30], x -> x * 2) AS doubled FROM t",
            catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["doubled"];
        Assert.True(value.IsArray);
        Assert.Equal(DataKind.Int32, value.Kind);
    }

    [Fact]
    public async Task EndToEnd_ArrayTransformWithClosureCapture()
    {
        // Closure capture: the lambda references `scale`, a column from the
        // enclosing row. Each row's mapped array should reflect that row's
        // scale value.
        Pool pool = CreatePool();
        TableCatalog catalog = CreateCatalog(pool);
        catalog.Add(new InMemoryTableProvider(pool, "t",
            columns: ["scale"],
            columnKinds: [DataKind.Int32],
            rows: [[10]]));

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_transform([1, 2, 3], x -> x * scale) AS scaled FROM t",
            catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["scaled"];
        Assert.True(value.IsArray);
        Assert.Equal(DataKind.Int32, value.Kind);
        // The closure-capture wiring is verified by the absence of "Name
        // 'scale' is not a declared variable in scope" — that error would
        // have surfaced if the captured row weren't bound to the lambda.
    }

    // ----- helpers -----

    private (ExpressionEvaluator Evaluator, EvaluationFrame Frame) MakeEvaluatorAndFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(
            store: arena, accountant: accountant);
        DatumIngest.Execution.ExecutionContext scoped = context.Derive(
            variableScope: scope, variableStore: arena);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
        return (evaluator, frame);
    }

    private static ValueRef MakeLambda(
        ExpressionEvaluator evaluator, EvaluationFrame frame, string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call =
            (FunctionCallExpression)select.Statement.Columns[0].Expression;
        LambdaExpression ast = (LambdaExpression)call.Arguments[1];
        return ValueRef.FromLambda(LambdaValue.Capture(ast, frame.Row));
    }
}
