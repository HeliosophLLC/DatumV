using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.DatumFile.V2.Decoding;
using DatumIngest.Ingestion;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Ingestion;

/// <summary>
/// End-to-end test for the v2 ingest path: CSV bytes →
/// <see cref="Ingester.IngestV2Async(FileFormatDescriptor, OutputDescriptor, CancellationToken)"/>
/// → <c>.datum</c> bytes (v2 format) → <see cref="DatumFileReaderV2"/>.
/// Verifies the v2 writer is correctly wired into the ingestion pipeline
/// and that the resulting file is structurally valid and readable.
/// </summary>
public sealed class IngesterV2EndToEndTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ingest_v2_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* swallowed: mmap views may still be releasing */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CsvToV2Datum_BasicTypes_RoundTripsViaDatumFileReaderV2()
    {
        const string csv =
            "id,score,name\n" +
            "1,1.5,alice\n" +
            "2,2.7,bob\n" +
            "3,3.14,charlie\n";

        string datumPath = Path.Combine(_tempDir, "basic.datum");
        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        OutputDescriptor destination = new(datumPath);

        FormatRegistry registry = new([new CsvFileFormat()]);
        Pool pool = new(new PoolBacking());
        Ingester ingester = new(registry, pool);

        IngestionResult result = await ingester.IngestV2Async(source, destination);

        Assert.Equal(3, result.RowCount);
        Assert.True(result.BytesWritten > 0);
        Assert.Equal(3, result.Schema.Columns.Count);
        Assert.Equal("id", result.Schema.Columns[0].Name);
        Assert.Equal("score", result.Schema.Columns[1].Name);
        Assert.Equal("name", result.Schema.Columns[2].Name);

        // Read back via the v2 reader.
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(3L, reader.TotalRowCount);
        Assert.Equal(3, reader.Footer.Columns.Count);

        // id column — typically Int32 from CSV inference.
        IPageDecoderV2 idDec = reader.OpenPageDecoder(0, 0);
        Assert.Equal(1, ExtractIntegral(idDec.ReadValue(0)));
        Assert.Equal(2, ExtractIntegral(idDec.ReadValue(1)));
        Assert.Equal(3, ExtractIntegral(idDec.ReadValue(2)));

        // score column — Float64.
        IPageDecoderV2 scoreDec = reader.OpenPageDecoder(1, 0);
        Assert.Equal(1.5, ExtractFloat(scoreDec.ReadValue(0)), 6);
        Assert.Equal(2.7, ExtractFloat(scoreDec.ReadValue(1)), 6);
        Assert.Equal(3.14, ExtractFloat(scoreDec.ReadValue(2)), 6);

        // name column — String, all under 16 bytes so all inline.
        IPageDecoderV2 nameDec = reader.OpenPageDecoder(2, 0);
        Assert.Equal("alice", nameDec.ReadValue(0).AsString());
        Assert.Equal("bob", nameDec.ReadValue(1).AsString());
        Assert.Equal("charlie", nameDec.ReadValue(2).AsString());
    }

    [Fact]
    public async Task CsvToV2Datum_LongStrings_GoToSidecar()
    {
        // One row with a string > 15 UTF-8 bytes. The variable-slot
        // encoder should drop it into the sidecar, leaving a pointer in
        // the slot. The companion .datum-blob file should exist.
        const string csv =
            "id,name\n" +
            "1,this string is definitely longer than fifteen bytes\n" +
            "2,short\n";

        string datumPath = Path.Combine(_tempDir, "withsidecar.datum");
        string sidecarPath = Path.ChangeExtension(datumPath, ".datum-blob");
        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        OutputDescriptor destination = new(datumPath);

        FormatRegistry registry = new([new CsvFileFormat()]);
        Pool pool = new(new PoolBacking());
        Ingester ingester = new(registry, pool);

        IngestionResult result = await ingester.IngestV2Async(source, destination);

        Assert.Equal(2, result.RowCount);
        Assert.True(File.Exists(sidecarPath), "Long string should have materialized the sidecar.");

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        IPageDecoderV2 nameDec = reader.OpenPageDecoder(1, 0);

        DataValue longRow = nameDec.ReadValue(0);
        Assert.True(longRow.IsInSidecar, "Long string row should decode to a sidecar pointer.");

        DataValue shortRow = nameDec.ReadValue(1);
        Assert.True(shortRow.IsInline, "Short string row should decode inline.");
        Assert.Equal("short", shortRow.AsString());
    }

    [Fact]
    public async Task CsvToV2Datum_ScanThroughTableProvider_ReturnsAllRows()
    {
        // Verifies the version-dispatch in DatumFileTableProvider.Open and
        // the V2 provider's ScanAsync — full ingest → catalog Add → scan
        // round-trip with a mix of inline and sidecar-bound payloads.
        const string csv =
            "id,name\n" +
            "1,short\n" +
            "2,this string is definitely longer than fifteen bytes\n" +
            "3,medium one\n";

        string datumPath = Path.Combine(_tempDir, "scan.datum");
        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        OutputDescriptor destination = new(datumPath);

        FormatRegistry registry = new([new CsvFileFormat()]);
        Pool pool = new(new PoolBacking());
        Ingester ingester = new(registry, pool);
        await ingester.IngestV2Async(source, destination);

        // Open via TableCatalog to exercise the version-dispatch factory
        // and sidecar registration path.
        using TableCatalog catalog = new(pool);
        ITableProvider provider = catalog.Add(new TableDescriptor("scan", datumPath));
        Assert.IsType<DatumFileTableProviderV2>(provider);
        Assert.Equal(3L, provider.GetRowCount());
        Assert.Equal(2, provider.GetSchema().Columns.Count);

        // Scan with no projection — should read every column.
        List<RowBatch> batches = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: default))
        {
            batches.Add(batch);
        }
        Assert.NotEmpty(batches);

        int totalRows = batches.Sum(b => b.Count);
        Assert.Equal(3, totalRows);

        // Verify the long string round-trips through the sidecar path.
        // Walk the batches and find the row whose id == 2; resolve the
        // sidecar-bound string through the catalog's registry directly
        // (DataValue.AsString doesn't yet have a sidecar-aware overload —
        // a small follow-up).
        bool foundLongRow = false;
        foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                int id = (int)ExtractIntegral(row[0]);
                DataValue name = row[1];
                if (id == 2)
                {
                    Assert.True(name.IsInSidecar, "Long string should be sidecar-backed.");
                    var blob = catalog.SidecarRegistry.Resolve(name.SidecarStoreId);
                    Assert.NotNull(blob);
                    string resolved = System.Text.Encoding.UTF8.GetString(
                        blob!.Read(name.SidecarOffset, name.SidecarLength));
                    Assert.Equal("this string is definitely longer than fifteen bytes", resolved);
                    foundLongRow = true;
                }
            }
            pool.ReturnRowBatch(batch);
        }
        Assert.True(foundLongRow, "Did not find row with id=2 during scan.");
    }

    private static long ExtractIntegral(DataValue value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8(),
        DataKind.Int16 => value.AsInt16(),
        DataKind.Int32 => value.AsInt32(),
        DataKind.Int64 => value.AsInt64(),
        DataKind.UInt8 => value.AsUInt8(),
        DataKind.UInt16 => value.AsUInt16(),
        DataKind.UInt32 => value.AsUInt32(),
        DataKind.UInt64 => (long)value.AsUInt64(),
        DataKind.Float32 => (long)value.AsFloat32(),
        DataKind.Float64 => (long)value.AsFloat64(),
        _ => throw new InvalidOperationException($"Unexpected numeric kind: {value.Kind}")
    };

    private static double ExtractFloat(DataValue value) => value.Kind switch
    {
        DataKind.Float32 => value.AsFloat32(),
        DataKind.Float64 => value.AsFloat64(),
        DataKind.Int32 => value.AsInt32(),
        DataKind.Int64 => value.AsInt64(),
        _ => throw new InvalidOperationException($"Unexpected numeric kind: {value.Kind}")
    };
}
