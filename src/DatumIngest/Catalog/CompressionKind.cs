namespace DatumIngest.Catalog;

/// <summary>
/// Identifies the compression applied to a data source file.
/// </summary>
public enum CompressionKind
{
    /// <summary>
    /// No compression — the file is read as-is.
    /// </summary>
    None = 0,

    /// <summary>
    /// Gzip compression (<c>.gz</c> extension, RFC 1952).
    /// </summary>
    Gzip = 1,
}
