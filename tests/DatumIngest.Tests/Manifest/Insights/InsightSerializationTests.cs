namespace DatumIngest.Tests.Manifest.Insights;

using DatumIngest.Manifest;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;

/// <summary>
/// Tests that <see cref="DatasetInsight"/> and related types survive JSON round-trip
/// via <see cref="ManifestSerializer"/>.
/// </summary>
public sealed class InsightSerializationTests
{
    [Fact]
    public void RoundTrip_ManifestWithInsights_PreservesInsights()
    {
        QueryResultsManifest original = MakeManifestWithInsights();

        string json = ManifestSerializer.Serialize(original);
        QueryResultsManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Insights);
        Assert.Equal(original.Insights!.Count, deserialized.Insights.Count);

        DatasetInsight originalInsight = original.Insights[0];
        DatasetInsight roundTripped = deserialized.Insights[0];

        Assert.Equal(originalInsight.Kind, roundTripped.Kind);
        Assert.Equal(originalInsight.Category, roundTripped.Category);
        Assert.Equal(originalInsight.Severity, roundTripped.Severity);
        Assert.Equal(originalInsight.Confidence, roundTripped.Confidence);
        Assert.Equal(originalInsight.Scope, roundTripped.Scope);
        Assert.Equal(originalInsight.Observation, roundTripped.Observation);
        Assert.Equal(originalInsight.Risk, roundTripped.Risk);
        Assert.Equal(originalInsight.Recommendation, roundTripped.Recommendation);
        Assert.Equal(originalInsight.RecommendedApplyMode, roundTripped.RecommendedApplyMode);
    }

    [Fact]
    public void RoundTrip_InsightActions_PreservesAllFields()
    {
        QueryResultsManifest original = MakeManifestWithInsights();

        string json = ManifestSerializer.Serialize(original);
        QueryResultsManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.NotNull(deserialized?.Insights);
        InsightAction originalAction = original.Insights![0].Actions[0];
        InsightAction roundTripped = deserialized.Insights[0].Actions[0];

        Assert.Equal(originalAction.Kind, roundTripped.Kind);
        Assert.Equal(originalAction.Column, roundTripped.Column);
        Assert.Equal(originalAction.Expression, roundTripped.Expression);
        Assert.Equal(originalAction.Lossy, roundTripped.Lossy);
        Assert.Equal(originalAction.Reversible, roundTripped.Reversible);
        Assert.Equal(originalAction.BundleIdentifier, roundTripped.BundleIdentifier);
    }

    [Fact]
    public void RoundTrip_QueryAnnotations_PreservesFields()
    {
        QueryResultsManifest original = MakeManifestWithInsights();

        string json = ManifestSerializer.Serialize(original);
        QueryResultsManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.NotNull(deserialized?.QueryAnnotations);
        Assert.Equal(original.QueryAnnotations!.Count, deserialized.QueryAnnotations.Count);

        QueryAnnotation originalAnnotation = original.QueryAnnotations[0];
        QueryAnnotation roundTripped = deserialized.QueryAnnotations[0];

        Assert.Equal(originalAnnotation.Column, roundTripped.Column);
        Assert.Equal(originalAnnotation.InsightKind, roundTripped.InsightKind);
        Assert.Equal(originalAnnotation.Note, roundTripped.Note);
        Assert.Equal(originalAnnotation.Confidence, roundTripped.Confidence);
    }

    [Fact]
    public void RoundTrip_RecommendedQuery_Preserved()
    {
        QueryResultsManifest original = MakeManifestWithInsights();

        string json = ManifestSerializer.Serialize(original);
        QueryResultsManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.Equal(original.RecommendedQuery, deserialized?.RecommendedQuery);
        Assert.Equal(original.FullSuggestedQuery, deserialized?.FullSuggestedQuery);
    }

    [Fact]
    public void RoundTrip_NullInsights_StaysNull()
    {
        QueryResultsManifest original = new()
        {
            RowCount = 100,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = [MakeNumericFeature("col")]
        };

        string json = ManifestSerializer.Serialize(original);
        QueryResultsManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.Insights);
        Assert.Null(deserialized.RecommendedQuery);
        Assert.Null(deserialized.QueryAnnotations);
    }

    [Fact]
    public void Serialize_EnumsAsStrings()
    {
        QueryResultsManifest manifest = MakeManifestWithInsights();

        string json = ManifestSerializer.Serialize(manifest);

        // Enums should serialize as strings, not integers.
        Assert.Contains("\"ConstantFeature\"", json);
        Assert.Contains("\"AutoSafe\"", json);
        Assert.Contains("\"DataQuality\"", json);
        Assert.Contains("\"Warning\"", json);
        Assert.Contains("\"Feature\"", json);
    }

    // ── Helpers ──

    private static QueryResultsManifest MakeManifestWithInsights()
    {
        DatasetInsight insight = new()
        {
            Kind = InsightKind.ConstantFeature,
            Category = InsightCategory.DataQuality,
            Severity = InsightSeverity.Warning,
            Confidence = 0.99,
            Scope = InsightScope.Feature,
            Observation = "Column 'status' is constant.",
            Risk = "Zero information gain.",
            Recommendation = "Drop column 'status'.",
            AffectedFeatures = ["status"],
            Actions =
            [
                new InsightAction(ActionKind.Drop, "status", null, null, true, true, null)
            ],
            RecommendedApplyMode = ApplyMode.AutoSafe
        };

        QueryAnnotation annotation = new("status", InsightKind.ConstantFeature, "Dropped: constant", 0.99);

        return new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = new DateTime(2026, 3, 23, 12, 0, 0, DateTimeKind.Utc),
            Features = [MakeNumericFeature("age"), MakeNumericFeature("status")],
            Insights = [insight],
            RecommendedQuery = "SELECT age FROM source",
            FullSuggestedQuery = "SELECT age FROM source",
            QueryAnnotations = [annotation]
        };
    }

    private static NumericFeatureManifest MakeNumericFeature(string name)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Scalar,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            EstimatedDistinctCount = 50,
            TopKValues = [],
            Min = 0.0,
            Max = 100.0,
            Mean = 50.0,
            Variance = 25.0,
            StandardDeviation = 5.0,
            Skewness = 0.0,
            Kurtosis = 3.0,
            Histogram = new HistogramData([], []),
            ZeroCount = 0,
            ZeroRatio = 0.0,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = false
        };
    }
}
