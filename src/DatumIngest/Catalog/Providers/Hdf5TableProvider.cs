using System.Runtime.CompilerServices;
using DatumIngest.Model;
using PureHDF;
using PureHDF.Selections;
using PureHDF.VOL.Native;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Reads HDF5 files via PureHDF (pure managed, cross-platform).
/// Each 1-D dataset becomes a column. Multi-dimensional datasets are restored to their
/// original <see cref="DataKind"/>: rank-2 as <see cref="DataKind.Vector"/>, rank-3 as
/// <see cref="DataKind.Matrix"/> (or <see cref="DataKind.Tensor"/> when the dataset carries
/// a <c>datumingest_kind=tensor</c> attribute), and rank-4 or higher as
/// <see cref="DataKind.Tensor"/>.
/// Datasets inside groups use a flattened slash-separated name (e.g. "sensors/temperature").
/// </summary>
public sealed class Hdf5TableProvider : ITableProvider, ISeekableTableProvider
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

        // Build column names array and name index once.
        string[] columnNames = new string[columnDataList.Count];
        for (int columnIndex = 0; columnIndex < columnDataList.Count; columnIndex++)
        {
            columnNames[columnIndex] = columnDataList[columnIndex].Name;
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
        {
            nameIndex[columnNames[columnIndex]] = columnIndex;
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

            yield return new Row(columnNames, values, nameIndex);
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
                SupportsSeek: true,
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

    /// <inheritdoc />
    public async IAsyncEnumerable<Row> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using NativeFile file = H5File.OpenRead(descriptor.FilePath);

        List<DatasetEntry> allEntries = new();
        DiscoverDatasets(file, "", allEntries);

        List<DatasetEntry> entries = requiredColumns is not null
            ? allEntries.FindAll(entry => requiredColumns.Contains(entry.Path))
            : allEntries;

        if (entries.Count == 0)
        {
            yield break;
        }

        // Determine actual row count from datasets.
        long totalRows = 0;
        foreach (DatasetEntry entry in entries)
        {
            if (entry.Dataset.Space.Rank >= 1)
            {
                long datasetRows = (long)entry.Dataset.Space.Dimensions[0];
                totalRows = totalRows == 0 ? datasetRows : Math.Min(totalRows, datasetRows);
            }
        }

        // Clamp the requested range to available rows.
        long effectiveStart = Math.Min(startRow, totalRows);
        int effectiveCount = (int)Math.Min(count, totalRows - effectiveStart);

        if (effectiveCount <= 0)
        {
            yield break;
        }

        // Read sliced column data.
        List<ColumnData> columnDataList = new(entries.Count);
        foreach (DatasetEntry entry in entries)
        {
            ColumnData columnData = ReadDatasetColumnSlice(entry, effectiveStart, effectiveCount);
            columnDataList.Add(columnData);
        }

        // Build column names array and name index once.
        string[] columnNames = new string[columnDataList.Count];
        for (int columnIndex = 0; columnIndex < columnDataList.Count; columnIndex++)
        {
            columnNames[columnIndex] = columnDataList[columnIndex].Name;
        }

        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int columnIndex = 0; columnIndex < columnNames.Length; columnIndex++)
        {
            nameIndex[columnNames[columnIndex]] = columnIndex;
        }

        for (long rowIndex = 0; rowIndex < effectiveCount; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DataValue[] values = new DataValue[columnDataList.Count];
            for (int columnIndex = 0; columnIndex < columnDataList.Count; columnIndex++)
            {
                values[columnIndex] = columnDataList[columnIndex].GetValue(rowIndex);
            }

            yield return new Row(columnNames, values, nameIndex);
        }

        await Task.CompletedTask;
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

        if (rank == 2)
        {
            return DataKind.Vector;
        }

        if (rank == 3)
        {
            return HasTensorKindAttribute(dataset) ? DataKind.Tensor : DataKind.Matrix;
        }

        if (rank >= 4)
        {
            return DataKind.Tensor;
        }

        if (type.Class == H5DataTypeClass.FixedPoint)
        {
            if (type.Size == 1 && !type.FixedPoint.IsSigned)
            {
                return DataKind.UInt8;
            }
            return DataKind.Float32;
        }

        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            return DataKind.Float32;
        }

        return DataKind.String;
    }

    /// <summary>
    /// Checks whether the dataset carries the <c>datumingest_kind</c> attribute with value
    /// <c>"tensor"</c>, written by <see cref="Output.Writers.Hdf5OutputWriter"/> to
    /// disambiguate rank-3 tensors from matrices.
    /// </summary>
    private static bool HasTensorKindAttribute(IH5Dataset dataset)
    {
        if (!dataset.AttributeExists(Output.Writers.Hdf5OutputWriter.TensorKindAttributeName))
        {
            return false;
        }

        IH5Attribute attribute = dataset.Attribute(Output.Writers.Hdf5OutputWriter.TensorKindAttributeName);
        string value = attribute.Read<string>();
        return string.Equals(value, Output.Writers.Hdf5OutputWriter.TensorKindAttributeValue, StringComparison.Ordinal);
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
            DataValue.FromFloat32(_data[rowIndex]);
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
    /// Holds pre-read columnar data for a single rank-3 HDF5 dataset, yielding one
    /// <see cref="DataValue.FromMatrix"/> per row.
    /// </summary>
    private sealed class MatrixColumnData : ColumnData
    {
        private readonly float[] _flatData;
        private readonly int _matrixRows;
        private readonly int _matrixColumns;
        private readonly int _elementCount;

        public MatrixColumnData(string name, float[] flatData, long rowCount, int matrixRows, int matrixColumns)
            : base(name, rowCount)
        {
            _flatData = flatData;
            _matrixRows = matrixRows;
            _matrixColumns = matrixColumns;
            _elementCount = matrixRows * matrixColumns;
        }

        public override DataValue GetValue(long rowIndex)
        {
            float[] matrix = new float[_elementCount];
            Array.Copy(_flatData, rowIndex * _elementCount, matrix, 0, _elementCount);
            return DataValue.FromMatrix(matrix, _matrixRows, _matrixColumns);
        }
    }

    /// <summary>
    /// Holds pre-read columnar data for a single rank-4 or higher HDF5 dataset, yielding
    /// one <see cref="DataValue.FromTensor"/> per row.
    /// </summary>
    private sealed class TensorColumnData : ColumnData
    {
        private readonly float[] _flatData;
        private readonly int[] _shape;
        private readonly int _elementCount;

        public TensorColumnData(string name, float[] flatData, long rowCount, int[] shape)
            : base(name, rowCount)
        {
            _flatData = flatData;
            _shape = shape;
            _elementCount = 1;
            foreach (int dim in shape)
            {
                _elementCount *= dim;
            }
        }

        public override DataValue GetValue(long rowIndex)
        {
            float[] tensor = new float[_elementCount];
            Array.Copy(_flatData, rowIndex * _elementCount, tensor, 0, _elementCount);
            return DataValue.FromTensor(tensor, _shape);
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

        // Multi-dimensional numeric datasets: restore the original DataKind by rank.
        if (rank >= 2 && (type.Class == H5DataTypeClass.FloatingPoint || type.Class == H5DataTypeClass.FixedPoint))
        {
            long rowCount = (long)dimensions[0];
            float[] flatData = ReadAsFloatArray(dataset, type);

            if (rank == 2)
            {
                return new VectorColumnData(entry.Path, flatData, rowCount, (int)dimensions[1]);
            }

            if (rank == 3 && !HasTensorKindAttribute(dataset))
            {
                return new MatrixColumnData(entry.Path, flatData, rowCount, (int)dimensions[1], (int)dimensions[2]);
            }

            // rank >= 3 tensor or rank >= 4: trailing dimensions form the per-element shape.
            int[] shape = new int[rank - 1];
            for (int d = 1; d < rank; d++)
            {
                shape[d - 1] = (int)dimensions[d];
            }
            return new TensorColumnData(entry.Path, flatData, rowCount, shape);
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
    /// Reads a slice of a dataset into the appropriate <see cref="ColumnData"/> subclass
    /// using a <see cref="HyperslabSelection"/> to avoid materialising the entire dataset.
    /// </summary>
    private static ColumnData ReadDatasetColumnSlice(
        DatasetEntry entry,
        long startRow,
        int sliceRowCount)
    {
        IH5Dataset dataset = entry.Dataset;
        IH5DataType type = dataset.Type;
        byte rank = dataset.Space.Rank;

        // 1-D selection used for scalar, string, and uint8 datasets.
        HyperslabSelection selection = new(start: (ulong)startRow, block: (ulong)sliceRowCount);

        if (type.Class == H5DataTypeClass.String || type.Class == H5DataTypeClass.VariableLength)
        {
            string[] data = dataset.Read<string[]>(fileSelection: selection);
            return new StringColumnData(entry.Path, data);
        }

        if (rank >= 2 && (type.Class == H5DataTypeClass.FloatingPoint || type.Class == H5DataTypeClass.FixedPoint))
        {
            ulong[] dimensions = dataset.Space.Dimensions;

            // Build an N-D hyperslab selection that slices only the first dimension (rows).
            ulong[] starts = new ulong[rank];
            ulong[] blocks = new ulong[rank];
            starts[0] = (ulong)startRow;
            blocks[0] = (ulong)sliceRowCount;
            for (int dimensionIndex = 1; dimensionIndex < rank; dimensionIndex++)
            {
                starts[dimensionIndex] = 0;
                blocks[dimensionIndex] = dimensions[dimensionIndex];
            }

            HyperslabSelection multiDimensionalSelection = new(rank: rank, starts: starts, blocks: blocks);
            float[] flatData = ReadAsFloatArraySlice(dataset, type, multiDimensionalSelection);

            if (rank == 2)
            {
                return new VectorColumnData(entry.Path, flatData, sliceRowCount, (int)dimensions[1]);
            }

            if (rank == 3 && !HasTensorKindAttribute(dataset))
            {
                return new MatrixColumnData(entry.Path, flatData, sliceRowCount, (int)dimensions[1], (int)dimensions[2]);
            }

            // rank >= 3 tensor or rank >= 4: trailing dimensions form the per-element shape.
            int[] shape = new int[rank - 1];
            for (int d = 1; d < rank; d++)
            {
                shape[d - 1] = (int)dimensions[d];
            }
            return new TensorColumnData(entry.Path, flatData, sliceRowCount, shape);
        }

        if (type.Class == H5DataTypeClass.FixedPoint && type.Size == 1 && !type.FixedPoint.IsSigned)
        {
            byte[] data = dataset.Read<byte[]>(fileSelection: selection);
            return new UInt8ColumnData(entry.Path, data);
        }

        if (type.Class == H5DataTypeClass.FixedPoint || type.Class == H5DataTypeClass.FloatingPoint)
        {
            float[] data = ReadAsFloatArraySlice(dataset, type, selection);
            return new ScalarColumnData(entry.Path, data);
        }

        string[] fallback = new string[sliceRowCount];
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

    /// <summary>
    /// Reads a slice of a numeric dataset as a float[] array using a file selection.
    /// </summary>
    private static float[] ReadAsFloatArraySlice(
        IH5Dataset dataset,
        IH5DataType type,
        HyperslabSelection selection)
    {
        if (type.Class == H5DataTypeClass.FloatingPoint)
        {
            if (type.Size == 4)
            {
                return dataset.Read<float[]>(fileSelection: selection);
            }

            double[] doubleData = dataset.Read<double[]>(fileSelection: selection);
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
                int[] intData = dataset.Read<int[]>(fileSelection: selection);
                float[] floatData = new float[intData.Length];
                for (int index = 0; index < intData.Length; index++)
                {
                    floatData[index] = intData[index];
                }
                return floatData;
            }

            long[] longData = dataset.Read<long[]>(fileSelection: selection);
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
