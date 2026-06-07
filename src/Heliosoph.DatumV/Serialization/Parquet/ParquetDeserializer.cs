using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Serialization.Parquet;

/// <summary>
/// Deserializes Parquet files into <see cref="RowBatch"/> streams for
/// the generic ingest pipeline. Reads each row group fully into memory,
/// assembles per-row <see cref="DataValue"/>s, and emits batches that
/// match the file's leaf-column schema. Same shape <c>open_parquet</c>
/// would produce on an identical file — so <c>datum ingest foo.parquet</c>
/// lands a queryable typed table.
/// </summary>
public sealed class ParquetDeserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 1024;

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public ParquetDeserializer(FileFormatDescriptor descriptor)
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
                $"ParquetDeserializer needs a direct file path; got '{_descriptor.FilePath}' which doesn't exist.");
        }

        await using Stream stream = File.OpenRead(_descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        DataField[] fields = reader.Schema.GetDataFields();
        ParquetColumnType[] columnTypes = new ParquetColumnType[fields.Length];
        string[] columnNames = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            columnTypes[i] = ParquetColumnType.From(fields[i]);
            columnNames[i] = fields[i].Name;
            if (!columnTypes[i].IsSupported)
            {
                throw new InvalidDataException(
                    $"Parquet column '{fields[i].Name}' has unsupported type " +
                    $"(CLR={fields[i].ClrType.Name}). Use open_parquet_meta to inspect first.");
            }
        }
        ColumnLookup outputLookup = new(columnNames);

        RowBatch? batch = null;
        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(rg);
            int rowCount = checked((int)rgReader.RowCount);

            batch ??= context.Pool.RentRowBatch(outputLookup, DefaultBatchSize);

            DataValue[][] perColumn = new DataValue[fields.Length][];
            for (int c = 0; c < fields.Length; c++)
            {
                DataColumn col = await rgReader.ReadColumnAsync(fields[c], cancellationToken).ConfigureAwait(false);
                perColumn[c] = ParquetColumnReader.ReadAsRows(col, columnTypes[c], rowCount, batch.Arena);
            }

            for (int r = 0; r < rowCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataValue[] row = context.Pool.RentDataValues(fields.Length);
                for (int c = 0; c < fields.Length; c++)
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
