using System.Collections.Generic;
using System.IO.Compression;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;
using Parquet;

namespace Heliosoph.DatumV.Export.Parquet;

/// <summary>
/// Parquet implementation of <see cref="IExportFormat"/>. Single-file output
/// (no directory sinks in v1); typed-media columns use the
/// <see cref="MediaDisposition.Inline"/> disposition (raw bytes into a
/// <c>BYTE_ARRAY</c> column); unsupported kinds are rejected at plan time
/// with a column-specific message.
/// </summary>
public sealed class ParquetExportFormat : IExportFormat
{
    /// <inheritdoc />
    public string Name => "parquet";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".parquet"];

    /// <inheritdoc />
    public bool RequiresDirectorySink => false;

    /// <inheritdoc />
    public MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options)
    {
        // The encoder factory is the single source of truth for what's supported.
        // Invoking it here at plan time turns runtime-unrepresentable kinds into
        // an ExportPlanException with a clear column-named message before any
        // file handle is opened or row is read. The plan-time call passes a
        // null SidecarRegistry — actual byte reads only happen at execute time,
        // when the sink holds the real registry.
        _ = ParquetColumnEncoder.Create(column, sidecarRegistry: null);

        // Validate global options (COMPRESSION, COMPRESSION_LEVEL,
        // ROW_GROUP_SIZE, ROW_GROUP_BYTE_BUDGET) at plan time too — they
        // don't depend on the column but ResolveDisposition is the only
        // per-export plan-time hook available, and re-running the resolver
        // per column is idempotent (string match + switch).
        _ = ResolveCompression(options);
        _ = ResolveCompressionLevel(options);
        ValidateRowGroupOptions(options);

        return MediaDisposition.Inline;
    }

    /// <summary>
    /// Plan-time validation for the row-group sizing options
    /// (<c>ROW_GROUP_SIZE</c>, <c>ROW_GROUP_BYTE_BUDGET</c>). Both must
    /// be positive; surfacing the rejection here means typo-bait values
    /// fail before any file handle is opened or row is read. Idempotent —
    /// safe to call per column.
    /// </summary>
    private static void ValidateRowGroupOptions(ExportOptions options)
    {
        if (options.TryGetLong("ROW_GROUP_SIZE", out long size) && size <= 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: ROW_GROUP_SIZE must be positive (got {size}).");
        }
        if (options.TryGetLong("ROW_GROUP_BYTE_BUDGET", out long bytes) && bytes <= 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: ROW_GROUP_BYTE_BUDGET must be positive (got {bytes}).");
        }
    }

    /// <inheritdoc />
    public IExportSink CreateSink(
        ExportTarget target,
        Schema schema,
        IReadOnlyList<MediaDisposition> columnDispositions,
        ExportOptions options,
        SidecarRegistry? sidecarRegistry,
        // Deliberately unused: Parquet timestamps are UTC-normalized
        // instants (isAdjustedToUTC semantics), session-independent by
        // design so round-trips don't vary with SET TIME ZONE.
        TimeZoneInfo? sessionTimeZone = null)
    {
        if (target is not ExportTarget.File fileTarget)
        {
            throw new ExportPlanException(
                "COPY TO parquet: target must be a single file path. " +
                "Directory targets are not supported by the v1 Parquet sink.");
        }

        int rowGroupSize = options.TryGetLong("ROW_GROUP_SIZE", out long parsed)
            ? checked((int)parsed)
            : ParquetExportSink.DefaultRowGroupSize;
        if (rowGroupSize <= 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: ROW_GROUP_SIZE must be positive (got {rowGroupSize}).");
        }

        long rowGroupByteBudget = options.TryGetLong("ROW_GROUP_BYTE_BUDGET", out long parsedBytes)
            ? parsedBytes
            : ParquetExportSink.DefaultRowGroupByteBudget;
        if (rowGroupByteBudget <= 0)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: ROW_GROUP_BYTE_BUDGET must be positive (got {rowGroupByteBudget}).");
        }

        CompressionMethod compression = ResolveCompression(options);
        CompressionLevel? compressionLevel = ResolveCompressionLevel(options);

        return new ParquetExportSink(
            fileTarget.Path, schema, rowGroupSize, sidecarRegistry,
            rowGroupByteBudget: rowGroupByteBudget,
            compressionMethod: compression,
            compressionLevel: compressionLevel);
    }

    /// <summary>
    /// Maps the SQL-surface <c>COMPRESSION</c> option string to a
    /// <see cref="CompressionMethod"/>. Defaults to
    /// <see cref="CompressionMethod.Snappy"/> when absent — matches
    /// Parquet.Net's writer default and the de-facto Parquet ecosystem
    /// default. Codec set kept to the ones Parquet.Net actively maintains
    /// and that consumers in the wild reliably accept; obsolete codecs
    /// (Lzo, Hadoop-LZ4) are deliberately not surfaced.
    /// </summary>
    private static CompressionMethod ResolveCompression(ExportOptions options)
    {
        string? raw = options.GetString("COMPRESSION");
        if (raw is null) return CompressionMethod.Snappy;

        return raw.Trim().ToLowerInvariant() switch
        {
            "none" or "uncompressed" => CompressionMethod.None,
            "snappy" => CompressionMethod.Snappy,
            "gzip" => CompressionMethod.Gzip,
            "zstd" => CompressionMethod.Zstd,
            "brotli" => CompressionMethod.Brotli,
            "lz4" or "lz4_raw" => CompressionMethod.Lz4Raw,
            _ => throw new ExportPlanException(
                $"COPY TO parquet: COMPRESSION value '{raw}' is not recognised. " +
                "Supported codecs: none, snappy (default), gzip, zstd, brotli, lz4."),
        };
    }

    /// <summary>
    /// Parses the optional <c>COMPRESSION_LEVEL</c> integer into a
    /// <see cref="CompressionLevel"/>. Honoured by Parquet.Net's gzip,
    /// zstd, and brotli codecs; ignored by snappy and lz4. Unspecified
    /// returns <see langword="null"/> so the writer keeps its codec-
    /// default level.
    /// </summary>
    private static CompressionLevel? ResolveCompressionLevel(ExportOptions options)
    {
        if (!options.TryGetLong("COMPRESSION_LEVEL", out long raw)) return null;

        return raw switch
        {
            0 => CompressionLevel.NoCompression,
            1 => CompressionLevel.Fastest,
            2 => CompressionLevel.Optimal,
            3 => CompressionLevel.SmallestSize,
            _ => throw new ExportPlanException(
                $"COPY TO parquet: COMPRESSION_LEVEL must be 0 (none), 1 (fastest), 2 (optimal), " +
                $"or 3 (smallest); got {raw}."),
        };
    }
}
