using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Arrow;
using ArrowSchema = Apache.Arrow.Schema;

namespace Heliosoph.DatumV.Tests.Serialization.Arrow;

/// <summary>
/// Unit tests for <see cref="ArrowFileFormat.CanHandle"/> covering the
/// extension-based and magic-byte-based detection rules. The magic-byte
/// path is critical for Arrow shards whose extensions get rewritten by
/// pipeline tooling (HF cache files, Polars cache writes commonly drop
/// the <c>.arrow</c> suffix).
/// </summary>
public sealed class ArrowFileFormatTests : IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"arrow-format-{Guid.NewGuid():N}");

    public ArrowFileFormatTests()
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
    [InlineData("data.arrow")]
    [InlineData("DATA.ARROW")]
    [InlineData("data.feather")]
    public async Task CanHandle_MatchesArrowExtensions(string fileName)
    {
        string path = TempPath(fileName);
        await WriteTinyArrow(path);

        ArrowFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<ArrowDeserializer>(deserializer);
    }

    [Fact]
    public async Task CanHandle_MatchesMagicBytes_OnUnknownExtension()
    {
        // Real Arrow file mislabelled as .bin — ARROW1\0\0 magic at offset 0
        // still picks it up. Common with HF cache / Polars cache outputs.
        string path = TempPath("shard-00000.bin");
        await WriteTinyArrow(path);

        ArrowFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.True(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.IsType<ArrowDeserializer>(deserializer);
    }

    [Fact]
    public void CanHandle_RejectsNonArrowBytes()
    {
        string path = TempPath("not.bin");
        File.WriteAllBytes(path, "this is not arrow"u8.ToArray());

        ArrowFileFormat format = new();
        using FileFormatDescriptor descriptor = new(path);

        Assert.False(format.CanHandle(descriptor, out IFormatDeserializer? deserializer));
        Assert.Null(deserializer);
    }

    private static async Task WriteTinyArrow(string path)
    {
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Build();
        var ids = new Int32Array.Builder().AppendRange([1, 2, 3]).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { ids }, length: 3);
        await using Stream stream = File.Create(path);
        using ArrowFileWriter writer = new(stream, schema);
        await writer.WriteRecordBatchAsync(batch);
        await writer.WriteEndAsync();
    }
}
