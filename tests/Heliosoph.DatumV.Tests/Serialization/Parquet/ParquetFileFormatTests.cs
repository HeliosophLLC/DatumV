using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Tests.Serialization.Parquet;

/// <summary>
/// Unit tests for <see cref="ParquetFileFormat.CanHandle"/> covering the
/// extension-based and magic-byte-based detection rules. The magic-byte
/// path is critical for Parquet shards whose extensions get rewritten
/// by ingest pipelines (Spark / dlt commonly emit <c>part-00000.snappy</c>
/// style filenames without the <c>.parquet</c> suffix).
/// </summary>
public sealed class ParquetFileFormatTests : IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"parquet-format-{Guid.NewGuid():N}");

    public ParquetFileFormatTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string TempPath(string name) => Path.Combine(_scratchDir, name);

    [Theory]
    [InlineData("data.parquet")]
    [InlineData("DATA.PARQUET")]
    [InlineData("data.pq")]
    public async Task CanHandle_MatchesParquetExtensions(string fileName)
    {
        string path = TempPath(fileName);
        await WriteTinyParquet(path);

        ParquetFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<ParquetDeserializer>(deserializer);
    }

    [Fact]
    public async Task CanHandle_MatchesMagicBytes_OnUnknownExtension()
    {
        // Real Parquet file mislabelled as .dat — PAR1 magic at offset 0
        // still picks it up. Common with Spark / dlt outputs.
        string path = TempPath("part-00000.dat");
        await WriteTinyParquet(path);

        ParquetFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<ParquetDeserializer>(deserializer);
    }

    [Fact]
    public void CanHandle_RejectsNonParquetBytes()
    {
        string path = TempPath("not.dat");
        File.WriteAllBytes(path, "this is not parquet"u8.ToArray());

        ParquetFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.False(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.Null(deserializer);
    }

    private static async Task WriteTinyParquet(string path)
    {
        var idField = new DataField<int>("id");
        var schema = new ParquetSchema(idField);

        await using Stream writeStream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        await rg.WriteColumnAsync(new DataColumn(idField, new int[] { 1, 2, 3 }));
    }
}
