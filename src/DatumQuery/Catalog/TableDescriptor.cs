namespace Axon.QueryEngine.Catalog;

/// <summary>
/// Describes a named table that can be opened by a provider.
/// </summary>
/// <param name="Provider">Provider identifier (e.g. "csv", "json", "zip", "hdf5", "parquet").</param>
/// <param name="Name">Logical table name used in SQL FROM clauses.</param>
/// <param name="FilePath">Absolute or relative file path to the data source.</param>
/// <param name="Options">Provider-specific key-value options (e.g. delimiter, header).</param>
public sealed record TableDescriptor(
    string Provider,
    string Name,
    string FilePath,
    IReadOnlyDictionary<string, string> Options);
