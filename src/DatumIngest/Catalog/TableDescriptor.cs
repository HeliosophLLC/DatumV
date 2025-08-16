namespace DatumIngest.Catalog;

/// <summary>
/// Describes a named table that can be opened by a provider.
/// </summary>
/// <param name="Provider">Provider identifier (e.g. "csv", "json", "zip", "hdf5", "parquet").</param>
/// <param name="Name">Logical table name used in SQL FROM clauses.</param>
/// <param name="FilePath">Absolute or relative file path to the data source.</param>
/// <param name="Options">Provider-specific key-value options (e.g. delimiter, header).</param>
/// <param name="Compression">
/// Compression applied to the file. When set to <see cref="CompressionKind.Gzip"/>,
/// providers that support streaming reads wrap the file stream in a decompression layer.
/// Providers requiring seekable access should receive a descriptor pointing to a
/// pre-decompressed temporary file with <see cref="CompressionKind.None"/>.
/// </param>
public sealed record TableDescriptor(
    string Provider,
    string Name,
    string FilePath,
    IReadOnlyDictionary<string, string> Options,
    CompressionKind Compression = CompressionKind.None);
