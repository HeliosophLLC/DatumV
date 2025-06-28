namespace Axon.QueryEngine.Output.Writers;

using Axon.QueryEngine.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

/// <summary>
/// Writes query results to Parquet files. Buffers rows and writes as a single row group on finalize.
/// </summary>
public sealed class ParquetOutputWriter : IOutputWriter
{
    private readonly string _filePath;
    private Schema? _schema;
    private readonly List<Row> _rows = new();
    private long _rowsWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetOutputWriter"/> class.
    /// </summary>
    /// <param name="filePath">The output Parquet file path.</param>
    public ParquetOutputWriter(string filePath)
    {
        _filePath = filePath;
    }

    /// <inheritdoc />
    public Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        _rows.Add(row);
        _rowsWritten++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        ParquetSchema parquetSchema = BuildParquetSchema(_schema);

        using FileStream stream = File.Create(_filePath);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(parquetSchema, stream);
        writer.CompressionMethod = CompressionMethod.Snappy;

        using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();

        for (int columnIndex = 0; columnIndex < _schema.Columns.Count; columnIndex++)
        {
            ColumnInfo column = _schema.Columns[columnIndex];
            DataColumn dataColumn = BuildDataColumn(parquetSchema.DataFields[columnIndex], column);
            await rowGroup.WriteColumnAsync(dataColumn);
        }

        long bytesWritten = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        return new OutputSummary(_rowsWritten, bytesWritten, [_filePath]);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static ParquetSchema BuildParquetSchema(Schema schema)
    {
        DataField[] fields = new DataField[schema.Columns.Count];

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo column = schema.Columns[i];
            fields[i] = column.Kind switch
            {
                DataKind.Scalar => new DataField<float>(column.Name),
                DataKind.UInt8 => new DataField<int>(column.Name),
                DataKind.String => new DataField<string>(column.Name),
                DataKind.JsonValue => new DataField<string>(column.Name),
                DataKind.Date => new DataField<string>(column.Name),
                DataKind.DateTime => new DataField<string>(column.Name),
                _ => new DataField<string>(column.Name)
            };
        }

        return new ParquetSchema(fields);
    }

    private DataColumn BuildDataColumn(DataField field, ColumnInfo column)
    {
        int rowCount = _rows.Count;

        return column.Kind switch
        {
            DataKind.Scalar => BuildFloatColumn(field, column.Name, rowCount),
            DataKind.UInt8 => BuildIntColumn(field, column.Name, rowCount),
            DataKind.String => BuildStringColumn(field, column),
            DataKind.JsonValue => BuildJsonColumn(field, column.Name, rowCount),
            DataKind.Date => BuildDateColumn(field, column.Name, rowCount),
            DataKind.DateTime => BuildDateTimeColumn(field, column.Name, rowCount),
            _ => BuildFallbackStringColumn(field, column.Name, rowCount)
        };
    }

    private DataColumn BuildFloatColumn(DataField field, string columnName, int rowCount)
    {
        float[] data = new float[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? float.NaN : value.AsScalar();
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildIntColumn(DataField field, string columnName, int rowCount)
    {
        int[] data = new int[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? 0 : value.AsUInt8();
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildStringColumn(DataField field, ColumnInfo column)
    {
        string[] data = new string[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            DataValue value = _rows[i][column.Name];
            data[i] = value.IsNull ? "" : value.AsString();
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildJsonColumn(DataField field, string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsJsonValue();
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildDateColumn(DataField field, string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsDate().ToString("yyyy-MM-dd");
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildDateTimeColumn(DataField field, string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsDateTime().ToString("O");
        }

        return new DataColumn(field, data);
    }

    private DataColumn BuildFallbackStringColumn(DataField field, string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : (value.ToString() ?? "");
        }

        return new DataColumn(field, data);
    }
}
