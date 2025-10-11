using DatumIngest.DatumFile;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Datum;

/// <summary>
/// Serializes a stream of <see cref="RowBatch"/> instances into the <c>.datum</c>
/// binary column-store format. Delegates to <see cref="DatumFileWriter"/> for encoding,
/// compression, row group management, and footer/header writing.
/// </summary>
/// <remarks>
/// The <c>.datum</c> format requires a seekable stream for header patching, so this
/// serializer writes via the file path on the <see cref="OutputDescriptor"/>.
/// Schema is inferred from the first non-empty batch.
/// </remarks>
public sealed class DatumSerializer : IFormatSerializer
{
    private readonly OutputDescriptor _descriptor;

    /// <summary>Creates a serializer for the given output descriptor.</summary>
    public DatumSerializer(OutputDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default)
    {
        DatumFileWriter? writer = null;
        IValueStore store = context.Arena;

        // DatumFileWriter's encoders resolve strings via ReferenceStore.Current(),
        // so we need a scope and must materialize arena-backed strings into it.
        ReferenceStore.BeginQueryScope();
        try
        {
            await foreach (RowBatch batch in rows.WithCancellation(cancellationToken))
            {
                if (batch.Count == 0) continue;

                if (writer is null)
                {
                    Schema schema = InferSchema(batch);
                    DatumFileSchema datumSchema = DatumFileSchema.FromSchema(schema);
                    writer = new DatumFileWriter(_descriptor.FilePath);
                    writer.Initialize(datumSchema);
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    // Materialize arena-backed strings into ReferenceStore for the encoder.
                    if (HasStringColumns(row))
                        row = MaterializeStrings(row, store);
                    writer.WriteRow(row);
                }
            }

            writer?.Finalize();
        }
        finally
        {
            writer?.Dispose();
            ReferenceStore.EndQueryScope();
        }
    }

    private static bool HasStringColumns(Row row)
    {
        for (int i = 0; i < row.FieldCount; i++)
        {
            DataKind kind = row[i].Kind;
            if (kind is DataKind.String or DataKind.JsonValue)
                return true;
        }
        return false;
    }

    private static Row MaterializeStrings(Row row, IValueStore store)
    {
        DataValue[] values = new DataValue[row.FieldCount];
        bool changed = false;

        for (int i = 0; i < row.FieldCount; i++)
        {
            DataValue v = row[i];
            if (!v.IsNull && v.Kind == DataKind.String)
            {
                values[i] = DataValue.FromString(v.AsString(store));
                changed = true;
            }
            else if (!v.IsNull && v.Kind == DataKind.JsonValue)
            {
                values[i] = DataValue.FromJsonValue(v.AsJsonValue(store));
                changed = true;
            }
            else
            {
                values[i] = v;
            }
        }

        if (!changed) return row;

        Dictionary<string, int> nameIndex = new(row.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < row.ColumnNames.Count; i++)
            nameIndex[row.ColumnNames[i]] = i;

        return new Row(row.ColumnNames, values, nameIndex);
    }

    private static Schema InferSchema(RowBatch batch)
    {
        Row firstRow = batch[0];
        List<ColumnInfo> columns = new(firstRow.FieldCount);

        for (int i = 0; i < firstRow.FieldCount; i++)
        {
            DataValue v = firstRow[i];
            // Scan the batch for a non-null value to get the true kind.
            DataKind kind = v.IsNull ? ScanForKind(batch, i, v.Kind) : v.Kind;
            columns.Add(new ColumnInfo(firstRow.ColumnNames[i], kind, nullable: true));
        }

        return new Schema(columns);
    }

    /// <summary>
    /// Scans subsequent rows in the batch for a non-null value to determine the column kind
    /// when the first row has a null.
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
