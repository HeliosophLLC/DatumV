using System.Collections.Generic;
using Heliosoph.DatumV.Model;

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
        // file handle is opened or row is read.
        _ = ParquetColumnEncoder.Create(column);
        return MediaDisposition.Inline;
    }

    /// <inheritdoc />
    public IExportSink CreateSink(
        ExportTarget target,
        Schema schema,
        IReadOnlyList<MediaDisposition> columnDispositions,
        ExportOptions options)
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

        return new ParquetExportSink(fileTarget.Path, schema, rowGroupSize);
    }
}
