using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// Phase-A1 substrate: construction, accessor, evaluator promotion, and
/// boundary-refusal behaviours for <see cref="LambdaValue"/> /
/// <see cref="DataKind.Lambda"/>.
/// </summary>
public sealed class LambdaValueTests : ServiceTestBase
{
    private static LambdaExpression ParseLambda(string lambdaSql)
    {
        // The parser accepts a lambda inside a function-call position (the
        // canonical higher-order context). Use a literal-array context that
        // we know exists; extract the parsed lambda from the AST.
        QueryExpression query = SqlParser.Parse(
            $"SELECT array_transform(arr, {lambdaSql}) FROM t");
        // Walk the AST down to the lambda node. The query is a SELECT whose
        // column expression is array_transform(arr, <lambda>); we pluck the
        // second argument.
        SelectQueryExpression select = (SelectQueryExpression)query;
        FunctionCallExpression call = (FunctionCallExpression)select.Statement.Columns[0].Expression;
        return (LambdaExpression)call.Arguments[1];
    }

    // ----- construction -----

    [Fact]
    public void LambdaValue_Capture_StampsParametersAndCaptures()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        Row row = Row.Empty;

        LambdaValue value = LambdaValue.Capture(ast, row);

        Assert.Same(ast, value.Body);
        Assert.Equal(["x"], value.Parameters);
        Assert.Equal(row, value.Captures);
    }

    [Fact]
    public void LambdaValue_Capture_TwoParameters()
    {
        LambdaExpression ast = ParseLambda("(a, b) -> a + b");
        LambdaValue value = LambdaValue.Capture(ast, Row.Empty);
        Assert.Equal(["a", "b"], value.Parameters);
    }

    // ----- ValueRef integration -----

    [Fact]
    public void ValueRef_FromLambda_RoundsTripThroughAsLambda()
    {
        LambdaExpression ast = ParseLambda("x -> x * 2");
        LambdaValue value = LambdaValue.Capture(ast, Row.Empty);
        ValueRef vref = ValueRef.FromLambda(value);

        Assert.Equal(DataKind.Lambda, vref.Kind);
        Assert.False(vref.IsNull);
        Assert.Same(value, vref.AsLambda());
    }

    [Fact]
    public void ValueRef_FromLambda_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ValueRef.FromLambda(null!));
    }

    [Fact]
    public void ValueRef_AsLambda_WrongKind_Throws()
    {
        ValueRef notALambda = ValueRef.FromInt32(42);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => notALambda.AsLambda());
        Assert.Contains("expected Lambda", ex.Message);
    }

    // ----- boundary refusal -----

    [Fact]
    public void ValueRef_ToDataValue_OnLambda_Throws()
    {
        LambdaExpression ast = ParseLambda("x -> x");
        LambdaValue value = LambdaValue.Capture(ast, Row.Empty);
        ValueRef vref = ValueRef.FromLambda(value);

        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => vref.ToDataValue(arena));
        Assert.Contains("cannot be persisted", ex.Message);
    }

    // ----- evaluator promotion (ValueRef path) -----

    [Fact]
    public async Task EvaluateAsValueRefAsync_LambdaExpression_ProducesLambdaValueRef()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        EvaluationFrame frame = new(
            Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());

        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        ValueRef result = await evaluator.EvaluateAsValueRefAsync(ast, frame, default);

        Assert.Equal(DataKind.Lambda, result.Kind);
        LambdaValue value = result.AsLambda();
        Assert.Same(ast, value.Body);
        Assert.Equal(["x"], value.Parameters);
    }

    // ----- evaluator refusal (DataValue path) -----

    [Fact]
    public async Task EvaluateAsync_LambdaExpression_ThrowsHelpfulMessage()
    {
        LambdaExpression ast = ParseLambda("x -> x + 1");
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        EvaluationFrame frame = new(
            Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());

        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        // The evaluator's outer catch wraps internal failures in
        // ExpressionEvaluationException (carrying source-span info); the
        // helpful message we care about is still propagated.
        ExpressionEvaluationException ex = await Assert.ThrowsAsync<ExpressionEvaluationException>(
            async () => await evaluator.EvaluateAsync(ast, frame, default));
        Assert.Contains("EvaluateAsValueRefAsync", ex.Message);
    }
}
