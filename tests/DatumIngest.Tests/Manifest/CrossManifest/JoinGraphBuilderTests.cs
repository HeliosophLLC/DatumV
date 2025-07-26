namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Tests for <see cref="JoinGraphBuilder"/> — graph construction from candidates
/// and transitive chain discovery via breadth-first search.
/// </summary>
public sealed class JoinGraphBuilderTests
{
    // ── Graph Building ──

    [Fact]
    public void BuildGraph_CandidateAboveThreshold_IncludesEdge()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", 0.8),
        ];

        IReadOnlyList<JoinGraphEdge> edges =
            JoinGraphBuilder.BuildGraph(candidates, CrossManifestThresholds.Default);

        Assert.Single(edges);
        Assert.Equal("A", edges[0].LeftTable);
        Assert.Equal("B", edges[0].RightTable);
        Assert.Equal(0, edges[0].CandidateIndex);
    }

    [Fact]
    public void BuildGraph_CandidateBelowThreshold_Excluded()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", 0.3), // Below default GraphEdgeMinConfidence=0.5.
        ];

        IReadOnlyList<JoinGraphEdge> edges =
            JoinGraphBuilder.BuildGraph(candidates, CrossManifestThresholds.Default);

        Assert.Empty(edges);
    }

    [Fact]
    public void BuildGraph_MultipleCandidates_CorrectIndices()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("A", "B", 0.8),
            MakeCandidate("B", "C", 0.7),
            MakeCandidate("A", "C", 0.3), // Below threshold.
        ];

        IReadOnlyList<JoinGraphEdge> edges =
            JoinGraphBuilder.BuildGraph(candidates, CrossManifestThresholds.Default);

        Assert.Equal(2, edges.Count);
        Assert.Equal(0, edges[0].CandidateIndex);
        Assert.Equal(1, edges[1].CandidateIndex);
    }

    // ── Transitive Chains ──

    [Fact]
    public void FindTransitiveChains_ThreeTableChain_Discovered()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.8),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.7),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        Assert.Single(chains);
        Assert.Equal(3, chains[0].Tables.Count);
        Assert.Equal(0.7, chains[0].MinConfidence);
    }

    [Fact]
    public void FindTransitiveChains_NoCycles()
    {
        // A–B–C–A forms a cycle. Chains should be acyclic.
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.8),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.7),
            new JoinGraphEdge("C", "A", CandidateIndex: 2, Confidence: 0.6),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        // All chains should have unique tables (no table appears twice).
        foreach (JoinChain chain in chains)
        {
            Assert.Equal(chain.Tables.Count, chain.Tables.Distinct().Count());
        }
    }

    [Fact]
    public void FindTransitiveChains_PairOnly_NoChains()
    {
        // Only a single edge — cannot form a 3-table chain.
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.8),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        Assert.Empty(chains);
    }

    [Fact]
    public void FindTransitiveChains_FourTableChain_Discovered()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.9),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.8),
            new JoinGraphEdge("C", "D", CandidateIndex: 2, Confidence: 0.7),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        // Should discover A→B→C, A→B→C→D, B→C→D.
        Assert.Contains(chains, c => c.Tables.Count == 4);
        Assert.Contains(chains, c => c.Tables.Count == 3);
    }

    [Fact]
    public void FindTransitiveChains_SortedByDescendingMinConfidence()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.6),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.9),
            new JoinGraphEdge("C", "D", CandidateIndex: 2, Confidence: 0.8),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        for (int i = 1; i < chains.Count; i++)
        {
            Assert.True(chains[i - 1].MinConfidence >= chains[i].MinConfidence);
        }
    }

    [Fact]
    public void FindTransitiveChains_RespectsMaxDepth()
    {
        CrossManifestThresholds thresholds = new() { ChainMaxDepth = 3 };

        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.9),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.8),
            new JoinGraphEdge("C", "D", CandidateIndex: 2, Confidence: 0.7),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, thresholds);

        // MaxDepth=3 means max 3 tables, so no 4-table chains.
        Assert.DoesNotContain(chains, c => c.Tables.Count > 3);
    }

    [Fact]
    public void FindTransitiveChains_DeduplicatesReversePaths()
    {
        List<JoinGraphEdge> edges =
        [
            new JoinGraphEdge("A", "B", CandidateIndex: 0, Confidence: 0.8),
            new JoinGraphEdge("B", "C", CandidateIndex: 1, Confidence: 0.7),
        ];

        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains(edges, CrossManifestThresholds.Default);

        // A→B→C and C→B→A should be deduplicated — only one should exist.
        Assert.Single(chains);
    }

    [Fact]
    public void FindTransitiveChains_EmptyEdges_ReturnsEmpty()
    {
        IReadOnlyList<JoinChain> chains =
            JoinGraphBuilder.FindTransitiveChains([], CrossManifestThresholds.Default);

        Assert.Empty(chains);
    }

    // ── Helpers ──

    private static JoinCandidate MakeCandidate(string leftTable, string rightTable, double confidence)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = ["id"],
            RightColumns = ["id"],
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
