using DatumIngest.Model;
using PureHDF;

namespace DatumIngest.Serialization.Hdf5;

/// <summary>
/// Holds pre-read columnar data for a single HDF5 dataset. Subclasses provide
/// typed, zero-boxing per-row access.
/// </summary>
internal abstract class Hdf5ColumnData
{
    public string Name { get; }
    public long RowCount { get; }

    protected Hdf5ColumnData(string name, long rowCount) { Name = name; RowCount = rowCount; }

    public abstract DataValue GetValue(long rowIndex, IValueStore store);
}

internal sealed class ScalarFloat32Column : Hdf5ColumnData
{
    private readonly float[] _data;
    public ScalarFloat32Column(string name, float[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromFloat32(_data[rowIndex]);
}

internal sealed class ScalarFloat64Column : Hdf5ColumnData
{
    private readonly double[] _data;
    public ScalarFloat64Column(string name, double[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromFloat64(_data[rowIndex]);
}

internal sealed class ScalarInt32Column : Hdf5ColumnData
{
    private readonly int[] _data;
    public ScalarInt32Column(string name, int[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromInt32(_data[rowIndex]);
}

internal sealed class ScalarInt64Column : Hdf5ColumnData
{
    private readonly long[] _data;
    public ScalarInt64Column(string name, long[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromInt64(_data[rowIndex]);
}

internal sealed class ScalarUInt8Column : Hdf5ColumnData
{
    private readonly byte[] _data;
    public ScalarUInt8Column(string name, byte[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromUInt8(_data[rowIndex]);
}

internal sealed class TypedNumericColumn<T> : Hdf5ColumnData where T : struct
{
    private readonly T[] _data;
    private readonly Func<T, DataValue> _converter;
    public TypedNumericColumn(string name, T[] data, Func<T, DataValue> converter) : base(name, data.Length) { _data = data; _converter = converter; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => _converter(_data[rowIndex]);
}

internal sealed class StringColumn : Hdf5ColumnData
{
    private readonly string[] _data;
    public StringColumn(string name, string[] data) : base(name, data.Length) { _data = data; }
    public override DataValue GetValue(long rowIndex, IValueStore store) => DataValue.FromString(_data[rowIndex], store);
}

internal sealed class VectorColumn : Hdf5ColumnData
{
    private readonly float[] _flatData;
    private readonly int _vectorLength;
    public VectorColumn(string name, float[] flatData, long rowCount, int vectorLength) : base(name, rowCount) { _flatData = flatData; _vectorLength = vectorLength; }
    public override DataValue GetValue(long rowIndex, IValueStore store)
    {
        float[] vector = new float[_vectorLength];
        Array.Copy(_flatData, rowIndex * _vectorLength, vector, 0, _vectorLength);
        return DataValue.FromVector(vector, store);
    }
}

internal sealed class MatrixColumn : Hdf5ColumnData
{
    private readonly float[] _flatData;
    private readonly int _rows, _columns, _elementCount;
    public MatrixColumn(string name, float[] flatData, long rowCount, int rows, int columns) : base(name, rowCount) { _flatData = flatData; _rows = rows; _columns = columns; _elementCount = rows * columns; }
    public override DataValue GetValue(long rowIndex, IValueStore store)
    {
        float[] matrix = new float[_elementCount];
        Array.Copy(_flatData, rowIndex * _elementCount, matrix, 0, _elementCount);
        return DataValue.FromMatrix(matrix, _rows, _columns, store);
    }
}

internal sealed class TensorColumn : Hdf5ColumnData
{
    private readonly float[] _flatData;
    private readonly int[] _shape;
    private readonly int _elementCount;
    public TensorColumn(string name, float[] flatData, long rowCount, int[] shape) : base(name, rowCount)
    {
        _flatData = flatData;
        _shape = shape;
        _elementCount = 1;
        foreach (int dim in shape) _elementCount *= dim;
    }
    public override DataValue GetValue(long rowIndex, IValueStore store)
    {
        float[] tensor = new float[_elementCount];
        Array.Copy(_flatData, rowIndex * _elementCount, tensor, 0, _elementCount);
        return DataValue.FromTensor(tensor, _shape, store);
    }
}

/// <summary>
/// Reads HDF5 datasets into typed <see cref="Hdf5ColumnData"/> subclasses.
/// </summary>
internal static class Hdf5ColumnReader
{
    internal static Hdf5ColumnData Read(Hdf5DatasetEntry entry)
    {
        IH5Dataset dataset = entry.Dataset;
        IH5DataType type = dataset.Type;
        byte rank = dataset.Space.Rank;
        ulong[] dimensions = dataset.Space.Dimensions;

        if (type.Class == H5DataTypeClass.String || type.Class == H5DataTypeClass.VariableLength)
            return new StringColumn(entry.Path, dataset.Read<string[]>());

        if (rank >= 2 && (type.Class == H5DataTypeClass.FloatingPoint || type.Class == H5DataTypeClass.FixedPoint))
        {
            long rowCount = (long)dimensions[0];
            float[] flatData = ReadAsFloatArray(dataset, type);

            if (rank == 2)
                return new VectorColumn(entry.Path, flatData, rowCount, (int)dimensions[1]);

            if (rank == 3 && !Hdf5SchemaMapper.HasTensorKindAttribute(dataset))
                return new MatrixColumn(entry.Path, flatData, rowCount, (int)dimensions[1], (int)dimensions[2]);

            int[] shape = new int[rank - 1];
            for (int d = 1; d < rank; d++)
                shape[d - 1] = (int)dimensions[d];
            return new TensorColumn(entry.Path, flatData, rowCount, shape);
        }

        if (type.Class == H5DataTypeClass.FixedPoint && type.Size == 1 && !type.FixedPoint.IsSigned)
            return new ScalarUInt8Column(entry.Path, dataset.Read<byte[]>());

        if (type.Class == H5DataTypeClass.FixedPoint)
            return ReadTypedInteger(entry.Path, dataset, type);

        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            return type.Size >= 8
                ? new ScalarFloat64Column(entry.Path, dataset.Read<double[]>())
                : new ScalarFloat32Column(entry.Path, dataset.Read<float[]>());
        }

        string[] fallback = new string[dimensions[0]];
        Array.Fill(fallback, string.Empty);
        return new StringColumn(entry.Path, fallback);
    }

    private static float[] ReadAsFloatArray(IH5Dataset dataset, IH5DataType type)
    {
        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            if (type.Size == 4) return dataset.Read<float[]>();
            double[] d = dataset.Read<double[]>();
            float[] f = new float[d.Length];
            for (int i = 0; i < d.Length; i++) f[i] = (float)d[i];
            return f;
        }

        if (type.Class == H5DataTypeClass.FixedPoint)
        {
            if (type.Size <= 4)
            {
                int[] ints = dataset.Read<int[]>();
                float[] f = new float[ints.Length];
                for (int i = 0; i < ints.Length; i++) f[i] = ints[i];
                return f;
            }
            long[] longs = dataset.Read<long[]>();
            float[] result = new float[longs.Length];
            for (int i = 0; i < longs.Length; i++) result[i] = longs[i];
            return result;
        }

        return [];
    }

    private static Hdf5ColumnData ReadTypedInteger(string path, IH5Dataset dataset, IH5DataType type)
    {
        return (type.Size, type.FixedPoint.IsSigned) switch
        {
            (1, true) => new TypedNumericColumn<sbyte>(path, dataset.Read<sbyte[]>(), DataValue.FromInt8),
            (2, false) => new TypedNumericColumn<ushort>(path, dataset.Read<ushort[]>(), DataValue.FromUInt16),
            (2, true) => new TypedNumericColumn<short>(path, dataset.Read<short[]>(), DataValue.FromInt16),
            (4, false) => new TypedNumericColumn<uint>(path, dataset.Read<uint[]>(), DataValue.FromUInt32),
            (4, true) => new ScalarInt32Column(path, dataset.Read<int[]>()),
            (8, false) => new TypedNumericColumn<ulong>(path, dataset.Read<ulong[]>(), DataValue.FromUInt64),
            _ => new ScalarInt64Column(path, dataset.Read<long[]>()),
        };
    }
}
