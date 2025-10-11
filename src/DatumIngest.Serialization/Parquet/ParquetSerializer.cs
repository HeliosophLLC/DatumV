using DatumIngest.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DatumIngest.Serialization.Parquet;

/// <summary>
/// Serializes a stream of <see cref="RowBatch"/> instances into Apache Parquet format.
/// Each batch is written as one row group. Schema is inferred from the first batch.
/// Uses native Parquet types (int, long, double, bool, DateTimeOffset) where possible.
/// </summary>
public sealed class ParquetSerializer : IFormatSerializer
{
    private readonly OutputDescriptor _descriptor;

    /// <summary>Creates a serializer for the given output descriptor.</summary>
    public ParquetSerializer(OutputDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = await _descriptor.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Peek the first non-empty batch to infer schema.
        RowBatch? firstBatch = null;
        var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current.Count > 0)
                {
                    firstBatch = enumerator.Current;
                    break;
                }
            }

            if (firstBatch is null)
                return; // No data — nothing to write.

            Row firstRow = firstBatch[0];
            IReadOnlyList<string> columnNames = firstRow.ColumnNames;
            DataKind[] columnKinds = new DataKind[firstRow.FieldCount];
            for (int i = 0; i < firstRow.FieldCount; i++)
                columnKinds[i] = firstRow[i].Kind;

            ParquetSchema schema = BuildSchema(columnNames, columnKinds);
            using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: cancellationToken);
            writer.CompressionMethod = CompressionMethod.Snappy;

            // Write the first batch.
            await WriteRowGroup(writer, schema, firstBatch, columnNames, columnKinds, context.Arena, cancellationToken);

            // Write remaining batches.
            while (await enumerator.MoveNextAsync())
            {
                RowBatch batch = enumerator.Current;
                if (batch.Count == 0) continue;
                await WriteRowGroup(writer, schema, batch, columnNames, columnKinds, context.Arena, cancellationToken);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static async Task WriteRowGroup(
        ParquetWriter writer,
        ParquetSchema schema,
        RowBatch batch,
        IReadOnlyList<string> columnNames,
        DataKind[] columnKinds,
        IValueStore store,
        CancellationToken cancellationToken)
    {
        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();

        for (int col = 0; col < columnNames.Count; col++)
        {
            DataField dataField = schema.DataFields[col];
            DataColumn dataColumn = BuildColumn(dataField, batch, col, columnKinds[col], store);
            await rowGroup.WriteColumnAsync(dataColumn, cancellationToken);
        }
    }

    private static DataColumn BuildColumn(
        DataField field, RowBatch batch, int colIndex, DataKind kind, IValueStore store)
    {
        int rowCount = batch.Count;

        return kind switch
        {
            DataKind.Boolean => BuildBooleanColumn(field, batch, colIndex, rowCount),
            DataKind.Int8 or DataKind.UInt8 or DataKind.Int16 or DataKind.UInt16 or DataKind.Int32
                => BuildIntColumn(field, batch, colIndex, rowCount),
            DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
                => BuildLongColumn(field, batch, colIndex, rowCount),
            DataKind.Float32 => BuildFloatColumn(field, batch, colIndex, rowCount),
            DataKind.Float64 => BuildDoubleColumn(field, batch, colIndex, rowCount),
            DataKind.Date => BuildDateColumn(field, batch, colIndex, rowCount),
            DataKind.DateTime => BuildDateTimeColumn(field, batch, colIndex, rowCount),
            DataKind.Time => BuildTimeStringColumn(field, batch, colIndex, rowCount),
            DataKind.Duration => BuildDurationColumn(field, batch, colIndex, rowCount),
            DataKind.String => BuildStringColumn(field, batch, colIndex, rowCount, store),
            DataKind.JsonValue => BuildJsonValueColumn(field, batch, colIndex, rowCount, store),
            DataKind.Uuid => BuildUuidStringColumn(field, batch, colIndex, rowCount),
            DataKind.UInt8Array or DataKind.Image => BuildBinaryColumn(field, batch, colIndex, rowCount),
            _ => BuildFallbackStringColumn(field, batch, colIndex, rowCount),
        };
    }

    // ───────────────────────── Column builders ─────────────────────────

    private static DataColumn BuildBooleanColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        bool?[] data = new bool?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsBoolean();
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildIntColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        int?[] data = new int?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            if (v.IsNull) { data[i] = null; continue; }
            data[i] = v.Kind switch
            {
                DataKind.Int8 => v.AsInt8(),
                DataKind.UInt8 => v.AsUInt8(),
                DataKind.Int16 => v.AsInt16(),
                DataKind.UInt16 => v.AsUInt16(),
                _ => v.AsInt32(),
            };
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildLongColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        long?[] data = new long?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            if (v.IsNull) { data[i] = null; continue; }
            data[i] = v.Kind switch
            {
                DataKind.UInt32 => v.AsUInt32(),
                DataKind.UInt64 => (long)v.AsUInt64(),
                _ => v.AsInt64(),
            };
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildFloatColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        float?[] data = new float?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsFloat32();
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildDoubleColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        double?[] data = new double?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsFloat64();
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildDateColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        DateOnly?[] data = new DateOnly?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsDate();
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildDateTimeColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        DateTime?[] data = new DateTime?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsDateTime().UtcDateTime;
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildTimeStringColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        string?[] data = new string?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsTime().ToString("HH:mm:ss.FFFFFFF");
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildDurationColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        double?[] data = new double?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsDuration().TotalSeconds;
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildStringColumn(DataField field, RowBatch batch, int col, int rowCount, IValueStore store)
    {
        string?[] data = new string?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsString(store);
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildJsonValueColumn(DataField field, RowBatch batch, int col, int rowCount, IValueStore store)
    {
        string?[] data = new string?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsJsonValue(store);
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildUuidStringColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        string?[] data = new string?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.AsUuid().ToString("D");
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildBinaryColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        byte[]?[] data = new byte[]?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            if (v.IsNull) { data[i] = null; continue; }
            data[i] = v.Kind == DataKind.Image ? v.AsImage() : v.AsUInt8Array();
        }
        return new DataColumn(field, data);
    }

    private static DataColumn BuildFallbackStringColumn(DataField field, RowBatch batch, int col, int rowCount)
    {
        string?[] data = new string?[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = batch[i][col];
            data[i] = v.IsNull ? null : v.ToString();
        }
        return new DataColumn(field, data);
    }

    // ───────────────────────── Schema ─────────────────────────

    private static ParquetSchema BuildSchema(IReadOnlyList<string> names, DataKind[] kinds)
    {
        Field[] fields = new Field[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            fields[i] = kinds[i] switch
            {
                DataKind.Boolean => new DataField<bool?>(names[i]),
                DataKind.Int8 or DataKind.UInt8 or DataKind.Int16 or DataKind.UInt16 or DataKind.Int32
                    => new DataField<int?>(names[i]),
                DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
                    => new DataField<long?>(names[i]),
                DataKind.Float32 => new DataField<float?>(names[i]),
                DataKind.Float64 => new DataField<double?>(names[i]),
                DataKind.Date => new DataField<DateOnly?>(names[i]),
                DataKind.DateTime => new DataField<DateTime?>(names[i]),
                DataKind.Duration => new DataField<double?>(names[i]),
                DataKind.UInt8Array or DataKind.Image => new DataField<byte[]>(names[i]),
                _ => new DataField<string>(names[i]),  // String, JsonValue, Uuid, Time, fallback
            };
        }
        return new ParquetSchema(fields);
    }
}
