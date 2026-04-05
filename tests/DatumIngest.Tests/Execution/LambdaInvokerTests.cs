using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Phase-A4: <see cref="ExpressionEvaluator.InvokeLambdaAsync"/> evaluates
/// a lambda body with parameter bindings + closure captures + frame
/// integration.
/// </summary>
public sealed class LambdaInvokerTests : ServiceTestBase
{
    private static LambdaExpression ParseLambda(string lambdaSql)
    {
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call = (FunctionCallExpression)select.Statement.Columns[0].Expression;
        return (LambdaExpression)call.Arguments[1];
    }

    private (ExpressionEvaluator Evaluator, EvaluationFrame Frame) MakeEvaluator()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        MemoryAccountant accountant = new();
        VariableScope scope = new(accountant);
        ExpressionEvaluator evaluator = new(
            FunctionRegistry.CreateDefault(),
            variableScope: scope,
            typeRegistry: new TypeRegistry(),
            accountant: accountant);
        EvaluationFrame frame = new(
            Row.Empty, arena, arena, accountant,
            types: new TypeRegistry(),
            lambdaInvoker: evaluator);
        return (evaluator, frame);
    }

    [Fact]
    public async Task InvokeLambdaAsync_SingleParameter_EvaluatesBodyWithBinding()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        var (evaluator, frame) = MakeEvaluator();
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        ValueRef result = await evaluator.InvokeLambdaAsync(
            lambda, new[] { ValueRef.FromInt32(41) }, frame, default);

        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public async Task InvokeLambdaAsync_TwoParameters_AreBoundByOrder()
    {
        LambdaExpression ast = ParseLambda("(a, b) -> a * b");
        var (evaluator, frame) = MakeEvaluator();
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        ValueRef result = await evaluator.InvokeLambdaAsync(
            lambda,
            new[] { ValueRef.FromInt32(6), ValueRef.FromInt32(7) },
            frame, default);

        Assert.Equal(42, result.AsInt32());
    }

    [Fact]
    public async Task InvokeLambdaAsync_RepeatedInvocation_IsDeterministic()
    {
        LambdaExpression ast = ParseLambda("x -> x * 2");
        var (evaluator, frame) = MakeEvaluator();
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        // Invoke 5 times with different args — same lambda value, same result each time
        // for the same input.
        for (int i = 0; i < 5; i++)
        {
            ValueRef result = await evaluator.InvokeLambdaAsync(
                lambda, new[] { ValueRef.FromInt32(i) }, frame, default);
            Assert.Equal(i * 2, result.AsInt32());
        }
    }

    [Fact]
    public async Task InvokeLambdaAsync_WrongArgCount_Throws()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        var (evaluator, frame) = MakeEvaluator();
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await evaluator.InvokeLambdaAsync(
                lambda,
                new[] { ValueRef.FromInt32(1), ValueRef.FromInt32(2) },  // too many
                frame, default));
        Assert.Contains("expects 1", ex.Message);
    }

    [Fact]
    public async Task InvokeLambdaAsync_NoVariableScope_Throws()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        // Evaluator constructed WITHOUT a VariableScope — should refuse invocation.
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        EvaluationFrame frame = new(Row.Empty, arena, arena, new MemoryAccountant());
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await evaluator.InvokeLambdaAsync(
                lambda, new[] { ValueRef.FromInt32(1) }, frame, default));
        Assert.Contains("VariableScope", ex.Message);
    }

    [Fact]
    public async Task InvokeLambdaAsync_ParameterShadowsCapturedRow()
    {
        // If the captures row has a column named the same as a parameter,
        // the parameter binding should win (shadow semantics).
        LambdaExpression ast = ParseLambda("x -> x");
        var (evaluator, frame) = MakeEvaluator();

        // Build a captures row that has a column 'x' = 100.
        ColumnLookup lookup = new(["x"]);
        DataValue[] values = [DataValue.FromInt32(100)];
        Row capturedRow = new(lookup, values);
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, capturedRow));

        // Invoke with arg = 7. Lambda should return 7 (parameter wins).
        ValueRef result = await evaluator.InvokeLambdaAsync(
            lambda, new[] { ValueRef.FromInt32(7) }, frame, default);
        Assert.Equal(7, result.AsInt32());
    }

    [Fact]
    public async Task InvokeLambdaAsync_BodyReferencesCapturedColumn()
    {
        // Body references a column that's not a parameter — should resolve
        // through the captured row.
        LambdaExpression ast = ParseLambda("x -> x + scale");
        var (evaluator, frame) = MakeEvaluator();

        ColumnLookup lookup = new(["scale"]);
        DataValue[] values = [DataValue.FromInt32(10)];
        Row capturedRow = new(lookup, values);
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, capturedRow));

        ValueRef result = await evaluator.InvokeLambdaAsync(
            lambda, new[] { ValueRef.FromInt32(5) }, frame, default);
        Assert.Equal(15, result.AsInt32());
    }

    [Fact]
    public void Frame_LambdaInvokerSlot_RoundTripsThroughWith()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        EvaluationFrame frame = new(
            Row.Empty, arena, arena, new MemoryAccountant(),
            lambdaInvoker: evaluator);
        Assert.Same(evaluator, frame.LambdaInvoker);

        // WithRow preserves the invoker.
        EvaluationFrame derived = frame.WithRow(Row.Empty);
        Assert.Same(evaluator, derived.LambdaInvoker);

        // WithLambdaInvoker(null) clears it.
        EvaluationFrame cleared = frame.WithLambdaInvoker(null);
        Assert.Null(cleared.LambdaInvoker);
    }
}
