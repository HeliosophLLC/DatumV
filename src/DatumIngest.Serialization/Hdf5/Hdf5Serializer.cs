using DatumIngest.Model;
using PureHDF;

namespace DatumIngest.Serialization.Hdf5;

/// <summary>
/// Serializes a stream of <see cref="RowBatch"/> instances into HDF5 format.
/// Each column becomes a dataset. All rows are buffered before writing since
/// HDF5 requires dataset dimensions upfront. Vectors, matrices, and tensors
/// are stored as true N-D datasets with chunked storage.
/// </summary>
public sealed class Hdf5Serializer : IFormatSerializer
{
    private const int ChunkRowSize = 1000;

    /// <summary>
    /// HDF5 attribute name written on tensor datasets to disambiguate from matrices.
    /// </summary>
    internal const string TensorKindAttributeName = "datumingest_kind";

    /// <summary>
    /// The attribute value that marks a dataset as a tensor.
    /// </summary>
    internal const string TensorKindAttributeValue = "tensor";

    private readonly OutputDescriptor _descriptor;

    /// <summary>Creates a serializer for the given output descriptor.</summary>
    public Hdf5Serializer(OutputDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async Task SerializeAsync(
        SerializationContext context,
        IAsyncEnumerable<RowBatch> rows,
        CancellationToken cancellationToken = default)
    {
        // Buffer all rows — HDF5 needs total row count for dataset dimensions.
        List<DataValue[]> allRows = [];
        IReadOnlyList<string>? columnNames = null;
        DataKind[]? columnKinds = null;

        await foreach (RowBatch batch in rows.WithCancellation(cancellationToken))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];

                if (columnNames is null)
                {
                    columnNames = row.ColumnNames;
                    columnKinds = new DataKind[row.FieldCount];
                    for (int c = 0; c < row.FieldCount; c++)
                        columnKinds[c] = row[c].Kind;
                }

                // Copy values out of the batch (they'll be returned to pool).
                DataValue[] values = new DataValue[row.FieldCount];
                for (int c = 0; c < row.FieldCount; c++)
                    values[c] = row[c];

                allRows.Add(values);
            }
        }

        if (columnNames is null || columnKinds is null || allRows.Count == 0)
            return;

        H5File file = new();
        int rowCount = allRows.Count;
        IValueStore store = context.Arena;

        for (int col = 0; col < columnNames.Count; col++)
        {
            object dataset = BuildDataset(allRows, col, columnKinds[col], rowCount, store);
            file[columnNames[col]] = dataset;
        }

        // PureHDF requires a readable+writable+seekable stream, so write via file path
        // (mirrors the deserializer which uses H5File.OpenRead with a file path).
        file.Write(_descriptor.FilePath);
    }

    private static object BuildDataset(
        List<DataValue[]> rows, int col, DataKind kind, int rowCount, IValueStore store)
    {
        return kind switch
        {
            DataKind.Float32 => BuildFloat32(rows, col, rowCount),
            DataKind.Float64 => BuildFloat64(rows, col, rowCount),
            DataKind.UInt8 => BuildUInt8(rows, col, rowCount),
            DataKind.Int8 => BuildInt8(rows, col, rowCount),
            DataKind.Int16 => BuildInt16(rows, col, rowCount),
            DataKind.UInt16 => BuildUInt16(rows, col, rowCount),
            DataKind.Int32 => BuildInt32(rows, col, rowCount),
            DataKind.UInt32 => BuildUInt32(rows, col, rowCount),
            DataKind.Int64 => BuildInt64(rows, col, rowCount),
            DataKind.UInt64 => BuildUInt64(rows, col, rowCount),
            DataKind.Boolean => BuildBoolean(rows, col, rowCount),
            DataKind.String => BuildString(rows, col, rowCount, store),
            DataKind.JsonValue => BuildJsonValue(rows, col, rowCount, store),
            DataKind.Date => BuildDateString(rows, col, rowCount),
            DataKind.DateTime => BuildDateTimeString(rows, col, rowCount),
            DataKind.Time => BuildTimeString(rows, col, rowCount),
            DataKind.Duration => BuildDuration(rows, col, rowCount),
            DataKind.Uuid => BuildUuidString(rows, col, rowCount),
            DataKind.Vector => BuildVector(rows, col, rowCount, store),
            DataKind.Matrix => BuildMatrix(rows, col, rowCount, store),
            DataKind.Tensor => BuildTensor(rows, col, rowCount, store),
            _ => BuildFallbackString(rows, col, rowCount),
        };
    }

    // ───────────────────────── Scalar builders ─────────────────────────

    private static float[] BuildFloat32(List<DataValue[]> rows, int col, int rowCount)
    {
        float[] data = new float[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? float.NaN : v.AsFloat32();
        }
        return data;
    }

    private static double[] BuildFloat64(List<DataValue[]> rows, int col, int rowCount)
    {
        double[] data = new double[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? double.NaN : v.AsFloat64();
        }
        return data;
    }

    private static byte[] BuildUInt8(List<DataValue[]> rows, int col, int rowCount)
    {
        byte[] data = new byte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? (byte)0 : v.AsUInt8();
        }
        return data;
    }

    private static sbyte[] BuildInt8(List<DataValue[]> rows, int col, int rowCount)
    {
        sbyte[] data = new sbyte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? (sbyte)0 : v.AsInt8();
        }
        return data;
    }

    private static short[] BuildInt16(List<DataValue[]> rows, int col, int rowCount)
    {
        short[] data = new short[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? (short)0 : v.AsInt16();
        }
        return data;
    }

    private static ushort[] BuildUInt16(List<DataValue[]> rows, int col, int rowCount)
    {
        ushort[] data = new ushort[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? (ushort)0 : v.AsUInt16();
        }
        return data;
    }

    private static int[] BuildInt32(List<DataValue[]> rows, int col, int rowCount)
    {
        int[] data = new int[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? 0 : v.AsInt32();
        }
        return data;
    }

    private static uint[] BuildUInt32(List<DataValue[]> rows, int col, int rowCount)
    {
        uint[] data = new uint[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? 0u : v.AsUInt32();
        }
        return data;
    }

    private static long[] BuildInt64(List<DataValue[]> rows, int col, int rowCount)
    {
        long[] data = new long[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? 0L : v.AsInt64();
        }
        return data;
    }

    private static ulong[] BuildUInt64(List<DataValue[]> rows, int col, int rowCount)
    {
        ulong[] data = new ulong[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? 0UL : v.AsUInt64();
        }
        return data;
    }

    private static byte[] BuildBoolean(List<DataValue[]> rows, int col, int rowCount)
    {
        byte[] data = new byte[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = (!v.IsNull && v.AsBoolean()) ? (byte)1 : (byte)0;
        }
        return data;
    }

    // ───────────────────────── String builders ─────────────────────────

    private static string[] BuildString(List<DataValue[]> rows, int col, int rowCount, IValueStore store)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsString(store);
        }
        return data;
    }

    private static string[] BuildJsonValue(List<DataValue[]> rows, int col, int rowCount, IValueStore store)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsJsonValue(store);
        }
        return data;
    }

    private static string[] BuildDateString(List<DataValue[]> rows, int col, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsDate().ToString("yyyy-MM-dd");
        }
        return data;
    }

    private static string[] BuildDateTimeString(List<DataValue[]> rows, int col, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsDateTime().ToString("O");
        }
        return data;
    }

    private static string[] BuildTimeString(List<DataValue[]> rows, int col, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsTime().ToString("HH:mm:ss.FFFFFFF");
        }
        return data;
    }

    private static double[] BuildDuration(List<DataValue[]> rows, int col, int rowCount)
    {
        double[] data = new double[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? double.NaN : v.AsDuration().TotalSeconds;
        }
        return data;
    }

    private static string[] BuildUuidString(List<DataValue[]> rows, int col, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.AsUuid().ToString("D");
        }
        return data;
    }

    private static string[] BuildFallbackString(List<DataValue[]> rows, int col, int rowCount)
    {
        string[] data = new string[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            data[i] = v.IsNull ? "" : v.ToString() ?? "";
        }
        return data;
    }

    // ───────────────────────── Multi-dimensional builders ─────────────────────────

    private static H5Dataset BuildVector(List<DataValue[]> rows, int col, int rowCount, IValueStore store)
    {
        int vectorLength = 0;
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                vectorLength = v.AsVector(store).Length;
                break;
            }
        }

        if (vectorLength == 0)
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0]);

        float[] flatData = new float[rowCount * vectorLength];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                float[] vector = v.AsVector(store);
                Array.Copy(vector, 0, flatData, i * vectorLength, vectorLength);
            }
        }

        uint rowChunk = (uint)Math.Min(ChunkRowSize, rowCount);
        return new H5Dataset(flatData, chunks: [rowChunk, (uint)vectorLength],
            fileDims: [(ulong)rowCount, (ulong)vectorLength]);
    }

    private static H5Dataset BuildMatrix(List<DataValue[]> rows, int col, int rowCount, IValueStore store)
    {
        int matRows = 0, matCols = 0;
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                v.AsMatrix(store, out matRows, out matCols);
                break;
            }
        }

        if (matRows == 0 || matCols == 0)
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0, 0]);

        int elemCount = matRows * matCols;
        float[] flatData = new float[rowCount * elemCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                float[] data = v.AsMatrix(store, out _, out _);
                Array.Copy(data, 0, flatData, i * elemCount, elemCount);
            }
        }

        uint rowChunk = (uint)Math.Min(ChunkRowSize, rowCount);
        return new H5Dataset(flatData, chunks: [rowChunk, (uint)matRows, (uint)matCols],
            fileDims: [(ulong)rowCount, (ulong)matRows, (ulong)matCols]);
    }

    private static H5Dataset BuildTensor(List<DataValue[]> rows, int col, int rowCount, IValueStore store)
    {
        int[] shape = [];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                v.AsTensor(store, out shape);
                break;
            }
        }

        if (shape.Length == 0)
            return new H5Dataset(Array.Empty<float>(), fileDims: [0, 0]);

        int elemCount = 1;
        foreach (int dim in shape) elemCount *= dim;

        float[] flatData = new float[rowCount * elemCount];
        for (int i = 0; i < rowCount; i++)
        {
            DataValue v = rows[i][col];
            if (!v.IsNull)
            {
                float[] data = v.AsTensor(store, out _);
                Array.Copy(data, 0, flatData, i * elemCount, elemCount);
            }
        }

        ulong[] fileDims = new ulong[1 + shape.Length];
        fileDims[0] = (ulong)rowCount;
        for (int d = 0; d < shape.Length; d++)
            fileDims[d + 1] = (ulong)shape[d];

        uint[] chunkDims = new uint[1 + shape.Length];
        chunkDims[0] = (uint)Math.Min(ChunkRowSize, rowCount);
        for (int d = 0; d < shape.Length; d++)
            chunkDims[d + 1] = (uint)shape[d];

        return new H5Dataset(flatData, chunks: chunkDims, fileDims: fileDims)
        {
            Attributes = new() { [TensorKindAttributeName] = TensorKindAttributeValue }
        };
    }
}
