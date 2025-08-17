namespace DatumIngest.Tests.Manifest.SchemaMatching;

using DatumIngest.Manifest;
using DatumIngest.Manifest.SchemaMatching;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Tests for Phase 4 vocabulary infrastructure: <see cref="ColumnVocabulary"/> set operations,
/// <see cref="VocabularyAccumulator"/> accumulation and capping, <see cref="ManifestBuilder"/>
/// vocabulary attachment, and <see cref="JoinEvidenceScorer"/> exact containment scoring.
/// </summary>
public sealed class VocabularyTests
{
    // ── ColumnVocabulary: Set Operations ──

    [Fact]
    public void ComputeIntersectionSize_IdenticalSets_ReturnsCount()
    {
        ColumnVocabulary left = MakeVocabulary("1", "2", "3");
        ColumnVocabulary right = MakeVocabulary("1", "2", "3");

        int intersection = ColumnVocabulary.ComputeIntersectionSize(left, right);

        Assert.Equal(3, intersection);
    }

    [Fact]
    public void ComputeIntersectionSize_DisjointSets_ReturnsZero()
    {
        ColumnVocabulary left = MakeVocabulary("1", "2", "3");
        ColumnVocabulary right = MakeVocabulary("4", "5", "6");

        int intersection = ColumnVocabulary.ComputeIntersectionSize(left, right);

        Assert.Equal(0, intersection);
    }

    [Fact]
    public void ComputeIntersectionSize_PartialOverlap_ReturnsSharedCount()
    {
        ColumnVocabulary left = MakeVocabulary("1", "2", "3", "4");
        ColumnVocabulary right = MakeVocabulary("3", "4", "5", "6");

        int intersection = ColumnVocabulary.ComputeIntersectionSize(left, right);

        Assert.Equal(2, intersection);
    }

    [Fact]
    public void ComputeIntersectionSize_EmptyLeft_ReturnsZero()
    {
        ColumnVocabulary left = MakeVocabulary();
        ColumnVocabulary right = MakeVocabulary("1", "2");

        int intersection = ColumnVocabulary.ComputeIntersectionSize(left, right);

        Assert.Equal(0, intersection);
    }

    [Fact]
    public void ComputeIntersectionSize_SubsetRelationship_ReturnsSubsetCount()
    {
        ColumnVocabulary subset = MakeVocabulary("2", "4");
        ColumnVocabulary superset = MakeVocabulary("1", "2", "3", "4", "5");

        int intersection = ColumnVocabulary.ComputeIntersectionSize(subset, superset);

        Assert.Equal(2, intersection);
    }

    [Fact]
    public void ComputeJaccard_IdenticalSets_ReturnsOne()
    {
        ColumnVocabulary left = MakeVocabulary("a", "b", "c");
        ColumnVocabulary right = MakeVocabulary("a", "b", "c");

        double jaccard = ColumnVocabulary.ComputeJaccard(left, right);

        Assert.Equal(1.0, jaccard, precision: 10);
    }

    [Fact]
    public void ComputeJaccard_DisjointSets_ReturnsZero()
    {
        ColumnVocabulary left = MakeVocabulary("a", "b");
        ColumnVocabulary right = MakeVocabulary("c", "d");

        double jaccard = ColumnVocabulary.ComputeJaccard(left, right);

        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void ComputeJaccard_BothEmpty_ReturnsZero()
    {
        ColumnVocabulary left = MakeVocabulary();
        ColumnVocabulary right = MakeVocabulary();

        double jaccard = ColumnVocabulary.ComputeJaccard(left, right);

        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void ComputeJaccard_PartialOverlap_ReturnsCorrectRatio()
    {
        // Left = {1, 2, 3}, Right = {2, 3, 4}
        // Intersection = {2, 3} = 2, Union = {1, 2, 3, 4} = 4
        // Jaccard = 2/4 = 0.5
        ColumnVocabulary left = MakeVocabulary("1", "2", "3");
        ColumnVocabulary right = MakeVocabulary("2", "3", "4");

        double jaccard = ColumnVocabulary.ComputeJaccard(left, right);

        Assert.Equal(0.5, jaccard, precision: 10);
    }

    [Fact]
    public void ComputeContainment_SubsetFullyContained_ReturnsOne()
    {
        ColumnVocabulary foreignKey = MakeVocabulary("1", "2", "3");
        ColumnVocabulary primaryKey = MakeVocabulary("1", "2", "3", "4", "5");

        double containment = ColumnVocabulary.ComputeContainment(foreignKey, primaryKey);

        Assert.Equal(1.0, containment, precision: 10);
    }

    [Fact]
    public void ComputeContainment_NoOverlap_ReturnsZero()
    {
        ColumnVocabulary source = MakeVocabulary("1", "2");
        ColumnVocabulary target = MakeVocabulary("3", "4");

        double containment = ColumnVocabulary.ComputeContainment(source, target);

        Assert.Equal(0.0, containment);
    }

    [Fact]
    public void ComputeContainment_EmptySource_ReturnsZero()
    {
        ColumnVocabulary source = MakeVocabulary();
        ColumnVocabulary target = MakeVocabulary("1", "2");

        double containment = ColumnVocabulary.ComputeContainment(source, target);

        Assert.Equal(0.0, containment);
    }

    [Fact]
    public void ComputeContainment_PartialOverlap_ReturnsCorrectRatio()
    {
        // Source = {1, 2, 3, 4}, Target = {2, 4, 6}
        // Intersection = {2, 4} = 2
        // Containment = 2/4 = 0.5
        ColumnVocabulary source = MakeVocabulary("1", "2", "3", "4");
        ColumnVocabulary target = MakeVocabulary("2", "4", "6");

        double containment = ColumnVocabulary.ComputeContainment(source, target);

        Assert.Equal(0.5, containment, precision: 10);
    }

    [Fact]
    public void ComputeContainment_Asymmetric_DirectionMatters()
    {
        ColumnVocabulary small = MakeVocabulary("1", "2");
        ColumnVocabulary large = MakeVocabulary("1", "2", "3", "4", "5");

        double smallInLarge = ColumnVocabulary.ComputeContainment(small, large);
        double largeInSmall = ColumnVocabulary.ComputeContainment(large, small);

        // FK {1,2} fully contained in PK {1,2,3,4,5}: 1.0
        Assert.Equal(1.0, smallInLarge, precision: 10);
        // PK partially contained in FK: 2/5 = 0.4
        Assert.Equal(0.4, largeInSmall, precision: 10);
    }

    // ── VocabularyAccumulator ──

    [Fact]
    public void VocabularyAccumulator_IntegerColumn_CollectsDistinctValues()
    {
        VocabularyAccumulator accumulator = new(DataKind.Int32);

        accumulator.Add(DataValue.FromInt32(3));
        accumulator.Add(DataValue.FromInt32(1));
        accumulator.Add(DataValue.FromInt32(2));
        accumulator.Add(DataValue.FromInt32(1)); // duplicate

        StatisticResult result = accumulator.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.Equal("vocabulary", result.Name);
        Assert.False(vocabulary.Capped);
        Assert.Equal(3, vocabulary.SortedValues.Count);
        // Ordinal sort: "1", "2", "3"
        Assert.Equal("1", vocabulary.SortedValues[0]);
        Assert.Equal("2", vocabulary.SortedValues[1]);
        Assert.Equal("3", vocabulary.SortedValues[2]);
    }

    [Fact]
    public void VocabularyAccumulator_StringColumn_CollectsDistinctValues()
    {
        VocabularyAccumulator accumulator = new(DataKind.String);

        accumulator.Add(DataValue.FromString("charlie"));
        accumulator.Add(DataValue.FromString("alice"));
        accumulator.Add(DataValue.FromString("bob"));
        accumulator.Add(DataValue.FromString("alice")); // duplicate

        StatisticResult result = accumulator.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.False(vocabulary.Capped);
        Assert.Equal(3, vocabulary.SortedValues.Count);
        // Ordinal sort: "alice", "bob", "charlie"
        Assert.Equal("alice", vocabulary.SortedValues[0]);
        Assert.Equal("bob", vocabulary.SortedValues[1]);
        Assert.Equal("charlie", vocabulary.SortedValues[2]);
    }

    [Fact]
    public void VocabularyAccumulator_Int64Column_CollectsDistinctValues()
    {
        VocabularyAccumulator accumulator = new(DataKind.Int64);

        accumulator.Add(DataValue.FromInt64(100));
        accumulator.Add(DataValue.FromInt64(200));
        accumulator.Add(DataValue.FromInt64(100)); // duplicate

        StatisticResult result = accumulator.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.False(vocabulary.Capped);
        Assert.Equal(2, vocabulary.SortedValues.Count);
    }

    [Fact]
    public void VocabularyAccumulator_CapsAtMaxDistinctValues()
    {
        VocabularyAccumulator accumulator = new(DataKind.Int32, maxDistinctValues: 5);

        for (int i = 0; i < 10; i++)
        {
            accumulator.Add(DataValue.FromInt32(i));
        }

        StatisticResult result = accumulator.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.True(vocabulary.Capped);
        // Should have exactly 5 values (stopped adding after cap)
        Assert.Equal(5, vocabulary.SortedValues.Count);
    }

    [Fact]
    public void VocabularyAccumulator_SkipsNullValues()
    {
        VocabularyAccumulator accumulator = new(DataKind.Int32);

        accumulator.Add(DataValue.FromInt32(1));
        accumulator.Add(DataValue.Null(DataKind.Int32));
        accumulator.Add(DataValue.FromInt32(2));
        accumulator.Add(DataValue.Null(DataKind.Int32));

        StatisticResult result = accumulator.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.Equal(2, vocabulary.SortedValues.Count);
    }

    [Fact]
    public void VocabularyAccumulator_Merge_CombinesDistinctValues()
    {
        VocabularyAccumulator left = new(DataKind.Int32);
        left.Add(DataValue.FromInt32(1));
        left.Add(DataValue.FromInt32(2));

        VocabularyAccumulator right = new(DataKind.Int32);
        right.Add(DataValue.FromInt32(2));
        right.Add(DataValue.FromInt32(3));

        left.Merge(right);

        StatisticResult result = left.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        Assert.False(vocabulary.Capped);
        Assert.Equal(3, vocabulary.SortedValues.Count);
    }

    [Fact]
    public void VocabularyAccumulator_Merge_PropagatesCapped()
    {
        VocabularyAccumulator left = new(DataKind.Int32, maxDistinctValues: 100);
        left.Add(DataValue.FromInt32(1));

        VocabularyAccumulator right = new(DataKind.Int32, maxDistinctValues: 3);
        for (int i = 0; i < 5; i++)
        {
            right.Add(DataValue.FromInt32(i + 10));
        }

        left.Merge(right);

        StatisticResult result = left.GetResult();
        VocabularyResult vocabulary = (VocabularyResult)result.Value!;

        // Capped because right was capped
        Assert.True(vocabulary.Capped);
    }

    // ── StatisticsCollector: Vocabulary Registration ──

    [Fact]
    public void StatisticsCollector_IntegerColumn_CollectsVocabulary()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("id", DataValue.FromInt32(1))));
        collector.AddRow(CreateRow(("id", DataValue.FromInt32(2))));
        collector.AddRow(CreateRow(("id", DataValue.FromInt32(3))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Contains("vocabulary", stats["id"].Results.Keys);
        VocabularyResult vocabulary = (VocabularyResult)stats["id"].Results["vocabulary"].Value!;
        Assert.Equal(3, vocabulary.SortedValues.Count);
        Assert.False(vocabulary.Capped);
    }

    [Fact]
    public void StatisticsCollector_StringColumn_CollectsVocabulary()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("name", DataValue.FromString("alice"))));
        collector.AddRow(CreateRow(("name", DataValue.FromString("bob"))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.Contains("vocabulary", stats["name"].Results.Keys);
    }

    [Fact]
    public void StatisticsCollector_FloatColumn_DoesNotCollectVocabulary()
    {
        StatisticsCollector collector = new();

        collector.AddRow(CreateRow(("value", DataValue.FromFloat32(1.5f))));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();

        Assert.DoesNotContain("vocabulary", stats["value"].Results.Keys);
    }

    // ── ManifestBuilder: Vocabulary Attachment ──

    [Fact]
    public void ManifestBuilder_IdentifierColumn_AttachesVocabulary()
    {
        // Simulate an integer identifier column: high NDV relative to row count.
        StatisticsCollector collector = new();
        int rowCount = 100;

        for (int i = 0; i < rowCount; i++)
        {
            collector.AddRow(CreateRow(("id", DataValue.FromInt32(i))));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Int32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount);

        FeatureManifest feature = manifest.Features.Single(f => f.Name == "id");
        Assert.Equal(ColumnRole.Identifier, feature.Role);
        Assert.NotNull(feature.Vocabulary);
        Assert.Equal(rowCount, feature.Vocabulary.Count);
    }

    [Fact]
    public void ManifestBuilder_ForeignKeyColumn_AttachesVocabulary()
    {
        // FK column: integer with moderate repetition (NDV < row count).
        StatisticsCollector collector = new();
        int rowCount = 5000;

        for (int i = 0; i < rowCount; i++)
        {
            // 1000 distinct values, each repeated ~5 times
            collector.AddRow(CreateRow(("user_id", DataValue.FromInt32(i % 1000))));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["user_id"] = DataKind.Int32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount);

        FeatureManifest feature = manifest.Features.Single(f => f.Name == "user_id");
        Assert.Equal(ColumnRole.ForeignKey, feature.Role);
        Assert.NotNull(feature.Vocabulary);
        Assert.Equal(1000, feature.Vocabulary.Count);
    }

    [Fact]
    public void ManifestBuilder_CategoricalColumn_DoesNotAttachVocabulary()
    {
        // Categorical column: very low NDV, strong repetition.
        StatisticsCollector collector = new();
        int rowCount = 1000;

        for (int i = 0; i < rowCount; i++)
        {
            collector.AddRow(CreateRow(("status", DataValue.FromString(i % 3 == 0 ? "active" : i % 3 == 1 ? "inactive" : "pending"))));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["status"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount);

        FeatureManifest feature = manifest.Features.Single(f => f.Name == "status");
        // Categorical columns should not get vocabulary (only Identifier/FK)
        Assert.Null(feature.Vocabulary);
    }

    [Fact]
    public void ManifestBuilder_VocabularySurvivesJsonRoundTrip_AsNull()
    {
        // Vocabulary is JsonIgnore — it should not appear in serialized output and
        // deserialized manifests should have null vocabulary.
        StatisticsCollector collector = new();
        int rowCount = 10;

        for (int i = 0; i < rowCount; i++)
        {
            collector.AddRow(CreateRow(("id", DataValue.FromInt32(i))));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Int32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, rowCount);

        string json = ManifestSerializer.Serialize(SourceManifest.Create("test", manifest));

        Assert.DoesNotContain("vocabulary", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── JoinEvidenceScorer: Exact Containment ──

    [Fact]
    public void ScoreEvidence_BothHaveVocabulary_ComputesExactJaccard()
    {
        NumericFeatureManifest left = MakeIdentifierFeature("order_id", 100,
            vocab: MakeVocabulary("1", "2", "3", "4", "5"));
        NumericFeatureManifest right = MakeIdentifierFeature("order_id", 100,
            vocab: MakeVocabulary("3", "4", "5", "6", "7"));

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("order_id", "order_id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, candidate, thresholds);

        // Intersection = {3, 4, 5} = 3, Union = {1..7} = 7
        // ExactJaccard = 3/7
        Assert.NotNull(evidence.ExactJaccard);
        Assert.Equal(3.0 / 7.0, evidence.ExactJaccard!.Value, precision: 5);
    }

    [Fact]
    public void ScoreEvidence_BothHaveVocabulary_ComputesBidirectionalContainment()
    {
        // Simulate FK→PK: FK {1,2,3} fully contained in PK {1,2,3,4,5}
        NumericFeatureManifest foreignKey = MakeIdentifierFeature("user_id", 3,
            vocab: MakeVocabulary("1", "2", "3"));
        NumericFeatureManifest primaryKey = MakeIdentifierFeature("user_id", 5,
            vocab: MakeVocabulary("1", "2", "3", "4", "5"));

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("user_id", "user_id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            foreignKey, 500, primaryKey, 1000, candidate, thresholds);

        // FK fully contained in PK: containment(left→right) = 3/3 = 1.0
        Assert.Equal(1.0, evidence.ContainmentLeftInRight!.Value, precision: 5);
        // PK partially referenced: containment(right→left) = 3/5 = 0.6
        Assert.Equal(0.6, evidence.ContainmentRightInLeft!.Value, precision: 5);
    }

    [Fact]
    public void ScoreEvidence_NoVocabulary_ContainmentIsNull()
    {
        NumericFeatureManifest left = MakeIdentifierFeature("id", 100);
        NumericFeatureManifest right = MakeIdentifierFeature("id", 100);

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("id", "id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, candidate, thresholds);

        Assert.Null(evidence.ExactJaccard);
        Assert.Null(evidence.ContainmentLeftInRight);
        Assert.Null(evidence.ContainmentRightInLeft);
    }

    [Fact]
    public void ScoreEvidence_OnlyOneHasVocabulary_ContainmentIsNull()
    {
        NumericFeatureManifest left = MakeIdentifierFeature("id", 100,
            vocab: MakeVocabulary("1", "2", "3"));
        NumericFeatureManifest right = MakeIdentifierFeature("id", 100);

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("id", "id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, candidate, thresholds);

        Assert.Null(evidence.ExactJaccard);
        Assert.Null(evidence.ContainmentLeftInRight);
    }

    [Fact]
    public void ScoreEvidence_ExactJaccard_UsedForCompositeConfidence()
    {
        // When exact Jaccard is available, it should influence the composite confidence
        // instead of TopK Jaccard. Verify that identical vocab produces higher confidence
        // than zero TopK.
        NumericFeatureManifest left = MakeIdentifierFeature("id", 100,
            topK: [], // empty TopK → TopK Jaccard = 0
            vocab: MakeVocabulary("1", "2", "3"));
        NumericFeatureManifest right = MakeIdentifierFeature("id", 100,
            topK: [], // empty TopK → TopK Jaccard = 0
            vocab: MakeVocabulary("1", "2", "3"));

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("id", "id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, candidate, thresholds);

        Assert.Equal(0.0, evidence.TopKJaccard);
        Assert.Equal(1.0, evidence.ExactJaccard!.Value, precision: 5);
        // Composite confidence should be elevated by exact Jaccard = 1.0
        Assert.True(evidence.CompositeConfidence > 0.5,
            $"Expected high confidence from exact Jaccard, got {evidence.CompositeConfidence}");
    }

    [Fact]
    public void ScoreEvidence_DisjointVocabulary_LowConfidence()
    {
        NumericFeatureManifest left = MakeIdentifierFeature("id", 3,
            topK: [new FrequencyEntry("1", 10)],
            vocab: MakeVocabulary("1", "2", "3"));
        NumericFeatureManifest right = MakeIdentifierFeature("id", 3,
            topK: [new FrequencyEntry("4", 10)],
            vocab: MakeVocabulary("4", "5", "6"));

        SchemaMatchingThresholds thresholds = new();
        ColumnMatchCandidate candidate = new("id", "id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, candidate, thresholds);

        Assert.Equal(0.0, evidence.ExactJaccard!.Value);
        Assert.Equal(0.0, evidence.ContainmentLeftInRight!.Value);
    }

    // ── Helpers ──

    private static ColumnVocabulary MakeVocabulary(params string[] values)
    {
        string[] sorted = (string[])values.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);
        return new ColumnVocabulary { Values = sorted };
    }

    private static NumericFeatureManifest MakeIdentifierFeature(
        string name,
        long estimatedDistinctCount,
        IReadOnlyList<FrequencyEntry>? topK = null,
        ColumnVocabulary? vocab = null)
    {
        NumericFeatureManifest feature = new()
        {
            Name = name,
            Kind = DataKind.Int32,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topK ?? [new FrequencyEntry("1", 100), new FrequencyEntry("2", 90)],
            Min = 1.0,
            Max = estimatedDistinctCount,
            Mean = estimatedDistinctCount / 2.0,
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
            Role = ColumnRole.Identifier,
        };

        if (vocab is not null)
        {
            feature.Vocabulary = vocab;
        }

        return feature;
    }

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];

        for (int i = 0; i < columns.Length; i++)
        {
            names[i] = columns[i].Name;
            values[i] = columns[i].Value;
        }

        return new Row(names, values);
    }
}
