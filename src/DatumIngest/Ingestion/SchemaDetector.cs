using System.Runtime.CompilerServices;
using DatumIngest.Model;

namespace DatumIngest.Ingestion;

/// <summary>
/// Infers a <see cref="Schema"/> from the first non-empty <see cref="RowBatch"/> in a
/// stream, then re-emits the full stream (including the peeked batch) for downstream
/// consumption. Column kinds are determined from the first non-null value in each column.
/// </summary>
public sealed class SchemaDetector
{
    private Schema? _schema;

    /// <summary>
    /// The inferred schema. Available after the first batch has been consumed from
    /// <see cref="DetectAndPassthrough"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Accessed before any batch has been consumed.</exception>
    public Schema Schema => _schema ?? throw new InvalidOperationException(
        "Schema has not been detected yet. Consume at least one batch from DetectAndPassthrough first.");

    /// <summary>
    /// Consumes the input stream, infers the schema from the first non-empty batch,
    /// then yields all batches (including the first) unchanged.
    /// </summary>
    /// <param name="source">The input batch stream from a deserializer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The same batches, in order, with the schema available on <see cref="Schema"/>.</returns>
    public async IAsyncEnumerable<RowBatch> DetectAndPassthrough(
        IAsyncEnumerable<RowBatch> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (RowBatch batch in source.WithCancellation(cancellationToken))
        {
            if (_schema is null && batch.Count > 0)
            {
                _schema = InferSchema(batch);
            }

            yield return batch;
        }
    }

    private static Schema InferSchema(RowBatch batch)
    {
        Row firstRow = batch[0];
        int fieldCount = firstRow.FieldCount;
        List<ColumnInfo> columns = new(fieldCount);

        for (int col = 0; col < fieldCount; col++)
        {
            DataValue v = firstRow[col];
            DataKind kind = v.IsNull ? ScanForKind(batch, col, v.Kind) : v.Kind;
            columns.Add(new ColumnInfo(firstRow.ColumnNames[col], kind, nullable: true));
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Scans subsequent rows in the batch for a non-null value to determine the column
    /// kind when the first row has a null.
    /// </summary>
    private static DataKind ScanForKind(RowBatch batch, int colIndex, DataKind fallback)
    {
        for (int i = 1; i < batch.Count; i++)
        {
            DataValue v = batch[i][colIndex];
            if (!v.IsNull)
                return v.Kind;
        }

        return fallback;
    }
}
