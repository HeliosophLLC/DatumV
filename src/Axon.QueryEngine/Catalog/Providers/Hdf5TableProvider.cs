using System.Runtime.CompilerServices;
using Axon.QueryEngine.Model;
using PureHDF;
using PureHDF.VOL.Native;

namespace Axon.QueryEngine.Catalog.Providers;

/// <summary>
/// Reads HDF5 files via PureHDF (pure managed, cross-platform).
/// Each 1-D dataset becomes a column; 2-D datasets yield one vector per row.
/// Datasets inside groups use a flattened slash-separated name (e.g. "sensors/temperature").
/// </summary>
public sealed class Hdf5TableProvider : ITableProvider
{
    /// <inheritdoc />
    public Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using NativeFile file = H5File.OpenRead(descriptor.FilePath);

        List<DatasetEntry> entries = new();
        DiscoverDatasets(file, "", entries);

        List<ColumnInfo> columns = new(entries.Count);
        foreach (DatasetEntry entry in entries)
        {
            DataKind kind = InferDataKind(entry.Dataset);
            columns.Add(new ColumnInfo(entry.Path, kind, nullable: true));
        }

        return Task.FromResult(new Schema(columns));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using NativeFile file = H5File.OpenRead(descriptor.FilePath);

        List<DatasetEntry> allEntries = new();
        DiscoverDatasets(file, "", allEntries);

        // Apply projection pushdown.
        List<DatasetEntry> entries = requiredColumns is not null
            ? allEntries.FindAll(entry => requiredColumns.Contains(entry.Path))
            : allEntries;

        if (entries.Count == 0)
        {
            yield break;
        }

        // Read all column data into memory (HDF5 datasets are fully materialized).
        List<ColumnData> columnDataList = new(entries.Count);
        long rowCount = 0;

        foreach (DatasetEntry entry in entries)
        {
            ColumnData columnData = ReadDatasetColumn(entry);
            columnDataList.Add(columnData);

            if (rowCount == 0)
            {
                rowCount = columnData.RowCount;
            }
            else if (columnData.RowCount != rowCount)
            {
                rowCount = Math.Min(rowCount, columnData.RowCount);
            }
        }

        // Build column names array once.
        string[] columnNames = new string[columnDataList.Count];
        for (int columnIndex = 0; columnIndex < columnDataList.Count; columnIndex++)
        {
            columnNames[columnIndex] = columnDataList[columnIndex].Name;
        }

        // Yield rows by zipping columns.
        for (long rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataValue[] values = new DataValue[columnDataList.Count];
            for (int columnIndex = 0; columnIndex < columnDataList.Count; columnIndex++)
            {
                values[columnIndex] = columnDataList[columnIndex].GetValue(rowIndex);
            }

            yield return new Row(columnNames, values);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            using NativeFile file = H5File.OpenRead(descriptor.FilePath);

            List<DatasetEntry> entries = new();
            DiscoverDatasets(file, "", entries);

            long? rowCount = null;
            if (entries.Count > 0)
            {
                IH5Dataset firstDataset = entries[0].Dataset;
                if (firstDataset.Space.Rank >= 1)
                {
                    rowCount = (long)firstDataset.Space.Dimensions[0];
                }
            }

            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: rowCount,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }
        catch
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: null,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }
    }

    // ───────────────────── Dataset discovery ─────────────────────

    /// <summary>
    /// Holds the flattened path and reference to a discovered dataset.
    /// </summary>
    private sealed record DatasetEntry(string Path, IH5Dataset Dataset);

    /// <summary>
    /// Recursively discovers all datasets in a group, building flattened slash-separated paths.
    /// </summary>
    private static void DiscoverDatasets(IH5Group group, string parentPath, List<DatasetEntry> results)
    {
        foreach (IH5Object child in group.Children())
        {
            string fullPath = parentPath.Length == 0
                ? child.Name
                : $"{parentPath}/{child.Name}";

            if (child is IH5Dataset dataset)
            {
                results.Add(new DatasetEntry(fullPath, dataset));
            }
            else if (child is IH5Group childGroup)
            {
                DiscoverDatasets(childGroup, fullPath, results);
            }
        }
    }

    // ───────────────────── Type inference ─────────────────────

    /// <summary>
    /// Determines the <see cref="DataKind"/> for a dataset based on its PureHDF type
    /// and dimensionality.
    /// </summary>
    private static DataKind InferDataKind(IH5Dataset dataset)
    {
        IH5DataType type = dataset.Type;
        byte rank = dataset.Space.Rank;

        if (type.Class == H5DataTypeClass.String || type.Class == H5DataTypeClass.VariableLength)
        {
            return DataKind.String;
        }

        if (rank >= 2)
        {
            return DataKind.Vector;
        }

        if (type.Class == H5DataTypeClass.FixedPoint)
        {
            if (type.Size == 1 && !type.FixedPoint.IsSigned)
            {
                return DataKind.UInt8;
            }
            return DataKind.Scalar;
        }

        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            return DataKind.Scalar;
        }

        return DataKind.String;
    }

    // ───────────────────── Data reading ─────────────────────

    /// <summary>
    /// Holds pre-read columnar data for a single dataset.
    /// </summary>
    private abstract class ColumnData
    {
        public string Name { get; }
        public long RowCount { get; }

        protected ColumnData(string name, long rowCount)
        {
            Name = name;
            RowCount = rowCount;
        }

        public abstract DataValue GetValue(long rowIndex);
    }

    private sealed class ScalarColumnData : ColumnData
    {
        private readonly float[] _data;

        public ScalarColumnData(string name, float[] data)
            : base(name, data.Length)
        {
            _data = data;
        }

        public override DataValue GetValue(long rowIndex) =>
            DataValue.FromScalar(_data[rowIndex]);
    }

    private sealed class UInt8ColumnData : ColumnData
    {
        private readonly byte[] _data;

        public UInt8ColumnData(string name, byte[] data)
            : base(name, data.Length)
        {
            _data = data;
        }

        public override DataValue GetValue(long rowIndex) =>
            DataValue.FromUInt8(_data[rowIndex]);
    }

    private sealed class StringColumnData : ColumnData
    {
        private readonly string[] _data;

        public StringColumnData(string name, string[] data)
            : base(name, data.Length)
        {
            _data = data;
        }

        public override DataValue GetValue(long rowIndex) =>
            DataValue.FromString(_data[rowIndex]);
    }

    private sealed class VectorColumnData : ColumnData
    {
        private readonly float[] _flatData;
        private readonly int _vectorLength;

        public VectorColumnData(string name, float[] flatData, long rowCount, int vectorLength)
            : base(name, rowCount)
        {
            _flatData = flatData;
            _vectorLength = vectorLength;
        }

        public override DataValue GetValue(long rowIndex)
        {
            float[] vector = new float[_vectorLength];
            Array.Copy(_flatData, rowIndex * _vectorLength, vector, 0, _vectorLength);
            return DataValue.FromVector(vector);
        }
    }

    /// <summary>
    /// Reads an entire dataset into the appropriate <see cref="ColumnData"/> subclass.
    /// </summary>
    private static ColumnData ReadDatasetColumn(DatasetEntry entry)
    {
        IH5Dataset dataset = entry.Dataset;
        IH5DataType type = dataset.Type;
        byte rank = dataset.Space.Rank;
        ulong[] dimensions = dataset.Space.Dimensions;

        // String datasets (fixed-length or variable-length).
        if (type.Class == H5DataTypeClass.String || type.Class == H5DataTypeClass.VariableLength)
        {
            string[] data = dataset.Read<string[]>();
            return new StringColumnData(entry.Path, data);
        }

        // Multi-dimensional numeric datasets yield vectors per row.
        if (rank >= 2 && (type.Class == H5DataTypeClass.FloatingPoint || type.Class == H5DataTypeClass.FixedPoint))
        {
            long rowCount = (long)dimensions[0];
            int vectorLength = 1;
            for (int dimensionIndex = 1; dimensionIndex < dimensions.Length; dimensionIndex++)
            {
                vectorLength *= (int)dimensions[dimensionIndex];
            }

            float[] flatData = ReadAsFloatArray(dataset, type);
            return new VectorColumnData(entry.Path, flatData, rowCount, vectorLength);
        }

        // 1-D unsigned byte datasets.
        if (type.Class == H5DataTypeClass.FixedPoint && type.Size == 1 && !type.FixedPoint.IsSigned)
        {
            byte[] data = dataset.Read<byte[]>();
            return new UInt8ColumnData(entry.Path, data);
        }

        // 1-D numeric datasets (integer or float) → Scalar column.
        if (type.Class == H5DataTypeClass.FixedPoint || type.Class == H5DataTypeClass.FloatingPoint)
        {
            float[] data = ReadAsFloatArray(dataset, type);
            return new ScalarColumnData(entry.Path, data);
        }

        // Unsupported type fallback.
        string[] fallback = new string[dimensions[0]];
        Array.Fill(fallback, string.Empty);
        return new StringColumnData(entry.Path, fallback);
    }

    /// <summary>
    /// Reads a numeric dataset as a float[] array, converting from the native type.
    /// </summary>
    private static float[] ReadAsFloatArray(IH5Dataset dataset, IH5DataType type)
    {
        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            if (type.Size == 4)
            {
                return dataset.Read<float[]>();
            }

            // float64 → float32 conversion.
            double[] doubleData = dataset.Read<double[]>();
            float[] floatData = new float[doubleData.Length];
            for (int index = 0; index < doubleData.Length; index++)
            {
                floatData[index] = (float)doubleData[index];
            }
            return floatData;
        }

        if (type.Class == H5DataTypeClass.FixedPoint)
        {
            if (type.Size <= 4)
            {
                int[] intData = dataset.Read<int[]>();
                float[] floatData = new float[intData.Length];
                for (int index = 0; index < intData.Length; index++)
                {
                    floatData[index] = intData[index];
                }
                return floatData;
            }

            long[] longData = dataset.Read<long[]>();
            float[] result = new float[longData.Length];
            for (int index = 0; index < longData.Length; index++)
            {
                result[index] = longData[index];
            }
            return result;
        }

        return [];
    }
}
