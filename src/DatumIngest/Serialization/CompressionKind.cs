namespace DatumIngest.Serialization;

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

    /// <summary>
    /// Bzip2 compression (<c>.bz2</c> extension). Block-sort + Huffman; slower
    /// than gzip on the wire but a common wrapper for older speech / scientific
    /// corpora (LJSpeech, CMU Arctic). Decoded via SharpZipLib —
    /// <see cref="System.IO.Compression"/> has no built-in bz2.
    /// </summary>
    Bzip2 = 2,
}
