using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Unit tests for <see cref="StratifiedSampleOperator"/> and <see cref="BalancedSampleOperator"/>.
/// </summary>
public sealed class StratifiedSampleTests : ServiceTestBase
{
    private ExecutionContext CreateContext(int? maxStratifyClasses = null)
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            new LocalBufferPool())
        {
            MaxStratifyClasses = maxStratifyClasses,
        };
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        return await op.CollectRowsAsync(context);
    }

    /// <summary>
    /// Generates test rows with the given class distribution.
    /// </summary>
    private static Row[] GenerateClassRows(params (string label, int count)[] classes)
    {
        List<Row> rows = [];
        int id = 1;
        foreach ((string label, int count) in classes)
        {
            for (int i = 0; i < count; i++)
            {
                rows.Add(MakeRow(
                    ("id", DataValue.FromFloat32(id++)),
                    ("label", DataValue.FromString(label))));
            }
        }

        return rows.ToArray();
    }

    // ─────────────── StratifiedSampleOperator tests ───────────────

    [Fact]
    public async Task Stratified_PreservesClassDistribution()
    {
        Row[] rows = GenerateClassRows(("cat", 500), ("dog", 500));
        MockOperator source = new(rows);

        StratifiedSampleOperator op = new(source, 50.0, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        // With 50% sampling of 1000 rows, expect roughly 500 ± some variance.
        Assert.InRange(result.Count, 400, 600);

        int catCount = result.Count(r => r["label"].AsString() == "cat");
        int dogCount = result.Count(r => r["label"].AsString() == "dog");

        // Both classes should be present and roughly proportional.
        Assert.InRange(catCount, 150, 350);
        Assert.InRange(dogCount, 150, 350);
    }

    [Fact]
    public async Task Stratified_100Percent_ReturnsAll()
    {
        Row[] rows = GenerateClassRows(("cat", 10), ("dog", 10));
        MockOperator source = new(rows);

        StratifiedSampleOperator op = new(source, 100.0, ["label"], seed: 1);
        List<Row> result = await CollectAsync(op);

        Assert.Equal(20, result.Count);
    }

    [Fact]
    public async Task Stratified_0Percent_ReturnsNone()
    {
        Row[] rows = GenerateClassRows(("cat", 10), ("dog", 10));
        MockOperator source = new(rows);

        StratifiedSampleOperator op = new(source, 0.0, ["label"], seed: 1);
        List<Row> result = await CollectAsync(op);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Stratified_Repeatable_IsDeterministic()
    {
        Row[] rows = GenerateClassRows(("cat", 100), ("dog", 100));

        StratifiedSampleOperator op1 = new(new MockOperator(rows), 50.0, ["label"], seed: 42);
        StratifiedSampleOperator op2 = new(new MockOperator(rows), 50.0, ["label"], seed: 42);

        List<Row> result1 = await CollectAsync(op1);
        List<Row> result2 = await CollectAsync(op2);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i]["id"].AsFloat32(), result2[i]["id"].AsFloat32());
        }
    }

    [Fact]
    public async Task Stratified_DifferentSeeds_ProduceDifferentResults()
    {
        Row[] rows = GenerateClassRows(("cat", 200), ("dog", 200));

        StratifiedSampleOperator op1 = new(new MockOperator(rows), 50.0, ["label"], seed: 1);
        StratifiedSampleOperator op2 = new(new MockOperator(rows), 50.0, ["label"], seed: 99);

        List<Row> result1 = await CollectAsync(op1);
        List<Row> result2 = await CollectAsync(op2);

        // Different seeds should produce different row selections.
        // (It's theoretically possible for them to be identical, but astronomically unlikely with 400 rows.)
        bool anyDifference = false;
        int minCount = Math.Min(result1.Count, result2.Count);
        for (int i = 0; i < minCount; i++)
        {
            if (result1[i]["id"].AsFloat32() != result2[i]["id"].AsFloat32())
            {
                anyDifference = true;
                break;
            }
        }

        Assert.True(anyDifference || result1.Count != result2.Count);
    }

    [Fact]
    public void Stratified_DescribeForExplain_IncludesColumns()
    {
        MockOperator source = new();
        StratifiedSampleOperator op = new(source, 10.0, ["label"], seed: 42);

        OperatorPlanDescription desc = op.DescribeForExplain();

        Assert.Equal("Stratified Sample", desc.OperatorName);
        Assert.Equal("Stratified", desc.Properties!["method"]);
        Assert.Equal("10.0%", desc.Properties["percentage"]);
        Assert.Equal("label", desc.Properties["columns"]);
        Assert.Equal("42", desc.Properties["seed"]);
    }

    // ─────────────── BalancedSampleOperator tests ───────────────

    [Fact]
    public async Task Balanced_ReturnsExactCountPerClass()
    {
        Row[] rows = GenerateClassRows(("cat", 500), ("dog", 200), ("bird", 100));
        MockOperator source = new(rows);

        BalancedSampleOperator op = new(source, countPerClass: 50, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        // Exactly 50 per class = 150 total.
        Assert.Equal(150, result.Count);

        int catCount = result.Count(r => r["label"].AsString() == "cat");
        int dogCount = result.Count(r => r["label"].AsString() == "dog");
        int birdCount = result.Count(r => r["label"].AsString() == "bird");

        Assert.Equal(50, catCount);
        Assert.Equal(50, dogCount);
        Assert.Equal(50, birdCount);
    }

    [Fact]
    public async Task Balanced_SmallClass_ReturnsAll()
    {
        Row[] rows = GenerateClassRows(("cat", 100), ("dog", 5));
        MockOperator source = new(rows);

        BalancedSampleOperator op = new(source, countPerClass: 50, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        int catCount = result.Count(r => r["label"].AsString() == "cat");
        int dogCount = result.Count(r => r["label"].AsString() == "dog");

        Assert.Equal(50, catCount);
        Assert.Equal(5, dogCount); // Only 5 available — all returned.
    }

    [Fact]
    public async Task Balanced_EmptyInput_ReturnsEmpty()
    {
        MockOperator source = new();

        BalancedSampleOperator op = new(source, countPerClass: 10, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Balanced_SingleClass_ReturnsReservoir()
    {
        Row[] rows = GenerateClassRows(("cat", 100));
        MockOperator source = new(rows);

        BalancedSampleOperator op = new(source, countPerClass: 10, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        Assert.Equal(10, result.Count);
        Assert.All(result, r => Assert.Equal("cat", r["label"].AsString()));
    }

    [Fact]
    public async Task Balanced_Repeatable_IsDeterministic()
    {
        Row[] rows = GenerateClassRows(("cat", 100), ("dog", 100));

        BalancedSampleOperator op1 = new(new MockOperator(rows), countPerClass: 10, ["label"], seed: 42);
        BalancedSampleOperator op2 = new(new MockOperator(rows), countPerClass: 10, ["label"], seed: 42);

        List<Row> result1 = await CollectAsync(op1);
        List<Row> result2 = await CollectAsync(op2);

        Assert.Equal(result1.Count, result2.Count);
        for (int i = 0; i < result1.Count; i++)
        {
            Assert.Equal(result1[i]["id"].AsFloat32(), result2[i]["id"].AsFloat32());
        }
    }

    [Fact]
    public async Task Balanced_ExceedsMaxClasses_Throws()
    {
        // Create rows with many distinct classes.
        List<Row> rows = [];
        for (int i = 0; i < 20; i++)
        {
            rows.Add(MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("label", DataValue.FromString($"class_{i}"))));
        }

        MockOperator source = new(rows.ToArray());
        BalancedSampleOperator op = new(source, countPerClass: 5, ["label"], seed: 42);

        // Set a very low max to trigger the error.
        ExecutionContext context = CreateContext(maxStratifyClasses: 10);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectAsync(op, context));

        Assert.Contains("distinct classes", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public async Task Balanced_CompositeKey_StratifiesCorrectly()
    {
        Row[] rows =
        [
            .. Enumerable.Range(1, 20).Select(i => MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("label", DataValue.FromString("cat")),
                ("split", DataValue.FromString("train")))),
            .. Enumerable.Range(21, 10).Select(i => MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("label", DataValue.FromString("cat")),
                ("split", DataValue.FromString("test")))),
            .. Enumerable.Range(31, 15).Select(i => MakeRow(
                ("id", DataValue.FromFloat32(i)),
                ("label", DataValue.FromString("dog")),
                ("split", DataValue.FromString("train")))),
        ];

        MockOperator source = new(rows);
        BalancedSampleOperator op = new(source, countPerClass: 5, ["label", "split"], seed: 42);
        List<Row> result = await CollectAsync(op);

        // 3 composite classes: (cat,train), (cat,test), (dog,train)
        // Each should have exactly 5 rows (all have >= 5 rows).
        Assert.Equal(15, result.Count);

        int catTrain = result.Count(r => r["label"].AsString() == "cat" && r["split"].AsString() == "train");
        int catTest = result.Count(r => r["label"].AsString() == "cat" && r["split"].AsString() == "test");
        int dogTrain = result.Count(r => r["label"].AsString() == "dog" && r["split"].AsString() == "train");

        Assert.Equal(5, catTrain);
        Assert.Equal(5, catTest);
        Assert.Equal(5, dogTrain);
    }

    [Fact]
    public async Task Balanced_EmitsClassFirstOrder()
    {
        // cat rows first, then dog rows in the input.
        Row[] rows = GenerateClassRows(("cat", 10), ("dog", 10));
        MockOperator source = new(rows);

        BalancedSampleOperator op = new(source, countPerClass: 5, ["label"], seed: 42);
        List<Row> result = await CollectAsync(op);

        Assert.Equal(10, result.Count);

        // First 5 rows should all be "cat" (first-seen class), next 5 should be "dog".
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("cat", result[i]["label"].AsString());
        }

        for (int i = 5; i < 10; i++)
        {
            Assert.Equal("dog", result[i]["label"].AsString());
        }
    }

    [Fact]
    public void Balanced_DescribeForExplain_IncludesCountAndColumns()
    {
        MockOperator source = new();
        BalancedSampleOperator op = new(source, countPerClass: 100, ["label", "split"], seed: 7);

        OperatorPlanDescription desc = op.DescribeForExplain();

        Assert.Equal("Balanced Sample", desc.OperatorName);
        Assert.Equal("Balanced", desc.Properties!["method"]);
        Assert.Equal("100", desc.Properties["count"]);
        Assert.Equal("label, split", desc.Properties["columns"]);
        Assert.Equal("7", desc.Properties["seed"]);
        Assert.NotEmpty(desc.Warnings);
    }
}
