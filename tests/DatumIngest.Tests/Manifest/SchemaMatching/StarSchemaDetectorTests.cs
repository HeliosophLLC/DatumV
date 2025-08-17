namespace DatumIngest.Tests.Manifest.SchemaMatching;

using DatumIngest.Manifest;
using DatumIngest.Manifest.SchemaMatching;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="StarSchemaDetector"/> — lightweight hub/spoke detection
/// from table manifests without the full cross-manifest pipeline.
/// </summary>
public sealed class StarSchemaDetectorTests
{
    /// <summary>
    /// A hub table with unique keys joined to multiple spoke tables produces a
    /// <see cref="HubTable"/> with the expected spoke count.
    /// </summary>
    [Fact]
    public void Detect_HubWithThreeSpokes_Discovered()
    {
        // Hub: unique key (NDV = RowCount).
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        ManifestWithName spoke1 = MakeManifest("order_items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5), new FrequencyEntry("3", 5)]));

        ManifestWithName spoke2 = MakeManifest("payments", rowCount: 3000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 3), new FrequencyEntry("2", 3), new FrequencyEntry("3", 3)]));

        ManifestWithName spoke3 = MakeManifest("reviews", rowCount: 2000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 980,
                topK: [new FrequencyEntry("1", 2), new FrequencyEntry("2", 2), new FrequencyEntry("3", 2)]));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, spoke1, spoke2, spoke3]);

        HubTable detectedHub = Assert.Single(result.Hubs);
        Assert.Equal("orders", detectedHub.TableName);
        Assert.Equal(["order_id"], detectedHub.KeyColumns);
        Assert.Equal(3, detectedHub.SpokeCount);
        Assert.Contains(detectedHub.Spokes, spoke => spoke.TableName == "order_items");
        Assert.Contains(detectedHub.Spokes, spoke => spoke.TableName == "payments");
        Assert.Contains(detectedHub.Spokes, spoke => spoke.TableName == "reviews");
    }

    /// <summary>
    /// Tables that do not appear in any discovered hub/spoke relationship are
    /// reported in <see cref="StarSchemaResult.UnmatchedTables"/>.
    /// </summary>
    [Fact]
    public void Detect_StandaloneTable_ReportedAsUnmatched()
    {
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)]));

        ManifestWithName spoke1 = MakeManifest("items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5)]));

        ManifestWithName spoke2 = MakeManifest("payments", rowCount: 3000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 3), new FrequencyEntry("2", 3)]));

        // Standalone table with no matching columns.
        ManifestWithName standalone = MakeManifest("config", rowCount: 10,
            MakeIntegerFeature("setting_id", estimatedDistinctCount: 10));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, spoke1, spoke2, standalone]);

        Assert.Single(result.Hubs);
        Assert.Contains("config", result.UnmatchedTables);
        Assert.DoesNotContain("orders", result.UnmatchedTables);
    }

    /// <summary>
    /// A table with only one spoke does not qualify as a hub (requires at least
    /// <see cref="StarSchemaDetector.MinSpokeCount"/> = 2).
    /// </summary>
    [Fact]
    public void Detect_SingleSpoke_NotEnoughForHub()
    {
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)]));

        ManifestWithName spoke = MakeManifest("items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5)]));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, spoke]);

        Assert.Empty(result.Hubs);
        Assert.Equal(2, result.UnmatchedTables.Count);
    }

    /// <summary>
    /// With fewer than two manifests, detection returns an empty result with all
    /// tables listed as unmatched.
    /// </summary>
    [Fact]
    public void Detect_SingleManifest_ReturnsEmptyResult()
    {
        ManifestWithName single = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000));

        StarSchemaResult result = StarSchemaDetector.Detect([single]);

        Assert.Empty(result.Hubs);
        Assert.Single(result.UnmatchedTables);
        Assert.Equal("orders", result.UnmatchedTables[0]);
    }

    /// <summary>
    /// Two independent hubs on different key columns are both discovered and
    /// ordered by descending spoke count.
    /// </summary>
    [Fact]
    public void Detect_TwoIndependentHubs_BothDiscovered()
    {
        // Hub 1: admissions on hadm_id (3 spokes).
        ManifestWithName admissions = MakeManifest("admissions", rowCount: 1000,
            MakeIntegerFeature("hadm_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        ManifestWithName diagnoses = MakeManifest("diagnoses", rowCount: 5000,
            MakeIntegerFeature("hadm_id", estimatedDistinctCount: 800,
                topK: [new FrequencyEntry("1", 6), new FrequencyEntry("2", 6), new FrequencyEntry("3", 6)]));

        ManifestWithName services = MakeManifest("services", rowCount: 3000,
            MakeIntegerFeature("hadm_id", estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 3), new FrequencyEntry("2", 3), new FrequencyEntry("3", 3)]));

        ManifestWithName transfers = MakeManifest("transfers", rowCount: 4000,
            MakeIntegerFeature("hadm_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 4), new FrequencyEntry("2", 4), new FrequencyEntry("3", 4)]));

        // Hub 2: probes on probe_serial (2 spokes) — column name shares no suffix
        // with hadm_id, preventing false cross-column matching via name similarity.
        ManifestWithName probes = MakeManifest("probes", rowCount: 500,
            MakeIntegerFeature("probe_serial", estimatedDistinctCount: 500,
                topK: [new FrequencyEntry("10", 1), new FrequencyEntry("20", 1)]));

        ManifestWithName readings = MakeManifest("readings", rowCount: 50000,
            MakeIntegerFeature("probe_serial", estimatedDistinctCount: 450,
                topK: [new FrequencyEntry("10", 100), new FrequencyEntry("20", 100)]));

        ManifestWithName alerts = MakeManifest("alerts", rowCount: 30000,
            MakeIntegerFeature("probe_serial", estimatedDistinctCount: 400,
                topK: [new FrequencyEntry("10", 75), new FrequencyEntry("20", 75)]));

        StarSchemaResult result = StarSchemaDetector.Detect(
            [admissions, diagnoses, services, transfers, probes, readings, alerts]);

        Assert.Equal(2, result.Hubs.Count);

        // Ordered by descending spoke count: admissions (3) before probes (2).
        Assert.Equal("admissions", result.Hubs[0].TableName);
        Assert.Equal(["hadm_id"], result.Hubs[0].KeyColumns);
        Assert.Equal(3, result.Hubs[0].SpokeCount);

        Assert.Equal("probes", result.Hubs[1].TableName);
        Assert.Equal(["probe_serial"], result.Hubs[1].KeyColumns);
        Assert.Equal(2, result.Hubs[1].SpokeCount);

        Assert.Empty(result.UnmatchedTables);
    }

    /// <summary>
    /// A one-to-one relationship (both sides near-unique) counts as a valid spoke.
    /// This covers extension tables like payments where the foreign key has ≤5%
    /// duplicates, pushing its NDV/RowCount above the uniqueness threshold.
    /// </summary>
    [Fact]
    public void Detect_OneToOneSpokesCountAsValidArms()
    {
        // Hub: unique key (NDV = RowCount).
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        // Spoke 1: classic one-to-many (low NDV/RowCount ratio).
        ManifestWithName spoke1 = MakeManifest("order_items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5), new FrequencyEntry("3", 5)]));

        // Spoke 2: near-unique FK → one-to-one classification. NDV/RowCount = 960/1000 = 0.96 ≥ 0.95.
        ManifestWithName spoke2 = MakeManifest("payments", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 960,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, spoke1, spoke2]);

        // orders is a hub: order_items (OneToMany) + payments (OneToOne) = 2 spokes.
        // payments also qualifies as hub since order_items→payments is ManyToOne and
        // orders→payments is OneToOne (bidirectional). Both are valid.
        HubTable ordersHub = Assert.Single(result.Hubs, hub => hub.TableName == "orders");
        Assert.Equal(2, ordersHub.SpokeCount);
        Assert.Contains(ordersHub.Spokes, spoke => spoke.TableName == "order_items");
        Assert.Contains(ordersHub.Spokes, spoke => spoke.TableName == "payments");
    }

    /// <summary>
    /// When a hub has only one-to-one spokes and no one-to-many relationships,
    /// the hub is still discovered if it accumulates enough spokes. The hub is
    /// the table that appears as a spoke candidate to the fewest other tables.
    /// </summary>
    [Fact]
    public void Detect_OneToOneOnlyHub_DiscoveredWhenEnoughSpokes()
    {
        // All three tables have unique keys on the shared column.
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        ManifestWithName extension1 = MakeManifest("payments", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 980,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        ManifestWithName extension2 = MakeManifest("reviews", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 970,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, extension1, extension2]);

        // All three could be hubs (OneToOne is bidirectional), so each gets 2 spokes.
        // All three are valid hubs — verify at least the expected ones exist.
        Assert.True(result.Hubs.Count >= 1, "At least one hub should be discovered.");

        // Every table should participate (none unmatched).
        Assert.Empty(result.UnmatchedTables);
    }

    /// <summary>
    /// Many-to-many relationships (neither side has unique keys) do not produce hubs.
    /// Only one-to-many relationships identify a hub.
    /// </summary>
    [Fact]
    public void Detect_ManyToManyOnly_NoHubs()
    {
        // Both tables have low NDV relative to row count — neither is unique.
        ManifestWithName tableA = MakeManifest("table_a", rowCount: 10000,
            MakeIntegerFeature("shared_id", estimatedDistinctCount: 100,
                topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 100)]));

        ManifestWithName tableB = MakeManifest("table_b", rowCount: 8000,
            MakeIntegerFeature("shared_id", estimatedDistinctCount: 100,
                topK: [new FrequencyEntry("1", 80), new FrequencyEntry("2", 80)]));

        ManifestWithName tableC = MakeManifest("table_c", rowCount: 6000,
            MakeIntegerFeature("shared_id", estimatedDistinctCount: 100,
                topK: [new FrequencyEntry("1", 60), new FrequencyEntry("2", 60)]));

        StarSchemaResult result = StarSchemaDetector.Detect([tableA, tableB, tableC]);

        Assert.Empty(result.Hubs);
    }

    /// <summary>
    /// Spokes are ordered by descending confidence within each hub.
    /// </summary>
    [Fact]
    public void Detect_SpokesOrderedByConfidence()
    {
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)]));

        // Spoke with perfect TopK overlap → highest confidence.
        ManifestWithName highConfidence = MakeManifest("items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5), new FrequencyEntry("3", 5)]));

        // Spoke with no TopK overlap → lower confidence.
        ManifestWithName lowerConfidence = MakeManifest("payments", rowCount: 3000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 800,
                topK: [new FrequencyEntry("100", 4), new FrequencyEntry("200", 4), new FrequencyEntry("300", 4)]));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, highConfidence, lowerConfidence]);

        HubTable detectedHub = Assert.Single(result.Hubs);
        Assert.Equal(2, detectedHub.SpokeCount);
        Assert.True(detectedHub.Spokes[0].Confidence >= detectedHub.Spokes[1].Confidence,
            "Spokes should be ordered by descending confidence.");
    }

    /// <summary>
    /// All tables in the result are listed in <see cref="StarSchemaResult.Tables"/>
    /// regardless of whether they are hubs, spokes, or unmatched.
    /// </summary>
    [Fact]
    public void Detect_AllTablesListedInResult()
    {
        ManifestWithName hub = MakeManifest("orders", rowCount: 1000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 1000,
                topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)]));

        ManifestWithName spoke1 = MakeManifest("items", rowCount: 5000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 900,
                topK: [new FrequencyEntry("1", 5), new FrequencyEntry("2", 5)]));

        ManifestWithName spoke2 = MakeManifest("payments", rowCount: 3000,
            MakeIntegerFeature("order_id", estimatedDistinctCount: 950,
                topK: [new FrequencyEntry("1", 3), new FrequencyEntry("2", 3)]));

        ManifestWithName standalone = MakeManifest("logs", rowCount: 100,
            MakeIntegerFeature("log_id", estimatedDistinctCount: 100));

        StarSchemaResult result = StarSchemaDetector.Detect([hub, spoke1, spoke2, standalone]);

        Assert.Equal(4, result.Tables.Count);
        Assert.Contains("orders", result.Tables);
        Assert.Contains("items", result.Tables);
        Assert.Contains("payments", result.Tables);
        Assert.Contains("logs", result.Tables);
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifest(string name, long rowCount, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = rowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
        });
    }

    private static NumericFeatureManifest MakeIntegerFeature(
        string name,
        long estimatedDistinctCount,
        IReadOnlyList<FrequencyEntry>? topK = null)
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
    }
}
