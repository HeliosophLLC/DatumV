namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;

/// <summary>
/// Tests that <see cref="CrossManifestResult"/> and related types survive JSON round-trip
/// via <see cref="ManifestSerializer"/>.
/// </summary>
public sealed class CrossManifestSerializationTests
{
    [Fact]
    public void RoundTrip_CrossManifestResult_PreservesAllFields()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Tables.Count, deserialized.Tables.Count);
        Assert.Equal(original.Tables[0], deserialized.Tables[0]);
        Assert.Equal(original.Candidates.Count, deserialized.Candidates.Count);
        Assert.Equal(original.JoinGraph.Count, deserialized.JoinGraph.Count);
    }

    [Fact]
    public void RoundTrip_JoinCandidate_PreservesEvidence()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        JoinCandidate originalCandidate = original.Candidates[0];
        JoinCandidate roundTripped = deserialized.Candidates[0];

        Assert.Equal(originalCandidate.LeftTable, roundTripped.LeftTable);
        Assert.Equal(originalCandidate.RightTable, roundTripped.RightTable);
        Assert.Equal(originalCandidate.LeftColumns.Count, roundTripped.LeftColumns.Count);
        Assert.Equal(originalCandidate.LeftColumns[0], roundTripped.LeftColumns[0]);
        Assert.Equal(originalCandidate.Confidence, roundTripped.Confidence);
        Assert.Equal(originalCandidate.EstimatedJoinType, roundTripped.EstimatedJoinType);
        Assert.Equal(originalCandidate.EstimatedFanout, roundTripped.EstimatedFanout);
    }

    [Fact]
    public void RoundTrip_JoinEvidence_PreservesAllSignals()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        JoinEvidence originalEvidence = original.Candidates[0].Evidence;
        JoinEvidence roundTripped = deserialized.Candidates[0].Evidence;

        Assert.Equal(originalEvidence.NameSimilarity, roundTripped.NameSimilarity);
        Assert.Equal(originalEvidence.TypeCompatibility, roundTripped.TypeCompatibility);
        Assert.Equal(originalEvidence.TopKJaccard, roundTripped.TopKJaccard);
        Assert.Equal(originalEvidence.CardinalityRatio, roundTripped.CardinalityRatio);
        Assert.Equal(originalEvidence.RangeOverlap, roundTripped.RangeOverlap);
        Assert.Equal(originalEvidence.NullKeyRatio, roundTripped.NullKeyRatio);
        Assert.Equal(originalEvidence.UniqueKeyScore, roundTripped.UniqueKeyScore);
        Assert.Equal(originalEvidence.CompositeConfidence, roundTripped.CompositeConfidence);
    }

    [Fact]
    public void RoundTrip_JoinGraphEdge_PreservesFields()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        JoinGraphEdge originalEdge = original.JoinGraph[0];
        JoinGraphEdge roundTripped = deserialized.JoinGraph[0];

        Assert.Equal(originalEdge.LeftTable, roundTripped.LeftTable);
        Assert.Equal(originalEdge.RightTable, roundTripped.RightTable);
        Assert.Equal(originalEdge.CandidateIndex, roundTripped.CandidateIndex);
        Assert.Equal(originalEdge.Confidence, roundTripped.Confidence);
    }

    [Fact]
    public void RoundTrip_TransitiveChains_PreservesFields()
    {
        CrossManifestResult original = MakeResultWithChains();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.TransitiveChains);
        Assert.Equal(original.TransitiveChains!.Count, deserialized.TransitiveChains.Count);

        JoinChain originalChain = original.TransitiveChains[0];
        JoinChain roundTripped = deserialized.TransitiveChains[0];

        Assert.Equal(originalChain.Tables.Count, roundTripped.Tables.Count);
        Assert.Equal(originalChain.Edges.Count, roundTripped.Edges.Count);
        Assert.Equal(originalChain.MinConfidence, roundTripped.MinConfidence);
    }

    [Fact]
    public void RoundTrip_NullOptionalFields_PreservesNulls()
    {
        CrossManifestResult original = new()
        {
            Tables = ["t1", "t2"],
            Candidates = [],
            JoinGraph = [],
            TransitiveChains = null,
            Insights = null,
            RecommendedQuery = null,
            QueryAnnotations = null,
        };

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.TransitiveChains);
        Assert.Null(deserialized.Insights);
        Assert.Null(deserialized.RecommendedQuery);
        Assert.Null(deserialized.QueryAnnotations);
    }

    [Fact]
    public void RoundTrip_JoinClassificationEnum_SerializesAsString()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);

        // The JsonStringEnumConverter should serialize as "OneToMany" not as integer.
        Assert.Contains("OneToMany", json);
    }

    [Fact]
    public void RoundTrip_RecommendedQuery_PreservesValue()
    {
        CrossManifestResult original = MakeResult();

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.RecommendedQuery, deserialized.RecommendedQuery);
    }

    [Fact]
    public void RoundTrip_QualityWarnings_Preserved()
    {
        JoinCandidate candidateWithWarnings = new()
        {
            LeftTable = "orders",
            RightTable = "customers",
            LeftColumns = ["id"],
            RightColumns = ["id"],
            Evidence = MakeEvidence(),
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = ["High null-key ratio", "Cardinality mismatch"],
        };

        CrossManifestResult original = new()
        {
            Tables = ["orders", "customers"],
            Candidates = [candidateWithWarnings],
            JoinGraph = [],
        };

        string json = ManifestSerializer.SerializeCrossManifest(original);
        CrossManifestResult? deserialized = ManifestSerializer.DeserializeCrossManifest(json);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Candidates[0].QualityWarnings);
        Assert.Equal(2, deserialized.Candidates[0].QualityWarnings!.Count);
        Assert.Equal("High null-key ratio", deserialized.Candidates[0].QualityWarnings![0]);
    }

    // ── Helpers ──

    private static CrossManifestResult MakeResult()
    {
        return new CrossManifestResult
        {
            Tables = ["orders", "customers"],
            Candidates =
            [
                new JoinCandidate
                {
                    LeftTable = "orders",
                    RightTable = "customers",
                    LeftColumns = ["customer_id"],
                    RightColumns = ["customer_id"],
                    Evidence = MakeEvidence(),
                    Confidence = 0.85,
                    EstimatedJoinType = JoinClassification.OneToMany,
                    EstimatedFanout = 3.5,
                    QualityWarnings = null,
                },
            ],
            JoinGraph =
            [
                new JoinGraphEdge("orders", "customers", CandidateIndex: 0, Confidence: 0.85),
            ],
            TransitiveChains = null,
            Insights = null,
            RecommendedQuery = "SELECT * FROM \"orders\" INNER JOIN \"customers\" ON \"orders\".\"customer_id\" = \"customers\".\"customer_id\";",
            QueryAnnotations = null,
        };
    }

    private static CrossManifestResult MakeResultWithChains()
    {
        return new CrossManifestResult
        {
            Tables = ["orders", "customers", "products"],
            Candidates =
            [
                new JoinCandidate
                {
                    LeftTable = "orders",
                    RightTable = "customers",
                    LeftColumns = ["customer_id"],
                    RightColumns = ["customer_id"],
                    Evidence = MakeEvidence(),
                    Confidence = 0.85,
                    EstimatedJoinType = JoinClassification.OneToMany,
                    EstimatedFanout = null,
                    QualityWarnings = null,
                },
                new JoinCandidate
                {
                    LeftTable = "orders",
                    RightTable = "products",
                    LeftColumns = ["product_id"],
                    RightColumns = ["product_id"],
                    Evidence = MakeEvidence(),
                    Confidence = 0.75,
                    EstimatedJoinType = JoinClassification.ManyToOne,
                    EstimatedFanout = null,
                    QualityWarnings = null,
                },
            ],
            JoinGraph =
            [
                new JoinGraphEdge("orders", "customers", CandidateIndex: 0, Confidence: 0.85),
                new JoinGraphEdge("orders", "products", CandidateIndex: 1, Confidence: 0.75),
            ],
            TransitiveChains =
            [
                new JoinChain(
                    ["customers", "orders", "products"],
                    [0, 1],
                    MinConfidence: 0.75),
            ],
            Insights = null,
            RecommendedQuery = null,
            QueryAnnotations = null,
        };
    }

    private static JoinEvidence MakeEvidence()
    {
        return new JoinEvidence
        {
            NameSimilarity = 1.0,
            TypeCompatibility = 1.0,
            TopKJaccard = 0.6,
            CardinalityRatio = 0.9,
            RangeOverlap = 0.8,
            NullKeyRatio = 0.02,
            UniqueKeyScore = 0.95,
            CompositeConfidence = 0.85,
        };
    }
}
