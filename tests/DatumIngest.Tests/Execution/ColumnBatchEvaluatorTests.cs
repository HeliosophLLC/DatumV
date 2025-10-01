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

    [Fact]
    public void CastColumn_ArenaBackedString_ToFloat64()
    {
        // Reproduces: CAST(arena_string AS FLOAT64) fails with
        // "This string value is arena-backed. Use AsString(StringArena) to materialise it."
        // because CastFunction.Execute calls .AsString() without an arena.
        using ColumnBatch batch = ColumnBatch.Create(["value"], rowCapacity: 2);
        (int off1, int len1) = batch.StringArena.Append("3.14");
        (int off2, int len2) = batch.StringArena.Append("2.72");
        batch.SetValue(0, 0, DataValue.FromStringSlice(off1, len1));
        batch.SetValue(0, 1, DataValue.FromStringSlice(off2, len2));
        batch.SetRowCount(2);

        Expression expression = new CastExpression(new ColumnReference("value"), "Float64");

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(DataKind.Float64, result[0].Kind);
        Assert.Equal(3.14, result[0].AsFloat64(), precision: 5);
        Assert.Equal(2.72, result[1].AsFloat64(), precision: 5);
    }

    [Fact]
    public void FunctionCall_ArenaBackedString()
    {
        // Scalar functions (e.g. lower()) must handle arena-backed string inputs.
        using ColumnBatch batch = ColumnBatch.Create(["name"], rowCapacity: 2);
        (int off1, int len1) = batch.StringArena.Append("HELLO");
        (int off2, int len2) = batch.StringArena.Append("WORLD");
        batch.SetValue(0, 0, DataValue.FromStringSlice(off1, len1));
        batch.SetValue(0, 1, DataValue.FromStringSlice(off2, len2));
        batch.SetRowCount(2);

        Expression expression = new FunctionCallExpression("lower", [new ColumnReference("name")]);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal("hello", result[0].AsString());
        Assert.Equal("world", result[1].AsString());
    }

    [Fact]
    public void Arithmetic_ArenaBackedString()
    {
        // Arithmetic on arena-backed string columns (implicit string-to-float coercion).
        using ColumnBatch batch = ColumnBatch.Create(["value"], rowCapacity: 2);
        (int off1, int len1) = batch.StringArena.Append("10");
        (int off2, int len2) = batch.StringArena.Append("20");
        batch.SetValue(0, 0, DataValue.FromStringSlice(off1, len1));
        batch.SetValue(0, 1, DataValue.FromStringSlice(off2, len2));
        batch.SetRowCount(2);

        Expression expression = new BinaryExpression(
            new ColumnReference("value"), BinaryOperator.Add, new LiteralExpression(5f));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(15f, result[0].AsFloat32());
        Assert.Equal(25f, result[1].AsFloat32());
    }

    [Fact]
    public void IsTruthy_ArenaBackedString()
    {
        // Arena-backed non-empty string should be truthy in AND/OR/CASE conditions.
        using ColumnBatch batch = ColumnBatch.Create(["flag"], rowCapacity: 2);
        (int off1, int len1) = batch.StringArena.Append("yes");
        (int off2, int len2) = batch.StringArena.Append("");
        batch.SetValue(0, 0, DataValue.FromStringSlice(off1, len1));
        batch.SetValue(0, 1, DataValue.FromStringSlice(off2, len2));
        batch.SetRowCount(2);

        // NOT "flag" — tests IsTruthy on arena-backed strings.
        Expression expression = new UnaryExpression(
            UnaryOperator.Not, new ColumnReference("flag"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.False(result[0].AsBoolean()); // NOT "yes" → false
        Assert.True(result[1].AsBoolean());  // NOT "" → true
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

    // ─────── Selection vector: poisoned-value CASE tests ───────
    //
    // These tests place values in the batch that would throw if processed,
    // then verify CASE short-circuits correctly (only evaluates branches
    // for rows that actually need them).  If a method ignores the selection
    // vector and processes a poisoned row, the test fails with an exception.

    /// <summary>
    /// Helper: creates a single-column <see cref="ColumnBatch"/> with arena-backed
    /// strings, simulating values decoded from a .datum file.
    /// </summary>
    private static ColumnBatch ArenaStringBatch(string columnName, params string[] values)
    {
        ColumnBatch batch = ColumnBatch.Create([columnName], values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            (int offset, int length) = batch.StringArena.Append(values[i]);
            batch.SetValue(0, i, DataValue.FromStringSlice(offset, length));
        }

        batch.SetRowCount(values.Length);
        return batch;
    }

    [Fact]
    public void CaseWhen_ElseBranch_NotEvaluatedForMatchingRows()
    {
        // CASE WHEN col = 'NULL' THEN 0.0 ELSE CAST(col AS FLOAT64) END
        // Row 1 has col='NULL' — CAST('NULL' AS FLOAT64) would throw if evaluated.
        using ColumnBatch batch = ArenaStringBatch("col", "3.14", "NULL", "2.72");

        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("NULL")),
                    new LiteralExpression(0.0)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Float64"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(3.14, result[0].AsFloat64(), precision: 5);
        Assert.Equal(0.0, result[1].AsFloat64(), precision: 5);
        Assert.Equal(2.72, result[2].AsFloat64(), precision: 5);
    }

    [Fact]
    public void CaseWhen_ThenBranch_NotEvaluatedForNonMatchingRows()
    {
        // CASE WHEN col = '42' THEN CAST(col AS INT32) ELSE -1 END
        // Row 1 has col='bad' — CAST('bad' AS INT32) would throw if evaluated.
        using ColumnBatch batch = ArenaStringBatch("col", "42", "bad", "42");

        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("42")),
                    new CastExpression(new ColumnReference("col"), "Int32")),
            ],
            ElseResult: new LiteralExpression(-1));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(42, result[0].AsInt32());
        Assert.Equal(-1, result[1].AsInt32());
        Assert.Equal(42, result[2].AsInt32());
    }

    [Fact]
    public void CaseWhen_MultipleBranches_OnlyMatchingBranchEvaluated()
    {
        // CASE
        //   WHEN col = 'int'   THEN CAST(val AS INT32)
        //   WHEN col = 'float' THEN CAST(val AS FLOAT64)
        //   ELSE -1
        // END
        //
        // Row 0: col='int',   val='42'    → matches branch 1, CAST AS INT32 = 42
        // Row 1: col='float', val='3.14'  → matches branch 2, CAST AS FLOAT64 = 3.14
        // Row 2: col='other', val='boom'  → matches ELSE = -1
        //
        // Without selection vectors:
        //   Branch 1 CAST('3.14' AS INT32) throws for row 1
        //   Branch 1 CAST('boom' AS INT32) throws for row 2
        //   Branch 2 CAST('boom' AS FLOAT64) throws for row 2
        using ColumnBatch batch = ColumnBatch.Create(["col", "val"], rowCapacity: 3);

        string[] cols = ["int", "float", "other"];
        string[] vals = ["42", "3.14", "boom"];
        for (int i = 0; i < 3; i++)
        {
            (int co, int cl) = batch.StringArena.Append(cols[i]);
            batch.SetValue(0, i, DataValue.FromStringSlice(co, cl));
            (int vo, int vl) = batch.StringArena.Append(vals[i]);
            batch.SetValue(1, i, DataValue.FromStringSlice(vo, vl));
        }

        batch.SetRowCount(3);

        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("int")),
                    new CastExpression(new ColumnReference("val"), "Int32")),
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("float")),
                    new CastExpression(new ColumnReference("val"), "Float64")),
            ],
            ElseResult: new LiteralExpression(-1));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(42, result[0].AsInt32());
        Assert.Equal(3.14, result[1].AsFloat64(), precision: 5);
        Assert.Equal(-1, result[2].AsInt32());
    }

    [Fact]
    public void CaseWhen_NestedCase_InnerCaseRespectsOuterSelection()
    {
        // CASE WHEN col = 'nested' THEN
        //   CASE WHEN col = 'nested' THEN 99 ELSE CAST(col AS INT32) END
        // ELSE 0 END
        // Inner ELSE has CAST(col AS INT32) — would throw for 'nested', but inner
        // condition matches so inner ELSE is skipped.
        using ColumnBatch batch = ArenaStringBatch("col", "nested", "other");

        Expression innerCase = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("nested")),
                    new LiteralExpression(99)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Int32"));

        Expression outerCase = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("nested")),
                    innerCase),
            ],
            ElseResult: new LiteralExpression(0));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(outerCase, batch);

        Assert.Equal(99, result[0].AsInt32());
        Assert.Equal(0, result[1].AsInt32());
    }

    [Fact]
    public void SimpleCaseWhen_ElseNotEvaluatedForMatchedRows()
    {
        // CASE col WHEN 'A' THEN 1 WHEN 'B' THEN 2 ELSE CAST(col AS INT32) END
        // Rows with 'A' and 'B' are matched — CAST('A'/'B' AS INT32) would throw.
        using ColumnBatch batch = ArenaStringBatch("col", "A", "B", "99");

        Expression expression = new CaseExpression(
            Operand: new ColumnReference("col"),
            WhenClauses:
            [
                new WhenClause(new LiteralExpression("A"), new LiteralExpression(1)),
                new WhenClause(new LiteralExpression("B"), new LiteralExpression(2)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Int32"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(1, result[0].AsInt32());
        Assert.Equal(2, result[1].AsInt32());
        Assert.Equal(99, result[2].AsInt32());
    }

    [Fact]
    public void CaseWhen_FunctionInBranch_NotCalledForInactiveRows()
    {
        // CASE WHEN col = 'skip' THEN 0 ELSE CAST(col AS FLOAT64) + 1.0 END
        // The ELSE branch has both a CAST and arithmetic — both must respect
        // the selection vector. col='skip' would throw in CAST.
        using ColumnBatch batch = ArenaStringBatch("col", "5.0", "skip", "10.0");

        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("skip")),
                    new LiteralExpression(0.0)),
            ],
            ElseResult: new BinaryExpression(
                new CastExpression(new ColumnReference("col"), "Float64"),
                BinaryOperator.Add,
                new LiteralExpression(1.0)));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        Assert.Equal(6.0f, result[0].AsFloat32(), 0.001f);
        Assert.Equal(0.0, result[1].AsFloat64(), precision: 5);
        Assert.Equal(11.0f, result[2].AsFloat32(), 0.001f);
    }

    [Fact]
    public void CaseWhen_NestedCaseInElse_InheritsOuterActiveVector()
    {
        // CASE WHEN col = 'NULL' THEN 0
        //   ELSE CASE
        //     WHEN CAST(col AS FLOAT64) > 100 THEN 999
        //     ELSE CAST(col AS FLOAT64)
        //   END
        // END
        // Row 0: col='NULL'  → outer WHEN matches, inner CASE never runs
        //   (CAST('NULL' AS FLOAT64) would throw if evaluated)
        // Row 1: col='50.0'  → outer ELSE → inner ELSE → 50.0
        // Row 2: col='200.0' → outer ELSE → inner WHEN → 999
        using ColumnBatch batch = ArenaStringBatch("col", "NULL", "50.0", "200.0");

        Expression castCol = new CastExpression(new ColumnReference("col"), "Float64");

        Expression innerCase = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(castCol, BinaryOperator.GreaterThan, new LiteralExpression(100.0)),
                    new LiteralExpression(999.0)),
            ],
            ElseResult: castCol);

        Expression outerCase = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("NULL")),
                    new LiteralExpression(0.0)),
            ],
            ElseResult: innerCase);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(outerCase, batch);

        Assert.Equal(0.0, result[0].AsFloat64(), precision: 5);
        Assert.Equal(50.0, result[1].AsFloat64(), precision: 5);
        Assert.Equal(999.0, result[2].AsFloat64(), precision: 5);
    }

    [Fact]
    public void CaseWhen_InsideFunctionCall_SelectionVectorPropagates()
    {
        // COALESCE(CASE WHEN col = 'NULL' THEN NULL ELSE CAST(col AS FLOAT64) END, 0.0)
        // The CASE is nested inside a COALESCE function call.
        // Row 0: col='3.14' → CASE returns 3.14, COALESCE returns 3.14
        // Row 1: col='NULL' → CASE returns NULL, COALESCE returns 0.0
        // Row 2: col='7.5'  → CASE returns 7.5,  COALESCE returns 7.5
        using ColumnBatch batch = ArenaStringBatch("col", "3.14", "NULL", "7.5");

        Expression caseExpr = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("NULL")),
                    new LiteralExpression(null)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Float64"));

        Expression coalesce = new FunctionCallExpression(
            "COALESCE", [caseExpr, new LiteralExpression(0.0)]);

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(coalesce, batch);

        Assert.Equal(3.14, result[0].AsFloat64(), precision: 5);
        Assert.Equal(0.0, result[1].AsFloat64(), precision: 5);
        Assert.Equal(7.5, result[2].AsFloat64(), precision: 5);
    }

    [Fact]
    public void CaseWhen_LargeBatch_SelectionVectorSizingCorrect()
    {
        // 1000-row batch to test that active arrays from ArrayPool (which may
        // be larger than requested) don't cause off-by-one or out-of-bounds issues.
        const int rowCount = 1000;
        string[] columnNames = ["col"];
        ColumnBatch batch = ColumnBatch.Create(columnNames, rowCount);

        for (int i = 0; i < rowCount; i++)
        {
            // Even rows: parseable float. Odd rows: 'NULL' (poison for CAST).
            string value = i % 2 == 0 ? i.ToString() : "NULL";
            (int offset, int length) = batch.StringArena.Append(value);
            batch.SetValue(0, i, DataValue.FromStringSlice(offset, length));
        }

        batch.SetRowCount(rowCount);

        // CASE WHEN col = 'NULL' THEN -1.0 ELSE CAST(col AS FLOAT64) END
        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("NULL")),
                    new LiteralExpression(-1.0)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Float64"));

        using ColumnBatchEvaluator evaluator = new(_functions);
        DataValue[] result = evaluator.EvaluateColumn(expression, batch);

        for (int i = 0; i < rowCount; i++)
        {
            if (i % 2 == 0)
            {
                Assert.Equal((double)i, result[i].AsFloat64(), precision: 5);
            }
            else
            {
                Assert.Equal(-1.0, result[i].AsFloat64(), precision: 5);
            }
        }

        batch.Dispose();
    }

    [Fact]
    public void CaseWhen_MatchesRowEvaluator_WithPoisonedValues()
    {
        // Parity test: run the same CASE expression through both evaluators
        // and verify identical results. Uses the motivating bug's pattern.
        // CASE WHEN col = 'NULL' THEN 0.0 ELSE CAST(col AS FLOAT64) END
        Expression expression = new CaseExpression(
            Operand: null,
            WhenClauses:
            [
                new WhenClause(
                    new BinaryExpression(
                        new ColumnReference("col"), BinaryOperator.Equal, new LiteralExpression("NULL")),
                    new LiteralExpression(0.0)),
            ],
            ElseResult: new CastExpression(new ColumnReference("col"), "Float64"));

        string[] inputStrings = ["3.14", "NULL", "2.72", "NULL", "100.5"];

        // Row-at-a-time evaluator.
        ExpressionEvaluator rowEvaluator = new(_functions);
        DataValue[] rowResults = new DataValue[inputStrings.Length];
        for (int i = 0; i < inputStrings.Length; i++)
        {
            Row row = new(["col"], [DataValue.FromString(inputStrings[i])]);
            rowResults[i] = rowEvaluator.Evaluate(expression, row);
        }

        // Column-at-a-time evaluator with arena-backed strings.
        using ColumnBatch batch = ArenaStringBatch("col", inputStrings);
        using ColumnBatchEvaluator columnEvaluator = new(_functions);
        DataValue[] columnResults = columnEvaluator.EvaluateColumn(expression, batch);

        for (int i = 0; i < inputStrings.Length; i++)
        {
            Assert.Equal(rowResults[i].AsFloat64(), columnResults[i].AsFloat64(), precision: 5);
        }
    }
}
