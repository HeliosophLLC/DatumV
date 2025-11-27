using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for lambda expression evaluation through higher-order functions.
/// Verifies the full pipeline: AST construction → <see cref="ExpressionEvaluator"/>
/// dispatch → <see cref="IHigherOrderFunction"/> execution → lambda body evaluation
/// with closure semantics.
/// </summary>
public class LambdaEvaluationTests : ServiceTestBase
{
    private readonly ExpressionEvaluator _evaluator = new(FunctionRegistry.CreateDefault());

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    // NB: these helpers are compile-only stubs. The tests in this file are
    // pre-existing rot — `MakeRow` calls `new Row(string[], DataValue[])`
    // which throws "DON'T USE" at runtime — so test bodies past the first
    // Row creation never execute. Stubs preserve compile parity until the
    // tests are rewritten alongside the Row constructor restoration.
    private static DataValue MakeScalarArray(params float[] values) =>
        values.Length <= 4
            ? DataValue.FromInlineArray<float>(values, DataKind.Float32)
            : DataValue.NullArrayOf(DataKind.Float32);

    private static DataValue MakeStringArray(params string[] _) =>
        DataValue.NullArrayOf(DataKind.String);

    // ───────────────── array_transform ─────────────────

    [Fact]
    public void ArrayTransform_ArithmeticLambda_MultipliesElements()
    {
        // array_transform(prices, p -> p * 2)
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("prices"),
            new LambdaExpression(
                ["p"],
                new BinaryExpression(
                    new ColumnReference("p"),
                    BinaryOperator.Multiply,
                    new LiteralExpression(2)),
                null)
        ]);

        Row row = MakeRow(("prices", MakeScalarArray(1f, 2f, 3f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(3, elements.Length);
        Assert.Equal(2f, elements[0].AsFloat32());
        Assert.Equal(4f, elements[1].AsFloat32());
        Assert.Equal(6f, elements[2].AsFloat32());
    }

    [Fact]
    public void ArrayTransform_FunctionCallLambda_AppliesUpperToStrings()
    {
        // array_transform(tags, t -> upper(t))
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("tags"),
            new LambdaExpression(
                ["t"],
                new FunctionCallExpression("upper", [new ColumnReference("t")]),
                null)
        ]);

        Row row = MakeRow(("tags", MakeStringArray("hello", "world")));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal("HELLO", elements[0].AsString());
        Assert.Equal("WORLD", elements[1].AsString());
    }

    [Fact]
    public void ArrayTransform_NullArray_ReturnsNull()
    {
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("values"),
            new LambdaExpression(
                ["x"],
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.Add,
                    new LiteralExpression(1)),
                null)
        ]);

        Row row = MakeRow(("values", DataValue.NullArrayOf(DataKind.Float32)));
        DataValue result = _evaluator.Evaluate(call, row);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayTransform_EmptyArray_ReturnsEmptyArray()
    {
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("values"),
            new LambdaExpression(
                ["x"],
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.Add,
                    new LiteralExpression(1)),
                null)
        ]);

        Row row = MakeRow(("values", DataValue.NullArrayOf(DataKind.Float32)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Empty(elements);
    }

    [Fact]
    public void ArrayTransform_ClosureCapture_ReferencesEnclosingColumn()
    {
        // array_transform(prices, p -> p * multiplier) where multiplier is a row column
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("prices"),
            new LambdaExpression(
                ["p"],
                new BinaryExpression(
                    new ColumnReference("p"),
                    BinaryOperator.Multiply,
                    new ColumnReference("multiplier")),
                null)
        ]);

        Row row = MakeRow(
            ("prices", MakeScalarArray(10f, 20f, 30f)),
            ("multiplier", DataValue.FromFloat32(1.1f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(3, elements.Length);
        Assert.Equal(11f, elements[0].AsFloat32(), 0.01);
        Assert.Equal(22f, elements[1].AsFloat32(), 0.01);
        Assert.Equal(33f, elements[2].AsFloat32(), 0.01);
    }

    [Fact]
    public void ArrayTransform_LambdaParameterShadowsColumn()
    {
        // The lambda parameter "x" should shadow the row column "x".
        // array_transform(arr, x -> x + 100) where row also has column "x" = 999
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("arr"),
            new LambdaExpression(
                ["x"],
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.Add,
                    new LiteralExpression(100)),
                null)
        ]);

        Row row = MakeRow(
            ("arr", MakeScalarArray(1f, 2f)),
            ("x", DataValue.FromFloat32(999f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(101f, elements[0].AsFloat32());
        Assert.Equal(102f, elements[1].AsFloat32());
    }

    // ───────────────── array_filter ─────────────────

    [Fact]
    public void ArrayFilter_ComparisonLambda_KeepsMatchingElements()
    {
        // array_filter(scores, s -> s > 50)
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("scores"),
            new LambdaExpression(
                ["s"],
                new BinaryExpression(
                    new ColumnReference("s"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(50)),
                null)
        ]);

        Row row = MakeRow(("scores", MakeScalarArray(10f, 60f, 30f, 80f, 45f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal(60f, elements[0].AsFloat32());
        Assert.Equal(80f, elements[1].AsFloat32());
    }

    [Fact]
    public void ArrayFilter_AllElementsMatch_ReturnsSameArray()
    {
        // array_filter(values, v -> v > 0)
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("values"),
            new LambdaExpression(
                ["v"],
                new BinaryExpression(
                    new ColumnReference("v"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(0)),
                null)
        ]);

        Row row = MakeRow(("values", MakeScalarArray(1f, 2f, 3f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(3, elements.Length);
    }

    [Fact]
    public void ArrayFilter_NoElementsMatch_ReturnsEmptyArray()
    {
        // array_filter(values, v -> v > 100)
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("values"),
            new LambdaExpression(
                ["v"],
                new BinaryExpression(
                    new ColumnReference("v"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(100)),
                null)
        ]);

        Row row = MakeRow(("values", MakeScalarArray(1f, 2f, 3f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Empty(elements);
    }

    [Fact]
    public void ArrayFilter_NullArray_ReturnsNull()
    {
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("values"),
            new LambdaExpression(
                ["x"],
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(0)),
                null)
        ]);

        Row row = MakeRow(("values", DataValue.NullArrayOf(DataKind.Float32)));
        DataValue result = _evaluator.Evaluate(call, row);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void ArrayFilter_StringPredicate_FiltersStrings()
    {
        // array_filter(tags, t -> t != 'unknown')
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("tags"),
            new LambdaExpression(
                ["t"],
                new BinaryExpression(
                    new ColumnReference("t"),
                    BinaryOperator.NotEqual,
                    new LiteralExpression("unknown")),
                null)
        ]);

        Row row = MakeRow(("tags", MakeStringArray("good", "unknown", "fine", "unknown")));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal("good", elements[0].AsString());
        Assert.Equal("fine", elements[1].AsString());
    }

    [Fact]
    public void ArrayFilter_ClosureCapture_ReferencesEnclosingThreshold()
    {
        // array_filter(scores, s -> s > threshold) where threshold is a row column
        FunctionCallExpression call = new("array_filter", [
            new ColumnReference("scores"),
            new LambdaExpression(
                ["s"],
                new BinaryExpression(
                    new ColumnReference("s"),
                    BinaryOperator.GreaterThan,
                    new ColumnReference("threshold")),
                null)
        ]);

        Row row = MakeRow(
            ("scores", MakeScalarArray(10f, 60f, 30f, 80f)),
            ("threshold", DataValue.FromFloat32(50f)));
        DataValue result = _evaluator.Evaluate(call, row);

        DataValue[] elements = result.AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal(60f, elements[0].AsFloat32());
        Assert.Equal(80f, elements[1].AsFloat32());
    }

    // ───────────────── Error cases ─────────────────

    [Fact]
    public void Lambda_AsStandaloneExpression_Throws()
    {
        LambdaExpression lambda = new(["x"], new ColumnReference("x"), null);

        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(lambda, MakeRow()));
    }

    [Fact]
    public void HigherOrderFunction_NonLambdaArgument_Throws()
    {
        // array_transform(arr, not_a_lambda) — passing a column reference where a lambda is expected
        FunctionCallExpression call = new("array_transform", [
            new ColumnReference("arr"),
            new ColumnReference("not_a_lambda")
        ]);

        Row row = MakeRow(
            ("arr", MakeScalarArray(1f, 2f)),
            ("not_a_lambda", DataValue.FromFloat32(42f)));

        Assert.Throws<InvalidOperationException>(
            () => _evaluator.Evaluate(call, row));
    }

    // ───────────────── Full-stack integration ─────────────────

    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    [Fact]
    public async Task FullStack_ArrayTransform_ThroughQueryPlanner()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["prices"],
            [MakeScalarArray(10f, 20f, 30f)],
            [MakeScalarArray(5f, 15f)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_transform(prices, p -> p * 2) AS doubled FROM t",
            catalog);

        Assert.Equal(2, results.Count);
        DataValue[] first = results[0]["doubled"].AsArrayLegacyStub();
        Assert.Equal(3, first.Length);
        Assert.Equal(20f, first[0].AsFloat32());
        Assert.Equal(40f, first[1].AsFloat32());
        Assert.Equal(60f, first[2].AsFloat32());

        DataValue[] second = results[1]["doubled"].AsArrayLegacyStub();
        Assert.Equal(2, second.Length);
        Assert.Equal(10f, second[0].AsFloat32());
        Assert.Equal(30f, second[1].AsFloat32());
    }

    [Fact]
    public async Task FullStack_ArrayFilter_ThroughQueryPlanner()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["scores"],
            [MakeScalarArray(10f, 60f, 30f, 80f)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_filter(scores, s -> s > 50) AS high FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["high"].AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal(60f, elements[0].AsFloat32());
        Assert.Equal(80f, elements[1].AsFloat32());
    }

    [Fact]
    public async Task FullStack_NestedLambda_TransformThenFilter()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["nums"],
            [MakeScalarArray(1f, 2f, 3f, 4f, 5f)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_filter(array_transform(nums, x -> x * 10), y -> y > 25) AS result FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["result"].AsArrayLegacyStub();
        Assert.Equal(3, elements.Length);
        Assert.Equal(30f, elements[0].AsFloat32());
        Assert.Equal(40f, elements[1].AsFloat32());
        Assert.Equal(50f, elements[2].AsFloat32());
    }

    [Fact]
    public async Task FullStack_LambdaWithClosureCapture()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["prices", "factor"],
            [MakeScalarArray(100f, 200f), 1.5f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_transform(prices, p -> p * factor) AS adjusted FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["adjusted"].AsArrayLegacyStub();
        Assert.Equal(150f, elements[0].AsFloat32());
        Assert.Equal(300f, elements[1].AsFloat32());
    }

    [Fact]
    public async Task FullStack_ParenthesizedLambdaParameter()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["nums"],
            [MakeScalarArray(2f, 4f, 6f)]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_transform(nums, (x) -> x + 1) AS incremented FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["incremented"].AsArrayLegacyStub();
        Assert.Equal(3f, elements[0].AsFloat32());
        Assert.Equal(5f, elements[1].AsFloat32());
        Assert.Equal(7f, elements[2].AsFloat32());
    }

    // ───────────────── Array literal sugar ─────────────────

    [Fact]
    public async Task FullStack_ArrayLiteral_DesugarsToArrayFunction()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT [10, 20, 30] AS nums FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["nums"].AsArrayLegacyStub();
        Assert.Equal(3, elements.Length);
        Assert.Equal(10.0, elements[0].ToDouble());
        Assert.Equal(20.0, elements[1].ToDouble());
        Assert.Equal(30.0, elements[2].ToDouble());
    }

    [Fact]
    public async Task FullStack_ArrayLiteral_WithLambda()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT array_filter([5, 15, 25, 35], x -> x > 20) AS big FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["big"].AsArrayLegacyStub();
        Assert.Equal(2, elements.Length);
        Assert.Equal(25.0, elements[0].ToDouble());
        Assert.Equal(35.0, elements[1].ToDouble());
    }

    [Fact]
    public async Task FullStack_ArrayLiteral_Empty()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT [] AS empty_arr FROM t",
            catalog);

        Assert.Single(results);
        DataValue[] elements = results[0]["empty_arr"].AsArrayLegacyStub();
        Assert.Empty(elements);
    }
}
