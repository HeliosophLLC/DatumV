namespace Axon.QueryEngine.Tests.Statistics.Interactions;

using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics.Interactions;

public sealed class ColumnInteractionCollectorTests
{
    [Fact]
    public void GetInteractions_NumericPair_ProducesPearsonSpearmanMI()
    {
        ColumnInteractionCollector collector = new();
        Row row = CreateRow(("x", DataValue.FromScalar(1.0f)), ("y", DataValue.FromScalar(2.0f)));

        for (int i = 0; i < 100; i++)
        {
            row = CreateRow(("x", DataValue.FromScalar(i)), ("y", DataValue.FromScalar(i * 2.0f)));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        ColumnInteractionResult result = results[0];
        Assert.Equal("x", result.ColumnA);
        Assert.Equal("y", result.ColumnB);
        Assert.NotNull(result.Pearson);
        Assert.NotNull(result.Spearman);
        Assert.NotNull(result.MutualInformation);
        Assert.NotNull(result.TheilUAB);
        Assert.NotNull(result.TheilUBA);
        Assert.Null(result.CramerV);
        Assert.Null(result.AnovaFStatistic);
    }

    [Fact]
    public void GetInteractions_CategoricalPair_ProducesCramerVAndMI()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            Row row = CreateRow(
                ("category", DataValue.FromString((i % 3).ToString())),
                ("color", DataValue.FromString((i % 4).ToString())));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        Assert.NotNull(results[0].CramerV);
        Assert.NotNull(results[0].MutualInformation);
        Assert.NotNull(results[0].TheilUAB);
        Assert.NotNull(results[0].TheilUBA);
        Assert.Null(results[0].Pearson);
        Assert.Null(results[0].Spearman);
        Assert.Null(results[0].AnovaFStatistic);
    }

    [Fact]
    public void GetInteractions_MixedPair_ProducesAnovaAndMI()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            Row row = CreateRow(
                ("group", DataValue.FromString((i % 5).ToString())),
                ("score", DataValue.FromScalar(i * 1.5f)));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        Assert.NotNull(results[0].AnovaFStatistic);
        Assert.NotNull(results[0].MutualInformation);
        Assert.NotNull(results[0].TheilUAB);
        Assert.NotNull(results[0].TheilUBA);
        Assert.Null(results[0].Pearson);
        Assert.Null(results[0].Spearman);
        Assert.Null(results[0].CramerV);
    }

    [Fact]
    public void GetInteractions_ImageColumnExcluded()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 50; i++)
        {
            Row row = CreateRow(
                ("label", DataValue.FromString("cat")),
                ("image", DataValue.FromImage(new byte[] { 0xFF, 0xD8, 0xFF })));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Empty(results);
    }

    [Fact]
    public void GetInteractions_ThreeColumns_ProducesThreePairs()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 50; i++)
        {
            Row row = CreateRow(
                ("a", DataValue.FromScalar(i)),
                ("b", DataValue.FromScalar(i * 2.0f)),
                ("c", DataValue.FromScalar(i * 3.0f)));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.ColumnA == "a" && r.ColumnB == "b");
        Assert.Contains(results, r => r.ColumnA == "a" && r.ColumnB == "c");
        Assert.Contains(results, r => r.ColumnA == "b" && r.ColumnB == "c");
    }

    [Fact]
    public void GetInteractions_NoEligibleColumns_ReturnsEmpty()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            Row row = CreateRow(
                ("blob", DataValue.FromUInt8Array(new byte[] { 1, 2, 3 })));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Empty(results);
    }

    [Fact]
    public void GetInteractions_SingleEligibleColumn_ReturnsEmpty()
    {
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            Row row = CreateRow(("x", DataValue.FromScalar(i)));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Empty(results);
    }

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }
}
