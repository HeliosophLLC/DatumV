using System.IO.Compression;
using DatumQuery.Catalog;
using DatumQuery.Catalog.Providers;
using DatumQuery.Model;

namespace DatumQuery.Tests.Catalog;

public sealed class ZipTableProviderTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _zipPath;

    public ZipTableProviderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DatumTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _zipPath = Path.Combine(_tempDirectory, "test.zip");

        using FileStream stream = File.Create(_zipPath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);

        ZipArchiveEntry entry1 = archive.CreateEntry("hello.txt");
        using (StreamWriter writer1 = new(entry1.Open()))
        {
            writer1.Write("Hello, World!");
        }

        ZipArchiveEntry entry2 = archive.CreateEntry("data/nested.bin");
        using (Stream entryStream2 = entry2.Open())
        {
            entryStream2.Write(new byte[] { 1, 2, 3, 4, 5 });
        }

        ZipArchiveEntry entry3 = archive.CreateEntry("empty.txt");
        // Leave empty — zero-length file
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private TableDescriptor Descriptor()
    {
        return new TableDescriptor("zip", "test", _zipPath, new Dictionary<string, string>());
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }
        return rows;
    }

    // ───────────────────── Schema ─────────────────────

    [Fact]
    public async Task GetSchema_ReturnsFileNameAndFileBytes()
    {
        ZipTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor(), CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("file_name", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("file_bytes", schema.Columns[1].Name);
        Assert.Equal(DataKind.UInt8Array, schema.Columns[1].Kind);
    }

    // ───────────────────── Row reading ─────────────────────

    [Fact]
    public async Task Open_ReadsAllEntries()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_FileNamesAreCorrect()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        List<string> names = rows.Select(row => row["file_name"].AsString()).OrderBy(name => name).ToList();
        Assert.Contains("hello.txt", names);
        Assert.Contains("data/nested.bin", names);
        Assert.Contains("empty.txt", names);
    }

    [Fact]
    public async Task Open_FileBytesAreCorrect()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        Row helloRow = rows.First(row => row["file_name"].AsString() == "hello.txt");
        byte[] bytes = helloRow["file_bytes"].AsUInt8Array();
        Assert.Equal("Hello, World!", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Open_BinaryContentPreserved()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        Row binRow = rows.First(row => row["file_name"].AsString() == "data/nested.bin");
        byte[] bytes = binRow["file_bytes"].AsUInt8Array();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes);
    }

    [Fact]
    public async Task Open_EmptyFileHasEmptyBytes()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        Row emptyRow = rows.First(row => row["file_name"].AsString() == "empty.txt");
        byte[] bytes = emptyRow["file_bytes"].AsUInt8Array();
        Assert.Empty(bytes);
    }

    // ───────────────────── Lazy file_bytes ─────────────────────

    [Fact]
    public async Task Open_FileBytesAreLazy()
    {
        ZipTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), null, CancellationToken.None));

        // file_name should be immediately available (eager)
        Row row = rows[0];
        Assert.False(row["file_name"].IsNull);

        // file_bytes should be accessible (lazy but materializes on access)
        DataValue fileBytesValue = row["file_bytes"];
        Assert.False(fileBytesValue.IsNull);
    }

    // ───────────────────── Projection pushdown ─────────────────────

    [Fact]
    public async Task Open_ProjectionPushdown_FileNameOnly()
    {
        ZipTableProvider provider = new();
        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "file_name" };

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor(), required, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].FieldCount);
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_FileBytesIsExpensive()
    {
        ZipTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(), CancellationToken.None);

        Assert.True(capabilities.ColumnCosts.ContainsKey("file_bytes"));
        Assert.Equal(ColumnCost.Expensive, capabilities.ColumnCosts["file_bytes"]);
    }

    /// <summary>
    /// Verifies that the provider declares <c>file_name</c> as its key column
    /// for keyed access.
    /// </summary>
    [Fact]
    public async Task GetCapabilities_DeclaresKeyColumn()
    {
        ZipTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor(), CancellationToken.None);

        Assert.Equal("file_name", capabilities.KeyColumn);
    }

    // ───────────────────── Keyed fetch ─────────────────────

    /// <summary>
    /// Fetching known entry names returns the correct rows with decompressed bytes.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_ReturnsMatchingEntries()
    {
        ZipTableProvider provider = new();
        HashSet<DataValue> keys = new()
        {
            DataValue.FromString("hello.txt"),
            DataValue.FromString("data/nested.bin"),
        };

        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(), "file_name", keys, null, CancellationToken.None));

        Assert.Equal(2, rows.Count);

        Row? helloRow = rows.FirstOrDefault(r => r["file_name"].AsString() == "hello.txt");
        Assert.NotNull(helloRow);
        byte[] helloBytes = helloRow["file_bytes"].AsUInt8Array();
        Assert.Equal("Hello, World!", System.Text.Encoding.UTF8.GetString(helloBytes));

        Row? nestedRow = rows.FirstOrDefault(r => r["file_name"].AsString() == "data/nested.bin");
        Assert.NotNull(nestedRow);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, nestedRow["file_bytes"].AsUInt8Array());
    }

    /// <summary>
    /// Fetching non-existent entry names returns no rows.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_NonExistentKeys_ReturnsEmpty()
    {
        ZipTableProvider provider = new();
        HashSet<DataValue> keys = new()
        {
            DataValue.FromString("does_not_exist.txt"),
        };

        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(), "file_name", keys, null, CancellationToken.None));

        Assert.Empty(rows);
    }

    /// <summary>
    /// Keyed fetch respects projection pushdown — fetches only requested columns.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_ProjectionPushdown()
    {
        ZipTableProvider provider = new();
        HashSet<DataValue> keys = new()
        {
            DataValue.FromString("hello.txt"),
        };
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "file_name",
            "file_bytes",
        };

        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(), "file_name", keys, requiredColumns, CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
    }

    /// <summary>
    /// Keyed fetch with empty key set returns no rows.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_EmptyKeySet_ReturnsEmpty()
    {
        ZipTableProvider provider = new();
        HashSet<DataValue> keys = new();

        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(Descriptor(), "file_name", keys, null, CancellationToken.None));

        Assert.Empty(rows);
    }

    // ───────────────────── Cancellation ─────────────────────

    [Fact]
    public async Task Open_RespectsCancellation()
    {
        ZipTableProvider provider = new();
        CancellationTokenSource cancellationTokenSource = new();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReadAllAsync(
                provider.OpenAsync(Descriptor(), null, cancellationTokenSource.Token));
        });
    }

    // ───────────────────── Keyed fetch — many entries ─────────────────────

    /// <summary>
    /// Fetching many keys from a larger archive returns all expected entries
    /// with correct data, exercising the file-order scan path.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_ManyEntries_ReturnsAllInFileOrder()
    {
        const int entryCount = 20;
        string largePath = CreateMultiEntryZip(entryCount);

        ZipTableProvider provider = new();
        TableDescriptor descriptor = new("zip", "large", largePath, new Dictionary<string, string>());

        HashSet<DataValue> keys = new();
        for (int index = 0; index < entryCount; index++)
        {
            keys.Add(DataValue.FromString($"entry_{index}.bin"));
        }

        List<Row> rows = await ReadAllAsync(
            provider.FetchByKeysAsync(descriptor, "file_name", keys, null, CancellationToken.None));

        Assert.Equal(entryCount, rows.Count);

        // Verify every expected file_name is present.
        HashSet<string> returnedNames = new(rows.Select(r => r["file_name"].AsString()));
        for (int index = 0; index < entryCount; index++)
        {
            Assert.Contains($"entry_{index}.bin", returnedNames);
        }

        // Spot-check one entry's content.
        Row? entry5 = rows.FirstOrDefault(r => r["file_name"].AsString() == "entry_5.bin");
        Assert.NotNull(entry5);
        byte[] bytes = entry5["file_bytes"].AsUInt8Array();
        Assert.Equal(5, bytes[0]);
    }

    /// <summary>
    /// Keyed fetch respects cancellation.
    /// </summary>
    [Fact]
    public async Task FetchByKeys_RespectsCancellation()
    {
        const int entryCount = 20;
        string largePath = CreateMultiEntryZip(entryCount);

        ZipTableProvider provider = new();
        TableDescriptor descriptor = new("zip", "large", largePath, new Dictionary<string, string>());

        CancellationTokenSource cancellationTokenSource = new();
        await cancellationTokenSource.CancelAsync();

        HashSet<DataValue> keys = new();
        for (int index = 0; index < entryCount; index++)
        {
            keys.Add(DataValue.FromString($"entry_{index}.bin"));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReadAllAsync(
                provider.FetchByKeysAsync(
                    descriptor, "file_name", keys, null, cancellationTokenSource.Token));
        });
    }

    /// <summary>
    /// Creates a test ZIP with the specified number of entries, each containing
    /// a single byte equal to its index (mod 256).
    /// </summary>
    private string CreateMultiEntryZip(int entryCount)
    {
        string path = Path.Combine(_tempDirectory, $"multi_{entryCount}.zip");

        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);

        for (int index = 0; index < entryCount; index++)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"entry_{index}.bin");
            using Stream entryStream = entry.Open();
            entryStream.WriteByte((byte)(index % 256));
        }

        return path;
    }
}
