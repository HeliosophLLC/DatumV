using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// One HDF5 attribute: name + mapped element type + materialised value.
/// Values are read into managed primitives so the
/// <c>open_h5_meta</c> TVF can fold them into its <c>attributes</c>
/// JSON column without going back to PureHDF per row.
/// </summary>
/// <param name="Name">Attribute name.</param>
/// <param name="Type">Element kind + shape mapped from the HDF5 datatype.</param>
/// <param name="Value">
/// Materialised value (or <c>null</c> for unsupported dtypes / read
/// failures): a single boxed primitive for scalar attributes, an
/// <see cref="System.Array"/> of typed primitives for array-shaped
/// attributes. <see cref="Type"/>.<see cref="Hdf5DatasetType.IsSupported"/>
/// is <c>false</c> when reading was skipped.
/// </param>
internal readonly record struct Hdf5AttributeRecord(
    string Name,
    Hdf5DatasetType Type,
    object? Value)
{
    /// <summary>
    /// Reads <paramref name="attribute"/> from an open HDF5 file into an
    /// <see cref="Hdf5AttributeRecord"/>. Unsupported dtypes produce a
    /// record with <see cref="Value"/> = <c>null</c>; callers can still
    /// surface the name and the underlying class string for diagnostic
    /// JSON output.
    /// </summary>
    public static Hdf5AttributeRecord Read(IH5Attribute attribute)
    {
        Hdf5DatasetType type = Hdf5DatasetType.From(attribute.Type, attribute.Space);
        if (!type.IsSupported)
        {
            return new Hdf5AttributeRecord(attribute.Name, type, Value: null);
        }

        object? value = ReadTyped(attribute, type);
        return new Hdf5AttributeRecord(attribute.Name, type, value);
    }

    private static object? ReadTyped(IH5Attribute attribute, Hdf5DatasetType type)
    {
        // PureHDF.IH5Attribute.Read<T> always returns an array (T = element[]).
        // For scalars we take element [0]; for array-shaped attributes we
        // return the array itself so JSON serialisation can render `[…]`.
        object? array = type.ElementKind switch
        {
            Model.DataKind.Int8 => attribute.Read<sbyte[]>(),
            Model.DataKind.UInt8 => attribute.Read<byte[]>(),
            Model.DataKind.Int16 => attribute.Read<short[]>(),
            Model.DataKind.UInt16 => attribute.Read<ushort[]>(),
            Model.DataKind.Int32 => attribute.Read<int[]>(),
            Model.DataKind.UInt32 => attribute.Read<uint[]>(),
            Model.DataKind.Int64 => attribute.Read<long[]>(),
            Model.DataKind.UInt64 => attribute.Read<ulong[]>(),
            Model.DataKind.Float32 => attribute.Read<float[]>(),
            Model.DataKind.Float64 => attribute.Read<double[]>(),
            Model.DataKind.String => attribute.Read<string[]>(),
            _ => null,
        };

        if (array is null) return null;

        if (type.IsScalar)
        {
            // Scalar attributes are still read as a one-element array.
            System.Array arr = (System.Array)array;
            return arr.Length == 0 ? null : arr.GetValue(0);
        }

        return array;
    }
}
