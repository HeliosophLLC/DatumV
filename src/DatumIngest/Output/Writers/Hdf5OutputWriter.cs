namespace DatumIngest.Output.Writers;

using DatumIngest.Model;
using PureHDF;

/// <summary>
/// Writes query results to HDF5 files. Each column becomes a dataset.
/// Buffers all rows in memory, then writes a single HDF5 file on finalize.
/// </summary>
public sealed class Hdf5OutputWriter : IOutputWriter
{
    private readonly string? _filePath;
    private readonly Stream? _outputStream;
    private Schema? _schema;
    private readonly List<Row> _rows = new();
    private long _rowsWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="Hdf5OutputWriter"/> class that writes to a file.
    /// </summary>
    /// <param name="filePath">The output HDF5 file path.</param>
    public Hdf5OutputWriter(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Hdf5OutputWriter"/> class that writes to a stream.
    /// The caller retains ownership of the stream and is responsible for disposing it.
    /// </summary>
    /// <param name="outputStream">The stream to write HDF5 data to.</param>
    public Hdf5OutputWriter(Stream outputStream)
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
    public Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        H5File file = new();

        foreach (ColumnInfo column in _schema.Columns)
        {
            object dataset = BuildDataset(column);
            file[column.Name] = dataset;
        }

        if (_outputStream is not null)
        {
            file.Write(_outputStream);
            long bytesWritten = _outputStream.CanSeek ? _outputStream.Position : 0;
            return Task.FromResult(new OutputSummary(_rowsWritten, bytesWritten, []));
        }

        file.Write(_filePath!);

        long fileBytesWritten = File.Exists(_filePath) ? new FileInfo(_filePath!).Length : 0;
        return Task.FromResult(new OutputSummary(_rowsWritten, fileBytesWritten, [_filePath!]));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private object BuildDataset(ColumnInfo column)
    {
        int rowCount = _rows.Count;

        return column.Kind switch
        {
            DataKind.Scalar => BuildScalarDataset(column.Name, rowCount),
            DataKind.UInt8 => BuildUInt8Dataset(column.Name, rowCount),
            DataKind.String or DataKind.JsonValue => BuildStringDataset(column),
            DataKind.Vector => BuildVectorDataset(column.Name, rowCount),
            DataKind.Matrix => BuildMatrixDataset(column.Name, rowCount),
            DataKind.UInt8Array or DataKind.Image => BuildBinaryDataset(column),
            DataKind.Date or DataKind.DateTime => BuildDateStringDataset(column),
            DataKind.Uuid => BuildUuidStringDataset(column.Name, rowCount),
            DataKind.Boolean => BuildBooleanDataset(column.Name, rowCount),
            DataKind.Time => BuildTimeStringDataset(column.Name, rowCount),
            DataKind.Duration => BuildDurationDoubleDataset(column.Name, rowCount),
            DataKind.Array => BuildArrayStringDataset(column.Name, rowCount),
            _ => BuildStringDataset(column)
        };
    }

    private float[] BuildScalarDataset(string columnName, int rowCount)
    {
        float[] data = new float[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? float.NaN : value.AsScalar();
        }

        return data;
    }

    private byte[] BuildUInt8Dataset(string columnName, int rowCount)
    {
        byte[] data = new byte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? (byte)0 : value.AsUInt8();
        }

        return data;
    }

    private string[] BuildStringDataset(ColumnInfo column)
    {
        string[] data = new string[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            DataValue value = _rows[i][column.Name];
            if (value.IsNull)
            {
                data[i] = "";
            }
            else if (column.Kind == DataKind.JsonValue)
            {
                data[i] = value.AsJsonValue();
            }
            else
            {
                data[i] = value.AsString();
            }
        }

        return data;
    }

    /// <summary>
    /// Builds a variable-length byte array dataset for binary columns (UInt8Array, Image).
    /// </summary>
    private byte[][] BuildBinaryDataset(ColumnInfo column)
    {
        byte[][] data = new byte[_rows.Count][];
        for (int i = 0; i < _rows.Count; i++)
        {
            DataValue value = _rows[i][column.Name];
            if (value.IsNull)
            {
                data[i] = [];
            }
            else if (column.Kind == DataKind.Image)
            {
                data[i] = value.AsImage();
            }
            else
            {
                data[i] = value.AsUInt8Array();
            }
        }

        return data;
    }

    private string[] BuildDateStringDataset(ColumnInfo column)
    {
        string[] data = new string[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            DataValue value = _rows[i][column.Name];
            if (value.IsNull)
            {
                data[i] = "";
            }
            else if (column.Kind == DataKind.Date)
            {
                data[i] = value.AsDate().ToString("yyyy-MM-dd");
            }
            else
            {
                data[i] = value.AsDateTime().ToString("O");
            }
        }

        return data;
    }

    private H5Dataset BuildVectorDataset(string columnName, int rowCount)
    {
        // Find the vector length from the first non-null row
        int vectorLength = 0;
        foreach (Row row in _rows)
        {
            DataValue value = row[columnName];
            if (!value.IsNull)
            {
                vectorLength = value.AsVector().Length;
                break;
            }
        }

        if (vectorLength == 0)
        {
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0]);
        }

        float[] flatData = new float[rowCount * vectorLength];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            if (!value.IsNull)
            {
                float[] vector = value.AsVector();
                Array.Copy(vector, 0, flatData, i * vectorLength, vectorLength);
            }
        }

        return new H5Dataset(flatData, fileDims: [(ulong)rowCount, (ulong)vectorLength]);
    }

    private H5Dataset BuildMatrixDataset(string columnName, int rowCount)
    {
        int matrixRows = 0;
        int matrixCols = 0;

        foreach (Row row in _rows)
        {
            DataValue value = row[columnName];
            if (!value.IsNull)
            {
                value.AsMatrix(out matrixRows, out matrixCols);
                break;
            }
        }

        if (matrixRows == 0 || matrixCols == 0)
        {
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0, 0]);
        }

        float[] flatData = new float[rowCount * matrixRows * matrixCols];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            if (!value.IsNull)
            {
                float[] matrixData = value.AsMatrix(out _, out _);
                Array.Copy(matrixData, 0, flatData, i * matrixRows * matrixCols, matrixRows * matrixCols);
            }
        }

        return new H5Dataset(flatData, fileDims: [(ulong)rowCount, (ulong)matrixRows, (ulong)matrixCols]);
    }

    private string[] BuildUuidStringDataset(string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsUuid().ToString("D");
        }

        return data;
    }

    private byte[] BuildBooleanDataset(string columnName, int rowCount)
    {
        byte[] data = new byte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = (!value.IsNull && value.AsBoolean()) ? (byte)1 : (byte)0;
        }

        return data;
    }

    private string[] BuildTimeStringDataset(string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.AsTime().ToString("HH:mm:ss.FFFFFFF");
        }

        return data;
    }

    private double[] BuildDurationDoubleDataset(string columnName, int rowCount)
    {
        double[] data = new double[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? double.NaN : value.AsDuration().TotalSeconds;
        }

        return data;
    }

    /// <summary>
    /// Builds a string dataset for <see cref="DataKind.Array"/> columns by serializing
    /// each array value as a JSON array string.
    /// </summary>
    private string[] BuildArrayStringDataset(string columnName, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? "" : value.ToString() ?? "";
        }

        return data;
    }
}
