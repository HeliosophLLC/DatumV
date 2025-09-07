namespace DatumIngest.Catalog;

/// <summary>
/// Controls which operations are permitted on a table.
/// </summary>
public enum TableMutability
{
    /// <summary>
    /// The table is read-only. No DDL or DML write operations are allowed.
    /// This is the default for externally sourced tables (CSV, Parquet, etc.).
    /// </summary>
    ReadOnly,

    /// <summary>
    /// The table is owned by the session that created it and supports full DDL/DML.
    /// This is the default for temporary tables created via <c>CREATE TEMP TABLE</c>.
    /// </summary>
    SessionOwned,

    /// <summary>
    /// The table is a persistent <c>.datum</c> table that supports full DDL/DML.
    /// Reserved for future use with <c>CREATE TABLE</c> (non-temporary).
    /// </summary>
    Writable,
}

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
/// <param name="Mutability">
/// Controls which operations are permitted on this table.
/// Defaults to <see cref="TableMutability.ReadOnly"/> for externally sourced tables.
/// </param>
/// <param name="PrimaryKeyColumns">
/// Column names that form the primary key, in declaration order.
/// When non-null and non-empty, INSERT operations enforce uniqueness.
/// </param>
public sealed record TableDescriptor(
    string Provider,
    string Name,
    string FilePath,
    IReadOnlyDictionary<string, string> Options,
    CompressionKind Compression = CompressionKind.None,
    TableMutability Mutability = TableMutability.ReadOnly,
    IReadOnlyList<string>? PrimaryKeyColumns = null);
