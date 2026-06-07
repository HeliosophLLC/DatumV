using System;
using System.Threading;
using System.Threading.Tasks;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Per-format runtime sink that consumes a stream of <see cref="RowBatch"/>
/// values and writes them to an <see cref="ExportTarget"/>. Two-phase
/// (write / finish) because Parquet, HDF5, and FITS each need a finalize
/// step that is more than just closing the file handle — row-group flushing
/// and footer writes for Parquet, chunk index for HDF5, header rewrite for
/// FITS.
/// </summary>
public interface IExportSink : IAsyncDisposable
{
    /// <summary>
    /// Appends the given batch. May buffer (Parquet row groups, HDF5 chunks)
    /// or flush immediately (CSV, JSONL). Throws
    /// <see cref="ExportRuntimeException"/> for values that the format cannot
    /// represent but planner-time validation could not detect (e.g. an
    /// oversized binary that exceeds a per-row cap).
    /// </summary>
    ValueTask WriteAsync(RowBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Flushes any remaining buffered state, writes the format's trailer
    /// (Parquet footer + statistics, FITS BINTABLE row count in header), and
    /// closes the underlying file handle. Idempotent.
    /// </summary>
    ValueTask FinishAsync(CancellationToken cancellationToken);

    /// <summary>Total rows the sink has accepted via <see cref="WriteAsync"/>.</summary>
    long RowsWritten { get; }

    /// <summary>Total bytes written to the target, as observed by the sink.</summary>
    long BytesWritten { get; }
}
