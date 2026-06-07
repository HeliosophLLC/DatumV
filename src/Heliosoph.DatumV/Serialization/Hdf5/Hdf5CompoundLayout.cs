using Heliosoph.DatumV.Model;
using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Parsed layout of an HDF5 compound dtype: one entry per member with
/// the member name, mapped element kind, byte offset within a row, and
/// byte size on disk. Built once per dataset and consumed by the row
/// decoder to project the raw byte stream into typed
/// <see cref="DataValue"/> fields.
/// </summary>
/// <remarks>
/// <para>
/// v1 supports compound dtypes whose members are themselves primitive
/// (fixed-width integer / IEEE float / boolean / fixed-width string).
/// Nested compound members, variable-length members (vlen string / vlen
/// array), array members, and reference members trip the "unsupported"
/// flag — the reader will refuse the dataset with a clear message. Most
/// real-world astronomical and ML compound datasets are flat-primitive
/// (catalog rows, event lists, particle records), so v1 covers the
/// dominant case.
/// </para>
/// </remarks>
internal sealed class Hdf5CompoundLayout
{
    /// <summary>Field metadata in member order.</summary>
    public required IReadOnlyList<Hdf5CompoundField> Fields { get; init; }

    /// <summary>Total byte size of one compound row on disk (sum of member sizes plus alignment padding).</summary>
    public required int RowByteSize { get; init; }

    /// <summary>
    /// True when every member's dtype is one the v1 row decoder knows
    /// how to project. When false, callers should surface the dataset as
    /// <c>is_supported = false</c> in <c>open_h5_meta</c> and refuse it
    /// in <c>open_h5_dataset</c>.
    /// </summary>
    public required bool IsFullySupported { get; init; }

    /// <summary>
    /// Builds the layout from <paramref name="compound"/>'s member list.
    /// Each member's <see cref="IH5DataType"/> is mapped through the
    /// same primitive-dtype path the top-level reader uses.
    /// </summary>
    public static Hdf5CompoundLayout Build(ICompoundType compound, int rowByteSize)
    {
        Hdf5CompoundField[] fields = new Hdf5CompoundField[compound.Members.Length];
        bool allSupported = true;

        for (int i = 0; i < compound.Members.Length; i++)
        {
            CompoundMember member = compound.Members[i];
            (DataKind kind, bool supported) = MapMember(member.Type);
            fields[i] = new Hdf5CompoundField
            {
                Name = member.Name,
                Kind = kind,
                IsSupported = supported,
                ByteOffset = checked((int)member.Offset),
                ByteSize = checked((int)member.Type.Size),
            };
            if (!supported) allSupported = false;
        }

        return new Hdf5CompoundLayout
        {
            Fields = fields,
            RowByteSize = rowByteSize,
            IsFullySupported = allSupported,
        };
    }

    /// <summary>
    /// Maps an HDF5 member dtype to the mapped <see cref="DataKind"/>
    /// the row decoder will project into. Mirrors the top-level
    /// <see cref="Hdf5DatasetType.From"/> logic for primitive cases,
    /// with extra restrictions for v1: variable-length members and
    /// nested compounds aren't supported.
    /// </summary>
    private static (DataKind Kind, bool Supported) MapMember(IH5DataType memberType)
    {
        switch (memberType.Class)
        {
            case H5DataTypeClass.FixedPoint:
                return MapFixedPoint(memberType);
            case H5DataTypeClass.FloatingPoint:
                return memberType.Size switch
                {
                    4 => (DataKind.Float32, true),
                    8 => (DataKind.Float64, true),
                    _ => (DataKind.Unknown, false),
                };
            case H5DataTypeClass.String:
                // Fixed-width string members are common (e.g. an 8-byte name).
                // Variable-length string members go through VariableLength
                // and aren't supported in v1.
                return (DataKind.String, true);
            // Nested compounds, vlen members, arrays of members, references,
            // bit fields, enumerated, opaque — none in v1.
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

/// <summary>One field's slot inside an HDF5 compound dtype.</summary>
internal sealed class Hdf5CompoundField
{
    public required string Name { get; init; }
    public required DataKind Kind { get; init; }
    public required bool IsSupported { get; init; }
    public required int ByteOffset { get; init; }
    public required int ByteSize { get; init; }
}
