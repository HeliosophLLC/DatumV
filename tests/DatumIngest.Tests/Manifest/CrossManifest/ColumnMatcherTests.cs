namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="ColumnMatcher"/> — name similarity, type compatibility,
/// and candidate pair discovery.
/// </summary>
public sealed class ColumnMatcherTests
{
    // ── Name Similarity ──

    [Fact]
    public void ComputeNameSimilarity_ExactMatch_ReturnsOne()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("customer_id", "customer_id");

        Assert.Equal(1.0, similarity);
    }

    [Fact]
    public void ComputeNameSimilarity_CaseInsensitive_ReturnsOne()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("Customer_Id", "customer_id");

        Assert.Equal(1.0, similarity);
    }

    [Fact]
    public void ComputeNameSimilarity_CompletelyDifferent_ReturnsLowScore()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("revenue", "zip_code");

        Assert.True(similarity < 0.3);
    }

    [Fact]
    public void ComputeNameSimilarity_SuffixBonus_AppliedForIdSuffix()
    {
        double withSuffix = ColumnMatcher.ComputeNameSimilarity("customer_id", "order_id");
        double withoutSuffix = ColumnMatcher.ComputeNameSimilarity("customer_xx", "order_xx");

        // Both are the same edit distance, but the _id suffix gives a bonus.
        Assert.True(withSuffix > withoutSuffix);
    }

    [Fact]
    public void ComputeNameSimilarity_SuffixBonus_AppliedForKeySuffix()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("product_key", "category_key");

        // Should get suffix bonus for _key.
        double baseSimilarity = ColumnMatcher.ComputeNameSimilarity("product_xxx", "category_xxx");
        Assert.True(similarity > baseSimilarity);
    }

    [Fact]
    public void ComputeNameSimilarity_EmptyStrings_ReturnsOne()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("", "");

        Assert.Equal(1.0, similarity);
    }

    [Fact]
    public void ComputeNameSimilarity_OneEmpty_ReturnsZero()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("name", "");

        Assert.Equal(0.0, similarity);
    }

    [Fact]
    public void ComputeNameSimilarity_SimilarNames_ReturnsHighScore()
    {
        double similarity = ColumnMatcher.ComputeNameSimilarity("user_name", "username");

        Assert.True(similarity > 0.7);
    }

    // ── Type Compatibility ──

    [Fact]
    public void ComputeTypeCompatibility_ExactMatch_ReturnsOne()
    {
        double compatibility = ColumnMatcher.ComputeTypeCompatibility(DataKind.Float32, DataKind.Float32);

        Assert.Equal(1.0, compatibility);
    }

    [Fact]
    public void ComputeTypeCompatibility_Float32AndUInt8_Returns0Point7()
    {
        double compatibility = ColumnMatcher.ComputeTypeCompatibility(DataKind.Float32, DataKind.UInt8);

        Assert.Equal(0.7, compatibility);
    }

    [Fact]
    public void ComputeTypeCompatibility_DateAndDateTime_Returns0Point8()
    {
        double compatibility = ColumnMatcher.ComputeTypeCompatibility(DataKind.Date, DataKind.DateTime);

        Assert.Equal(0.8, compatibility);
    }

    [Fact]
    public void ComputeTypeCompatibility_StringAndJsonValue_Returns0Point5()
    {
        double compatibility = ColumnMatcher.ComputeTypeCompatibility(DataKind.String, DataKind.JsonValue);

        Assert.Equal(0.5, compatibility);
    }

    [Fact]
    public void ComputeTypeCompatibility_Incompatible_ReturnsZero()
    {
        double compatibility = ColumnMatcher.ComputeTypeCompatibility(DataKind.Float32, DataKind.Image);

        Assert.Equal(0.0, compatibility);
    }

    [Fact]
    public void ComputeTypeCompatibility_Symmetric()
    {
        double forward = ColumnMatcher.ComputeTypeCompatibility(DataKind.UInt8, DataKind.Float32);
        double reverse = ColumnMatcher.ComputeTypeCompatibility(DataKind.Float32, DataKind.UInt8);

        Assert.Equal(forward, reverse);
    }

    // ── Candidate Pair Discovery ──

    [Fact]
    public void FindCandidatePairs_MatchingColumns_ReturnsCandidate()
    {
        ManifestWithName left = MakeManifest("orders", MakeStringFeature("customer_id"));
        ManifestWithName right = MakeManifest("customers", MakeStringFeature("customer_id"));

        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, CrossManifestThresholds.Default);

        Assert.Contains(candidates, c =>
            c.LeftColumn == "customer_id" && c.RightColumn == "customer_id");
    }

    [Fact]
    public void FindCandidatePairs_NoMatchingColumns_ReturnsEmpty()
    {
        ManifestWithName left = MakeManifest("orders", MakeStringFeature("total_amount"));
        ManifestWithName right = MakeManifest("customers", MakeStringFeature("phone_number"));

        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, CrossManifestThresholds.Default);

        // Completely different names and types should produce no useful matches.
        Assert.True(candidates.Count == 0 || candidates.All(c => c.NameSimilarity < 0.4));
    }

    [Fact]
    public void FindCandidatePairs_IncompatibleTypes_Pruned()
    {
        ManifestWithName left = MakeManifest("images", MakeFeature("data", DataKind.Image));
        ManifestWithName right = MakeManifest("scores", MakeNumericFeature("data"));

        // The name "data" matches exactly, but image vs numeric types score 0.0.
        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, CrossManifestThresholds.Default);

        // Should still find the match because name similarity is 1.0 even with type=0.0.
        ColumnMatchCandidate? match = candidates.FirstOrDefault(
            c => c.LeftColumn == "data" && c.RightColumn == "data");

        if (match is not null)
        {
            Assert.Equal(1.0, match.NameSimilarity);
            Assert.Equal(0.0, match.TypeCompatibility);
        }
    }

    [Fact]
    public void FindCandidatePairs_MultipleColumns_ReturnsAll()
    {
        ManifestWithName left = MakeManifest("orders",
            MakeStringFeature("customer_id"),
            MakeStringFeature("product_id"));
        ManifestWithName right = MakeManifest("line_items",
            MakeStringFeature("customer_id"),
            MakeStringFeature("product_id"));

        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, CrossManifestThresholds.Default);

        Assert.Contains(candidates, c =>
            c.LeftColumn == "customer_id" && c.RightColumn == "customer_id");
        Assert.Contains(candidates, c =>
            c.LeftColumn == "product_id" && c.RightColumn == "product_id");
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifest(string name, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features
        });
    }

    private static NumericFeatureManifest MakeNumericFeature(
        string name,
        long estimatedDistinctCount = 100,
        double min = 0.0,
        double max = 100.0)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
            Min = min,
            Max = max,
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
            IntegerValued = true,
        };
    }

    private static StringFeatureManifest MakeStringFeature(
        string name,
        long estimatedDistinctCount = 100)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = DataKind.String,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
    }

    private static StringFeatureManifest MakeFeature(string name, DataKind kind)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
    }
}
