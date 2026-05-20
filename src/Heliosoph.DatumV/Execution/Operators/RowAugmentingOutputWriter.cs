using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Output writer for the "source row + N extra columns" shape shared by
/// <see cref="RowEnricherOperator"/>, <see cref="ScalarSubqueryOperator"/>,
/// <see cref="WindowOperator"/>, and <see cref="ModelInvocationOperator"/>.
/// Wraps the per-row stabilise-source + rent-output-array boilerplate so each
/// augmenter only owns its extras-filling logic.
/// </summary>
/// <remarks>
/// <para>
/// Two-step contract: <see cref="BeginRow"/> ensures an output batch is open,
/// rents the output <see cref="DataValue"/>[] sized to the output lookup, and
/// stabilises the first <c>sourceColumnCount</c> cells of the source row into
/// the destination batch's arena. The caller then fills positions
/// <c>[sourceColumnCount..outputLookup.Count)</c> using the returned batch's
/// <c>Arena</c> as the evaluation target store. Finally, <see cref="Commit"/>
/// attaches the row to the in-progress batch and returns the batch when it
/// fills — same shape as <see cref="RowCopyOutputWriter"/>.
/// </para>
/// <para>
/// The stabilise pass means source-row values whose payloads live in the source
/// batch's arena survive the source batch returning to the pool. Extras-side
/// values that materialise via the evaluator's target-store routing (so they
/// land in the output arena directly) need no further stabilisation; extras
/// that come from another arena must be stabilised by the caller.
/// </para>
/// </remarks>
internal sealed class RowAugmentingOutputWriter : OutputBatchAccumulator
{
    public RowAugmentingOutputWriter(ExecutionContext context) : base(context)
    {
    }

    /// <summary>
    /// Rents the output array sized to <paramref name="outputLookup"/>, ensures
    /// an output batch is open, and copies+stabilises the first
    /// <paramref name="sourceColumnCount"/> cells of <paramref name="sourceRow"/>
    /// into the open batch's arena. Returns the rented array and the open batch —
    /// the caller uses <c>batch.Arena</c> as the destination store when filling
    /// positions <c>[sourceColumnCount..outputLookup.Count)</c>.
    /// </summary>
    public (DataValue[] OutValues, RowBatch DestinationBatch) BeginRow(
        ColumnLookup outputLookup,
        Row sourceRow,
        IValueStore sourceArena,
        int sourceColumnCount)
    {
        RowBatch current = EnsureRentedAndGetCurrent(outputLookup);
        DataValue[] outValues = Pool.RentDataValues(outputLookup.Count);
        IValueStore destArena = current.Arena;
        for (int i = 0; i < sourceColumnCount; i++)
        {
            outValues[i] = DataValueRetention.Stabilize(sourceRow[i], sourceArena, destArena);
        }
        return (outValues, current);
    }

    /// <summary>
    /// Adds the completed augmented row to the in-progress batch. Returns the
    /// batch when it fills, or <see langword="null"/> otherwise — same contract
    /// as <see cref="RowCopyOutputWriter.Add(ColumnLookup, RowBatch, int)"/>.
    /// </summary>
    public RowBatch? Commit(ColumnLookup outputLookup, DataValue[] outValues)
    {
        RowBatch current = EnsureRentedAndGetCurrent(outputLookup);
        current.Add(outValues);
        return TakeIfFull();
    }
}
