using System.Runtime.CompilerServices;
using DatumIngest.Model;
using Parquet;
using Parquet.Data;

namespace DatumIngest.Serialization.Parquet;

/// <summary>
/// Deserializes Apache Parquet files into <see cref="RowBatch"/> streams.
/// Uses typed array casts to avoid boxing, and stores strings via
/// <see cref="SerializationContext.Arena"/> for all reference-type payloads.
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
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);

        ParquetSchemaMapper.ProjectedColumns projection =
            ParquetSchemaMapper.BuildProjection(reader.Schema.Fields, requiredColumns: null);

        if (projection.Count == 0)
            yield break;

        IReadOnlyList<string> names = projection.ColumnNames;
        IReadOnlyList<DataKind> kinds = projection.ColumnKinds;
        IReadOnlyList<bool> isList = projection.IsListColumn;
        IReadOnlyList<DataKind> elementKinds = projection.ElementKinds;

        Dictionary<string, int> nameIndex = new(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            nameIndex[names[i]] = i;

        IValueStore store = context.Arena;
        RowBatch? batch = null;

        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using ParquetRowGroupReader rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            long rowCount = rowGroupReader.RowCount;

            // Read all projected columns for this row group.
            DataColumn[] dataColumns = new DataColumn[projection.Count];
            for (int col = 0; col < projection.Count; col++)
            {
                dataColumns[col] = await rowGroupReader.ReadColumnAsync(projection.DataFields[col], cancellationToken: cancellationToken);
            }

            // Pre-process list columns into per-row array values (pool-rented).
            DataValue[]?[]? listColumnValues = null;
            for (int col = 0; col < projection.Count; col++)
            {
                if (isList[col])
                {
                    listColumnValues ??= new DataValue[projection.Count][];
                    listColumnValues[col] = ParquetListReconstructor.Reconstruct(
                        dataColumns[col], elementKinds[col], rowCount, store, context.Pool);
                }
            }

            // Yield row-by-row with zero-boxing extraction.
            for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = context.Pool.RentDataValues(projection.Count);

                for (int col = 0; col < projection.Count; col++)
                {
                    if (isList[col] && listColumnValues is not null)
                    {
                        values[col] = listColumnValues[col]![(int)rowIndex];
                    }
                    else
                    {
                        values[col] = ParquetValueExtractor.Extract(
                            dataColumns[col], kinds[col], rowIndex, store);
                    }
                }

                batch ??= context.Pool.RentBatch(DefaultBatchSize);
                batch.Add(new Row(names, values, nameIndex));

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }

            // Return pool-rented list column arrays.
            if (listColumnValues is not null)
            {
                for (int col = 0; col < listColumnValues.Length; col++)
                {
                    if (listColumnValues[col] is not null)
                        context.Pool.ReturnDataValues(listColumnValues[col]!);
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }
}
