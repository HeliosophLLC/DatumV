namespace DatumIngest.Tests.Manifest;

using DatumIngest.Manifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="ColumnRoleClassifier"/>.
/// </summary>
public sealed class ColumnRoleClassifierTests : ServiceTestBase
{
    private readonly Arena _arena = new();

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }
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
            DataKind.Int32, estimatedDistinctCount: 500, nullRatio: 0.0,
            topKValues: [new FrequencyEntry("1", 600), new FrequencyEntry("2", 500), new FrequencyEntry("3", 200)]);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 2000);

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

    [Fact]
    public void Classify_SmallContiguousRange_ReturnsCategorical()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 7, nullRatio: 0.0,
            min: 0, max: 6);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 100000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    // ─────────────── ForeignKey (NDV floor) ───────────────

    [Fact]
    public void Classify_HighAbsoluteNdvInteger_ReturnsForeignKey()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 5000, nullRatio: 0.0,
            min: 1, max: 206209);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    // ─────────────── Measure (contiguous range) ───────────────

    [Fact]
    public void Classify_ContiguousIntegerRange_ReturnsMeasure()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int64, estimatedDistinctCount: 31, nullRatio: 0.06,
            min: 0, max: 30);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 3421083);

        Assert.Equal(ColumnRole.Measure, role);
    }

    [Fact]
    public void Classify_LargeContiguousRange_ReturnsForeignKey()
    {
        NumericFeatureManifest manifest = MakeNumericManifest(
            DataKind.Int32, estimatedDistinctCount: 10000, nullRatio: 0.0,
            min: 1, max: 10000);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 1000000);

        Assert.Equal(ColumnRole.ForeignKey, role);
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

    // ─────────────── String Identifier (CharacterClass) ───────────────

    [Fact]
    public void Classify_FixedLengthHexHighCardinality_ReturnsIdentifier()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 9500, nullRatio: 0.0, minLength: 32, maxLength: 32,
            characterClass: CharacterClass.Hexadecimal);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_FixedLengthBase64HighCardinality_ReturnsIdentifier()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 9500, nullRatio: 0.0, minLength: 24, maxLength: 24,
            characterClass: CharacterClass.Base64);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_FixedLengthAlphanumericHighCardinality_ReturnsIdentifier()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 9500, nullRatio: 0.0, minLength: 20, maxLength: 20,
            characterClass: CharacterClass.Alphanumeric);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Identifier, role);
    }

    [Fact]
    public void Classify_FixedLengthHexLowCardinality_ReturnsForeignKey()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 500, nullRatio: 0.0, minLength: 32, maxLength: 32,
            characterClass: CharacterClass.Hexadecimal);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.ForeignKey, role);
    }

    [Fact]
    public void Classify_VariableLengthHexHighCardinality_ReturnsCategorical()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 9500, nullRatio: 0.0, minLength: 16, maxLength: 32,
            characterClass: CharacterClass.Hexadecimal);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_FixedLengthMixedHighCardinality_ReturnsCategorical()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 9500, nullRatio: 0.0, minLength: 32, maxLength: 32,
            characterClass: CharacterClass.Mixed);

        ColumnRole role = ColumnRoleClassifier.Classify(manifest, rowCount: 10000);

        Assert.Equal(ColumnRole.Categorical, role);
    }

    [Fact]
    public void Classify_ShortFixedLengthAlphanumeric_ReturnsCategorical()
    {
        StringFeatureManifest manifest = MakeStringManifest(
            estimatedDistinctCount: 27, nullRatio: 0.0, minLength: 2, maxLength: 2,
            characterClass: CharacterClass.Alphanumeric);

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
    public void Classify_ByteArray_ReturnsBinary()
    {
        FeatureManifest manifest = MakeFallbackManifest(
            DataKind.UInt8, estimatedDistinctCount: 1000, nullRatio: 0.0, isArray: true);

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

    // ─────────────── ManifestBuilder integration ───────────────

    [Fact]
    public void ManifestBuilder_SetsRoleOnFeatures()
    {
        DatumIngest.Statistics.StatisticsCollector collector = new();

        ColumnLookup columnLookup = new (["id", "category", "score", "name"]);

        for (int index = 0; index < 100; index++)
        {
            DataValue[] values =
            [
                DataValue.FromInt64(index),
                DataValue.FromInt8((sbyte)(index % 3)),
                DataValue.FromFloat64(index * 1.5 + 0.1),
                DataValue.FromString($"item_{index}", _arena)
            ];
            collector.AddRow(new DatumIngest.Model.Row(columnLookup, values), _arena);
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

        ColumnLookup columnLookup = new (["flag"]);
        for (int index = 0; index < 100; index++)
        {
            DataValue[] values = [DataValue.FromBoolean(index % 3 != 0)];
            collector.AddRow(new Row(columnLookup, values), _arena);
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
        IReadOnlyList<FrequencyEntry>? topKValues = null,
        double min = 0.0,
        double max = 10000.0)
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
            Min = min,
            Max = max,
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
        int maxLength,
        int minLength = 1,
        CharacterClass characterClass = CharacterClass.Mixed)
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
            MinLength = minLength,
            MaxLength = maxLength,
            CharacterClass = characterClass
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
        double nullRatio,
        bool isArray = false)
    {
        return new TemporalFeatureManifest
        {
            Name = "test_column",
            Kind = kind,
            IsArray = isArray,
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
