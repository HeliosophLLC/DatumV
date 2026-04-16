using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Ordering;

/// <summary>
/// Manages a collection of sorted runs spilled to disk during the unbounded ORDER BY
/// path. Each <see cref="Spill"/> call freezes a sorted in-memory buffer into a new
/// single-partition <see cref="SpillReaderWriter"/>; the runs are then drained by
/// the k-way merge phase.
/// </summary>
/// <remarks>
/// Each run gets its own <see cref="SpillReaderWriter"/> — the on-disk encoding is
/// per-run, with the consolidated arena holding stabilised payloads. The unbounded
/// path rotates the in-memory <see cref="Arena"/> after each spill (releasing the
/// old buffer's bytes), but that rotation belongs to the iterator's residency
/// bookkeeping, not the spiller — kept separate so this stays a thin collection.
/// </remarks>
internal sealed class SortedRunSpiller : IDisposable
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;
    private readonly List<SpillReaderWriter> _runs = new();

    public SortedRunSpiller(ExecutionContext context)
    {
        _context = context;
        _pool = context.Pool;
    }

    /// <summary>The number of runs spilled so far.</summary>
    public int Count => _runs.Count;

    /// <summary>The spilled runs in insertion order, for the k-way merge phase.</summary>
    public IReadOnlyList<SpillReaderWriter> Runs => _runs;

    /// <summary>
    /// Freezes <paramref name="sortedBuffer"/> into a new single-partition run.
    /// Builds a <see cref="RowBatch"/> over <paramref name="bufferArena"/> from the
    /// buffer's pre-sorted <see cref="KeyedRow.Row"/> payloads and hands it to the
    /// spiller, which stabilises the values into its own consolidated arena and
    /// returns the input batch — releasing all the rented <see cref="DataValue"/>[]s
    /// back to the pool.
    /// </summary>
    public SpillReaderWriter Spill(
        ColumnLookup schema,
        Arena bufferArena,
        List<KeyedRow> sortedBuffer)
    {
        SpillReaderWriter run = new(
            _pool, schema, _context.SpillDirectory, partitionCount: 1);

        RowBatch runBatch = _pool.RentRowBatch(schema, sortedBuffer.Count, bufferArena);
        foreach (KeyedRow keyedRow in sortedBuffer)
        {
            runBatch.Add(keyedRow.Row.RawValues);
        }

        run.Write(runBatch, partition: 0);
        _runs.Add(run);
        return run;
    }

    /// <summary>
    /// Disposes every spilled run (deleting its temp directory + arena file).
    /// Safe to call multiple times; safe from <c>finally</c> on exception paths.
    /// </summary>
    public void Dispose()
    {
        foreach (SpillReaderWriter run in _runs)
        {
            run.Dispose();
        }
        _runs.Clear();
    }
}
