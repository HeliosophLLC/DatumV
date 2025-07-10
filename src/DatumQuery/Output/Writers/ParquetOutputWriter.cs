namespace DatumQuery.Output.Writers;

using DatumQuery.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

/// <summary>
/// Writes query results to Parquet files. Buffers rows and writes as a single row group on finalize.
/// Binary columns (<see cref="DataKind.UInt8Array"/>, <see cref="DataKind.Image"/>) are externalized
/// to an <c>images/</c> folder next to the Parquet file, with GUID filenames and the relative path
/// stored as a string column in the Parquet data.
/// </summary>
public sealed class ParquetOutputWriter : IOutputWriter
{
    private readonly string? _filePath;
    private readonly Stream? _outputStream;
    private Schema? _schema;
    private readonly List<Row> _rows = new();
    private readonly List<string> _externalizedFiles = new();
    private long _rowsWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetOutputWriter"/> class that writes to a file.
    /// Binary columns are externalized to an <c>images/</c> folder next to the Parquet file.
    /// </summary>
    /// <param name="filePath">The output Parquet file path.</param>
    public ParquetOutputWriter(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetOutputWriter"/> class that writes to a stream.
    /// Binary columns (<see cref="DataKind.UInt8Array"/>, <see cref="DataKind.Image"/>) are embedded
    /// directly as <c>byte[]</c> in the Parquet data instead of being externalized to disk.
    /// The caller retains ownership of the stream and is responsible for disposing it.
    /// </summary>
    /// <param name="outputStream">The stream to write Parquet data to.</param>
    public ParquetOutputWriter(Stream outputStream)
    {
        _outputStream = outputStream;
    }

    /// <inheritdoc />
    public Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;

        if (_filePath is not null)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
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

        bool isStreamMode = _outputStream is not null;

        // Externalize binary columns to images/ folder only in file mode.
        if (!isStreamMode)
        {
            ExternalizeBinaryColumns(_schema);
        }

        ParquetSchema parquetSchema = BuildParquetSchema(_schema, isStreamMode);

        Stream targetStream = isStreamMode ? _outputStream! : File.Create(_filePath!);
        try
        {
            using ParquetWriter writer = await ParquetWriter.CreateAsync(parquetSchema, targetStream);
            writer.CompressionMethod = CompressionMethod.Snappy;

            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();

            for (int columnIndex = 0; columnIndex < _schema.Columns.Count; columnIndex++)
            {
                ColumnInfo column = _schema.Columns[columnIndex];
                DataColumn dataColumn = BuildDataColumn(parquetSchema.DataFields[columnIndex], column, isStreamMode);
                await rowGroup.WriteColumnAsync(dataColumn);
            }
        }
        finally
        {
            // Only dispose the stream if we created it (file mode).
            if (!isStreamMode)
            {
                await targetStream.DisposeAsync();
            }
        }

        if (isStreamMode)
        {
            long bytesWritten = _outputStream!.CanSeek ? _outputStream.Position : 0;
            return new OutputSummary(_rowsWritten, bytesWritten, []);
        }

        long fileBytesWritten = File.Exists(_filePath) ? new FileInfo(_filePath!).Length : 0;
        List<string> allFiles = [_filePath!, .. _externalizedFiles];
        return new OutputSummary(_rowsWritten, fileBytesWritten, allFiles);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void ExternalizeBinaryColumns(Schema schema)
    {
        string parquetDir = Path.GetDirectoryName(_filePath!) ?? ".";
        string imagesDir = Path.Combine(parquetDir, "images");
        bool imagesDirCreated = false;

        // Build shared schema arrays once for all replacements.
        string[]? sharedNames = null;
        Dictionary<string, int>? sharedNameIndex = null;
        if (_rows.Count > 0)
        {
            Row firstRow = _rows[0];
            sharedNames = new string[firstRow.FieldCount];
            for (int i = 0; i < firstRow.FieldCount; i++)
            {
                sharedNames[i] = firstRow.ColumnNames[i];
            }

            sharedNameIndex = new Dictionary<string, int>(sharedNames.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sharedNames.Length; i++)
            {
                sharedNameIndex[sharedNames[i]] = i;
            }
        }

        for (int columnIndex = 0; columnIndex < schema.Columns.Count; columnIndex++)
        {
            ColumnInfo column = schema.Columns[columnIndex];
            if (column.Kind is not (DataKind.UInt8Array or DataKind.Image))
            {
                continue;
            }

            if (!imagesDirCreated)
            {
                Directory.CreateDirectory(imagesDir);
                imagesDirCreated = true;
            }

            for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
            {
                Row row = _rows[rowIndex];
                DataValue value = row[column.Name];

                if (value.IsNull)
                {
                    // Replace with a null-path marker.
                    row = ReplaceColumnValue(row, column.Name, DataValue.FromString(""), sharedNames!, sharedNameIndex!);
                    _rows[rowIndex] = row;
                    continue;
                }

                byte[] bytes = column.Kind == DataKind.Image
                    ? value.AsImage()
                    : value.AsUInt8Array();

                string extension = DetectFileExtension(bytes);
                string fileName = $"{Guid.NewGuid():N}{extension}";
                string absolutePath = Path.Combine(imagesDir, fileName);

                File.WriteAllBytes(absolutePath, bytes);
                _externalizedFiles.Add(absolutePath);

                // Replace the binary value with the relative path string.
                string relativePath = $"images/{fileName}";
                row = ReplaceColumnValue(row, column.Name, DataValue.FromString(relativePath), sharedNames!, sharedNameIndex!);
                _rows[rowIndex] = row;
            }
        }
    }

    /// <summary>
    /// Replaces a single column value in a row, reusing the shared schema arrays
    /// to avoid per-call name and name-index allocations.
    /// </summary>
    private static Row ReplaceColumnValue(
        Row row,
        string columnName,
        DataValue newValue,
        string[] sharedNames,
        Dictionary<string, int> sharedNameIndex)
    {
        DataValue[] values = new DataValue[row.FieldCount];

        for (int i = 0; i < row.FieldCount; i++)
        {
            values[i] = row.ColumnNames[i] == columnName ? newValue : row[i];
        }

        return new Row(sharedNames, values, sharedNameIndex);
    }

    private static string DetectFileExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ".png";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
        {
            return ".webp";
        }

        return ".bin";
    }

    private static ParquetSchema BuildParquetSchema(Schema schema, bool embedBinary)
    {
        DataField[] fields = new DataField[schema.Columns.Count];

        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo column = schema.Columns[i];
            fields[i] = column.Kind switch
            {
                DataKind.Scalar => new DataField<float>(column.Name),
                DataKind.UInt8 => new DataField<int>(column.Name),
                DataKind.UInt8Array or DataKind.Image when embedBinary => new DataField<byte[]>(column.Name),
                DataKind.UInt8Array or DataKind.Image => new DataField<string>(column.Name),
                DataKind.String => new DataField<string>(column.Name),
                DataKind.JsonValue => new DataField<string>(column.Name),
                DataKind.Date => new DataField<string>(column.Name),
                DataKind.DateTime => new DataField<string>(column.Name),
                _ => new DataField<string>(column.Name)
            };
        }

        return new ParquetSchema(fields);
    }

    private DataColumn BuildDataColumn(DataField field, ColumnInfo column, bool embedBinary)
    {
        int rowCount = _rows.Count;

        // After externalization, binary columns have been replaced with string paths.
        // In stream mode, binary columns are embedded directly.
        return column.Kind switch
        {
            DataKind.Scalar => BuildFloatColumn(field, column.Name, rowCount),
            DataKind.UInt8 => BuildIntColumn(field, column.Name, rowCount),
            DataKind.UInt8Array or DataKind.Image when embedBinary => BuildBinaryColumn(field, column, rowCount),
            DataKind.UInt8Array or DataKind.Image => BuildExternalizedPathColumn(field, column.Name, rowCount),
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

    private DataColumn BuildExternalizedPathColumn(DataField field, string columnName, int rowCount)
    {
        // After externalization, these cells contain string paths.
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsString();
        }

        return new DataColumn(field, data);
    }

    /// <summary>
    /// Builds a binary column that embeds raw byte arrays directly in the Parquet data.
    /// Used in stream mode where externalization to disk is not possible.
    /// </summary>
    private DataColumn BuildBinaryColumn(DataField field, ColumnInfo column, int rowCount)
    {
        byte[][] data = new byte[rowCount][];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][column.Name];
            if (value.IsNull)
            {
                data[i] = [];
            }
            else
            {
                data[i] = column.Kind == DataKind.Image
                    ? value.AsImage()
                    : value.AsUInt8Array();
            }
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
