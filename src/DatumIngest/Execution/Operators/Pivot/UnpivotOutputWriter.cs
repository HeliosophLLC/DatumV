using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.Pivot;

/// <summary>
/// Output-side plumbing for <see cref="UnpivotOperator"/>. Picks key-column
/// values from a source row by ordinal, stabilises them into the output
/// batch's arena alongside the cell value, and stamps the source column's
/// name into the trailing slot. Wraps the shared
/// <see cref="OutputBatchAccumulator"/> rent / IsFull / flush plumbing.
/// </summary>
internal sealed class UnpivotOutputWriter : OutputBatchAccumulator
{
    public UnpivotOutputWriter(ExecutionContext context) : base(context)
    {
    }

    /// <summary>
    /// Emits one unpivoted row: <paramref name="keyOrdinals"/> indexes into
    /// <paramref name="sourceRow"/> for the leading key columns, then the
    /// cell value, then <paramref name="sourceColumnName"/> as the name
    /// column. Values are stabilised from <paramref name="sourceArena"/> into
    /// the output batch's arena so downstream consumers resolve them through
    /// <c>batch.Arena</c>. Returns the in-progress batch detached from the
    /// writer when it fills, or <see langword="null"/> when not full.
    /// </summary>
    public RowBatch? Emit(
        ColumnLookup outputLookup,
        Row sourceRow,
        IValueStore sourceArena,
        int[] keyOrdinals,
        DataValue cellValue,
        string sourceColumnName)
    {
        RowBatch current = EnsureRentedAndGetCurrent(outputLookup);
        DataValue[] values = Pool.RentDataValues(outputLookup.Count);

        for (int k = 0; k < keyOrdinals.Length; k++)
        {
            values[k] = DataValueRetention.Stabilize(
                sourceRow[keyOrdinals[k]], sourceArena, current.Arena);
        }
        values[keyOrdinals.Length] = DataValueRetention.Stabilize(
            cellValue, sourceArena, current.Arena);
        values[keyOrdinals.Length + 1] = DataValue.FromString(sourceColumnName, current.Arena);

        current.Add(values);
        return TakeIfFull();
    }
}
