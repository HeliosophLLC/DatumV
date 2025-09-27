using DatumIngest.Execution;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="TemporalConstantFolder"/> — the plan-time pass that resolves
/// CURRENT_TIMESTAMP, CURRENT_DATE, CURRENT_TIME, now(), and current_time() to constants.
/// </summary>
public class TemporalConstantFolderTests
{
    private static readonly DateTimeOffset TestClock =
        new(2026, 4, 15, 14, 30, 45, 500, TimeSpan.Zero);

    // ───────────────────── CurrentTimestampExpression folding ─────────────────────

    [Fact]
    public void Fold_CurrentDate_ToCastDateLiteral()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentDate);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("Date", cast.TargetType);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        Assert.Equal("2026-04-15", literal.Value);
    }

    [Fact]
    public void Fold_CurrentTimestamp_ToCastDateTimeLiteral()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("DateTime", cast.TargetType);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        string value = Assert.IsType<string>(literal.Value);
        Assert.Contains("2026-04-15", value);
        Assert.Contains("14:30:45", value);
    }

    [Fact]
    public void Fold_CurrentTime_ToCastTimeLiteral()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("Time", cast.TargetType);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        string value = Assert.IsType<string>(literal.Value);
        Assert.Contains("14:30:45", value);
    }

    // ───────────────────── Precision truncation ─────────────────────

    [Fact]
    public void Fold_CurrentTimestamp_Precision0_TruncatesToWholeSeconds()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp, Precision: 0);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        string value = Assert.IsType<string>(literal.Value);
        // Should not contain fractional seconds
        Assert.DoesNotContain(".5", value);
    }

    [Fact]
    public void Fold_CurrentTimestamp_Precision3_TruncatesToMilliseconds()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp, Precision: 3);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        string value = Assert.IsType<string>(literal.Value);
        Assert.Contains("14:30:45.5", value);
    }

    [Fact]
    public void Fold_CurrentTime_Precision0_TruncatesToWholeSeconds()
    {
        Expression input = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTime, Precision: 0);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(cast.Expression);
        string value = Assert.IsType<string>(literal.Value);
        Assert.Equal("14:30:45", value);
    }

    // ───────────────────── now() and current_time() function folding ─────────────────────

    [Fact]
    public void Fold_NowFunction_ToTimestampLiteral()
    {
        Expression input = new FunctionCallExpression("now", []);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("DateTime", cast.TargetType);
    }

    [Fact]
    public void Fold_CurrentTimeFunction_ToTimeLiteral()
    {
        Expression input = new FunctionCallExpression("current_time", []);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        CastExpression cast = Assert.IsType<CastExpression>(result);
        Assert.Equal("Time", cast.TargetType);
    }

    [Fact]
    public void Fold_NowFunctionWithArgs_NotFolded()
    {
        // now() with arguments should NOT be folded (it's a different function or an error).
        Expression input = new FunctionCallExpression("now", [new LiteralExpression(1.0)]);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        Assert.IsType<FunctionCallExpression>(result);
    }

    // ───────────────────── Non-temporal expressions pass through ─────────────────────

    [Fact]
    public void Fold_RegularFunction_Unchanged()
    {
        Expression input = new FunctionCallExpression("upper", [new LiteralExpression("hello")]);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        FunctionCallExpression func = Assert.IsType<FunctionCallExpression>(result);
        Assert.Equal("upper", func.FunctionName);
    }

    [Fact]
    public void Fold_Literal_Unchanged()
    {
        Expression input = new LiteralExpression(42.0);
        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        Assert.Same(input, result);
    }

    // ───────────────────── Batch clock consistency ─────────────────────

    [Fact]
    public void Fold_TwoCurrentTimestamps_SameValue()
    {
        Expression ts1 = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp);
        Expression ts2 = new CurrentTimestampExpression(CurrentTimestampKind.CurrentTimestamp);

        Expression result1 = TemporalConstantFolder.FoldExpression(ts1, TestClock);
        Expression result2 = TemporalConstantFolder.FoldExpression(ts2, TestClock);

        CastExpression cast1 = Assert.IsType<CastExpression>(result1);
        CastExpression cast2 = Assert.IsType<CastExpression>(result2);

        LiteralExpression lit1 = Assert.IsType<LiteralExpression>(cast1.Expression);
        LiteralExpression lit2 = Assert.IsType<LiteralExpression>(cast2.Expression);

        Assert.Equal(lit1.Value, lit2.Value);
    }

    // ───────────────────── Nested expression folding ─────────────────────

    [Fact]
    public void Fold_NestedInBinary_FoldsInnerConstant()
    {
        Expression input = new BinaryExpression(
            new CurrentTimestampExpression(CurrentTimestampKind.CurrentDate),
            BinaryOperator.Equal,
            new ColumnReference(null, "d"));

        Expression result = TemporalConstantFolder.FoldExpression(input, TestClock);

        BinaryExpression binary = Assert.IsType<BinaryExpression>(result);
        Assert.IsType<CastExpression>(binary.Left);
        Assert.IsType<ColumnReference>(binary.Right);
    }
}
