namespace DatumIngest.Output.Writers;

using DatumIngest.Model;
using PureHDF;

/// <summary>
/// Writes query results to HDF5 files. Each column becomes a dataset.
/// Buffers all rows in memory, then writes a single HDF5 file on finalize.
/// </summary>
/// <remarks>
/// Multi-dimensional kinds are stored as true N-D HDF5 datasets:
/// <see cref="DataKind.Vector"/> produces rank-2 <c>[rows, dims]</c>,
/// <see cref="DataKind.Matrix"/> produces rank-3 <c>[rows, matRows, matCols]</c>, and
/// <see cref="DataKind.Tensor"/> produces rank-(1+N) <c>[rows, shape[0], …, shape[N-1]]</c>.
/// All multi-dimensional datasets use chunked HDF5 storage (chunk row count: <see cref="ChunkRowSize"/>)
/// to enable efficient partial reads via hyperslab selection.
/// </remarks>
public sealed class Hdf5OutputWriter : IOutputWriter
{
    private readonly string? _filePath;
    private readonly Stream? _outputStream;
    private Schema? _schema;
    private readonly List<Row> _rows = new();
    private long _rowsWritten;

    /// <summary>
    /// Number of rows per HDF5 chunk for multi-dimensional datasets.
    /// Chunked storage enables efficient hyperslab selection at query time.
    /// </summary>
    private const int ChunkRowSize = 1000;

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
            DataKind.Float32 => BuildScalarDataset(column.Name, rowCount),
            DataKind.Float64 => BuildFloat64Dataset(column.Name, rowCount),
            DataKind.UInt8 => BuildUInt8Dataset(column.Name, rowCount),
            DataKind.Int8 => BuildInt8Dataset(column.Name, rowCount),
            DataKind.Int16 => BuildInt16Dataset(column.Name, rowCount),
            DataKind.UInt16 => BuildUInt16Dataset(column.Name, rowCount),
            DataKind.Int32 => BuildInt32Dataset(column.Name, rowCount),
            DataKind.UInt32 => BuildUInt32Dataset(column.Name, rowCount),
            DataKind.Int64 => BuildInt64Dataset(column.Name, rowCount),
            DataKind.UInt64 => BuildUInt64Dataset(column.Name, rowCount),
            DataKind.String or DataKind.JsonValue => BuildStringDataset(column),
            DataKind.Vector => BuildVectorDataset(column.Name, rowCount),
            DataKind.Matrix => BuildMatrixDataset(column.Name, rowCount),
            DataKind.Tensor => BuildTensorDataset(column.Name, rowCount),
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
            data[i] = value.IsNull ? float.NaN : value.AsFloat32();
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

    private double[] BuildFloat64Dataset(string columnName, int rowCount)
    {
        double[] data = new double[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? double.NaN : value.AsFloat64();
        }

        return data;
    }

    private sbyte[] BuildInt8Dataset(string columnName, int rowCount)
    {
        sbyte[] data = new sbyte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? (sbyte)0 : value.AsInt8();
        }

        return data;
    }

    private short[] BuildInt16Dataset(string columnName, int rowCount)
    {
        short[] data = new short[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? (short)0 : value.AsInt16();
        }

        return data;
    }

    private ushort[] BuildUInt16Dataset(string columnName, int rowCount)
    {
        ushort[] data = new ushort[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? (ushort)0 : value.AsUInt16();
        }

        return data;
    }

    private int[] BuildInt32Dataset(string columnName, int rowCount)
    {
        int[] data = new int[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? 0 : value.AsInt32();
        }

        return data;
    }

    private uint[] BuildUInt32Dataset(string columnName, int rowCount)
    {
        uint[] data = new uint[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? 0u : value.AsUInt32();
        }

        return data;
    }

    private long[] BuildInt64Dataset(string columnName, int rowCount)
    {
        long[] data = new long[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? 0L : value.AsInt64();
        }

        return data;
    }

    private ulong[] BuildUInt64Dataset(string columnName, int rowCount)
    {
        ulong[] data = new ulong[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            data[i] = value.IsNull ? 0UL : value.AsUInt64();
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

        uint rowChunk = (uint)Math.Min(ChunkRowSize, rowCount);
        return new H5Dataset(flatData, chunks: [rowChunk, (uint)vectorLength], fileDims: [(ulong)rowCount, (ulong)vectorLength]);
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

        uint rowChunk = (uint)Math.Min(ChunkRowSize, rowCount);
        return new H5Dataset(flatData, chunks: [rowChunk, (uint)matrixRows, (uint)matrixCols], fileDims: [(ulong)rowCount, (ulong)matrixRows, (ulong)matrixCols]);
    }

    private H5Dataset BuildTensorDataset(string columnName, int rowCount)
    {
        // Find the per-element shape from the first non-null row.
        int[] shape = [];
        foreach (Row row in _rows)
        {
            DataValue value = row[columnName];
            if (!value.IsNull)
            {
                value.AsTensor(out shape);
                break;
            }
        }

        if (shape.Length == 0)
        {
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0]);
        }

        int elementCount = 1;
        foreach (int dim in shape)
        {
            elementCount *= dim;
        }

        float[] flatData = new float[rowCount * elementCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue value = _rows[i][columnName];
            if (!value.IsNull)
            {
                float[] tensorData = value.AsTensor(out _);
                Array.Copy(tensorData, 0, flatData, i * elementCount, elementCount);
            }
        }

        // fileDims: [rowCount, shape[0], shape[1], ...]
        ulong[] fileDims = new ulong[1 + shape.Length];
        fileDims[0] = (ulong)rowCount;
        for (int d = 0; d < shape.Length; d++)
        {
            fileDims[d + 1] = (ulong)shape[d];
        }

        uint[] chunkDims = new uint[1 + shape.Length];
        chunkDims[0] = (uint)Math.Min(ChunkRowSize, rowCount);
        for (int d = 0; d < shape.Length; d++)
        {
            chunkDims[d + 1] = (uint)shape[d];
        }

        return new H5Dataset(flatData, chunks: chunkDims, fileDims: fileDims)
        {
            Attributes = new() { [TensorKindAttributeName] = TensorKindAttributeValue }
        };
    }

    /// <summary>
    /// HDF5 attribute name written on tensor datasets to disambiguate from matrices,
    /// which share the same rank-3 layout when the tensor shape is two-dimensional.
    /// </summary>
    internal const string TensorKindAttributeName = "datumingest_kind";

    /// <summary>
    /// The attribute value that marks a dataset as a tensor.
    /// </summary>
    internal const string TensorKindAttributeValue = "tensor";

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
