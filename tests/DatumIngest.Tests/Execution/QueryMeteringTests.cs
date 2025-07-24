using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Integration tests verifying that <see cref="ExpressionEvaluator"/> accumulates
/// Query Unit costs on the attached <see cref="QueryMeter"/> when evaluating
/// scalar function calls.
/// </summary>
public class QueryMeteringTests
{
    /// <summary>
    /// Evaluating a single scalar function call adds its QU cost to the meter.
    /// </summary>
    [Fact]
    public void EvaluateFunction_AccumulatesQueryUnits()
    {
        QueryMeter meter = new();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);
        Row row = new(["x"], [DataValue.FromString("hello")]);

        // `len` has a QU cost of 1 (default).
        evaluator.Evaluate(
            new FunctionCallExpression("len", [new ColumnReference("x")]),
            row);

        Assert.Equal(1, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// Multiple function calls accumulate their costs additively.
    /// </summary>
    [Fact]
    public void MultipleFunctionCalls_AccumulateCosts()
    {
        QueryMeter meter = new();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);
        Row row = new(["x"], [DataValue.FromScalar(1.5f)]);

        // Three calls to `abs` (QU cost 1 each) → total 3 QU.
        for (int i = 0; i < 3; i++)
        {
            evaluator.Evaluate(
                new FunctionCallExpression("abs", [new ColumnReference("x")]),
                row);
        }

        Assert.Equal(3, meter.FunctionQueryUnits);
    }

    /// <summary>
    /// When no meter is attached, function evaluation works without error.
    /// </summary>
    [Fact]
    public void NoMeter_FunctionStillExecutes()
    {
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault());
        Row row = new(["x"], [DataValue.FromScalar(4f)]);

        DataValue result = evaluator.Evaluate(
            new FunctionCallExpression("sqrt", [new ColumnReference("x")]),
            row);

        Assert.Equal(2f, result.AsScalar(), 0.001f);
    }

    /// <summary>
    /// Budget enforcement: adding costs that exceed the budget sets IsBudgetExceeded.
    /// </summary>
    [Fact]
    public void BudgetExceeded_AfterFunctionCalls()
    {
        QueryMeter meter = new(budget: 2);
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);
        Row row = new(["x"], [DataValue.FromScalar(-5f)]);

        // Three calls to `abs` (QU cost 1 each) → 3 QU, budget is 2.
        for (int i = 0; i < 3; i++)
        {
            evaluator.Evaluate(
                new FunctionCallExpression("abs", [new ColumnReference("x")]),
                row);
        }

        Assert.True(meter.IsBudgetExceeded);
    }

    /// <summary>
    /// Non-function expressions (literals, column references) do not add QU.
    /// </summary>
    [Fact]
    public void NonFunctionExpression_DoesNotAccumulate()
    {
        QueryMeter meter = new();
        ExpressionEvaluator evaluator = new(FunctionRegistry.CreateDefault(), meter);
        Row row = new(["x"], [DataValue.FromScalar(42f)]);

        evaluator.Evaluate(new LiteralExpression(1), row);
        evaluator.Evaluate(new ColumnReference("x"), row);

        Assert.Equal(0, meter.FunctionQueryUnits);
    }
}
