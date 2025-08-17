namespace DatumIngest.Tests.Output;

using DatumIngest.Model;
using DatumIngest.Output;
using DatumIngest.Output.Writers;

/// <summary>
/// Tests for <see cref="DatumOutputWriter"/> covering basic write behaviour,
/// null values, multi-row-group flushing, and <see cref="OutputSummary"/> accuracy.
/// Round-trip correctness (write then read back) is covered by
/// <see cref="DatumIngest.Tests.Catalog.DatumFileTableProviderTests"/>.
/// </summary>
public sealed class DatumOutputWriterTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"datum_writer_{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ──────────────────── Basic write ────────────────────

    [Fact]
    public async Task FinalizeAsync_EmptyDataset_CreatesNonEmptyFile()
    {
        string path = Path.Combine(_tempDirectory, "empty.datum");
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, false)]);

        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(0, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0, "File should contain header + footer even when empty.");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FinalizeAsync_ScalarAndString_ReturnsCorrectSummary()
    {
        string path = Path.Combine(_tempDirectory, "scalar_string.datum");
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, false),
            new ColumnInfo("label", DataKind.String, false)
        ]);

        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("id", DataValue.FromFloat32(1f)), ("label", DataValue.FromString("alpha"))));
        await writer.WriteRowAsync(CreateRow(("id", DataValue.FromFloat32(2f)), ("label", DataValue.FromString("beta"))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.True(summary.BytesWritten > 0);
        Assert.Contains(path, summary.FilesCreated);
    }

    [Fact]
    public async Task FinalizeAsync_NullableColumns_DoesNotThrow()
    {
        string path = Path.Combine(_tempDirectory, "nulls.datum");
        Schema schema = new([
            new ColumnInfo("name", DataKind.String, nullable: true),
            new ColumnInfo("score", DataKind.Float32, nullable: true)
        ]);

        await using DatumOutputWriter writer = new(path);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(
            ("name", DataValue.Null(DataKind.String)),
            ("score", DataValue.FromFloat32(99f))));
        await writer.WriteRowAsync(CreateRow(
            ("name", DataValue.FromString("Alice")),
            ("score", DataValue.Null(DataKind.Float32))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(2, summary.RowsWritten);
        Assert.True(File.Exists(path));
    }

    // ──────────────────── Stream constructor ────────────────────

    [Fact]
    public async Task StreamConstructor_WritesToProvidedStream()
    {
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, false)]);

        using MemoryStream stream = new();
        await using DatumOutputWriter writer = new(stream);
        await writer.InitializeAsync(schema);
        await writer.WriteRowAsync(CreateRow(("value", DataValue.FromFloat32(3.14f))));
        OutputSummary summary = await writer.FinalizeAsync();

        Assert.Equal(1, summary.RowsWritten);
        Assert.True(stream.Length > 0);
        // No file path recorded for stream-backed writers.
        Assert.Empty(summary.FilesCreated);
    }

    // ──────────────────── Guard conditions ────────────────────

    [Fact]
    public async Task WriteRowAsync_BeforeInitialize_Throws()
    {
        string path = Path.Combine(_tempDirectory, "uninit.datum");
        await using DatumOutputWriter writer = new(path);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteRowAsync(CreateRow(("x", DataValue.FromFloat32(1f)))));
    }

    [Fact]
    public async Task FinalizeAsync_BeforeInitialize_Throws()
    {
        string path = Path.Combine(_tempDirectory, "uninit2.datum");
        await using DatumOutputWriter writer = new(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FinalizeAsync());
    }

    // ──────────────────── Helpers ────────────────────

    private static Row CreateRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = new string[columns.Length];
        DataValue[] values = new DataValue[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            names[index] = columns[index].Name;
            values[index] = columns[index].Value;
        }
        return new Row(names, values);
    }
}
