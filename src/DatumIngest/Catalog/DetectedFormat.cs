namespace DatumIngest.Catalog;

/// <summary>
/// Result of file format detection, pairing the provider name with any
/// outer compression layer that must be handled before the provider reads the data.
/// </summary>
/// <param name="Provider">Provider identifier (e.g. "csv", "parquet").</param>
/// <param name="Compression">Compression applied to the file, if any.</param>
public sealed record DetectedFormat(string Provider, CompressionKind Compression);
