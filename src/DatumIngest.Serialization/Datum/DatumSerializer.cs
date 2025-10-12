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

        // A ReferenceStore scope is still needed for zone map min/max DataValues,
        // which are serialized by the footer writer via parameterless AsString().
        // This scope will be removed once the footer writer is store-aware.
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
                    writer.Store = context.Arena;
                    writer.Initialize(datumSchema);
                }

                for (int i = 0; i < batch.Count; i++)
                    writer.WriteRow(batch[i]);
            }

            writer?.Finalize();
        }
        finally
        {
            writer?.Dispose();
            ReferenceStore.EndQueryScope();
        }
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
