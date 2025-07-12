using System.Runtime.CompilerServices;
using DatumIngest.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads Apache Parquet files via the Parquet.Net low-level columnar API.
/// Maps Parquet column types to <see cref="DataKind"/>, supports projection pushdown,
/// and reads across multiple row groups.
/// </summary>
public sealed class ParquetTableProvider : ITableProvider
{
    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        DataField[] dataFields = reader.Schema.GetDataFields();
        List<ColumnInfo> columns = new(dataFields.Length);

        foreach (DataField field in dataFields)
        {
            DataKind kind = MapClrTypeToDataKind(field.ClrType);
            columns.Add(new ColumnInfo(field.Name, kind, nullable: field.IsNullable));
        }

        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        DataField[] allFields = reader.Schema.GetDataFields();

        // Apply projection pushdown
        DataField[] projectedFields;
        if (requiredColumns is not null)
        {
            projectedFields = Array.FindAll(allFields, field => requiredColumns.Contains(field.Name));
        }
        else
        {
            projectedFields = allFields;
        }

        if (projectedFields.Length == 0)
        {
            yield break;
        }

        // Build column names array once
        string[] columnNames = new string[projectedFields.Length];
        DataKind[] columnKinds = new DataKind[projectedFields.Length];
        for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
        {
            columnNames[fieldIndex] = projectedFields[fieldIndex].Name;
            columnKinds[fieldIndex] = MapClrTypeToDataKind(projectedFields[fieldIndex].ClrType);
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int fieldIndex = 0; fieldIndex < columnNames.Length; fieldIndex++)
        {
            nameIndex[columnNames[fieldIndex]] = fieldIndex;
        }

        // Iterate all row groups
        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using ParquetRowGroupReader rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            long rowCount = rowGroupReader.RowCount;

            // Read all projected columns for this row group
            DataColumn[] dataColumns = new DataColumn[projectedFields.Length];
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                dataColumns[fieldIndex] = await rowGroupReader.ReadColumnAsync(projectedFields[fieldIndex]);
            }

            // Yield row-by-row
            for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = new DataValue[projectedFields.Length];
                for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
                {
                    values[fieldIndex] = ExtractValue(
                        dataColumns[fieldIndex],
                        columnKinds[fieldIndex],
                        rowIndex);
                }

                yield return new Row(columnNames, values, nameIndex);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        long totalRows = 0;
        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using ParquetRowGroupReader rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            totalRows += rowGroupReader.RowCount;
        }

        return new ProviderCapabilities(
            EstimatedRowCount: totalRows,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>());
    }

    // ───────────────────── Type mapping ─────────────────────

    /// <summary>
    /// Maps a Parquet CLR type to the engine's <see cref="DataKind"/>.
    /// </summary>
    private static DataKind MapClrTypeToDataKind(Type clrType)
    {
        // Unwrap Nullable<T> to get the underlying type
        Type underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlyingType == typeof(float) ||
            underlyingType == typeof(double) ||
            underlyingType == typeof(int) ||
            underlyingType == typeof(long) ||
            underlyingType == typeof(short) ||
            underlyingType == typeof(decimal))
        {
            return DataKind.Scalar;
        }

        if (underlyingType == typeof(byte))
        {
            return DataKind.UInt8;
        }

        if (underlyingType == typeof(string))
        {
            return DataKind.String;
        }

        if (underlyingType == typeof(byte[]))
        {
            return DataKind.UInt8Array;
        }

        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
        {
            return DataKind.DateTime;
        }

        if (underlyingType == typeof(DateOnly))
        {
            return DataKind.Date;
        }

        // Booleans, enums, and other types → String representation
        return DataKind.String;
    }

    // ───────────────────── Value extraction ─────────────────────

    /// <summary>
    /// Extracts a single <see cref="DataValue"/> from a Parquet <see cref="DataColumn"/>
    /// at the given row index.
    /// </summary>
    private static DataValue ExtractValue(DataColumn column, DataKind kind, long rowIndex)
    {
        Array data = column.Data;
        object? element = data.GetValue(rowIndex);

        if (element is null)
        {
            return DataValue.Null(kind);
        }

        return kind switch
        {
            DataKind.Scalar => ConvertToScalar(element),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(element)),
            DataKind.String => DataValue.FromString(element.ToString() ?? string.Empty),
            DataKind.UInt8Array => DataValue.FromUInt8Array((byte[])element),
            DataKind.DateTime => DataValue.FromDateTime(Convert.ToDateTime(element)),
            DataKind.Date => DataValue.FromDate(element is DateOnly dateOnly
                ? dateOnly
                : DateOnly.FromDateTime(Convert.ToDateTime(element))),
            _ => DataValue.FromString(element.ToString() ?? string.Empty)
        };
    }

    /// <summary>
    /// Converts a numeric value to a <see cref="DataValue"/> scalar (float32).
    /// </summary>
    private static DataValue ConvertToScalar(object value)
    {
        float floatValue = value switch
        {
            float floatVal => floatVal,
            double doubleVal => (float)doubleVal,
            int intVal => intVal,
            long longVal => longVal,
            short shortVal => shortVal,
            byte byteVal => byteVal,
            decimal decimalVal => (float)decimalVal,
            _ => Convert.ToSingle(value)
        };

        return DataValue.FromScalar(floatValue);
    }
}
