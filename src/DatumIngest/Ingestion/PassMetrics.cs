namespace DatumIngest.Ingestion;

/// <summary>
/// Deterministic per-pass metrics captured for both the optional type-scan pass
/// and the main ingestion pass. Scoped to what is measurable independent of
/// concurrent work in the hosting process — row/batch/byte counters and
/// wall-clock elapsed. Allocation and GC-generation counters were intentionally
/// excluded because they're process-wide (<c>GC.GetTotalAllocatedBytes</c>,
/// <c>GC.CollectionCount</c>) and become meaningless when multiple requests run
/// concurrently under Server GC. Capture those at the benchmark/test level when
/// needed, not per-request.
/// </summary>
/// <param name="RowCount">Number of rows processed during the pass.</param>
/// <param name="BatchCount">Number of <see cref="Model.RowBatch"/>es yielded; zero for the scan pass which does not batch.</param>
/// <param name="BytesRead">Bytes read from the source stream.</param>
/// <param name="ArenaBytesWritten">
/// Total bytes written into per-batch arenas during the pass. Zero for the scan pass
/// since it does not materialize values.
/// </param>
/// <param name="Elapsed">Wall-clock duration of the pass.</param>
public sealed record PassMetrics(
    long RowCount,
    long BatchCount,
    long BytesRead,
    long ArenaBytesWritten,
    TimeSpan Elapsed);
