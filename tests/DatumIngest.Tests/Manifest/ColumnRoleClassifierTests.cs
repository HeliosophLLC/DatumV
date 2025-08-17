namespace DatumIngest.Tests.Manifest;

using DatumIngest.Manifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="ColumnRoleClassifier"/>.
/// </summary>
public sealed class ColumnRoleClassifierTests
{
    // ─────────────── Identifier ───────────────

    [Fact]
    public void Classify_HighCardinalityInteger_ReturnsIdentifier()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int64, estimatedDistinctCount: 9500, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_UniqueUuid_ReturnsIdentifier()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Uuid, estimatedDistinctCount: 10000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_HighCardinalityInteger_WithHighNulls_ReturnsForeignKey()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int64, estimatedDistinctCount: 9500, nullRatio: 0.05);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    // ─────────────── ForeignKey ───────────────

    [Fact]
    public void Classify_ModerateCardinalityInteger_ReturnsForeignKey()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 5000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    [Fact]
    public void Classify_RepeatingUuid_ReturnsForeignKey()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Uuid, estimatedDistinctCount: 500, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    // ─────────────── Categorical ───────────────

    [Fact]
    public void Classify_TrivialVocabularyInteger_ReturnsCategorical()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int8, estimatedDistinctCount: 5, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_StrongRepetitionInteger_ReturnsCategorical()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 500, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_ModerateRepetitionWithConcentratedTopK_ReturnsCategorical()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 2000, nullRatio: 0.0,
            topKValues: [new FrequencyEntry("1", 3000), new FrequencyEntry("2", 2500), new FrequencyEntry("3", 1000)]);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_Boolean_ReturnsCategorical()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Boolean, estimatedDistinctCount: 2, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_LowCardinalityString_ReturnsCategorical()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 10, nullRatio: 0.0, maxLength: 20);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    // ─────────────── Measure ───────────────

    [Fact]
    public void Classify_FractionalFloat_ReturnsMeasure()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Float64, estimatedDistinctCount: 9000, nullRatio: 0.0, integerValued: false);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Measure, role);
    }

    [Fact]
    public void Classify_Float32WithFractionalValues_ReturnsMeasure()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Float32, estimatedDistinctCount: 5000, nullRatio: 0.0, integerValued: false);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Measure, role);
    }

    // ─────────────── Float32 integer-valued reclassification ───────────────

    [Fact]
    public void Classify_IntegerValuedFloat_HighCardinality_ReturnsIdentifier()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Float32, estimatedDistinctCount: 9600, nullRatio: 0.0, integerValued: true);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_IntegerValuedFloat_ModerateCardinality_ReturnsForeignKey()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Float32, estimatedDistinctCount: 5000, nullRatio: 0.0, integerValued: true);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    [Fact]
    public void Classify_IntegerValuedFloat_TrivialVocabulary_ReturnsCategorical()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Float32, estimatedDistinctCount: 10, nullRatio: 0.0, integerValued: true);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    // ─────────────── Temporal ───────────────

    [Fact]
    public void Classify_Date_ReturnsTemporal()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Date, estimatedDistinctCount: 365, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Temporal, role);
    }

    [Fact]
    public void Classify_DateTime_ReturnsTemporal()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.DateTime, estimatedDistinctCount: 10000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Temporal, role);
    }

    [Fact]
    public void Classify_Duration_ReturnsTemporal()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Duration, estimatedDistinctCount: 100, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Temporal, role);
    }

    // ─────────────── Text ───────────────

    [Fact]
    public void Classify_HighCardinalityLongStrings_ReturnsText()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 8000, nullRatio: 0.0, maxLength: 200);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Text, role);
    }

    [Fact]
    public void Classify_HighCardinalityShortStrings_ReturnsCategorical()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 8000, nullRatio: 0.0, maxLength: 30);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    // ─────────────── Binary ───────────────

    [Fact]
    public void Classify_Image_ReturnsBinary()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Image, estimatedDistinctCount: 1000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000);

        Assert.Equal(ColumnRole.Binary, role);
    }

    [Fact]
    public void Classify_UInt8Array_ReturnsBinary()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.UInt8Array, estimatedDistinctCount: 1000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000);

        Assert.Equal(ColumnRole.Binary, role);
    }

    // ─────────────── Structural ───────────────

    [Fact]
    public void Classify_Vector_ReturnsStructural()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Vector, estimatedDistinctCount: 1000, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000);

        Assert.Equal(ColumnRole.Structural, role);
    }

    [Fact]
    public void Classify_Tensor_ReturnsStructural()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.Tensor, estimatedDistinctCount: 100, nullRatio: 0.0);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000);

        Assert.Equal(ColumnRole.Structural, role);
    }

    // ─────────────── ManifestBuilder integration ───────────────

    [Fact]
    public void ManifestBuilder_SetsRoleOnFeatures()
    {
        DatumIngest.Statistics.StatisticsCollector collector = new();

        string[] names = ["id", "category", "score", "name"];
        for (int index = 0; index < 100; index++)
        {
            DataValue[] values =
            [
                DataValue.FromInt64(index),
                DataValue.FromInt8((sbyte)(index % 3)),
                DataValue.FromFloat64(index * 1.5 + 0.1),
                DataValue.FromString($"item_{index}")
            ];
            collector.AddRow(new DatumIngest.Model.Row(names, values));
        }

        IReadOnlyDictionary<string, DatumIngest.Statistics.ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["id"] = DataKind.Int64,
            ["category"] = DataKind.Int8,
            ["score"] = DataKind.Float64,
            ["name"] = DataKind.String
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        FeatureManifest idFeature = manifest.Features.First(feature => feature.Name == "id");
        FeatureManifest categoryFeature = manifest.Features.First(feature => feature.Name == "category");
        FeatureManifest scoreFeature = manifest.Features.First(feature => feature.Name == "score");
        FeatureManifest nameFeature = manifest.Features.First(feature => feature.Name == "name");

        Assert.Equal(ColumnRole.Identifier, idFeature.Role);
        Assert.Equal(ColumnRole.Categorical, categoryFeature.Role);
        Assert.Equal(ColumnRole.Measure, scoreFeature.Role);
        Assert.NotNull(nameFeature.Role);
    }

    [Fact]
    public void ManifestBuilder_BooleanColumn_CreatesBooleanManifest()
    {
        DatumIngest.Statistics.StatisticsCollector collector = new();

        string[] names = ["flag"];
        for (int index = 0; index < 100; index++)
        {
            DataValue[] values = [DataValue.FromBoolean(index % 3 != 0)];
            collector.AddRow(new DatumIngest.Model.Row(names, values));
        }

        IReadOnlyDictionary<string, DatumIngest.Statistics.ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 100);

        FeatureManifest flagFeature = manifest.Features.First(feature => feature.Name == "flag");
        BooleanFeatureManifest booleanManifest = Assert.IsType<BooleanFeatureManifest>(flagFeature);
        Assert.Equal(ColumnRole.Categorical, booleanManifest.Role);
        Assert.True(booleanManifest.TrueRatio > 0.5, "Expected ~66% true ratio");
        Assert.True(booleanManifest.TrueRatio < 0.8, "Expected ~66% true ratio");
    }

    // ─────────────── Helpers ───────────────

    /// <summary>
    /// Creates a <see cref="NumericFeatureManifest"/> with controllable statistics
    /// for classifier testing.
    /// </summary>
    private static NumericFeatureManifest MakeNumericManifest(
        DataKind kind,
        long estimatedDistinctCount,
        double nullRatio,
        bool integerValued = true,
        IReadOnlyList<FrequencyEntry>? topKValues = null)
    {
        return new NumericFeatureManifest
        {
            Name = "test_column",
            Kind = kind,
            Count = 10000,
            NullCount = (long)(10000 * nullRatio),
            ValidCount = 10000 - (long)(10000 * nullRatio),
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topKValues ?? [],
            NullRatio = nullRatio,
            DominantValueRatio = null,
            Min = 0.0,
            Max = 10000.0,
            Mean = 5000.0,
            Variance = 1000.0,
            StandardDeviation = 31.62,
            Skewness = 0.0,
            Kurtosis = 3.0,
            Histogram = new HistogramData([0.0, 5000.0, 10000.0], [5000, 5000]),
            ZeroCount = 0,
            ZeroRatio = 0.0,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = integerValued
        };
    }

    /// <summary>
    /// Creates a <see cref="StringFeatureManifest"/> with controllable statistics
    /// for classifier testing.
    /// </summary>
    private static StringFeatureManifest MakeStringManifest(
        long estimatedDistinctCount,
        double nullRatio,
        int maxLength)
    {
        return new StringFeatureManifest
        {
            Name = "test_column",
            Kind = DataKind.String,
            Count = 10000,
            NullCount = (long)(10000 * nullRatio),
            ValidCount = 10000 - (long)(10000 * nullRatio),
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
            NullRatio = nullRatio,
            DominantValueRatio = null,
            MinLength = 1,
            MaxLength = maxLength
        };
    }

    /// <summary>
    /// Creates a minimal <see cref="FeatureManifest"/> subclass for non-numeric, non-string kinds.
    /// Uses <see cref="TemporalFeatureManifest"/> as a lightweight carrier because its
    /// required properties are nullable. The classifier dispatches on <see cref="FeatureManifest.Kind"/>,
    /// not on the concrete manifest subtype, so this is safe for all fallback kinds.
    /// </summary>
    private static FeatureManifest MakeFallbackManifest(
        DataKind kind,
        long estimatedDistinctCount,
        double nullRatio)
    {
        return new TemporalFeatureManifest
        {
            Name = "test_column",
            Kind = kind,
            Count = 10000,
            NullCount = (long)(10000 * nullRatio),
            ValidCount = 10000 - (long)(10000 * nullRatio),
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
            NullRatio = nullRatio,
            Earliest = null,
            Latest = null
        };
    }
}
