namespace Heliosoph.DatumV.Tests.Manifest.Insights;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Manifest.Insights;
using Heliosoph.DatumV.Model;

/// <summary>
/// Integration tests for <see cref="InsightAnalyzer"/> — verifies that the full pipeline
/// (rules → clustering → action routing → sorting) works end-to-end with realistic manifests.
/// </summary>
public sealed class InsightAnalyzerTests : ServiceTestBase
{
    [Fact]
    public void Analyze_ConstantColumn_ProducesDropInsight()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("constant_col", variance: 0.0, isConstant: true));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        DatasetInsight? constantInsight = insights.FirstOrDefault(
            i => i.Kind == InsightKind.ConstantFeature);

        Assert.NotNull(constantInsight);
        Assert.Equal(ApplyMode.AutoSafe, constantInsight.RecommendedApplyMode);
        Assert.Single(constantInsight.Actions);
        Assert.Equal(ActionKind.Drop, constantInsight.Actions[0].Kind);
        Assert.Contains("constant_col", constantInsight.AffectedFeatures);
    }

    [Fact]
    public void Analyze_HighMissingness_ProducesWarning()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("sparse_col", nullRatio: 0.5, nullCount: 500));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        DatasetInsight? missingnessInsight = insights.FirstOrDefault(
            i => i.Kind == InsightKind.HighMissingness);

        Assert.NotNull(missingnessInsight);
        Assert.Equal(InsightSeverity.Warning, missingnessInsight.Severity);
    }

    [Fact]
    public void Analyze_CriticalMissingness_ProducesCritical()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("almost_empty", nullRatio: 0.9, nullCount: 900));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        DatasetInsight? criticalInsight = insights.FirstOrDefault(
            i => i.Kind == InsightKind.CriticalMissingness);

        Assert.NotNull(criticalInsight);
        Assert.Equal(InsightSeverity.Critical, criticalInsight.Severity);
    }

    [Fact]
    public void Analyze_RightSkewedColumn_DetectsSkewness()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("income", skewness: 3.5, min: 0.1));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        Assert.Contains(insights, i => i.Kind == InsightKind.RightSkewed);
    }

    [Fact]
    public void Analyze_NormalColumn_ProducesNoInsights()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("normal_col",
                mean: 50.0, variance: 25.0, stdDev: 5.0,
                skewness: 0.1, kurtosis: 3.0,
                min: 30.0, max: 70.0,
                nullRatio: 0.01, zeroRatio: 0.01,
                estimatedDistinctCount: 100));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        Assert.Empty(insights);
    }

    [Fact]
    public void Analyze_EmptyManifest_ProducesNoInsights()
    {
        QueryResultsManifest manifest = new()
        {
            RowCount = 0,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = []
        };

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        Assert.Empty(insights);
    }

    [Fact]
    public void Analyze_CustomThresholds_Respected()
    {
        InsightThresholds strict = new() { HighMissingnessMinRatio = 0.9 };

        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("col", nullRatio: 0.5, nullCount: 500));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest, strict);

        // 0.5 < 0.9 strict threshold — should not fire.
        Assert.DoesNotContain(insights, i => i.Kind == InsightKind.HighMissingness);
    }

    [Fact]
    public void Analyze_ResultsSortedBySeverityThenConfidence()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("critical_col", nullRatio: 0.9, nullCount: 900, isConstant: true),
            MakeNumericFeature("warning_col", skewness: 4.0, min: 0.1));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        Assert.True(insights.Count >= 2);

        // Critical insights before warnings.
        int firstCriticalIndex = -1;
        int firstWarningIndex = -1;

        for (int i = 0; i < insights.Count; i++)
        {
            if (insights[i].Severity == InsightSeverity.Critical && firstCriticalIndex == -1)
            {
                firstCriticalIndex = i;
            }

            if (insights[i].Severity == InsightSeverity.Warning && firstWarningIndex == -1)
            {
                firstWarningIndex = i;
            }
        }

        if (firstCriticalIndex >= 0 && firstWarningIndex >= 0)
        {
            Assert.True(firstCriticalIndex < firstWarningIndex);
        }
    }

    [Fact]
    public void Analyze_ZeroInflatedAndSkewed_DetectsSyndrome()
    {
        QueryResultsManifest manifest = MakeManifest(
            MakeNumericFeature("capital_gains",
                zeroRatio: 0.92, zeroCount: 920,
                skewness: 5.0, min: 0.0, max: 100000.0,
                mean: 1000.0, variance: 50000000.0, stdDev: 7071.0));

        IReadOnlyList<DatasetInsight> insights = InsightAnalyzer.Analyze(manifest);

        // Should detect the syndrome since both components fire on the same column.
        DatasetInsight? syndrome = insights.FirstOrDefault(
            i => i.Kind == InsightKind.ZeroInflatedSkewedNumeric);

        Assert.NotNull(syndrome);
    }

    // ── Helpers ──

    private static QueryResultsManifest MakeManifest(params FeatureManifest[] features)
    {
        return new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features
        };
    }

    private static NumericFeatureManifest MakeNumericFeature(
        string name,
        double mean = 50.0,
        double variance = 25.0,
        double stdDev = 5.0,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double min = 0.0,
        double max = 100.0,
        double nullRatio = 0.0,
        long nullCount = 0,
        double zeroRatio = 0.0,
        long zeroCount = 0,
        long estimatedDistinctCount = 100,
        bool isConstant = false,
        bool integerValued = false)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000 - nullCount,
            NullCount = nullCount,
            ValidCount = 1000 - nullCount,
            NullRatio = nullRatio,
            EstimatedDistinctCount = isConstant ? 1 : estimatedDistinctCount,
            TopKValues = isConstant ? [new FrequencyEntry("42", 1000 - nullCount)] : [],
            Min = min,
            Max = max,
            Mean = mean,
            Variance = isConstant ? 0.0 : variance,
            StandardDeviation = isConstant ? 0.0 : stdDev,
            Skewness = skewness,
            Kurtosis = kurtosis,
            Histogram = new HistogramData([], []),
            ZeroCount = zeroCount,
            ZeroRatio = zeroRatio,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = integerValued
        };
    }
}
