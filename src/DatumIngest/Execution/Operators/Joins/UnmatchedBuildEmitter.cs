using System.Collections;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Emits unmatched build-side rows after the probe phase completes. Used by
/// OUTER joins (where the build side must fully appear in the output) — the
/// hash and nested-loop paths share the same logic, this collapses both into
/// one async-iterator helper.
/// </summary>
/// <remarks>
/// For each build row whose <see cref="BitArray"/> bit is unset:
/// <list type="bullet">
/// <item><description>If any probe row exists, emit a combined-shape row with
/// the probe side null-padded (using the supplied
/// <see cref="NullPadCache"/>).</description></item>
/// <item><description>If no probe row ever existed (<c>GetFirstRowForNullPadAsync</c>
/// returns null), emit the build row solo via pass-through.</description></item>
/// </list>
/// </remarks>
internal sealed class UnmatchedBuildEmitter
{
    private readonly bool _flipped;
    private readonly JoinOutputWriter _writer;
    private readonly NullPadCache _cachedNullProbe;

    public UnmatchedBuildEmitter(bool flipped, JoinOutputWriter writer, NullPadCache cachedNullProbe)
    {
        _flipped = flipped;
        _writer = writer;
        _cachedNullProbe = cachedNullProbe;
    }

    /// <summary>
    /// Iterates <paramref name="buildRows"/>, skipping rows whose
    /// <paramref name="buildMatched"/> bit is set, and yields each emit batch
    /// when the writer's output batch fills.
    /// </summary>
    /// <param name="buildMatched">Bit set for every build row that matched at least one probe row.</param>
    /// <param name="buildRows">Stabilised build-side rows (read-only).</param>
    /// <param name="probeSource">Probe-side operator, re-executed to grab a null-pad template.</param>
    /// <param name="buildStore">
    /// Arena where build rows' values live (typically <c>context.Store</c>).
    /// Passed to <c>EmitPassThrough</c> for the no-probe fallback case.
    /// </param>
    /// <param name="context">Execution context.</param>
    public async IAsyncEnumerable<RowBatch> EmitAsync(
        BitArray buildMatched,
        IReadOnlyList<Row> buildRows,
        IQueryOperator probeSource,
        Arena buildStore,
        ExecutionContext context)
    {
        Row? nullProbe = null;

        for (int index = 0; index < buildRows.Count; index++)
        {
            if (buildMatched[index]) continue;

            nullProbe ??= await GetFirstRowForNullPadAsync(probeSource, context).ConfigureAwait(false);

            if (nullProbe is not null)
            {
                Row nullProbeRow = _cachedNullProbe.GetOrCreate(nullProbe.Value);
                Row leftRow = _flipped ? buildRows[index] : nullProbeRow;
                Row rightRow = _flipped ? nullProbeRow : buildRows[index];
                if (_writer.EmitCombined(leftRow, rightRow) is RowBatch ready)
                    yield return ready;
            }
            else
            {
                // No probe rows ever — emit the build row solo. Copy into a
                // fresh DataValue[] so the output batch owns its rows
                // independent of the build-side rentals.
                if (_writer.EmitPassThrough(buildRows[index], buildStore) is RowBatch ready)
                    yield return ready;
            }
        }
    }

    /// <summary>
    /// Re-executes the probe source long enough to grab its first row as a
    /// template for null padding. Returns <see langword="null"/> if the source
    /// produces no rows at all.
    /// </summary>
    private static async Task<Row?> GetFirstRowForNullPadAsync(
        IQueryOperator source, ExecutionContext context)
    {
        await foreach (RowBatch batch in source.ExecuteAsync(context).ConfigureAwait(false))
        {
            if (batch.Count > 0)
            {
                return batch[0];
            }
        }

        return null;
    }
}
