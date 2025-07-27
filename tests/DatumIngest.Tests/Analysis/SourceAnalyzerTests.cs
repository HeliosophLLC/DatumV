using DatumIngest.Analysis;
using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Analysis;

/// <summary>
/// Tests for <see cref="SourceAnalyzer"/> single-pass analysis that produces
/// both a <see cref="SourceIndexSet"/> and a <see cref="SourceManifest"/>.
/// </summary>
public sealed class SourceAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_SingleTable_ProducesBothArtifacts()
    {
        Row[] rows =
        [
            MakeRow(("id", DataValue.FromScalar(1.0f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2.0f)), ("name", DataValue.FromString("bob"))),
        ];

        InMemoryTableProvider provider = new(rows);
        TableDescriptor descriptor = CreateDescriptor("orders");
        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(descriptor, provider)], sourceStream: null, CancellationToken.None);

        // Index
        Assert.Single(result.IndexSet.Tables);
        Assert.True(result.IndexSet.Tables.ContainsKey("orders"));
        Assert.Equal(2, result.IndexSet.Tables["orders"].Schema.TotalRowCount);

        // Manifest
        Assert.Single(result.Manifest.Tables);
        Assert.True(result.Manifest.Tables.ContainsKey("orders"));
        Assert.Equal(2, result.Manifest.Tables["orders"].RowCount);
    }

    [Fact]
    public async Task AnalyzeAsync_MultipleTables_ProducesAllEntries()
    {
        Row[] ordersRows =
        [
            MakeRow(("id", DataValue.FromScalar(1.0f)), ("total", DataValue.FromScalar(99.0f))),
            MakeRow(("id", DataValue.FromScalar(2.0f)), ("total", DataValue.FromScalar(42.0f))),
        ];

        Row[] itemsRows =
        [
            MakeRow(("orderId", DataValue.FromScalar(1.0f)), ("product", DataValue.FromString("widget"))),
        ];

        InMemoryTableProvider ordersProvider = new(ordersRows);
        InMemoryTableProvider itemsProvider = new(itemsRows);
        TableDescriptor ordersDescriptor = CreateDescriptor("data.orders");
        TableDescriptor itemsDescriptor = CreateDescriptor("data.items");
        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(ordersDescriptor, ordersProvider), (itemsDescriptor, itemsProvider)],
            sourceStream: null, CancellationToken.None);

        // Index
        Assert.Equal(2, result.IndexSet.Tables.Count);
        Assert.Equal(2, result.IndexSet.Tables["data.orders"].Schema.TotalRowCount);
        Assert.Equal(1, result.IndexSet.Tables["data.items"].Schema.TotalRowCount);

        // Manifest
        Assert.Equal(2, result.Manifest.Tables.Count);
        Assert.Equal(2, result.Manifest.Tables["data.orders"].RowCount);
        Assert.Equal(1, result.Manifest.Tables["data.items"].RowCount);
    }

    [Fact]
    public async Task AnalyzeAsync_SharesFingerprint_AcrossAllTables()
    {
        Row[] rows = [MakeRow(("x", DataValue.FromScalar(1.0f)))];
        SourceFingerprint fingerprint = new(123, new byte[] { 1, 2, 3 });
        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(CreateDescriptor("a"), new InMemoryTableProvider(rows)),
             (CreateDescriptor("b"), new InMemoryTableProvider(rows))],
            sourceStream: null, fingerprint, CancellationToken.None);

        Assert.Equal(fingerprint, result.IndexSet.Fingerprint);
        Assert.Equal(fingerprint, result.IndexSet.Tables["a"].Fingerprint);
        Assert.Equal(fingerprint, result.IndexSet.Tables["b"].Fingerprint);
    }

    [Fact]
    public async Task AnalyzeAsync_ManifestContainsFeatures()
    {
        Row[] rows =
        [
            MakeRow(("score", DataValue.FromScalar(1.0f)), ("label", DataValue.FromString("cat"))),
            MakeRow(("score", DataValue.FromScalar(2.0f)), ("label", DataValue.FromString("dog"))),
            MakeRow(("score", DataValue.FromScalar(3.0f)), ("label", DataValue.FromString("cat"))),
        ];

        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(CreateDescriptor("train"), new InMemoryTableProvider(rows))],
            sourceStream: null, CancellationToken.None);

        QueryResultsManifest manifest = result.Manifest.Tables["train"];
        Assert.Equal(3, manifest.RowCount);
        Assert.Equal(2, manifest.Features.Count);

        List<string> featureNames = manifest.Features.Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Contains("label", featureNames);
        Assert.Contains("score", featureNames);
    }

    [Fact]
    public async Task AnalyzeAsync_WithInteractions_CollectsInteractions()
    {
        Row[] rows =
        [
            MakeRow(("x", DataValue.FromScalar(1.0f)), ("y", DataValue.FromScalar(10.0f))),
            MakeRow(("x", DataValue.FromScalar(2.0f)), ("y", DataValue.FromScalar(20.0f))),
            MakeRow(("x", DataValue.FromScalar(3.0f)), ("y", DataValue.FromScalar(30.0f))),
        ];

        SourceAnalyzer analyzer = new(chunkSize: 100, withInteractions: true);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(CreateDescriptor("correlated"), new InMemoryTableProvider(rows))],
            sourceStream: null, CancellationToken.None);

        QueryResultsManifest manifest = result.Manifest.Tables["correlated"];
        Assert.NotNull(manifest.Interactions);
        Assert.True(manifest.Interactions.Count > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_IndexRoundTrips_ThroughWriterReader()
    {
        Row[] rows =
        [
            MakeRow(("value", DataValue.FromScalar(1.0f))),
            MakeRow(("value", DataValue.FromScalar(2.0f))),
        ];

        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(CreateDescriptor("test"), new InMemoryTableProvider(rows))],
            sourceStream: null, CancellationToken.None);

        using MemoryStream stream = new();
        IndexWriter writer = new();
        writer.Write(result.IndexSet, stream);

        stream.Position = 0;
        IndexReader reader = new();
        SourceIndexSet restored = reader.Read(stream);

        Assert.Equal(result.IndexSet.Tables.Count, restored.Tables.Count);
        Assert.True(restored.Tables.ContainsKey("test"));
        Assert.Equal(2, restored.Tables["test"].Schema.TotalRowCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ManifestRoundTrips_ThroughSerializer()
    {
        Row[] rows =
        [
            MakeRow(("score", DataValue.FromScalar(42.0f))),
        ];

        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(
            [(CreateDescriptor("data"), new InMemoryTableProvider(rows))],
            sourceStream: null, CancellationToken.None);

        string json = ManifestSerializer.Serialize(result.Manifest);
        SourceManifest? restored = ManifestSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.True(restored.Tables.ContainsKey("data"));
        Assert.Equal(1, restored.Tables["data"].RowCount);
    }

    // ───────────── Catalog overload ─────────────

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    /// <summary>
    /// Verifies that the catalog overload resolves tables and produces analysis results.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WithCatalog_ProducesResultsForAllTables()
    {
        TableCatalog catalog = new();
        catalog.Register("data", FixturePath("simple.csv"));

        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(catalog, CancellationToken.None);

        Assert.True(result.IndexSet.Tables.ContainsKey("data"));
        Assert.True(result.Manifest.Tables.ContainsKey("data"));
        Assert.True(result.Manifest.Tables["data"].RowCount > 0);
    }

    /// <summary>
    /// Verifies that the catalog overload handles multi-table expansion from JSON.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WithCatalog_MultiTableJson_AnalyzesAllSubTables()
    {
        TableCatalog catalog = new();
        await catalog.RegisterAsync("data", FixturePath("root_object.json"), CancellationToken.None);

        SourceAnalyzer analyzer = new(chunkSize: 100);

        SourceAnalysisResult result = await analyzer.AnalyzeAsync(catalog, CancellationToken.None);

        Assert.True(result.IndexSet.Tables.ContainsKey("data.licenses"));
        Assert.True(result.IndexSet.Tables.ContainsKey("data.captions"));
        Assert.True(result.Manifest.Tables.ContainsKey("data.licenses"));
        Assert.True(result.Manifest.Tables.ContainsKey("data.captions"));
    }

    // ───────────── Helpers ─────────────

    private static TableDescriptor CreateDescriptor(string name)
    {
        return new TableDescriptor("test", name, $"{name}.test", new Dictionary<string, string>());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    /// <summary>
    /// Simple in-memory provider for testing. Yields pre-built rows.
    /// </summary>
    internal sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = new();

            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
