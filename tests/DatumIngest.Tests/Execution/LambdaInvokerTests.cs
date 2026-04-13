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
        DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(
            store: arena, accountant: accountant);
        DatumIngest.Execution.ExecutionContext scoped = context.Derive(
            variableScope: scope, variableStore: arena);
        ExpressionEvaluator evaluator = scoped.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
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
    public async Task InvokeLambdaAsync_NoVariableScope_LazilyCreatesOne()
    {
        // The evaluator's variable scope is lazily initialised on first
        // lambda invocation when not explicitly supplied. This lets the
        // operator pipeline (ProjectOperator, etc.) — which constructs
        // evaluators without an explicit scope — invoke lambdas without
        // requiring every operator to know about scope management.
        LambdaExpression ast = ParseLambda("x -> x + 1");
        // Evaluator constructed WITHOUT a VariableScope — should now succeed
        // because the scope is created on demand.
        using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext();
        ExpressionEvaluator evaluator = context.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty);
        ValueRef lambda = ValueRef.FromLambda(LambdaValue.Capture(ast, Row.Empty));

        ValueRef result = await evaluator.InvokeLambdaAsync(
            lambda, new[] { ValueRef.FromInt32(41) }, frame, default);
        Assert.Equal(42, result.AsInt32());
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
        using DatumIngest.Execution.ExecutionContext context = CreateExecutionContext(store: arena);
        ExpressionEvaluator evaluator = context.CreateEvaluator();
        EvaluationFrame frame = evaluator.CreateFrame(Row.Empty, arena);
        Assert.Same(evaluator, frame.LambdaInvoker);

        // WithRow preserves the invoker.
        EvaluationFrame derived = frame.WithRow(Row.Empty);
        Assert.Same(evaluator, derived.LambdaInvoker);

        // WithLambdaInvoker(null) clears it.
        EvaluationFrame cleared = frame.WithLambdaInvoker(null);
        Assert.Null(cleared.LambdaInvoker);
    }
}
