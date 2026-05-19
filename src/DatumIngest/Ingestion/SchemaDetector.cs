using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Ingestion;

/// <summary>
/// Infers a <see cref="Schema"/> from the first non-empty <see cref="RowBatch"/>.
/// Column kinds are determined from the first non-null value in each column.
/// </summary>
/// <remarks>
/// Scoped to a single ingestion. Call <see cref="Detect"/> for each incoming
/// <see cref="RowBatch"/>; the detector short-circuits after the first non-empty
/// batch has been consumed. Check <see cref="IsDetected"/> to avoid redundant calls,
/// though invoking <see cref="Detect"/> after detection is a no-op.
/// </remarks>
public sealed class SchemaDetector
{
    private Schema? _schema;

    /// <summary>Whether a schema has been inferred.</summary>
    public bool IsDetected => _schema is not null;

    /// <summary>
    /// The inferred schema. Available once <see cref="IsDetected"/> is <see langword="true"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Accessed before any batch has been inspected.</exception>
    public Schema Schema => _schema ?? throw new InvalidOperationException(
        "Schema has not been detected yet. Call Detect with a non-empty batch first.");

    /// <summary>
    /// Inspects <paramref name="batch"/> to infer the schema on the first non-empty call.
    /// Subsequent calls are no-ops.
    /// </summary>
    /// <param name="batch">A batch from the source stream.</param>
    public void Detect(RowBatch batch)
    {
        if (_schema is not null) return;
        if (batch.Count == 0) return;

        _schema = InferSchema(batch);
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
