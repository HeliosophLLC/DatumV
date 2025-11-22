namespace DatumIngest.DatumFile;

/// <summary>
/// Compression algorithms available for index pages and other internal
/// structures. Stored as a single byte where compression dispatch is
/// per-page (sorted index spill, B+Tree pages, bitmap chunks).
/// </summary>
/// <remarks>
/// The v2 <c>.datum</c> file format itself is uncompressed; this enum
/// is consumed by the <c>.datum-index</c> sidecar layer
/// (<see cref="DatumFile.Compression.DatumCompressor"/>) where Zstd is the
/// default codec for compressed index payloads.
/// </remarks>
public enum DatumCompression : byte
{
    /// <summary>No compression. For already-compressed payloads (JPEG/PNG/etc.).</summary>
    None = 0,

    /// <summary>Zstd via ZstdSharp.Port. Default for index pages.</summary>
    Zstd = 1,

    /// <summary>Deflate (raw zlib) via System.IO.Compression. BCL-only fallback.</summary>
    Zlib = 2,

    /// <summary>Brotli via System.IO.Compression. High ratio, slow encode.</summary>
    Brotli = 3,
}

/// <summary>
/// Index-layer compression constants. Consumed by the
/// <c>.datum-index</c> sidecar's bloom / B+Tree / sorted / bitmap pages.
/// </summary>
public static class DatumFileConstants
{
    /// <summary>Default Zstd compression level for index pages. Level 3 balances speed and ratio for ETL workloads.</summary>
    public const int DefaultZstdCompressionLevel = 3;
}
