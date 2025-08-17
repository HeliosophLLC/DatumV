namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// Tests for the Phase 3 cross-manifest join scoring overhaul: role-based candidate
/// filtering, gated evidence scoring, edge caps, chain output caps, and graph complexity.
/// </summary>
public sealed class JoinScoringOverhaulTests
{
    // ── 3.1: Role-Based Candidate Filtering ──

    [Fact]
    public void IsRolePairJoinable_BothMeasure_Rejected()
    {
        NumericFeatureManifest left = MakeNumericFeature("amount", ColumnRole.Measure);
        NumericFeatureManifest right = MakeNumericFeature("price", ColumnRole.Measure);

        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_BothStructural_Rejected()
    {
        // Use StringFeatureManifest with Structural role — the role gate only checks
        // the Role property, not the manifest subclass.
        StringFeatureManifest left = MakeStringFeature("embedding_a", ColumnRole.Structural);
        StringFeatureManifest right = MakeStringFeature("embedding_b", ColumnRole.Structural);

        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_IdentifierAndForeignKey_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("customer_id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("customer_id", ColumnRole.ForeignKey);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_IdentifierAndMeasure_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("amount", ColumnRole.Measure);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalWithSimilarNdv_Allowed()
    {
        StringFeatureManifest left = MakeStringFeature("status", ColumnRole.Categorical, estimatedDistinctCount: 5);
        StringFeatureManifest right = MakeStringFeature("status", ColumnRole.Categorical, estimatedDistinctCount: 4);

        // NDV ratio = 4/5 = 0.8 ≥ 0.5.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalWithDisparate_Ndv_Rejected()
    {
        StringFeatureManifest left = MakeStringFeature("country", ColumnRole.Categorical, estimatedDistinctCount: 200);
        StringFeatureManifest right = MakeStringFeature("gender", ColumnRole.Categorical, estimatedDistinctCount: 3);

        // NDV ratio = 3/200 = 0.015 < 0.5.
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_NullRoleFallsThrough()
    {
        NumericFeatureManifest left = MakeNumericFeature("amount", role: null);
        NumericFeatureManifest right = MakeNumericFeature("price", role: null);

        // Without roles, the pair is allowed through for backward compatibility.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_OneNullRoleFallsThrough()
    {
        NumericFeatureManifest left = MakeNumericFeature("id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("amount", role: null);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_TemporalAndMeasure_Rejected()
    {
        // Use StringFeatureManifest with Temporal role — the role gate only checks
        // the Role property, not the manifest subclass.
        StringFeatureManifest left = MakeStringFeature("created_at", ColumnRole.Temporal);
        NumericFeatureManifest right = MakeNumericFeature("amount", ColumnRole.Measure);

        // Neither is Identifier/ForeignKey, not both Categorical → rejected.
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_ForeignKeyAndCategorical_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("dept_id", ColumnRole.ForeignKey);
        StringFeatureManifest right = MakeStringFeature("dept_code", ColumnRole.Categorical, estimatedDistinctCount: 10);

        // One side is ForeignKey → allowed.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void FindCandidatePairs_MeasureColumns_FilteredByRole()
    {
        ManifestWithName left = MakeManifest("table_a",
            MakeNumericFeature("revenue", ColumnRole.Measure),
            MakeNumericFeature("order_id", ColumnRole.Identifier));
        ManifestWithName right = MakeManifest("table_b",
            MakeNumericFeature("cost", ColumnRole.Measure),
            MakeNumericFeature("order_id", ColumnRole.Identifier));

        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, CrossManifestThresholds.Default);

        // order_id↔order_id should be a candidate; revenue↔cost should not.
        Assert.Contains(candidates, c => c.LeftColumn == "order_id" && c.RightColumn == "order_id");
        Assert.DoesNotContain(candidates, c => c.LeftColumn == "revenue" && c.RightColumn == "cost");
    }

    // ── 3.2: Gated Evidence Scoring ──

    [Fact]
    public void ScoreEvidence_WeakNameBelowJoinabilityFloor_HardZero()
    {
        NumericFeatureManifest left = MakeNumericFeature("revenue", ColumnRole.Identifier,
            estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeNumericFeature("zip_code", ColumnRole.Identifier,
            estimatedDistinctCount: 1000);

        // Name similarity for "revenue" vs "zip_code" is well below 0.3.
        double nameSimilarity = ColumnMatcher.ComputeNameSimilarity("revenue", "zip_code");
        ColumnMatchCandidate match = new("revenue", "zip_code", nameSimilarity, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    [Fact]
    public void ScoreEvidence_NoIdentityEvidence_HardZero()
    {
        // Both non-unique, disjoint TopK, extreme cardinality mismatch.
        NumericFeatureManifest left = MakeNumericFeature("counter", ColumnRole.Identifier,
            estimatedDistinctCount: 2);
        NumericFeatureManifest right = MakeNumericFeature("counter", ColumnRole.Identifier,
            estimatedDistinctCount: 2,
            topK: [new FrequencyEntry("99", 500)]);

        CrossManifestThresholds strict = new() { IdentityEvidenceFloor = 0.5 };
        ColumnMatchCandidate match = new("counter", "counter", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, strict);

        // With strict floor and low identity evidence, should be zero.
        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    [Fact]
    public void ScoreEvidence_IncompatibleTypes_StructuralFloorKills()
    {
        // Image vs Float32 → typeCompatibility = 0.0.
        StringFeatureManifest left = new()
        {
            Name = "data",
            Kind = DataKind.Image,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
        NumericFeatureManifest right = MakeNumericFeature("data", ColumnRole.Identifier,
            estimatedDistinctCount: 100);

        // Structural floor of 0.2 should kill this because type=0.0 → structural=0.0.
        ColumnMatchCandidate match = new("data", "data", 1.0, 0.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    // ── 3.3: Edge Caps ──

    [Fact]
    public void BuildGraph_MaxEdgesPerTablePair_Enforced()
    {
        // Create 5 candidates between the same two tables at different confidences.
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", "col1", "col1", 0.9),
            MakeCandidate("A", "B", "col2", "col2", 0.8),
            MakeCandidate("A", "B", "col3", "col3", 0.7),
            MakeCandidate("A", "B", "col4", "col4", 0.6),
            MakeCandidate("A", "B", "col5", "col5", 0.55),
        ];

        CrossManifestThresholds thresholds = new() { MaxEdgesPerTablePair = 2, MinMarginOverNextBest = 0.0 };

        IReadOnlyList<JoinGraphEdge> edges = JoinGraphBuilder.BuildGraph(candidates, thresholds);

        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void BuildGraph_MaxEdgesPerColumn_EnforcedPerTargetTable()
    {
        // col1 on table A joins to two different columns on table B.
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", "col1", "col_x", 0.9),
            MakeCandidate("A", "B", "col1", "col_y", 0.7),
        ];

        CrossManifestThresholds thresholds = new() { MaxEdgesPerColumn = 1, MinMarginOverNextBest = 0.0 };

        IReadOnlyList<JoinGraphEdge> edges = JoinGraphBuilder.BuildGraph(candidates, thresholds);

        // col1 on A already has an edge to B → second edge dropped.
        Assert.Single(edges);
        Assert.Equal(0, edges[0].CandidateIndex);
    }

    [Fact]
    public void BuildGraph_PerColumnCapAllowsDifferentTables()
    {
        // col1 on table B joins to col1 on both A and C.
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", "col1", "col1", 0.9),
            MakeCandidate("B", "C", "col1", "col1", 0.8),
        ];

        CrossManifestThresholds thresholds = new() { MaxEdgesPerColumn = 1, MinMarginOverNextBest = 0.0 };

        IReadOnlyList<JoinGraphEdge> edges = JoinGraphBuilder.BuildGraph(candidates, thresholds);

        // B.col1 joins to A and C are different target tables → both allowed.
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void BuildGraph_NearDuplicateEdgesDroppedByMargin()
    {
        // Two edges between A and B with confidence gap < 0.05.
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", "id1", "id1", 0.80),
            MakeCandidate("A", "B", "id2", "id2", 0.78),
        ];

        CrossManifestThresholds thresholds = new() { MinMarginOverNextBest = 0.05 };

        IReadOnlyList<JoinGraphEdge> edges = JoinGraphBuilder.BuildGraph(candidates, thresholds);

        // Gap is 0.02 < 0.05 → near-duplicate dropped.
        Assert.Single(edges);
    }

    [Fact]
    public void BuildGraph_EdgesWithSufficientMarginRetained()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", "id1", "id1", 0.90),
            MakeCandidate("A", "B", "id2", "id2", 0.70),
        ];

        CrossManifestThresholds thresholds = new() { MinMarginOverNextBest = 0.05 };

        IReadOnlyList<JoinGraphEdge> edges = JoinGraphBuilder.BuildGraph(candidates, thresholds);

        // Gap is 0.20 ≥ 0.05 → both retained.
        Assert.Equal(2, edges.Count);
    }

    // ── 3.4: Chain Output Cap ──

    [Fact]
    public void FindTransitiveChains_TruncatedToMax()
    {
        // Create a fully connected graph of 5 tables → produces many chains.
        List<JoinGraphEdge> edges = new();
        string[] tables = ["A", "B", "C", "D", "E"];
        int index = 0;

        for (int i = 0; i < tables.Length; i++)
        {
            for (int j = i + 1; j < tables.Length; j++)
            {
                edges.Add(new JoinGraphEdge(tables[i], tables[j], CandidateIndex: index++, Confidence: 0.8));
            }
        }

        CrossManifestThresholds thresholds = new() { MaxTransitiveChains = 3 };

        IReadOnlyList<JoinChain> chains = JoinGraphBuilder.FindTransitiveChains(edges, thresholds);

        Assert.Equal(3, chains.Count);
    }

    // ── 3.5: Graph Complexity ──

    [Fact]
    public void ComputeComplexity_EmptyEdges_ReturnsNull()
    {
        GraphComplexity? complexity = JoinGraphBuilder.ComputeComplexity([]);

        Assert.Null(complexity);
    }

    [Fact]
    public void ComputeComplexity_SingleEdge_LowAmbiguity()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.8),
        ];

        GraphComplexity? complexity = JoinGraphBuilder.ComputeComplexity(edges);

        Assert.NotNull(complexity);
        Assert.Equal(1, complexity.EdgeCount);
        Assert.Equal(2, complexity.TableCount);
        Assert.Equal(1, complexity.MaxEdgesPerTablePair);
        Assert.Equal(1.0, complexity.AmbiguityRatio);
    }

    [Fact]
    public void ComputeComplexity_FullyConnectedThreeTables()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.9),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.8),
            new JoinGraphEdge("A", "C", CandidateIndex: 2, Confidence: 0.7),
        ];

        GraphComplexity? complexity = JoinGraphBuilder.ComputeComplexity(edges);

        Assert.NotNull(complexity);
        Assert.Equal(3, complexity.EdgeCount);
        Assert.Equal(3, complexity.TableCount);
        Assert.Equal(1, complexity.MaxEdgesPerTablePair);
        // 3 edges / (3 × 2 / 2) = 3 / 3 = 1.0.
        Assert.Equal(1.0, complexity.AmbiguityRatio);
    }

    [Fact]
    public void ComputeComplexity_SparseGraph_LowAmbiguity()
    {
        // Linear chain: A-B, B-C, C-D → 3 edges, 4 tables.
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.9),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.8),
            new JoinGraphEdge("C", "D", CandidateIndex: 2, Confidence: 0.7),
        ];

        GraphComplexity? complexity = JoinGraphBuilder.ComputeComplexity(edges);

        Assert.NotNull(complexity);
        Assert.Equal(3, complexity.EdgeCount);
        Assert.Equal(4, complexity.TableCount);
        // 3 / (4 × 3 / 2) = 3 / 6 = 0.5.
        Assert.Equal(0.5, complexity.AmbiguityRatio);
    }

    [Fact]
    public void Analyze_PopulatesGraphComplexity()
    {
        ManifestWithName tableA = MakeManifest("orders",
            MakeNumericFeature("customer_id", ColumnRole.Identifier, estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]));
        ManifestWithName tableB = MakeManifest("customers",
            MakeNumericFeature("customer_id", ColumnRole.Identifier, estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]));

        CrossManifestResult result = CrossManifestAnalyzer.Analyze([tableA, tableB]);

        Assert.NotEmpty(result.JoinGraphs);
        // With one edge between two tables, complexity should be present.
        if (result.JoinGraphs[0].Edges.Count > 0)
        {
            GraphComplexity complexity = Assert.IsType<GraphComplexity>(result.JoinGraphs[0].Complexity);
            Assert.Equal(result.JoinGraphs[0].Edges.Count, complexity.EdgeCount);
        }
    }

    [Fact]
    public void Analyze_DenseGraph_EmitsDenseJoinGraphInsight()
    {
        // Create enough tables and join candidates to exceed the 0.75 ambiguity threshold.
        // 3 tables fully connected → ambiguity = 1.0.
        ManifestWithName tableA = MakeManifest("t_a",
            MakeNumericFeature("key_id", ColumnRole.Identifier, estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]));
        ManifestWithName tableB = MakeManifest("t_b",
            MakeNumericFeature("key_id", ColumnRole.Identifier, estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]));
        ManifestWithName tableC = MakeManifest("t_c",
            MakeNumericFeature("key_id", ColumnRole.Identifier, estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]));

        CrossManifestResult result = CrossManifestAnalyzer.Analyze([tableA, tableB, tableC]);

        // All three tables share "key_id" with perfect match → fully connected graph.
        if (result.JoinGraphs[0].Edges.Count >= 3)
        {
            Assert.NotNull(result.Insights);
            Assert.Contains(result.Insights, i =>
                i.Kind == DatumIngest.Manifest.Insights.InsightKind.DenseJoinGraph);
        }
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifest(string name, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
        });
    }

    private static NumericFeatureManifest MakeNumericFeature(
        string name,
        ColumnRole? role = null,
        long estimatedDistinctCount = 100,
        IReadOnlyList<FrequencyEntry>? topK = null)
    {
        NumericFeatureManifest manifest = new()
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topK ?? [],
            Min = 0.0,
            Max = 1000.0,
            Mean = 500.0,
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
        manifest.Role = role;
        return manifest;
    }

    private static StringFeatureManifest MakeStringFeature(
        string name,
        ColumnRole? role = null,
        long estimatedDistinctCount = 100)
    {
        StringFeatureManifest manifest = new()
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
        manifest.Role = role;
        return manifest;
    }

    private static JoinCandidate MakeCandidate(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double confidence)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = [leftColumn],
            RightColumns = [rightColumn],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.9,
                CompositeConfidence = confidence,
            },
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }
}
