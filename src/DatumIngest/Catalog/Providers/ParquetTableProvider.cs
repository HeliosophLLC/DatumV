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
    IAsyncEnumerable<Row> IFilterableTableProvider.OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
        CancellationToken cancellationToken)
    {
        return OpenWithFilterAsync(descriptor, requiredColumns, filterHint, cancellationToken);
    }

    private async IAsyncEnumerable<Row> OpenWithFilterAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        Expression filterHint,
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

        // Collect referenced columns from the filter for statistics lookup.
        HashSet<string> filterColumns = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(filterHint))
        {
            filterColumns.Add(columnName);
        }

        // Build a lookup from column name to DataField for statistics queries.
        Dictionary<string, DataField> fieldsByName = new(allFields.Length, StringComparer.OrdinalIgnoreCase);
        foreach (DataField field in allFields)
        {
            fieldsByName[field.Name] = field;
        }

        TotalRowGroups = reader.RowGroupCount;
        PrunedRowGroups = 0;

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
            DataKind.Float32 => ConvertToScalar(value),
            DataKind.String => DataValue.FromString(value.ToString() ?? string.Empty),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(value)),
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
    public async IAsyncEnumerable<Row> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using FileStream stream = File.OpenRead(descriptor.FilePath);
        using ParquetReader reader = await ParquetReader.CreateAsync(stream);

        DataField[] allFields = reader.Schema.GetDataFields();

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

            for (long rowIndex = localStart; rowIndex < localEnd && remaining > 0; rowIndex++)
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
                remaining--;
            }

            cumulativeRowOffset = rowGroupEnd;
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

        if (underlyingType == typeof(float) ||
            underlyingType == typeof(double) ||
            underlyingType == typeof(int) ||
            underlyingType == typeof(long) ||
            underlyingType == typeof(short) ||
            underlyingType == typeof(decimal))
        {
            return DataKind.Float32;
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
            DataKind.Float32 => ConvertToScalar(element),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(element)),
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
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            _ => new DateTimeOffset(Convert.ToDateTime(value), TimeSpan.Zero),
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

        return DataValue.FromFloat32(floatValue);
    }
}
