using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Arrow;

/// <summary>
/// Converts a per-batch Apache Arrow column array into an array of
/// <see cref="DataValue"/>s — one entry per row in the batch. Handles
/// primitive scalars and 1-D array shapes (<see cref="ListArray"/>,
/// <see cref="FixedSizeListArray"/>); deeper nesting is deferred.
/// </summary>
internal static class ArrowColumnReader
{
    /// <summary>
    /// Decodes <paramref name="array"/> into a per-row
    /// <see cref="DataValue"/> array. <paramref name="rowCount"/> is the
    /// batch length and must equal <c>array.Length</c>.
    /// </summary>
    public static DataValue[] ReadAsRows(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        if (!type.IsSupported)
        {
            throw new InvalidOperationException(
                $"Arrow column has unsupported element kind {type.ElementKind} " +
                $"({type.UnderlyingTypeId}).");
        }

        if (type.IsArray)
        {
            return ReadArrayColumn(array, type, rowCount, arena);
        }
        return ReadScalarColumn(array, type, rowCount, arena);
    }

    private static DataValue[] ReadScalarColumn(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (array.IsNull(i))
            {
                result[i] = DataValue.Null(type.ElementKind);
                continue;
            }
            result[i] = BuildScalar(array, i, type, arena);
        }
        return result;
    }

    private static DataValue[] ReadArrayColumn(
        IArrowArray array,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        if (array is ListArray listArray)
        {
            return DecodeListRows(listArray, type, rowCount, arena);
        }
        if (array is FixedSizeListArray fixedListArray)
        {
            return DecodeFixedSizeListRows(fixedListArray, type, rowCount, arena);
        }
        throw new InvalidOperationException(
            $"Arrow array column has unexpected runtime type {array.GetType().Name}; expected ListArray or FixedSizeListArray.");
    }

    private static DataValue[] DecodeListRows(
        ListArray listArray,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (listArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(type.ElementKind);
                continue;
            }
            int start = listArray.ValueOffsets[i];
            int end = listArray.ValueOffsets[i + 1];
            result[i] = BuildArrayCell(listArray.Values, start, end - start, type, arena);
        }
        return result;
    }

    private static DataValue[] DecodeFixedSizeListRows(
        FixedSizeListArray fixedListArray,
        ArrowColumnType type,
        int rowCount,
        IValueStore arena)
    {
        int listSize = ((FixedSizeListType)fixedListArray.Data.DataType).ListSize;
        DataValue[] result = new DataValue[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            if (fixedListArray.IsNull(i))
            {
                result[i] = DataValue.NullArrayOf(type.ElementKind);
                continue;
            }
            int start = i * listSize;
            result[i] = BuildArrayCell(fixedListArray.Values, start, listSize, type, arena);
        }
        return result;
    }

    private static DataValue BuildScalar(IArrowArray array, int index, ArrowColumnType type, IValueStore arena)
    {
        return type.ElementKind switch
        {
            DataKind.Boolean => DataValue.FromBoolean(((BooleanArray)array).GetValue(index)!.Value),
            DataKind.Int8 => DataValue.FromInt8(((Int8Array)array).GetValue(index)!.Value),
            DataKind.UInt8 => DataValue.FromUInt8(((UInt8Array)array).GetValue(index)!.Value),
            DataKind.Int16 => DataValue.FromInt16(((Int16Array)array).GetValue(index)!.Value),
            DataKind.UInt16 => DataValue.FromUInt16(((UInt16Array)array).GetValue(index)!.Value),
            DataKind.Int32 => DataValue.FromInt32(((Int32Array)array).GetValue(index)!.Value),
            DataKind.UInt32 => DataValue.FromUInt32(((UInt32Array)array).GetValue(index)!.Value),
            DataKind.Int64 => DataValue.FromInt64(((Int64Array)array).GetValue(index)!.Value),
            DataKind.UInt64 => DataValue.FromUInt64(((UInt64Array)array).GetValue(index)!.Value),
            DataKind.Float32 when array is FloatArray fa => DataValue.FromFloat32(fa.GetValue(index)!.Value),
            DataKind.Float32 when array is HalfFloatArray hfa => DataValue.FromFloat32((float)hfa.GetValue(index)!.Value),
            DataKind.Float64 => DataValue.FromFloat64(((DoubleArray)array).GetValue(index)!.Value),
            DataKind.String => DataValue.FromString(((StringArray)array).GetString(index), arena),
            _ => throw new InvalidOperationException(
                $"Arrow scalar element kind {type.ElementKind} not yet wired."),
        };
    }

    private static DataValue BuildArrayCell(
        IArrowArray values,
        int start,
        int length,
        ArrowColumnType type,
        IValueStore arena)
    {
        switch (type.ElementKind)
        {
            case DataKind.Int8: return BuildPrimitive<sbyte>((Int8Array)values, start, length, DataKind.Int8, arena);
            case DataKind.UInt8: return BuildPrimitive<byte>((UInt8Array)values, start, length, DataKind.UInt8, arena);
            case DataKind.Int16: return BuildPrimitive<short>((Int16Array)values, start, length, DataKind.Int16, arena);
            case DataKind.UInt16: return BuildPrimitive<ushort>((UInt16Array)values, start, length, DataKind.UInt16, arena);
            case DataKind.Int32: return BuildPrimitive<int>((Int32Array)values, start, length, DataKind.Int32, arena);
            case DataKind.UInt32: return BuildPrimitive<uint>((UInt32Array)values, start, length, DataKind.UInt32, arena);
            case DataKind.Int64: return BuildPrimitive<long>((Int64Array)values, start, length, DataKind.Int64, arena);
            case DataKind.UInt64: return BuildPrimitive<ulong>((UInt64Array)values, start, length, DataKind.UInt64, arena);
            case DataKind.Float32 when values is FloatArray fa:
                return BuildPrimitive<float>(fa, start, length, DataKind.Float32, arena);
            case DataKind.Float64: return BuildPrimitive<double>((DoubleArray)values, start, length, DataKind.Float64, arena);
            case DataKind.String:
            {
                var strArr = (StringArray)values;
                string[] slice = new string[length];
                for (int i = 0; i < length; i++)
                {
                    slice[i] = strArr.GetString(start + i) ?? string.Empty;
                }
                return DataValue.FromStringArray(slice, arena);
            }
            case DataKind.Boolean:
            {
                var boolArr = (BooleanArray)values;
                byte[] packed = new byte[length];
                for (int i = 0; i < length; i++) packed[i] = boolArr.GetValue(start + i)!.Value ? (byte)1 : (byte)0;
                return DataValue.FromByteArray(packed, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Arrow array element kind {type.ElementKind} not yet wired.");
        }
    }

    private static DataValue BuildPrimitive<T>(
        PrimitiveArray<T> array,
        int start,
        int length,
        DataKind kind,
        IValueStore arena)
        where T : unmanaged, IEquatable<T>
    {
        T[] slice = new T[length];
        ReadOnlySpan<T> source = array.Values;
        source.Slice(start, length).CopyTo(slice);
        return DataValue.FromArenaArray<T>(slice, kind, arena);
    }
}
