using Heliosoph.DatumV.Model;
using Parquet.Data;

namespace Heliosoph.DatumV.Serialization.Parquet;

/// <summary>
/// Converts a Parquet <see cref="DataColumn"/> read out of a row group
/// into an array of <see cref="DataValue"/>s — one entry per logical
/// row in the row group. Handles primitive scalars and 1-D array
/// shapes (LIST&lt;T&gt; via DataField.IsArray); nested STRUCT and
/// LIST&lt;LIST&gt; come in Phase D.
/// </summary>
internal static class ParquetColumnReader
{
    /// <summary>
    /// Decodes <paramref name="column"/> into a per-row
    /// <see cref="DataValue"/> array. <paramref name="rowCount"/> is
    /// the row group's row count — used to size the result and to drive
    /// the repetition-level walk for array-shaped columns.
    /// </summary>
    public static DataValue[] ReadAsRows(
        DataColumn column,
        ParquetColumnType type,
        int rowCount,
        IValueStore arena)
    {
        if (!type.IsSupported)
        {
            throw new InvalidOperationException(
                $"Parquet column '{column.Field.Name}' has unsupported element kind {type.ElementKind}.");
        }

        if (type.IsArray)
        {
            return ReadArrayColumn(column, type, rowCount, arena);
        }
        return ReadScalarColumn(column, type, rowCount, arena);
    }

    private static DataValue[] ReadScalarColumn(
        DataColumn column,
        ParquetColumnType type,
        int rowCount,
        IValueStore arena)
    {
        Array data = column.Data;
        if (data.Length != rowCount)
        {
            throw new InvalidOperationException(
                $"Parquet column '{column.Field.Name}': data length {data.Length} != row count {rowCount}.");
        }

        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            result[i] = BuildScalar(data, i, type, arena);
        }
        return result;
    }

    /// <summary>
    /// Walks repetition levels to slice the flat array column into
    /// per-row sub-arrays. Repetition level 0 marks the first element
    /// of a new row; levels ≥ 1 continue the current row's list.
    /// Definition level &lt; max means a NULL element (skipped here —
    /// v1 elides nulls inside arrays; per-element nullability is part
    /// of the streaming follow-up).
    /// </summary>
    private static DataValue[] ReadArrayColumn(
        DataColumn column,
        ParquetColumnType type,
        int rowCount,
        IValueStore arena)
    {
        Array data = column.Data;
        int[]? repetitionLevels = column.RepetitionLevels;
        if (repetitionLevels is null || repetitionLevels.Length == 0)
        {
            // No repetition info but the column declared IsArray — degenerate
            // case (single-row file with one array). Treat as one row with
            // the whole flat array.
            DataValue[] singleton = new DataValue[1];
            singleton[0] = BuildArrayCell(data, 0, data.Length, type, arena);
            return singleton;
        }

        // First pass: find each row's [start, end) range in the flat array.
        int[] rowStarts = new int[rowCount + 1];
        int currentRow = -1;
        for (int i = 0; i < repetitionLevels.Length; i++)
        {
            if (repetitionLevels[i] == 0)
            {
                currentRow++;
                if (currentRow >= rowCount)
                {
                    throw new InvalidOperationException(
                        $"Parquet array column '{column.Field.Name}': repetition levels exceed row count {rowCount}.");
                }
                rowStarts[currentRow] = i;
            }
        }
        rowStarts[rowCount] = repetitionLevels.Length;

        DataValue[] result = new DataValue[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            int start = rowStarts[r];
            int length = rowStarts[r + 1] - start;
            result[r] = BuildArrayCell(data, start, length, type, arena);
        }
        return result;
    }

    private static DataValue BuildScalar(Array data, int index, ParquetColumnType type, IValueStore arena)
    {
        // For non-array columns where IsNullable was set, Parquet.Net's data
        // array is nullable-typed (e.g. int?[]). The boxed access via
        // GetValue handles both.
        object? raw = data.GetValue(index);
        if (raw is null) return DataValue.Null(type.ElementKind);

        return type.ElementKind switch
        {
            DataKind.Boolean => DataValue.FromBoolean((bool)raw),
            DataKind.Int8 => DataValue.FromInt8((sbyte)raw),
            DataKind.UInt8 => DataValue.FromUInt8((byte)raw),
            DataKind.Int16 => DataValue.FromInt16((short)raw),
            DataKind.UInt16 => DataValue.FromUInt16((ushort)raw),
            DataKind.Int32 => DataValue.FromInt32((int)raw),
            DataKind.UInt32 => DataValue.FromUInt32((uint)raw),
            DataKind.Int64 => DataValue.FromInt64((long)raw),
            DataKind.UInt64 => DataValue.FromUInt64((ulong)raw),
            DataKind.Float32 => DataValue.FromFloat32((float)raw),
            DataKind.Float64 => DataValue.FromFloat64((double)raw),
            DataKind.String => DataValue.FromString((string)raw, arena),
            _ => throw new InvalidOperationException(
                $"Parquet scalar element kind {type.ElementKind} not yet wired."),
        };
    }

    private static DataValue BuildArrayCell(
        Array data,
        int start,
        int length,
        ParquetColumnType type,
        IValueStore arena)
    {
        // Parquet.Net widens array-column storage to the nullable form
        // (e.g. int?[]), so we copy elements one at a time, materialising
        // the value type. Null elements within the array fall back to the
        // type's default — v1 doesn't carry per-element nullability through
        // typed arrays.
        switch (type.ElementKind)
        {
            case DataKind.Int8: return BuildPrimitiveArray<sbyte>(data, start, length, DataKind.Int8, arena);
            case DataKind.UInt8: return BuildByteArrayCell(data, start, length, arena);
            case DataKind.Int16: return BuildPrimitiveArray<short>(data, start, length, DataKind.Int16, arena);
            case DataKind.UInt16: return BuildPrimitiveArray<ushort>(data, start, length, DataKind.UInt16, arena);
            case DataKind.Int32: return BuildPrimitiveArray<int>(data, start, length, DataKind.Int32, arena);
            case DataKind.UInt32: return BuildPrimitiveArray<uint>(data, start, length, DataKind.UInt32, arena);
            case DataKind.Int64: return BuildPrimitiveArray<long>(data, start, length, DataKind.Int64, arena);
            case DataKind.UInt64: return BuildPrimitiveArray<ulong>(data, start, length, DataKind.UInt64, arena);
            case DataKind.Float32: return BuildPrimitiveArray<float>(data, start, length, DataKind.Float32, arena);
            case DataKind.Float64: return BuildPrimitiveArray<double>(data, start, length, DataKind.Float64, arena);
            case DataKind.String:
            {
                string[] slice = new string[length];
                for (int i = 0; i < length; i++)
                {
                    slice[i] = (string?)data.GetValue(start + i) ?? string.Empty;
                }
                return DataValue.FromStringArray(slice, arena);
            }
            case DataKind.Boolean:
            {
                bool[] slice = new bool[length];
                for (int i = 0; i < length; i++)
                {
                    object? raw = data.GetValue(start + i);
                    slice[i] = raw is bool b && b;
                }
                // No FromArenaArray<bool> overload — pack as UInt8 to match
                // the convention from FITS BinTable bool-array decoding.
                byte[] packed = new byte[length];
                for (int i = 0; i < length; i++) packed[i] = slice[i] ? (byte)1 : (byte)0;
                return DataValue.FromByteArray(packed, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Parquet array element kind {type.ElementKind} not yet wired.");
        }
    }

    private static DataValue BuildPrimitiveArray<T>(
        Array data, int start, int length, DataKind kind, IValueStore arena)
        where T : unmanaged
    {
        T[] slice = new T[length];
        for (int i = 0; i < length; i++)
        {
            object? raw = data.GetValue(start + i);
            slice[i] = raw is T value ? value : default;
        }
        return DataValue.FromArenaArray<T>(slice, kind, arena);
    }

    private static DataValue BuildByteArrayCell(Array data, int start, int length, IValueStore arena)
    {
        byte[] slice = new byte[length];
        for (int i = 0; i < length; i++)
        {
            object? raw = data.GetValue(start + i);
            slice[i] = raw is byte value ? value : (byte)0;
        }
        return DataValue.FromByteArray(slice, arena);
    }
}
