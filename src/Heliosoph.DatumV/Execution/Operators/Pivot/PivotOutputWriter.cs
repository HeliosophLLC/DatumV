using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Pivot;

/// <summary>
/// Output-side plumbing for <see cref="PivotOperator"/>. One group becomes one
/// output row whose layout is <c>[keyValues..., (pivotValue × aggregate) cells...]</c>.
/// Cell results come from <see cref="IAggregateAccumulator.ResultAsync"/>; key
/// values are stamped directly. Wraps the shared
/// <see cref="OutputBatchAccumulator"/> rent / IsFull / flush plumbing.
/// </summary>
internal sealed class PivotOutputWriter : OutputBatchAccumulator
{
    private readonly ColumnLookup _outputLookup;
    private readonly int _keyCount;
    private readonly int _cellCount;

    public PivotOutputWriter(ExecutionContext context, ColumnLookup outputLookup, int keyCount, int cellCount)
        : base(context)
    {
        _outputLookup = outputLookup;
        _keyCount = keyCount;
        _cellCount = cellCount;
    }

    /// <summary>
    /// Emits one group. <paramref name="keyValues"/> may be <see langword="null"/>
    /// for the global (no-key) group; otherwise it must have <see cref="_keyCount"/>
    /// elements. <paramref name="cellAccumulators"/> is the flat array of
    /// <c>(pivotValue × aggregate)</c> accumulators indexed by
    /// <c>(pivotOrdinal * aggregateCount) + aggregateIndex</c>.
    /// </summary>
    public async ValueTask<RowBatch?> EmitAsync(
        DataValue[]? keyValues,
        IAggregateAccumulator[] cellAccumulators,
        InvocationFrame frame)
    {
        RowBatch current = EnsureRentedAndGetCurrent(_outputLookup);
        DataValue[] values = Pool.RentDataValues(_outputLookup.Count);

        if (keyValues is not null)
        {
            for (int k = 0; k < _keyCount; k++)
            {
                values[k] = keyValues[k];
            }
        }

        for (int c = 0; c < _cellCount; c++)
        {
            values[_keyCount + c] = await cellAccumulators[c].ResultAsync(frame).ConfigureAwait(false);
        }

        current.Add(values);
        return TakeIfFull();
    }
}
