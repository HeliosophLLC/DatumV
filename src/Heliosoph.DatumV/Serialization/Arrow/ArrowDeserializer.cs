using System.Runtime.CompilerServices;
using Apache.Arrow.Ipc;
using Heliosoph.DatumV.Model;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;
using RecordBatch = Apache.Arrow.RecordBatch;

namespace Heliosoph.DatumV.Serialization.Arrow;

/// <summary>
/// Deserializes Apache Arrow IPC / Feather v2 files into
/// <see cref="RowBatch"/> streams for the generic ingest pipeline.
/// Walks the file's record batches and emits batches that match the
/// file's top-level column schema — the same shape <c>open_arrow</c>
/// would produce on an identical file, so
/// <c>datum ingest foo.arrow</c> lands a queryable typed table.
/// </summary>
public sealed class ArrowDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public ArrowDeserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_descriptor.FilePath))
        {
            throw new InvalidDataException(
                $"ArrowDeserializer needs a direct file path; got '{_descriptor.FilePath}' which doesn't exist.");
        }

        await using Stream stream = File.OpenRead(_descriptor.FilePath);
        using ArrowFileReader reader = new(stream);

        ArrowSchema arrowSchema = reader.Schema;
        int fieldCount = arrowSchema.FieldsList.Count;

        ArrowColumnType[] columnTypes = new ArrowColumnType[fieldCount];
        string[] columnNames = new string[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            ArrowField field = arrowSchema.FieldsList[i];
            columnTypes[i] = ArrowColumnType.From(field);
            columnNames[i] = field.Name;
            if (!columnTypes[i].IsSupported)
            {
                throw new InvalidDataException(
                    $"Arrow column '{field.Name}' has unsupported type " +
                    $"({columnTypes[i].LogicalTypeName}). Use open_arrow_meta to inspect first.");
            }
        }
        ColumnLookup outputLookup = new(columnNames);

        RowBatch? batch = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RecordBatch? arrowBatch = await reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (arrowBatch is null) break;

            int rowCount = arrowBatch.Length;
            batch ??= context.Pool.RentRowBatch(outputLookup, DefaultBatchSize);

            DataValue[][] perColumn = new DataValue[fieldCount][];
            for (int c = 0; c < fieldCount; c++)
            {
                perColumn[c] = ArrowColumnReader.ReadAsRows(
                    arrowBatch.Column(c), columnTypes[c], rowCount, batch.Arena);
            }

            for (int r = 0; r < rowCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataValue[] row = context.Pool.RentDataValues(fieldCount);
                for (int c = 0; c < fieldCount; c++)
                {
                    row[c] = perColumn[c][r];
                }
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = context.Pool.RentRowBatch(outputLookup, DefaultBatchSize);
                }
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }
}
