namespace DatumIngest.Tests.Statistics.Interactions;

using DatumIngest.Model;
using DatumIngest.Statistics.Interactions;

public sealed class ColumnInteractionCollectorTests : ServiceTestBase
{
    [Fact]
    public void GetInteractions_NumericPair_ProducesPearsonSpearmanMI()
    {
        ColumnLookup columnLookup = new(["x", "y"]);
        ColumnInteractionCollector collector = new();
        Row row = MakeRow(columnLookup, DataValue.FromFloat32(1.0f), DataValue.FromFloat32(2.0f));

        for (int i = 0; i < 100; i++)
        {
            row = MakeRow(columnLookup, DataValue.FromFloat32(i), DataValue.FromFloat32(i * 2.0f));
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
        Assert.Null(result.MissingnessCorrelation); // No nulls → zero-variance mask → NaN → null
        Assert.Null(result.CramerV);
        Assert.Null(result.AnovaFStatistic);
    }

    [Fact]
    public void GetInteractions_CategoricalPair_ProducesCramerVAndMI()
    {
        ColumnLookup columnLookup = new(["category", "color"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            Row row = MakeRow(
                columnLookup,
                DataValue.FromString((i % 3).ToString()),
                DataValue.FromString((i % 4).ToString())
            );
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        Assert.NotNull(results[0].CramerV);
        Assert.NotNull(results[0].MutualInformation);
        Assert.NotNull(results[0].TheilUAB);
        Assert.NotNull(results[0].TheilUBA);
        Assert.Null(results[0].MissingnessCorrelation); // No nulls → zero-variance mask → NaN → null
        Assert.Null(results[0].Pearson);
        Assert.Null(results[0].Spearman);
        Assert.Null(results[0].AnovaFStatistic);
    }

    [Fact]
    public void GetInteractions_MixedPair_ProducesAnovaAndMI()
    {
        ColumnLookup columnLookup = new(["group", "score"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            Row row = MakeRow(
                columnLookup,
                DataValue.FromString((i % 5).ToString()),
                DataValue.FromFloat32(i * 1.5f));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        Assert.NotNull(results[0].AnovaFStatistic);
        Assert.NotNull(results[0].MutualInformation);
        Assert.NotNull(results[0].TheilUAB);
        Assert.NotNull(results[0].TheilUBA);
        Assert.Null(results[0].MissingnessCorrelation); // No nulls → zero-variance mask → NaN → null
        Assert.Null(results[0].Pearson);
        Assert.Null(results[0].Spearman);
        Assert.Null(results[0].CramerV);
    }

    [Fact]
    public void GetInteractions_ThreeColumns_ProducesThreePairs()
    {
        ColumnLookup columnLookup = new(["a", "b", "c"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 50; i++)
        {
            Row row = MakeRow(
                columnLookup,
                DataValue.FromFloat32(i),
                DataValue.FromFloat32(i * 2.0f),
                DataValue.FromFloat32(i * 3.0f));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.ColumnA == "a" && r.ColumnB == "b");
        Assert.Contains(results, r => r.ColumnA == "a" && r.ColumnB == "c");
        Assert.Contains(results, r => r.ColumnA == "b" && r.ColumnB == "c");
    }

    [Fact]
    public void GetInteractions_SingleColumnOnly_ReturnsEmpty()
    {
        ColumnLookup columnLookup = new(["x"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            Row row = MakeRow(columnLookup, DataValue.FromUInt16(10));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Empty(results);
    }

    [Fact]
    public void GetInteractions_SingleColumn_ReturnsEmpty()
    {
        ColumnLookup columnLookup = new(["x"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            Row row = MakeRow(columnLookup, DataValue.FromFloat32(i));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Empty(results);
    }

    [Fact]
    public void GetInteractions_IneligiblePairOnly_ProducesMissingnessOnly()
    {
        using Arena arena = new();

        ColumnLookup columnLookup = new(["image", "vector"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 50; i++)
        {
            if (i % 4 == 0)
            {
                collector.AddRow(MakeRow(
                    columnLookup,
                    DataValue.Null(DataKind.Image),
                    DataValue.Null(DataKind.Vector)));
            }
            else
            {
                collector.AddRow(MakeRow(
                    columnLookup,
                    DataValue.FromImage([0xFF, 0xD8, 0xFF], arena),
                    DataValue.FromVector([1.0f, 2.0f], arena)));
            }
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        Assert.Single(results);
        ColumnInteractionResult result = results[0];
        Assert.NotNull(result.MissingnessCorrelation);
        Assert.Null(result.Pearson);
        Assert.Null(result.Spearman);
        Assert.Null(result.CramerV);
        Assert.Null(result.AnovaFStatistic);
        Assert.Null(result.MutualInformation);
    }

    [Fact]
    public void GetInteractions_MixedEligibility_ExpandedPairCount()
    {
        using Arena arena = new();

        ColumnLookup columnLookup = new(["score", "label", "image"]);
        ColumnInteractionCollector collector = new();

        for (int i = 0; i < 50; i++)
        {
            Row row = MakeRow(
                columnLookup,
                DataValue.FromFloat32(i),
                DataValue.FromString("cat"),
                DataValue.FromImage([0xFF], arena));
            collector.AddRow(row);
        }

        IReadOnlyList<ColumnInteractionResult> results = collector.GetInteractions();

        // C(3,2) = 3 pairs: score×label (mixed), score×image (missingness-only), label×image (missingness-only)
        Assert.Equal(3, results.Count);
    }

}
