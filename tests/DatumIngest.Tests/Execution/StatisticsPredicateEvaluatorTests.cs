using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="StatisticsPredicateEvaluator"/>, verifying that
/// partition pruning decisions are correct for all supported predicate shapes.
/// </summary>
public class StatisticsPredicateEvaluatorTests
{
    /// <summary>
    /// Builds a statistics dictionary for a single column with scalar min/max bounds.
    /// </summary>
    private static Dictionary<string, ColumnStatisticsRange> ScalarStats(
        string column, float min, float max, long rowCount = 100, long? nullCount = 0)
    {
        return new Dictionary<string, ColumnStatisticsRange>(StringComparer.OrdinalIgnoreCase)
        {
            [column] = new ColumnStatisticsRange(
                DataValue.FromFloat32(min),
                DataValue.FromFloat32(max),
                nullCount,
                rowCount)
        };
    }

    /// <summary>
    /// Builds a statistics dictionary for a single column with string min/max bounds.
    /// </summary>
    private static Dictionary<string, ColumnStatisticsRange> StringStats(
        string column, string min, string max, long rowCount = 100, long? nullCount = 0)
    {
        return new Dictionary<string, ColumnStatisticsRange>(StringComparer.OrdinalIgnoreCase)
        {
            [column] = new ColumnStatisticsRange(
                DataValue.FromString(min),
                DataValue.FromString(max),
                nullCount,
                rowCount)
        };
    }

    /// <summary>
    /// Builds a statistics dictionary for a single boolean column with min/max bounds.
    /// </summary>
    private static Dictionary<string, ColumnStatisticsRange> BoolStats(
        string column, bool min, bool max, long rowCount = 100, long? nullCount = 0)
    {
        return new Dictionary<string, ColumnStatisticsRange>(StringComparer.OrdinalIgnoreCase)
        {
            [column] = new ColumnStatisticsRange(
                DataValue.FromBoolean(min),
                DataValue.FromBoolean(max),
                nullCount,
                rowCount)
        };
    }

    // ──────────────── Comparison: col op literal ────────────────

    [Fact]
    public void Equal_LiteralOutsideRange_CanSkip()
    {
        // col = 50, but partition has [1, 10] → definitely no match
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(50.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_LiteralInsideRange_CannotSkip()
    {
        // col = 5, partition has [1, 10] → might match
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_LiteralAtMin_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(1.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_LiteralAtMax_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(10.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_LiteralBelowRange_CanSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 5f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(2.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotEqual_AllSameValue_CanSkip()
    {
        // col != 5, but min = max = 5 → all rows are 5, so none match != 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 5f, 5f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.NotEqual, new LiteralExpression(5.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotEqual_MixedValues_CannotSkip()
    {
        // col != 5, partition has [1, 10] → some rows might not be 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.NotEqual, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void LessThan_MinAboveLiteral_CanSkip()
    {
        // col < 5, but min = 10 → all values >= 10, none < 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 10f, 20f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.LessThan, new LiteralExpression(5.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void LessThan_MinEqualsLiteral_CanSkip()
    {
        // col < 5, but min = 5 → all values >= 5, none < 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 5f, 20f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.LessThan, new LiteralExpression(5.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void LessThan_MinBelowLiteral_CannotSkip()
    {
        // col < 5, min = 3 → some values might be < 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 3f, 20f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.LessThan, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void LessThanOrEqual_MinAboveLiteral_CanSkip()
    {
        // col <= 5, but min = 10
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 10f, 20f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.LessThanOrEqual, new LiteralExpression(5.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void LessThanOrEqual_MinEqualsLiteral_CannotSkip()
    {
        // col <= 5, min = 5 → could have rows where value = 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 5f, 20f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.LessThanOrEqual, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void GreaterThan_MaxBelowLiteral_CanSkip()
    {
        // col > 100, but max = 50
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(100.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void GreaterThan_MaxEqualsLiteral_CanSkip()
    {
        // col > 50, but max = 50 → no value > 50
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(50.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void GreaterThanOrEqual_MaxBelowLiteral_CanSkip()
    {
        // col >= 100, but max = 50
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.GreaterThanOrEqual, new LiteralExpression(100.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void GreaterThanOrEqual_MaxEqualsLiteral_CannotSkip()
    {
        // col >= 50, max = 50 → could match
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.GreaterThanOrEqual, new LiteralExpression(50.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── Reversed: literal op col ────────────────

    [Fact]
    public void Reversed_LiteralGreaterThanCol_FlipsToColLessThan()
    {
        // 100 > col → col < 100, partition [200, 300] → min 200 >= 100 → skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 200f, 300f);
        BinaryExpression predicate = new(
            new LiteralExpression(100.0), BinaryOperator.GreaterThan, new ColumnReference("x"));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Reversed_LiteralLessThanCol_FlipsToColGreaterThan()
    {
        // 100 < col → col > 100, partition [1, 50] → max 50 <= 100 → skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new LiteralExpression(100.0), BinaryOperator.LessThan, new ColumnReference("x"));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── AND / OR composition ────────────────

    [Fact]
    public void And_OneConjunctSkippable_CanSkip()
    {
        // x > 100 AND y > 0, partition x:[1, 50] → x > 100 skippable → whole AND skippable
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(100.0)),
            BinaryOperator.And,
            new BinaryExpression(new ColumnReference("y"), BinaryOperator.GreaterThan, new LiteralExpression(0.0)));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void And_NeitherSkippable_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(10.0)),
            BinaryOperator.And,
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.LessThan, new LiteralExpression(40.0)));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Or_BothSkippable_CanSkip()
    {
        // x > 100 OR x < 0, partition x:[1, 50] → both skippable
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(100.0)),
            BinaryOperator.Or,
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.LessThan, new LiteralExpression(0.0)));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Or_OneNotSkippable_CannotSkip()
    {
        // x > 100 OR x > 10, partition x:[1, 50] → x > 10 not skippable
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BinaryExpression predicate = new(
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(100.0)),
            BinaryOperator.Or,
            new BinaryExpression(new ColumnReference("x"), BinaryOperator.GreaterThan, new LiteralExpression(10.0)));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── BETWEEN ────────────────

    [Fact]
    public void Between_NoOverlap_MaxBelowLow_CanSkip()
    {
        // col BETWEEN 100 AND 200, partition [1, 50] → max 50 < low 100 → skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BetweenExpression predicate = new(
            new ColumnReference("x"), new LiteralExpression(100.0), new LiteralExpression(200.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Between_NoOverlap_MinAboveHigh_CanSkip()
    {
        // col BETWEEN 100 AND 200, partition [300, 400] → min 300 > high 200 → skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 300f, 400f);
        BetweenExpression predicate = new(
            new ColumnReference("x"), new LiteralExpression(100.0), new LiteralExpression(200.0));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Between_Overlapping_CannotSkip()
    {
        // col BETWEEN 10 AND 60, partition [1, 50] → overlaps
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 50f);
        BetweenExpression predicate = new(
            new ColumnReference("x"), new LiteralExpression(10.0), new LiteralExpression(60.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotBetween_AllValuesInRange_CanSkip()
    {
        // col NOT BETWEEN 0 AND 100, partition [10, 50] → all values in [0, 100] → skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 10f, 50f);
        BetweenExpression predicate = new(
            new ColumnReference("x"), new LiteralExpression(0.0), new LiteralExpression(100.0), Negated: true);

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotBetween_SomeValuesOutsideRange_CannotSkip()
    {
        // col NOT BETWEEN 20 AND 30, partition [10, 50] → some values outside [20, 30]
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 10f, 50f);
        BetweenExpression predicate = new(
            new ColumnReference("x"), new LiteralExpression(20.0), new LiteralExpression(30.0), Negated: true);

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── IN ────────────────

    [Fact]
    public void In_AllValuesOutsideRange_CanSkip()
    {
        // col IN (100, 200), partition [1, 10]
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        InExpression predicate = new(
            new ColumnReference("x"),
            [new LiteralExpression(100.0), new LiteralExpression(200.0)]);

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void In_OneValueInsideRange_CannotSkip()
    {
        // col IN (5, 200), partition [1, 10] → 5 is in [1, 10]
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        InExpression predicate = new(
            new ColumnReference("x"),
            [new LiteralExpression(5.0), new LiteralExpression(200.0)]);

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotIn_AllSameValueInList_CanSkip()
    {
        // col NOT IN (5), partition [5, 5] → all rows are 5, and 5 is in exclusion list
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 5f, 5f);
        InExpression predicate = new(
            new ColumnReference("x"),
            [new LiteralExpression(5.0)],
            Negated: true);

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NotIn_MixedValues_CannotSkip()
    {
        // col NOT IN (5), partition [1, 10] → some values aren't 5
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        InExpression predicate = new(
            new ColumnReference("x"),
            [new LiteralExpression(5.0)],
            Negated: true);

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── IS NULL / IS NOT NULL ────────────────

    [Fact]
    public void IsNull_ZeroNulls_CanSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f, nullCount: 0);
        IsNullExpression predicate = new(new ColumnReference("x"));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void IsNull_SomeNulls_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f, nullCount: 5);
        IsNullExpression predicate = new(new ColumnReference("x"));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void IsNotNull_AllNulls_CanSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f, rowCount: 100, nullCount: 100);
        IsNullExpression predicate = new(new ColumnReference("x"), Negated: true);

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void IsNotNull_SomeNonNulls_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f, rowCount: 100, nullCount: 50);
        IsNullExpression predicate = new(new ColumnReference("x"), Negated: true);

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void IsNull_NullCountUnknown_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f, nullCount: null);
        IsNullExpression predicate = new(new ColumnReference("x"));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── Edge cases ────────────────

    [Fact]
    public void UnknownColumn_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("z"), BinaryOperator.Equal, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NoStatistics_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = new();
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void NullMinMax_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = new(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = new ColumnStatisticsRange(null, null, NullCount: 0, RowCount: 100)
        };
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void UnsupportedExpression_FunctionCall_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        FunctionCallExpression predicate = new("abs", [new ColumnReference("x")]);

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void UnsupportedExpression_Like_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = StringStats("name", "A", "Z");
        BinaryExpression predicate = new(
            new ColumnReference("name"), BinaryOperator.Like, new LiteralExpression("%test%"));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void ColumnVsColumn_CannotSkip()
    {
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Equal, new ColumnReference("y"));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void StringComparison_LiteralOutsideRange_CanSkip()
    {
        // col = "zzz", partition ["A", "M"]
        Dictionary<string, ColumnStatisticsRange> statistics = StringStats("name", "A", "M");
        BinaryExpression predicate = new(
            new ColumnReference("name"), BinaryOperator.Equal, new LiteralExpression("zzz"));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void StringComparison_LiteralInsideRange_CannotSkip()
    {
        // col = "B", partition ["A", "M"]
        Dictionary<string, ColumnStatisticsRange> statistics = StringStats("name", "A", "M");
        BinaryExpression predicate = new(
            new ColumnReference("name"), BinaryOperator.Equal, new LiteralExpression("B"));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void ArithmeticOperator_CannotSkip()
    {
        // col + 5 is arithmetic, not comparison — should not skip
        Dictionary<string, ColumnStatisticsRange> statistics = ScalarStats("x", 1f, 10f);
        BinaryExpression predicate = new(
            new ColumnReference("x"), BinaryOperator.Add, new LiteralExpression(5.0));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    // ──────────────── Boolean columns ────────────────

    /// <summary>
    /// Verifies that a SQL <c>TRUE</c> literal correctly compares against
    /// <see cref="DataKind.Boolean"/> column statistics. Partition contains only
    /// <c>false</c> values (min = max = false), so <c>col = TRUE</c> must be skippable.
    /// Before the fix, <c>LiteralToDataValue(true)</c> returned <c>Float32(1f)</c>
    /// and <c>ToDouble(Boolean)</c> returned 0, causing inconsistent comparisons.
    /// </summary>
    [Fact]
    public void Equal_BoolTrueAgainstFalseOnlyPartition_CanSkip()
    {
        // reordered = TRUE, partition contains only false → definitely no match.
        Dictionary<string, ColumnStatisticsRange> statistics = BoolStats("reordered", false, false);
        BinaryExpression predicate = new(
            new ColumnReference("reordered"), BinaryOperator.Equal, new LiteralExpression(true));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_BoolFalseAgainstTrueOnlyPartition_CanSkip()
    {
        // reordered = FALSE, partition contains only true → definitely no match.
        Dictionary<string, ColumnStatisticsRange> statistics = BoolStats("reordered", true, true);
        BinaryExpression predicate = new(
            new ColumnReference("reordered"), BinaryOperator.Equal, new LiteralExpression(false));

        Assert.True(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_BoolTrueAgainstMixedPartition_CannotSkip()
    {
        // reordered = TRUE, partition has both false and true → might match.
        Dictionary<string, ColumnStatisticsRange> statistics = BoolStats("reordered", false, true);
        BinaryExpression predicate = new(
            new ColumnReference("reordered"), BinaryOperator.Equal, new LiteralExpression(true));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }

    [Fact]
    public void Equal_BoolTrueAgainstTrueOnlyPartition_CannotSkip()
    {
        // reordered = TRUE, partition contains only true → might match all rows.
        Dictionary<string, ColumnStatisticsRange> statistics = BoolStats("reordered", true, true);
        BinaryExpression predicate = new(
            new ColumnReference("reordered"), BinaryOperator.Equal, new LiteralExpression(true));

        Assert.False(StatisticsPredicateEvaluator.CanSkipPartition(predicate, statistics));
    }
}
