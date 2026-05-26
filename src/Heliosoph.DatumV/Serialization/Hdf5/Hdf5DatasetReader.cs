using Heliosoph.DatumV.Model;
using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Reads an HDF5 dataset into a flat typed array, dispatching on the
/// mapped <see cref="DataKind"/>. v1 reads the entire dataset
/// into memory — fine for the typical ML / catalog case where datasets
/// are tens to hundreds of MB. Bigger-than-RAM datasets will land via
/// the chunked-streaming follow-up.
/// </summary>
internal static class Hdf5DatasetReader
{
    /// <summary>
    /// Reads <paramref name="dataset"/> into a typed array boxed as
    /// <see cref="System.Array"/>. Caller dispatches on
    /// <paramref name="type"/>.<see cref="Hdf5DatasetType.ElementKind"/>
    /// to cast back to the concrete element type.
    /// </summary>
    public static System.Array ReadFlat(IH5Dataset dataset, Hdf5DatasetType type)
    {
        // Float-array reads work even for 2-D datasets — PureHDF flattens
        // by default. Shape reconstruction happens in the TVF row builder
        // using Dimensions.
        return type.ElementKind switch
        {
            DataKind.Boolean => dataset.Read<bool[]>(),
            DataKind.Int8 => dataset.Read<sbyte[]>(),
            DataKind.UInt8 => dataset.Read<byte[]>(),
            DataKind.Int16 => dataset.Read<short[]>(),
            DataKind.UInt16 => dataset.Read<ushort[]>(),
            DataKind.Int32 => dataset.Read<int[]>(),
            DataKind.UInt32 => dataset.Read<uint[]>(),
            DataKind.Int64 => dataset.Read<long[]>(),
            DataKind.UInt64 => dataset.Read<ulong[]>(),
            DataKind.Float32 => dataset.Read<float[]>(),
            DataKind.Float64 => dataset.Read<double[]>(),
            DataKind.String => dataset.Read<string[]>(),
            _ => throw new InvalidOperationException(
                $"Unsupported HDF5 element kind for read: {type.ElementKind}"),
        };
    }

    /// <summary>
    /// Builds a single <see cref="DataValue"/> for the scalar element at
    /// position <paramref name="index"/> in <paramref name="flat"/>,
    /// boxing through the right <see cref="DataValue"/> factory for the
    /// kind.
    /// </summary>
    public static DataValue ScalarAt(System.Array flat, int index, DataKind kind, IValueStore arena) =>
        kind switch
        {
            DataKind.Boolean => DataValue.FromBoolean(((bool[])flat)[index]),
            DataKind.Int8 => DataValue.FromInt8(((sbyte[])flat)[index]),
            DataKind.UInt8 => DataValue.FromUInt8(((byte[])flat)[index]),
            DataKind.Int16 => DataValue.FromInt16(((short[])flat)[index]),
            DataKind.UInt16 => DataValue.FromUInt16(((ushort[])flat)[index]),
            DataKind.Int32 => DataValue.FromInt32(((int[])flat)[index]),
            DataKind.UInt32 => DataValue.FromUInt32(((uint[])flat)[index]),
            DataKind.Int64 => DataValue.FromInt64(((long[])flat)[index]),
            DataKind.UInt64 => DataValue.FromUInt64(((ulong[])flat)[index]),
            DataKind.Float32 => DataValue.FromFloat32(((float[])flat)[index]),
            DataKind.Float64 => DataValue.FromFloat64(((double[])flat)[index]),
            DataKind.String => DataValue.FromString(((string[])flat)[index] ?? string.Empty, arena),
            _ => throw new InvalidOperationException(
                $"Unsupported HDF5 element kind for scalar build: {kind}"),
        };

    /// <summary>
    /// Slices a typed array column from <paramref name="flat"/> starting
    /// at <paramref name="start"/> for <paramref name="length"/> elements,
    /// returning the slice as an arena-backed array <see cref="DataValue"/>.
    /// </summary>
    public static DataValue SliceArray(
        System.Array flat,
        int start,
        int length,
        DataKind kind,
        IValueStore arena)
    {
        switch (kind)
        {
            case DataKind.Int8:
            {
                sbyte[] s = new sbyte[length];
                Array.Copy((sbyte[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<sbyte>(s, kind, arena);
            }
            case DataKind.UInt8:
            {
                byte[] s = new byte[length];
                Array.Copy((byte[])flat, start, s, 0, length);
                return DataValue.FromByteArray(s, arena);
            }
            case DataKind.Int16:
            {
                short[] s = new short[length];
                Array.Copy((short[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<short>(s, kind, arena);
            }
            case DataKind.UInt16:
            {
                ushort[] s = new ushort[length];
                Array.Copy((ushort[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<ushort>(s, kind, arena);
            }
            case DataKind.Int32:
            {
                int[] s = new int[length];
                Array.Copy((int[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<int>(s, kind, arena);
            }
            case DataKind.UInt32:
            {
                uint[] s = new uint[length];
                Array.Copy((uint[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<uint>(s, kind, arena);
            }
            case DataKind.Int64:
            {
                long[] s = new long[length];
                Array.Copy((long[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<long>(s, kind, arena);
            }
            case DataKind.UInt64:
            {
                ulong[] s = new ulong[length];
                Array.Copy((ulong[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<ulong>(s, kind, arena);
            }
            case DataKind.Float32:
            {
                float[] s = new float[length];
                Array.Copy((float[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<float>(s, kind, arena);
            }
            case DataKind.Float64:
            {
                double[] s = new double[length];
                Array.Copy((double[])flat, start, s, 0, length);
                return DataValue.FromArenaArray<double>(s, kind, arena);
            }
            case DataKind.String:
            {
                string[] s = new string[length];
                Array.Copy((string[])flat, start, s, 0, length);
                return DataValue.FromStringArray(s, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported HDF5 element kind for array slice: {kind}");
        }
    }

    /// <summary>
    /// Slices an outer-axis row from a flat dataset read and packs it as a
    /// multi-dim <see cref="DataValue"/> with <paramref name="innerShape"/>.
    /// Used by <c>open_h5_dataset</c> when a source dataset has rank &gt;= 3:
    /// the row stream slices on the outermost dimension and each row carries
    /// an <c>(R-1)</c>-dim cell.
    /// </summary>
    /// <param name="flat">Flat read of the full dataset (row-major).</param>
    /// <param name="start">Start index in <paramref name="flat"/>.</param>
    /// <param name="length">Element count for this row (= product of inner shape).</param>
    /// <param name="innerShape">Shape of one row's cell — full dataset dims minus the outer axis.</param>
    /// <param name="kind">Element kind.</param>
    /// <param name="arena">Arena receiving the packed bytes.</param>
    public static DataValue SliceMultiDim(
        System.Array flat,
        int start,
        int length,
        ReadOnlySpan<int> innerShape,
        DataKind kind,
        IValueStore arena)
    {
        switch (kind)
        {
            case DataKind.Int8:
            {
                sbyte[] s = new sbyte[length];
                Array.Copy((sbyte[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<sbyte>(s, innerShape, kind, arena);
            }
            case DataKind.UInt8:
            {
                byte[] s = new byte[length];
                Array.Copy((byte[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<byte>(s, innerShape, kind, arena);
            }
            case DataKind.Int16:
            {
                short[] s = new short[length];
                Array.Copy((short[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<short>(s, innerShape, kind, arena);
            }
            case DataKind.UInt16:
            {
                ushort[] s = new ushort[length];
                Array.Copy((ushort[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<ushort>(s, innerShape, kind, arena);
            }
            case DataKind.Int32:
            {
                int[] s = new int[length];
                Array.Copy((int[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<int>(s, innerShape, kind, arena);
            }
            case DataKind.UInt32:
            {
                uint[] s = new uint[length];
                Array.Copy((uint[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<uint>(s, innerShape, kind, arena);
            }
            case DataKind.Int64:
            {
                long[] s = new long[length];
                Array.Copy((long[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<long>(s, innerShape, kind, arena);
            }
            case DataKind.UInt64:
            {
                ulong[] s = new ulong[length];
                Array.Copy((ulong[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<ulong>(s, innerShape, kind, arena);
            }
            case DataKind.Float32:
            {
                float[] s = new float[length];
                Array.Copy((float[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<float>(s, innerShape, kind, arena);
            }
            case DataKind.Float64:
            {
                double[] s = new double[length];
                Array.Copy((double[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimArray<double>(s, innerShape, kind, arena);
            }
            case DataKind.String:
            {
                string[] s = new string[length];
                Array.Copy((string[])flat, start, s, 0, length);
                return DataValue.FromArenaMultiDimStringArray(s, innerShape, arena);
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported HDF5 element kind for multi-dim slice: {kind}");
        }
    }
}
