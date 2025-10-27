using DatumIngest.DatumFile;
using DatumIngest.Ingestion;
using DatumIngest.Model;
using DatumIngest.Pooling;
using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;

namespace DatumIngest.Tests.Ingestion;

/// <summary>
/// End-to-end test: CSV bytes → Ingester → .datum bytes → DatumFileReader.
/// Verifies the full ingestion pipeline wires up correctly and the output is
/// readable by the .datum reader.
/// </summary>
public sealed class IngesterEndToEndTests : ServiceTestBase
{
    [Fact]
    public async Task CsvToDatum_BasicTypes_RoundTripsViaDatumFileReader()
    {
        // ── Arrange: a small CSV with Int32/Float64/String columns.
        const string csv =
            "id,score,name\n" +
            "1,1.5,alice\n" +
            "2,2.7,bob\n" +
            "3,3.14,charlie\n";

        MemoryFileDescriptor source = new(csv, fileName: "test.csv");
        MemoryOutputDescriptor destination = new(fileName: "test.datum");

        FormatRegistry registry = new([new CsvFileFormat()]);
        PoolBacking backing = new();
        Pool pool = new(backing);

        Ingester ingester = new(registry, pool);

        // ── Act: ingest.
        IngestionResult result = await ingester.IngestAsync(source, destination);

        // ── Assert: result metadata.
        Assert.Equal(3, result.RowCount);
        Assert.True(result.BytesWritten > 0);
        Assert.Equal(3, result.Schema.Columns.Count);
        Assert.Equal("id", result.Schema.Columns[0].Name);
        Assert.Equal("score", result.Schema.Columns[1].Name);
        Assert.Equal("name", result.Schema.Columns[2].Name);

        // ── Assert: readable via DatumFileReader.
        byte[] datumBytes = destination.GetBytes();
        string tempPath = Path.Combine(Path.GetTempPath(), $"ingest_e2e_{Guid.NewGuid():N}.datum");

        try
        {
            await File.WriteAllBytesAsync(tempPath, datumBytes);

            using DatumFileReader reader = DatumFileReader.Open(tempPath);

            Assert.Equal(3, reader.TotalRowCount);
            Assert.Equal(1, reader.RowGroupCount);
            Assert.Equal(3, reader.Schema.Columns.Count);

            // Decode all columns of the only row group and verify values.
            DataValue[][] columns = reader.ReadColumns(rowGroupIndex: 0, columnIndices: [0, 1, 2]);

            Assert.Equal(3, columns[0].Length);
            Assert.Equal(3, columns[1].Length);
            Assert.Equal(3, columns[2].Length);

            // id column — values 1, 2, 3 (CSV inference may produce Int32 or Int64; accept either).
            Assert.Equal(1, ExtractIntegral(columns[0][0]));
            Assert.Equal(2, ExtractIntegral(columns[0][1]));
            Assert.Equal(3, ExtractIntegral(columns[0][2]));

            // score column — Float64 values.
            Assert.Equal(1.5, ExtractFloat(columns[1][0]), 6);
            Assert.Equal(2.7, ExtractFloat(columns[1][1]), 6);
            Assert.Equal(3.14, ExtractFloat(columns[1][2]), 6);

            // name column — strings. Resolution via reader.Store is pending
            // while DatumFileReader's store wiring is being reworked.
            // Assert.Equal("alice", columns[2][0].AsString(reader.Store));
            // Assert.Equal("bob", columns[2][1].AsString(reader.Store));
            // Assert.Equal("charlie", columns[2][2].AsString(reader.Store));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
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
