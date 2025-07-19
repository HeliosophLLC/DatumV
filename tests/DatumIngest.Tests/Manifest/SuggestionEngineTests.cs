namespace DatumIngest.Tests.Manifest;

using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Statistics;

/// <summary>
/// Tests for <see cref="SuggestionEngine"/>.
/// </summary>
public sealed class SuggestionEngineTests
{
    [Fact]
    public void Suggest_NumericHighZeroRatio_ReturnsZeroInflated()
    {
        NumericFeatureManifest feature = MakeNumericManifest(zeroRatio: 0.6, integerValued: false);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("zero-inflated", suggestions);
    }

    [Fact]
    public void Suggest_NumericZeroRatioBelowThreshold_NoZeroInflated()
    {
        NumericFeatureManifest feature = MakeNumericManifest(zeroRatio: 0.4, integerValued: false);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        if (suggestions is not null)
        {
            Assert.DoesNotContain("zero-inflated", suggestions);
        }
    }

    [Fact]
    public void Suggest_IntegerLowCardinality_ReturnsPossibleOrdinal()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            integerValued: true, estimatedDistinctCount: 16);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("possible-ordinal", suggestions);
    }

    [Fact]
    public void Suggest_IntegerHighCardinality_NoPossibleOrdinal()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            integerValued: true, estimatedDistinctCount: 500);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        if (suggestions is not null)
        {
            Assert.DoesNotContain("possible-ordinal", suggestions);
        }
    }

    [Fact]
    public void Suggest_NonIntegerLowCardinality_NoPossibleOrdinal()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            integerValued: false, estimatedDistinctCount: 10);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        if (suggestions is not null)
        {
            Assert.DoesNotContain("possible-ordinal", suggestions);
        }
    }

    [Fact]
    public void Suggest_HighDistinctLowTopKCoverage_ReturnsPossibleIdentifier()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            estimatedDistinctCount: 950,
            topKValues: [new FrequencyEntry("1", 2), new FrequencyEntry("2", 2)]);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("possible-identifier", suggestions);
    }

    [Fact]
    public void Suggest_HighDistinctHighTopKCoverage_NoPossibleIdentifier()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            estimatedDistinctCount: 950,
            topKValues: [new FrequencyEntry("X", 800)]);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        if (suggestions is not null)
        {
            Assert.DoesNotContain("possible-identifier", suggestions);
        }
    }

    [Fact]
    public void Suggest_RightSkewed_ReturnsRightSkewed()
    {
        NumericFeatureManifest feature = MakeNumericManifest(skewness: 3.5);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("right-skewed", suggestions);
    }

    [Fact]
    public void Suggest_LeftSkewed_ReturnsLeftSkewed()
    {
        NumericFeatureManifest feature = MakeNumericManifest(skewness: -2.5);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("left-skewed", suggestions);
    }

    [Fact]
    public void Suggest_HighKurtosis_ReturnsHeavyTailed()
    {
        NumericFeatureManifest feature = MakeNumericManifest(kurtosis: 10.0);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("heavy-tailed", suggestions);
    }

    [Fact]
    public void Suggest_HighMissingness_ReturnsHighMissingness()
    {
        NumericFeatureManifest feature = MakeNumericManifest(nullRatio: 0.5);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("high-missingness", suggestions);
    }

    [Fact]
    public void Suggest_LowDistinctCount_ReturnsLowCardinality()
    {
        NumericFeatureManifest feature = MakeNumericManifest(estimatedDistinctCount: 5);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("low-cardinality", suggestions);
    }

    [Fact]
    public void Suggest_HighDistinctRatio_ReturnsHighCardinality()
    {
        NumericFeatureManifest feature = MakeNumericManifest(estimatedDistinctCount: 600);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("high-cardinality", suggestions);
    }

    [Fact]
    public void Suggest_StringColumn_UniversalSuggestionsApply()
    {
        StringFeatureManifest feature = new()
        {
            Name = "notes",
            Kind = DataKind.String,
            Count = 100,
            NullCount = 400,
            ValidCount = 100,
            NullRatio = 0.8,
            EstimatedDistinctCount = 80,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 200
        };

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 500, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("high-missingness", suggestions);
    }

    [Fact]
    public void Suggest_StringPossibleIdentifier_Works()
    {
        StringFeatureManifest feature = new()
        {
            Name = "id",
            Kind = DataKind.String,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            EstimatedDistinctCount = 980,
            TopKValues = [new FrequencyEntry("abc", 2)],
            MinLength = 10,
            MaxLength = 10
        };

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("possible-identifier", suggestions);
        Assert.Contains("high-cardinality", suggestions);
    }

    [Fact]
    public void Suggest_NoSuggestionsApply_ReturnsNull()
    {
        NumericFeatureManifest feature = MakeNumericManifest(
            estimatedDistinctCount: 100,
            zeroRatio: 0.1,
            skewness: 0.5,
            kurtosis: 3.0,
            nullRatio: 0.01);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, SuggestionThresholds.Default);

        Assert.Null(suggestions);
    }

    [Fact]
    public void Suggest_CustomThresholds_Respected()
    {
        SuggestionThresholds strict = new()
        {
            ZeroInflatedMinRatio = 0.9,
            PossibleOrdinalMaxDistinct = 5
        };

        // 60% zeros — inflated by default threshold but not by strict.
        NumericFeatureManifest feature = MakeNumericManifest(
            zeroRatio: 0.6, integerValued: true, estimatedDistinctCount: 10);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 1000, strict);

        // zero-inflated should NOT fire (0.6 < 0.9 strict threshold).
        if (suggestions is not null)
        {
            Assert.DoesNotContain("zero-inflated", suggestions);
        }

        // possible-ordinal should NOT fire (10 > 5 strict threshold).
        if (suggestions is not null)
        {
            Assert.DoesNotContain("possible-ordinal", suggestions);
        }
    }

    [Fact]
    public void Suggest_MultipleSuggestionsCanCoexist()
    {
        // Capital-gains-like column: 92% zeros, integer-valued, right-skewed, heavy-tailed,
        // low distinct count (ordinal-ish).
        NumericFeatureManifest feature = MakeNumericManifest(
            zeroRatio: 0.92,
            integerValued: true,
            estimatedDistinctCount: 15,
            skewness: 5.0,
            kurtosis: 30.0);

        IReadOnlyList<string>? suggestions = SuggestionEngine.Suggest(
            feature, rowCount: 32000, SuggestionThresholds.Default);

        Assert.NotNull(suggestions);
        Assert.Contains("zero-inflated", suggestions);
        Assert.Contains("possible-ordinal", suggestions);
        Assert.Contains("right-skewed", suggestions);
        Assert.Contains("heavy-tailed", suggestions);
        Assert.Contains("low-cardinality", suggestions);
    }

    [Fact]
    public void ManifestBuilder_WithSuggestionThresholds_PopulatesSuggestions()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            // 92 zeros + 8 non-zero integers — should trigger zero-inflated, possible-ordinal.
            float value = i < 92 ? 0.0f : (float)(i - 91);
            collector.AddRow(MakeRow("capital_gains", DataValue.FromScalar(value)));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["capital_gains"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, suggestionThresholds: SuggestionThresholds.Default);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.True(feature.IntegerValued);
        Assert.NotNull(feature.Suggestions);
        Assert.Contains("zero-inflated", feature.Suggestions);
    }

    [Fact]
    public void ManifestBuilder_WithoutSuggestionThresholds_NoSuggestions()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("value", DataValue.FromScalar(1.0f)));
        collector.AddRow(MakeRow("value", DataValue.FromScalar(2.0f)));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);

        NumericFeatureManifest feature = Assert.IsType<NumericFeatureManifest>(manifest.Features[0]);
        Assert.Null(feature.Suggestions);
    }

    private static NumericFeatureManifest MakeNumericManifest(
        double zeroRatio = 0.0,
        bool integerValued = false,
        long estimatedDistinctCount = 100,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double? nullRatio = null,
        IReadOnlyList<FrequencyEntry>? topKValues = null)
    {
        return new NumericFeatureManifest
        {
            Name = "test",
            Kind = DataKind.Scalar,
            Count = 1000,
            NullCount = nullRatio.HasValue ? (long)(nullRatio.Value * 1000) : 0,
            ValidCount = 1000,
            NullRatio = nullRatio,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topKValues ?? [],
            Min = 0.0,
            Max = 100.0,
            Mean = 50.0,
            Variance = 25.0,
            StandardDeviation = 5.0,
            Skewness = skewness,
            Kurtosis = kurtosis,
            Histogram = new HistogramData([], []),
            ZeroCount = (long)(zeroRatio * 1000),
            ZeroRatio = zeroRatio,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = integerValued
        };
    }

    private static Row MakeRow(string columnName, DataValue value)
    {
        return new Row([columnName], [value]);
    }
}
