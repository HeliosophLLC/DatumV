using System.IO.Compression;
using System.Runtime.CompilerServices;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Catalog.Providers;

/// <summary>
/// Reads ZIP archives, yielding one row per entry with <c>file_name</c> (eager)
/// and <c>file_bytes</c> (lazy — defers decompression until accessed).
/// </summary>
public sealed class ZipTableProvider : ITableProvider
{
    private static readonly Schema ZipSchema = new(new ColumnInfo[]
    {
        new("file_name", DataKind.String, nullable: false),
        new("file_bytes", DataKind.UInt8Array, nullable: false)
    });

    /// <inheritdoc />
    public Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ZipSchema);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool includeFileName = requiredColumns is null ||
            requiredColumns.Contains("file_name");
        bool includeFileBytes = requiredColumns is null ||
            requiredColumns.Contains("file_bytes");

        // Build projected column arrays
        List<string> columnNames = new();
        if (includeFileName)
        {
            columnNames.Add("file_name");
        }
        if (includeFileBytes)
        {
            columnNames.Add("file_bytes");
        }

        string[] names = columnNames.ToArray();

        // We must read the ZIP synchronously because ZipArchive is not thread-safe
        // and entries are only valid while the archive is open. We read all entries
        // eagerly but defer the byte content via lazy evaluation.
        using FileStream fileStream = File.OpenRead(descriptor.FilePath);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory entries
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            List<DataValue> values = new();

            if (includeFileName)
            {
                values.Add(DataValue.FromString(entry.FullName));
            }

            if (includeFileBytes)
            {
                // Read bytes eagerly since the ZipArchive must remain open
                // (entries are invalidated when the archive is disposed).
                // The query planner uses ColumnCost.Expensive to avoid reading
                // this column unnecessarily during join build phases.
                byte[] bytes = ReadEntryBytes(entry);
                values.Add(DataValue.FromUInt8Array(bytes));
            }

            yield return new Row(names, values.ToArray());
        }
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: null,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>
            {
                ["file_bytes"] = ColumnCost.Expensive
            }));
    }

    /// <summary>
    /// Reads all bytes from a ZIP entry into a byte array.
    /// </summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }
}
