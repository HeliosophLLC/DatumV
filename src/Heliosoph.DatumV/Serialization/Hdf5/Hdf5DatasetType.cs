using Heliosoph.DatumV.Model;
using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Mapped HDF5 element kind + shape for a dataset (or array-valued
/// attribute). Carries the <see cref="Model.DataKind"/> that downstream
/// SQL columns should use, plus the on-disk dimensions so the row
/// pipeline can shape its output.
/// </summary>
/// <remarks>
/// <para>
/// HDF5 datatypes are richer than what one <see cref="DataKind"/> can
/// capture (compound dtypes, opaque blobs, references, bit fields,
/// enumerated types). v1 maps the common scalar dtypes (signed and
/// unsigned 8/16/32/64-bit integers, IEEE single/double floats,
/// fixed-width and variable-length strings) and surfaces everything
/// else with <see cref="IsSupported"/> = <c>false</c> so callers can
/// skip the dataset cleanly instead of crashing on an unexpected
/// shape.
/// </para>
/// </remarks>
internal readonly record struct Hdf5DatasetType(
    DataKind ElementKind,
    IReadOnlyList<ulong> Dimensions,
    bool IsScalar,
    bool IsSupported,
    H5DataTypeClass UnderlyingClass)
{
    /// <summary>
    /// Populated only when <see cref="UnderlyingClass"/> is
    /// <see cref="H5DataTypeClass.Compound"/>: the field layout the row
    /// decoder uses to project compound bytes into a Struct
    /// <see cref="DataValue"/>. <c>null</c> for non-compound dtypes.
    /// </summary>
    public Hdf5CompoundLayout? CompoundLayout { get; init; }

    /// <summary>True when the dataset has rank &gt; 0 (i.e. is array-shaped).</summary>
    public bool IsArrayShaped => !IsScalar && Dimensions.Count > 0;

    /// <summary>
    /// Total element count across all dimensions (1 for scalars, product
    /// of <see cref="Dimensions"/> otherwise).
    /// </summary>
    public ulong ElementCount
    {
        get
        {
            if (IsScalar) return 1;
            ulong product = 1;
            for (int i = 0; i < Dimensions.Count; i++)
            {
                product = checked(product * Dimensions[i]);
            }
            return product;
        }
    }

    /// <summary>
    /// Maps an HDF5 datatype + dataspace pair to a
    /// <see cref="Hdf5DatasetType"/>. The supported set is documented on
    /// the type itself; unsupported dtypes produce a record with
    /// <see cref="IsSupported"/> = <c>false</c> and
    /// <see cref="ElementKind"/> = <see cref="DataKind.Unknown"/>.
    /// </summary>
    public static Hdf5DatasetType From(IH5DataType type, IH5Dataspace space)
    {
        bool isScalar = space.Type == H5DataspaceType.Scalar;
        ulong[] dimensions = isScalar ? [] : space.Dimensions;

        // Compound dtypes go through a dedicated path: ElementKind becomes
        // Struct, IsSupported is gated on whether every member type maps
        // cleanly, and the field layout rides as an extra property the row
        // decoder needs.
        if (type.Class == H5DataTypeClass.Compound && type.Compound is { } compound)
        {
            Hdf5CompoundLayout layout = Hdf5CompoundLayout.Build(compound, checked((int)type.Size));
            return new Hdf5DatasetType(
                ElementKind: DataKind.Struct,
                Dimensions: dimensions,
                IsScalar: isScalar,
                IsSupported: layout.IsFullySupported,
                UnderlyingClass: type.Class)
            {
                CompoundLayout = layout,
            };
        }

        (DataKind kind, bool supported) = MapElementKind(type);
        return new Hdf5DatasetType(
            ElementKind: kind,
            Dimensions: dimensions,
            IsScalar: isScalar,
            IsSupported: supported,
            UnderlyingClass: type.Class);
    }

    private static (DataKind Kind, bool Supported) MapElementKind(IH5DataType type)
    {
        switch (type.Class)
        {
            case H5DataTypeClass.FixedPoint:
                return MapFixedPoint(type);

            case H5DataTypeClass.FloatingPoint:
                return type.Size switch
                {
                    4 => (DataKind.Float32, true),
                    8 => (DataKind.Float64, true),
                    _ => (DataKind.Unknown, false),
                };

            case H5DataTypeClass.String:
                // Both fixed-width and (within H5DataTypeClass.String) padded
                // strings land here. Variable-length strings come through
                // the VariableLength class below.
                return (DataKind.String, true);

            case H5DataTypeClass.VariableLength:
                // HDF5's wire-level representation for a variable-length
                // string is a vlen sequence of bytes (BaseType.Class =
                // FixedPoint, Size = 1). This is the dominant case in real
                // files — managed-string attributes written by PureHDF land
                // here, as do most "string" columns in Python-produced HDF5.
                // Other vlen shapes (vlen of int, vlen of float — used for
                // ragged arrays) are out of v1 scope.
                {
                    IH5DataType? baseType = type.VariableLength?.BaseType;
                    if (baseType?.Class == H5DataTypeClass.String) return (DataKind.String, true);
                    if (baseType?.Class == H5DataTypeClass.FixedPoint && baseType.Size == 1) return (DataKind.String, true);
                    return (DataKind.Unknown, false);
                }

            // Compound dtypes (HDF5 structs), opaque blobs, references, bit
            // fields, enumerated types, and arrays-of-arrays are all
            // deliberately deferred in v1.
            default:
                return (DataKind.Unknown, false);
        }
    }

    private static (DataKind Kind, bool Supported) MapFixedPoint(IH5DataType type)
    {
        bool signed = type.FixedPoint?.IsSigned ?? true;
        return (type.Size, signed) switch
        {
            (1, true) => (DataKind.Int8, true),
            (1, false) => (DataKind.UInt8, true),
            (2, true) => (DataKind.Int16, true),
            (2, false) => (DataKind.UInt16, true),
            (4, true) => (DataKind.Int32, true),
            (4, false) => (DataKind.UInt32, true),
            (8, true) => (DataKind.Int64, true),
            (8, false) => (DataKind.UInt64, true),
            _ => (DataKind.Unknown, false),
        };
    }
}
