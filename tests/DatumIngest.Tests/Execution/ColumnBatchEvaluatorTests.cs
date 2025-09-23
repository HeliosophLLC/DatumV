using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="ColumnBatchEvaluator"/> verifying that column-at-a-time evaluation
/// produces identical results to the row-at-a-time <see cref="ExpressionEvaluator"/>.
/// </summary>
public sealed class ColumnBatchEvaluatorTests
{
    private readonly FunctionRegistry _functions = FunctionRegistry.CreateDefault();

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Creates a <see cref="ColumnBatch"/> from named columns and row data.
    /// </summary>
    private static ColumnBatch MakeBatch(string[] columnNames, DataValue[][] rows)
    {
        int rowCount = rows.Length;
        int columnCount = columnNames.Length;
        ColumnBatch batch = ColumnBatch.Create(columnNames, rowCount);

        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                batch.SetValue(column, row, rows[row][column]);
            }
        }

        batch.SetRowCount(rowCount);
        return batch;
    }

    private static ColumnBatch SingleColumnBatch(string name, params DataValue[] values)
    {
        ColumnBatch batch = ColumnBatch.Create([name], values.Length);
        for (int row = 0; row < values.Length; row++)
        {
            batch.SetValue(0, row, values[row]);
        }

        batch.SetRowCount(values.Length);
        return batch;
    }

    // ───────────────────────── Literals ─────────────────────────

    [Fact]
    public void LiteralIntegerFillsColumn()
    {
        using ColumnBatch batch = ColumnBatch.Create(["dummy"], 3);
        batch.SetRowCount(3);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(new LiteralExpression(42), batch);

        Assert.Equal(DataValue.FromInt32(42), result[0]);
        Assert.Equal(DataValue.FromInt32(42), result[1]);
        Assert.Equal(DataValue.FromInt32(42), result[2]);
    }

    [Fact]
    public void LiteralNullFillsColumn()
    {
        using ColumnBatch batch = ColumnBatch.Create(["dummy"], 2);
        batch.SetRowCount(2);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(new LiteralExpression(null), batch);

        Assert.True(result[0].IsNull);
        Assert.True(result[1].IsNull);
    }

    // ───────────────────────── Column references ─────────────────────────

    [Fact]
    public void ColumnReferenceReturnsColumnBuffer()
    {
        using ColumnBatch batch = SingleColumnBatch("age",
            DataValue.FromInt32(10), DataValue.FromInt32(20), DataValue.FromInt32(30));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(new ColumnReference("age"), batch);

        Assert.Equal(DataValue.FromInt32(10), result[0]);
        Assert.Equal(DataValue.FromInt32(20), result[1]);
        Assert.Equal(DataValue.FromInt32(30), result[2]);
    }

    [Fact]
    public void QualifiedColumnReferenceResolves()
    {
        using ColumnBatch batch = MakeBatch(
            ["t.name"],
            [
                [DataValue.FromString("Alice")],
                [DataValue.FromString("Bob")],
            ]);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(new ColumnReference("t", "name"), batch);

        Assert.Equal("Alice", result[0].AsString());
        Assert.Equal("Bob", result[1].AsString());
    }

    [Fact]
    public void ColumnReferenceNotFoundThrows()
    {
        using ColumnBatch batch = SingleColumnBatch("x", DataValue.FromInt32(1));

        using ColumnBatchEvaluator evaluator = new(_functions);
        Assert.Throws<InvalidOperationException>(
            () => evaluator.EvaluateColumn(new ColumnReference("missing"), batch));
    }

    // ───────────────────────── Arithmetic ─────────────────────────

    [Fact]
    public void AdditionColumn()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(1f), DataValue.FromFloat32(10f)],
                [DataValue.FromFloat32(2f), DataValue.FromFloat32(20f)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Add, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(11f, result[0].AsFloat32());
        Assert.Equal(22f, result[1].AsFloat32());
    }

    [Fact]
    public void SubtractionColumn()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(10f), DataValue.FromFloat32(3f)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Subtract, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(7f, result[0].AsFloat32());
    }

    [Fact]
    public void MultiplyAndDivideColumn()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(6f), DataValue.FromFloat32(3f)],
            ]);

        using ColumnBatchEvaluator evaluator = new(_functions);

        DataValue[] multiply = evaluator.EvaluateColumn(
            new BinaryExpression(new ColumnReference("a"), BinaryOperator.Multiply, new ColumnReference("b")),
            batch);
        Assert.Equal(18f, multiply[0].AsFloat32());

        DataValue[] divide = evaluator.EvaluateColumn(
            new BinaryExpression(new ColumnReference("a"), BinaryOperator.Divide, new ColumnReference("b")),
            batch);
        Assert.Equal(2f, divide[0].AsFloat32());
    }

    [Fact]
    public void DivisionByZeroReturnsNaN()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(1f), DataValue.FromFloat32(0f)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Divide, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(float.IsNaN(result[0].AsFloat32()));
    }

    [Fact]
    public void ArithmeticNullPropagation()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(5f), DataValue.Null(DataKind.Float32)],
                [DataValue.Null(DataKind.Float32), DataValue.FromFloat32(3f)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Add, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].IsNull);
        Assert.True(result[1].IsNull);
    }

    // ───────────────────────── Comparison ─────────────────────────

    [Fact]
    public void EqualityComparison()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(1)],
                [DataValue.FromInt32(1), DataValue.FromInt32(2)],
                [DataValue.FromInt32(2), DataValue.FromInt32(1)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Equal, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean()); // equal
        Assert.False(result[1].AsBoolean()); // not equal
        Assert.False(result[2].AsBoolean()); // not equal
    }

    [Fact]
    public void LessThanComparison()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromInt32(1), DataValue.FromInt32(2)],
                [DataValue.FromInt32(2), DataValue.FromInt32(1)],
                [DataValue.FromInt32(1), DataValue.FromInt32(1)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.LessThan, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean());
        Assert.False(result[1].AsBoolean());
        Assert.False(result[2].AsBoolean());
    }

    [Fact]
    public void ComparisonNullPropagation()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromInt32(1), DataValue.Null(DataKind.Int32)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Equal, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].IsNull);
    }

    [Fact]
    public void StringComparison()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromString("abc"), DataValue.FromString("abc")],
                [DataValue.FromString("abc"), DataValue.FromString("xyz")],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Equal, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean());
        Assert.False(result[1].AsBoolean());
    }

    // ───────────────────────── AND / OR ─────────────────────────

    [Fact]
    public void LogicalAndColumn()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromBoolean(true), DataValue.FromBoolean(true)],
                [DataValue.FromBoolean(true), DataValue.FromBoolean(false)],
                [DataValue.FromBoolean(false), DataValue.FromBoolean(true)],
                [DataValue.FromBoolean(false), DataValue.FromBoolean(false)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.And, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean());
        Assert.False(result[1].AsBoolean());
        Assert.False(result[2].AsBoolean());
        Assert.False(result[3].AsBoolean());
    }

    [Fact]
    public void LogicalOrColumn()
    {
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromBoolean(true), DataValue.FromBoolean(true)],
                [DataValue.FromBoolean(true), DataValue.FromBoolean(false)],
                [DataValue.FromBoolean(false), DataValue.FromBoolean(true)],
                [DataValue.FromBoolean(false), DataValue.FromBoolean(false)],
            ]);

        Expression expression = new BinaryExpression(
            new ColumnReference("a"), BinaryOperator.Or, new ColumnReference("b"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean());
        Assert.True(result[1].AsBoolean());
        Assert.True(result[2].AsBoolean());
        Assert.False(result[3].AsBoolean());
    }

    // ───────────────────────── Unary ─────────────────────────

    [Fact]
    public void UnaryNotColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("flag",
            DataValue.FromBoolean(true), DataValue.FromBoolean(false), DataValue.Null(DataKind.Boolean));

        Expression expression = new UnaryExpression(UnaryOperator.Not, new ColumnReference("flag"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.False(result[0].AsBoolean());
        Assert.True(result[1].AsBoolean());
        Assert.True(result[2].IsNull);
    }

    [Fact]
    public void UnaryNegateColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromFloat32(5f), DataValue.FromFloat32(-3f));

        Expression expression = new UnaryExpression(UnaryOperator.Negate, new ColumnReference("value"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(-5f, result[0].AsFloat32());
        Assert.Equal(3f, result[1].AsFloat32());
    }

    // ───────────────────────── IS NULL ─────────────────────────

    [Fact]
    public void IsNullColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(1), DataValue.Null(DataKind.Int32), DataValue.FromInt32(3));

        Expression expression = new IsNullExpression(new ColumnReference("value"), Negated: false);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.False(result[0].AsBoolean());
        Assert.True(result[1].AsBoolean());
        Assert.False(result[2].AsBoolean());
    }

    [Fact]
    public void IsNotNullColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(1), DataValue.Null(DataKind.Int32));

        Expression expression = new IsNullExpression(new ColumnReference("value"), Negated: true);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean());
        Assert.False(result[1].AsBoolean());
    }

    // ───────────────────────── BETWEEN ─────────────────────────

    [Fact]
    public void BetweenColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(5), DataValue.FromInt32(15), DataValue.FromInt32(10));

        Expression expression = new BetweenExpression(
            new ColumnReference("value"),
            new LiteralExpression(5),
            new LiteralExpression(10),
            Negated: false);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean()); // 5 in [5,10]
        Assert.False(result[1].AsBoolean()); // 15 not in [5,10]
        Assert.True(result[2].AsBoolean()); // 10 in [5,10]
    }

    // ───────────────────────── IN ─────────────────────────

    [Fact]
    public void InExpressionWithLiterals()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(1), DataValue.FromInt32(2), DataValue.FromInt32(3));

        Expression expression = new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(1), new LiteralExpression(3)],
            Negated: false);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.True(result[0].AsBoolean()); // 1 in (1,3)
        Assert.False(result[1].AsBoolean()); // 2 not in (1,3)
        Assert.True(result[2].AsBoolean()); // 3 in (1,3)
    }

    [Fact]
    public void NotInExpression()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(1), DataValue.FromInt32(2));

        Expression expression = new InExpression(
            new ColumnReference("value"),
            [new LiteralExpression(1)],
            Negated: true);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.False(result[0].AsBoolean()); // 1 NOT IN (1) → false
        Assert.True(result[1].AsBoolean()); // 2 NOT IN (1) → true
    }

    // ───────────────────────── CASE ─────────────────────────

    [Fact]
    public void SearchedCaseColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(1), DataValue.FromInt32(2), DataValue.FromInt32(3));

        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("value"), BinaryOperator.Equal, new LiteralExpression(1)),
                    new LiteralExpression("one")),
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("value"), BinaryOperator.Equal, new LiteralExpression(2)),
                    new LiteralExpression("two")),
            ],
            ElseResult: new LiteralExpression("other"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal("one", result[0].AsString());
        Assert.Equal("two", result[1].AsString());
        Assert.Equal("other", result[2].AsString());
    }

    [Fact]
    public void SimpleCaseColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("status",
            DataValue.FromString("A"), DataValue.FromString("B"), DataValue.FromString("C"));

        Expression expression = new CaseExpression(
            Operand: new ColumnReference("status"),
            WhenClauses:
            [
                new WhenClause(new LiteralExpression("A"), new LiteralExpression(100)),
                new WhenClause(new LiteralExpression("B"), new LiteralExpression(200)),
            ],
            ElseResult: new LiteralExpression(0));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(100, result[0].AsInt32());
        Assert.Equal(200, result[1].AsInt32());
        Assert.Equal(0, result[2].AsInt32());
    }

    // ───────────────────────── CAST ─────────────────────────

    [Fact]
    public void CastColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromUInt8(100), DataValue.FromUInt8(200));

        Expression expression = new CastExpression(new ColumnReference("value"), "Float32");

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(DataKind.Float32, result[0].Kind);
        Assert.Equal(100f, result[0].AsFloat32());
        Assert.Equal(200f, result[1].AsFloat32());
    }

    // ───────────────────────── Function calls ─────────────────────────

    [Fact]
    public void FunctionCallColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("name",
            DataValue.FromString("hello"), DataValue.FromString("world"));

        Expression expression = new FunctionCallExpression("UPPER", [new ColumnReference("name")]);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal("HELLO", result[0].AsString());
        Assert.Equal("WORLD", result[1].AsString());
    }

    [Fact]
    public void CoalesceFunctionColumn()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.Null(DataKind.Float32), DataValue.FromFloat32(42f));

        Expression expression = new FunctionCallExpression(
            "COALESCE",
            [new ColumnReference("value"), new LiteralExpression(0f)]);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(0f, result[0].AsFloat32());
        Assert.Equal(42f, result[1].AsFloat32());
    }

    // ───────────────────────── Filter ─────────────────────────

    [Fact]
    public void EvaluateFilterReturnsSelectedIndices()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(10), DataValue.FromInt32(5),
            DataValue.FromInt32(20), DataValue.FromInt32(3));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"), BinaryOperator.GreaterThan, new LiteralExpression(8));

        using ColumnBatchEvaluator evaluator = new(_functions);
        int[] selected = new int[batch.RowCount];
        int count = evaluator.EvaluateFilter(predicate, batch, selected);

        Assert.Equal(2, count);
        Assert.Equal(0, selected[0]); // row 0 (value=10)
        Assert.Equal(2, selected[1]); // row 2 (value=20)
    }

    [Fact]
    public void EvaluateFilterWithNullsSkipsNullRows()
    {
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(10), DataValue.Null(DataKind.Int32), DataValue.FromInt32(20));

        Expression predicate = new BinaryExpression(
            new ColumnReference("value"), BinaryOperator.GreaterThan, new LiteralExpression(5));

        using ColumnBatchEvaluator evaluator = new(_functions);
        int[] selected = new int[batch.RowCount];
        int count = evaluator.EvaluateFilter(predicate, batch, selected);

        Assert.Equal(2, count);
        Assert.Equal(0, selected[0]);
        Assert.Equal(2, selected[1]);
    }

    // ───────────────────────── Complex expressions ─────────────────────────

    [Fact]
    public void NestedExpressionColumn()
    {
        // (a + b) * 2
        using ColumnBatch batch = MakeBatch(
            ["a", "b"],
            [
                [DataValue.FromFloat32(3f), DataValue.FromFloat32(4f)],
                [DataValue.FromFloat32(10f), DataValue.FromFloat32(5f)],
            ]);

        Expression expression = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("a"), BinaryOperator.Add, new ColumnReference("b")),
            BinaryOperator.Multiply,
            new LiteralExpression(2f));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(14f, result[0].AsFloat32()); // (3+4)*2
        Assert.Equal(30f, result[1].AsFloat32()); // (10+5)*2
    }

    [Fact]
    public void ComplexFilterWithAndOr()
    {
        // value > 5 AND value < 20
        using ColumnBatch batch = SingleColumnBatch("value",
            DataValue.FromInt32(3), DataValue.FromInt32(10),
            DataValue.FromInt32(25), DataValue.FromInt32(15));

        Expression predicate = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("value"), BinaryOperator.GreaterThan, new LiteralExpression(5)),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("value"), BinaryOperator.LessThan, new LiteralExpression(20)));

        using ColumnBatchEvaluator evaluator = new(_functions);
        int[] selected = new int[batch.RowCount];
        int count = evaluator.EvaluateFilter(predicate, batch, selected);

        Assert.Equal(2, count);
        Assert.Equal(1, selected[0]); // value=10
        Assert.Equal(3, selected[1]); // value=15
    }

    // ───────────────────────── Parity with ExpressionEvaluator ─────────────────────────

    [Fact]
    public void ColumnBatchMatchesRowEvaluatorForMixedExpression()
    {
        // CASE WHEN a > 5 THEN a * 2 ELSE 0 END
        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("a"), BinaryOperator.GreaterThan, new LiteralExpression(5)),
                    new BinaryExpression(
                        new ColumnReference("a"), BinaryOperator.Multiply, new LiteralExpression(2f))),
            ],
            ElseResult: new LiteralExpression(0f));

        DataValue[] inputValues =
        [
            DataValue.FromFloat32(3f),
            DataValue.FromFloat32(10f),
            DataValue.FromFloat32(7f),
        ];

        // Row-at-a-time evaluator.
        ExpressionEvaluator rowEvaluator = new(_functions);
        DataValue[] rowResults = new DataValue[inputValues.Length];
        for (int index = 0; index < inputValues.Length; index++)
        {
            Row row = new(["a"], [inputValues[index]]);
            rowResults[index] = rowEvaluator.Evaluate(expression, row);
        }

        // Column-at-a-time evaluator.
        using ColumnBatch batch = SingleColumnBatch("a", inputValues);
        using ColumnBatchEvaluator columnEvaluator = new(_functions);
        DataValue[] columnResults = columnEvaluator.EvaluateColumn(expression, batch);

        for (int index = 0; index < inputValues.Length; index++)
        {
            Assert.Equal(rowResults[index].AsFloat32(), columnResults[index].AsFloat32(), 0.0001f);
        }
    }

    // ─────────────── Source span error enrichment ───────────────

    [Fact]
    public void Error_IncludesSourceSpan_WhenExpressionHasSpan()
    {
        var span = new SourceSpan(14, 5, 20);
        var expr = new CastExpression(
            new LiteralExpression("not_a_number"), "Int32", span);

        ColumnBatch batch = SingleColumnBatch("dummy", DataValue.FromInt32(1));
        using ColumnBatchEvaluator evaluator = new(_functions);

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => evaluator.EvaluateColumn(expr, batch));

        Assert.Equal(span, ex.Span);
        Assert.Contains("Line 14", ex.Message);
        Assert.Contains("Col 5", ex.Message);
    }

    [Fact]
    public void Error_FallsBackToChildSpan_ForBinaryExpression()
    {
        var childSpan = new SourceSpan(3, 10, 5);
        var expr = new BinaryExpression(
            new ColumnReference(null, "missing_col", childSpan),
            BinaryOperator.Add,
            new LiteralExpression(1));

        ColumnBatch batch = SingleColumnBatch("dummy", DataValue.FromInt32(1));
        using ColumnBatchEvaluator evaluator = new(_functions);

        var ex = Assert.Throws<ExpressionEvaluationException>(
            () => evaluator.EvaluateColumn(expr, batch));

        Assert.Equal(childSpan, ex.Span);
        Assert.Contains("Line 3", ex.Message);
    }

    [Fact]
    public void Error_RethrowsUnchanged_WhenNoSpanAvailable()
    {
        var expr = new LiteralExpression(new object());

        ColumnBatch batch = SingleColumnBatch("dummy", DataValue.FromInt32(1));
        using ColumnBatchEvaluator evaluator = new(_functions);

        Assert.Throws<InvalidOperationException>(
            () => evaluator.EvaluateColumn(expr, batch));
    }
}
