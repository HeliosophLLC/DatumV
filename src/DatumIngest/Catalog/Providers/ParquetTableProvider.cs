using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads Apache Parquet files via the Parquet.Net low-level columnar API.
/// Maps Parquet column types to <see cref="DataKind"/>, supports projection pushdown,
/// statistics-based row group pruning, and reads across multiple row groups.
/// </summary>
public sealed class ParquetTableProvider : ITableProvider, IFilterableTableProvider, ISeekableTableProvider
{
    /// <summary>Total number of row groups examined in the most recent read.</summary>
    public int TotalRowGroups { get; private set; }

    /// <summary>Number of row groups skipped by statistics-based pruning in the most recent read.</summary>
    public int PrunedRowGroups { get; private set; }

    private const int DefaultBatchSize = 1024;

    /// <inheritdoc />
    public async Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        List<ColumnInfo> columns = new();

        foreach (Field field in reader.Schema.Fields)
        {
            if (field is ListField listField && listField.Item is DataField itemField)
            {
                DataKind elementKind = MapClrTypeToDataKind(itemField.ClrType);
                columns.Add(new ColumnInfo(field.Name, DataKind.Array, nullable: true, arrayElementKind: elementKind));
            }
            else if (field is DataField dataField)
            {
                DataKind kind = MapClrTypeToDataKind(dataField.ClrType);
                columns.Add(new ColumnInfo(field.Name, kind, nullable: dataField.IsNullable));
            }
        }

        return new Schema(columns);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        (DataField[] projectedFields, string[] columnNames, DataKind[] columnKinds,
            bool[] isListColumn, DataKind[] elementKinds) = BuildProjectedColumns(reader, requiredColumns);

        if (projectedFields.Length == 0)
        {
            yield break;
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int fieldIndex = 0; fieldIndex < columnNames.Length; fieldIndex++)
        {
            nameIndex[columnNames[fieldIndex]] = fieldIndex;
        }

        RowBatch? batch = null;

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

            // Pre-process list columns into per-row array values.
            DataValue[]?[]? listColumnValues = null;
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                if (isListColumn[fieldIndex])
                {
                    listColumnValues ??= new DataValue[projectedFields.Length][];
                    listColumnValues[fieldIndex] = ReconstructArrayValues(
                        dataColumns[fieldIndex], elementKinds[fieldIndex], rowCount);
                }
            }

            // Yield row-by-row
            for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(projectedFields.Length);
                for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
                {
                    if (isListColumn[fieldIndex] && listColumnValues is not null)
                    {
                        values[fieldIndex] = listColumnValues[fieldIndex]![(int)rowIndex];
                    }
                    else
                    {
                        values[fieldIndex] = ExtractValue(
                            dataColumns[fieldIndex],
                            columnKinds[fieldIndex],
                            rowIndex);
                    }
                }

                batch ??= RowBatch.Rent(DefaultBatchSize);
                batch.Add(new Row(columnNames, values, nameIndex));
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    IAsyncEnumerable<RowBatch> IFilterableTableProvider.OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        CancellationToken cancellationToken)
    {
        return OpenWithFilterAsync(descriptor, requiredColumns, filterHint, cancellationToken);
    }

    private async IAsyncEnumerable<RowBatch> OpenWithFilterAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        (DataField[] projectedFields, string[] columnNames, DataKind[] columnKinds,
            bool[] isListColumn, DataKind[] elementKinds) = BuildProjectedColumns(reader, requiredColumns);

        if (projectedFields.Length == 0)
        {
            yield break;
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int fieldIndex = 0; fieldIndex < columnNames.Length; fieldIndex++)
        {
            nameIndex[columnNames[fieldIndex]] = fieldIndex;
        }

        // Collect referenced columns from the filter for statistics lookup.
        HashSet<string> filterColumns = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filterHint))
        {
            filterColumns.Add(columnName);
        }

        // Build a lookup from column name to DataField for statistics queries.
        // Only scalar (non-list) columns have meaningful statistics.
        Dictionary<string, DataField> fieldsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (Field field in reader.Schema.Fields)
        {
            if (field is DataField dataField)
            {
                fieldsByName[field.Name] = dataField;
            }
        }

        TotalRowGroups = reader.RowGroupCount;
        PrunedRowGroups = 0;

        RowBatch? batch = null;

        // Iterate row groups with statistics-based pruning
        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using ParquetRowGroupReader rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            long rowCount = rowGroupReader.RowCount;

            // Try to skip this row group using column statistics.
            if (TryBuildStatistics(rowGroupReader, filterColumns, fieldsByName, rowCount,
                    out Dictionary<string, ColumnStatisticsRange>? statistics)
                && StatisticsPredicateEvaluator.CanSkipPartition(filterHint, statistics!))
            {
                PrunedRowGroups++;
                continue;
            }

            // Read all projected columns for this row group
            DataColumn[] dataColumns = new DataColumn[projectedFields.Length];
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                dataColumns[fieldIndex] = await rowGroupReader.ReadColumnAsync(projectedFields[fieldIndex]);
            }

            // Pre-process list columns into per-row array values.
            DataValue[]?[]? listColumnValues = null;
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                if (isListColumn[fieldIndex])
                {
                    listColumnValues ??= new DataValue[projectedFields.Length][];
                    listColumnValues[fieldIndex] = ReconstructArrayValues(
                        dataColumns[fieldIndex], elementKinds[fieldIndex], rowCount);
                }
            }

            // Yield row-by-row
            for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(projectedFields.Length);
                for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
                {
                    if (isListColumn[fieldIndex] && listColumnValues is not null)
                    {
                        values[fieldIndex] = listColumnValues[fieldIndex]![(int)rowIndex];
                    }
                    else
                    {
                        values[fieldIndex] = ExtractValue(
                            dataColumns[fieldIndex],
                            columnKinds[fieldIndex],
                            rowIndex);
                    }
                }

                batch ??= RowBatch.Rent(DefaultBatchSize);
                batch.Add(new Row(columnNames, values, nameIndex));
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Attempts to collect column statistics from a row group for the given filter columns.
    /// Returns <c>false</c> if no statistics are available for any referenced column.
    /// </summary>
    private static bool TryBuildStatistics(
        ParquetRowGroupReader rowGroupReader,
        HashSet<string> filterColumns,
        Dictionary<string, DataField> fieldsByName,
        long rowCount,
        out Dictionary<string, ColumnStatisticsRange>? statistics)
    {
        statistics = null;

        foreach (string columnName in filterColumns)
        {
            if (!fieldsByName.TryGetValue(columnName, out DataField? field))
            {
                continue;
            }

            DataColumnStatistics? parquetStatistics = rowGroupReader.GetStatistics(field);
            if (parquetStatistics is null)
            {
                continue;
            }

            DataKind kind = MapClrTypeToDataKind(field.ClrType);
            DataValue? minimum = ConvertStatisticsValue(parquetStatistics.MinValue, kind);
            DataValue? maximum = ConvertStatisticsValue(parquetStatistics.MaxValue, kind);

            if (minimum is null && maximum is null && parquetStatistics.NullCount is null)
            {
                continue; // No usable statistics for this column.
            }

            statistics ??= new Dictionary<string, ColumnStatisticsRange>(StringComparer.OrdinalIgnoreCase);
            statistics[columnName] = new ColumnStatisticsRange(
                minimum, maximum, parquetStatistics.NullCount, rowCount);
        }

        return statistics is not null;
    }

    /// <summary>
    /// Converts a Parquet statistics value (CLR-typed object) to a <see cref="DataValue"/>.
    /// </summary>
    private static DataValue? ConvertStatisticsValue(object? value, DataKind kind)
    {
        if (value is null)
        {
            return null;
        }

        return kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(Convert.ToSingle(value)),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(value)),
            DataKind.Int8 => DataValue.FromInt8(Convert.ToSByte(value)),
            DataKind.Int16 => DataValue.FromInt16(Convert.ToInt16(value)),
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(value)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(value)),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(value)),
            DataKind.UInt16 => DataValue.FromUInt16(Convert.ToUInt16(value)),
            DataKind.UInt32 => DataValue.FromUInt32(Convert.ToUInt32(value)),
            DataKind.UInt64 => DataValue.FromUInt64(Convert.ToUInt64(value)),
            DataKind.String => DataValue.FromString(value.ToString() ?? string.Empty),
            DataKind.DateTime => DataValue.FromDateTime(ConvertToDateTimeOffset(value)),
            DataKind.Date => value is DateOnly dateOnly
                ? DataValue.FromDate(dateOnly)
                : DataValue.FromDate(DateOnly.FromDateTime(Convert.ToDateTime(value))),
            _ => null,
        };
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
            SupportsSeek: true,
            ColumnCosts: new Dictionary<string, ColumnCost>());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        (DataField[] projectedFields, string[] columnNames, DataKind[] columnKinds,
            bool[] isListColumn, DataKind[] elementKinds) = BuildProjectedColumns(reader, requiredColumns);

        if (projectedFields.Length == 0)
        {
            yield break;
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int fieldIndex = 0; fieldIndex < columnNames.Length; fieldIndex++)
        {
            nameIndex[columnNames[fieldIndex]] = fieldIndex;
        }

        RowBatch? batch = null;

        // Walk row groups to find the range [startRow, startRow + count).
        long cumulativeRowOffset = 0;
        int remaining = count;

        for (int rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount && remaining > 0; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using ParquetRowGroupReader rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            long rowGroupRowCount = rowGroupReader.RowCount;
            long rowGroupEnd = cumulativeRowOffset + rowGroupRowCount;

            // Skip row groups entirely before the requested range.
            if (rowGroupEnd <= startRow)
            {
                cumulativeRowOffset = rowGroupEnd;
                continue;
            }

            // Determine the slice within this row group.
            long localStart = Math.Max(0, startRow - cumulativeRowOffset);
            long localEnd = Math.Min(rowGroupRowCount, startRow + count - cumulativeRowOffset);

            // Read all projected columns for this row group.
            DataColumn[] dataColumns = new DataColumn[projectedFields.Length];
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                dataColumns[fieldIndex] = await rowGroupReader.ReadColumnAsync(projectedFields[fieldIndex]);
            }

            // Pre-process list columns into per-row array values.
            DataValue[]?[]? listColumnValues = null;
            for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
            {
                if (isListColumn[fieldIndex])
                {
                    listColumnValues ??= new DataValue[projectedFields.Length][];
                    listColumnValues[fieldIndex] = ReconstructArrayValues(
                        dataColumns[fieldIndex], elementKinds[fieldIndex], rowGroupRowCount);
                }
            }

            for (long rowIndex = localStart; rowIndex < localEnd && remaining > 0; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DataValue[] values = DatumIngest.Execution.Pooling.GlobalPool.Backing.RentDataValues(projectedFields.Length);
                for (int fieldIndex = 0; fieldIndex < projectedFields.Length; fieldIndex++)
                {
                    if (isListColumn[fieldIndex] && listColumnValues is not null)
                    {
                        values[fieldIndex] = listColumnValues[fieldIndex]![(int)rowIndex];
                    }
                    else
                    {
                        values[fieldIndex] = ExtractValue(
                            dataColumns[fieldIndex],
                            columnKinds[fieldIndex],
                            rowIndex);
                    }
                }

                batch ??= RowBatch.Rent(DefaultBatchSize);
                batch.Add(new Row(columnNames, values, nameIndex));
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = null;
                }
                remaining--;
            }

            cumulativeRowOffset = rowGroupEnd;
        }

        if (batch is not null)
        {
            yield return batch;
        }
    }

    // ───────────────────── Type mapping ─────────────────────

    /// <summary>
    /// Maps a Parquet CLR type to the engine's <see cref="DataKind"/>.
    /// </summary>
    private static DataKind MapClrTypeToDataKind(Type clrType)
    {
        // Unwrap Nullable<T> to get the underlying type
        Type underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlyingType == typeof(float))
        {
            return DataKind.Float32;
        }

        if (underlyingType == typeof(double) || underlyingType == typeof(decimal))
        {
            return DataKind.Float64;
        }

        if (underlyingType == typeof(int))
        {
            return DataKind.Int32;
        }

        if (underlyingType == typeof(long))
        {
            return DataKind.Int64;
        }

        if (underlyingType == typeof(short))
        {
            return DataKind.Int16;
        }

        if (underlyingType == typeof(ushort))
        {
            return DataKind.UInt16;
        }

        if (underlyingType == typeof(uint))
        {
            return DataKind.UInt32;
        }

        if (underlyingType == typeof(ulong))
        {
            return DataKind.UInt64;
        }

        if (underlyingType == typeof(sbyte))
        {
            return DataKind.Int8;
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

        if (underlyingType == typeof(bool))
        {
            return DataKind.Boolean;
        }

        // Enums and other types → String representation
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
            DataKind.Float32 => DataValue.FromFloat32(Convert.ToSingle(element)),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(element)),
            DataKind.Int8 => DataValue.FromInt8(Convert.ToSByte(element)),
            DataKind.Int16 => DataValue.FromInt16(Convert.ToInt16(element)),
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(element)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(element)),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(element)),
            DataKind.UInt16 => DataValue.FromUInt16(Convert.ToUInt16(element)),
            DataKind.UInt32 => DataValue.FromUInt32(Convert.ToUInt32(element)),
            DataKind.UInt64 => DataValue.FromUInt64(Convert.ToUInt64(element)),
            DataKind.String => DataValue.FromString(element.ToString() ?? string.Empty),
            DataKind.UInt8Array => DataValue.FromUInt8Array((byte[])element),
            DataKind.DateTime => DataValue.FromDateTime(ConvertToDateTimeOffset(element)),
            DataKind.Date => DataValue.FromDate(element is DateOnly dateOnly
                ? dateOnly
                : DateOnly.FromDateTime(Convert.ToDateTime(element))),
            DataKind.Boolean => DataValue.FromBoolean(Convert.ToBoolean(element)),
            _ => DataValue.FromString(element.ToString() ?? string.Empty)
        };
    }

    /// <summary>
    /// Converts a CLR DateTime or DateTimeOffset value to <see cref="DateTimeOffset"/>.
    /// </summary>
    private static DateTimeOffset ConvertToDateTimeOffset(object value)
    {
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(Convert.ToDateTime(value).ToUniversalTime(), TimeSpan.Zero),
        };
    }

    // ───────────────────── List (array) column support ─────────────────────

    /// <summary>
    /// Builds projection metadata from the Parquet schema, handling both flat <see cref="DataField"/>
    /// columns and <see cref="ListField"/> (array) columns. Returns parallel arrays describing
    /// the projected columns.
    /// </summary>
    private static (DataField[] DataFields, string[] ColumnNames, DataKind[] ColumnKinds,
        bool[] IsListColumn, DataKind[] ElementKinds)
        BuildProjectedColumns(ParquetReader reader, IReadOnlySet<string>? requiredColumns)
    {
        List<DataField> dataFields = new();
        List<string> names = new();
        List<DataKind> kinds = new();
        List<bool> isList = new();
        List<DataKind> elementKinds = new();

        foreach (Field field in reader.Schema.Fields)
        {
            if (requiredColumns is not null && !requiredColumns.Contains(field.Name))
            {
                continue;
            }

            if (field is ListField listField && listField.Item is DataField itemField)
            {
                DataKind elementKind = MapClrTypeToDataKind(itemField.ClrType);
                dataFields.Add(itemField);
                names.Add(field.Name);
                kinds.Add(DataKind.Array);
                isList.Add(true);
                elementKinds.Add(elementKind);
            }
            else if (field is DataField dataField)
            {
                dataFields.Add(dataField);
                names.Add(field.Name);
                kinds.Add(MapClrTypeToDataKind(dataField.ClrType));
                isList.Add(false);
                elementKinds.Add(default);
            }
        }

        return (dataFields.ToArray(), names.ToArray(), kinds.ToArray(), isList.ToArray(), elementKinds.ToArray());
    }

    /// <summary>
    /// Reconstructs per-row <see cref="DataValue"/> array values from a flat Parquet list column
    /// using its repetition levels to determine list boundaries.
    /// </summary>
    private static DataValue[] ReconstructArrayValues(DataColumn column, DataKind elementKind, long rowCount)
    {
        DataValue[] result = new DataValue[rowCount];
        int[]? repetitionLevels = column.RepetitionLevels;
        Array data = column.Data;

        if (repetitionLevels is null || data.Length == 0)
        {
            for (long i = 0; i < rowCount; i++)
            {
                result[i] = DataValue.NullArray(elementKind);
            }

            return result;
        }

        int rowIndex = -1;
        List<DataValue> currentElements = new();

        for (int i = 0; i < repetitionLevels.Length; i++)
        {
            if (repetitionLevels[i] == 0)
            {
                // New list starts — emit the previous list.
                if (rowIndex >= 0)
                {
                    result[rowIndex] = currentElements.Count > 0
                        ? DataValue.FromArray(elementKind, currentElements.ToArray())
                        : DataValue.NullArray(elementKind);
                    currentElements.Clear();
                }

                rowIndex++;
            }

            object? element = data.GetValue(i);
            if (element is not null)
            {
                currentElements.Add(ExtractElementValue(element, elementKind));
            }
        }

        // Emit the last list.
        if (rowIndex >= 0 && rowIndex < rowCount)
        {
            result[rowIndex] = currentElements.Count > 0
                ? DataValue.FromArray(elementKind, currentElements.ToArray())
                : DataValue.NullArray(elementKind);
            rowIndex++;
        }

        // Fill remaining rows if any (e.g. trailing null lists with no RL entries).
        while (rowIndex < rowCount)
        {
            result[rowIndex] = DataValue.NullArray(elementKind);
            rowIndex++;
        }

        return result;
    }

    /// <summary>
    /// Converts a single CLR element value from a Parquet list column to a <see cref="DataValue"/>.
    /// </summary>
    private static DataValue ExtractElementValue(object element, DataKind kind)
    {
        return kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(Convert.ToSingle(element)),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(element)),
            DataKind.Int8 => DataValue.FromInt8(Convert.ToSByte(element)),
            DataKind.Int16 => DataValue.FromInt16(Convert.ToInt16(element)),
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(element)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(element)),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(element)),
            DataKind.UInt16 => DataValue.FromUInt16(Convert.ToUInt16(element)),
            DataKind.UInt32 => DataValue.FromUInt32(Convert.ToUInt32(element)),
            DataKind.UInt64 => DataValue.FromUInt64(Convert.ToUInt64(element)),
            DataKind.String => DataValue.FromString(element.ToString() ?? string.Empty),
            DataKind.Boolean => DataValue.FromBoolean(Convert.ToBoolean(element)),
            _ => DataValue.FromString(element.ToString() ?? string.Empty),
        };
    }

}
