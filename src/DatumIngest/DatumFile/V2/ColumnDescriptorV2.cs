using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2;

/// <summary>
/// V2 column descriptor — schema-level metadata for one column. Carries the
/// column's name, data kind, fixed shape (for Vector/Matrix/Tensor), and the
/// <see cref="EncoderKind"/> chosen by the writer at column-creation time.
/// </summary>
/// <param name="Name">Column name as it appears in query expressions.</param>
/// <param name="Kind">The <see cref="DataKind"/> of values in this column.</param>
/// <param name="Encoder">
/// Which of the three v2 encoders this column's pages use. Determined from
/// <paramref name="Kind"/> by <see cref="ColumnDescriptorV2.EncoderFor"/>;
/// stable for the column's lifetime.
/// </param>
/// <param name="IsNullable">True when the column may contain nulls. Drives the per-page null-bitmap presence.</param>
/// <param name="IsArray">
/// True when this column holds typed arrays of <paramref name="Kind"/> elements
/// rather than scalars. Combines with <paramref name="Kind"/> to specify the
/// array element type. Mutually exclusive with the legacy array kinds
/// (UInt8Array, Vector, Matrix, Tensor) — those carry the array-ness in the
/// <see cref="DataKind"/> itself and leave this <c>false</c>.
/// </param>
/// <param name="FixedShape">
/// Per-row dimensions for fixed-shape arrays (<c>Float32[384]</c> →
/// <c>[384]</c>, <c>Array&lt;Float32&gt;(3, 3)</c> → <c>[3, 3]</c>). All
/// entries positive. <see langword="null"/> for variable-length arrays and
/// non-array kinds. Frozen at first-row-flush time. Persisted via
/// <c>ColumnFlagsV2.HasFixedShape</c>.
/// </param>
/// <param name="IsTombstoned">
/// When <see langword="true"/>, the column has been soft-dropped via
/// <c>ALTER TABLE DROP COLUMN</c>. The column block (descriptor + page
/// directory + zone maps) remains in the footer for compaction-time
/// reclamation, but readers skip it at schema enumeration. Backed by
/// <c>ColumnFlagsV2.Tombstoned</c> in the on-disk footer.
/// </param>
/// <param name="MaxLength">
/// Declared character maximum length for a <see cref="DataKind.String"/>
/// column (<c>VARCHAR(N)</c> / <c>CHAR(N)</c> / <c>String(N)</c>).
/// <see langword="null"/> for bare strings (<c>TEXT</c> / <c>VARCHAR</c>
/// without a length) and for all non-string kinds. Persisted via
/// <c>ColumnFlagsV2.HasMaxLength</c>. INSERT-time enforcement happens
/// in <c>LiteralCoercion</c>.
/// </param>
/// <param name="IsBlankPadded">
/// True when the column was declared as <c>CHAR(N)</c> (blank-padded
/// fixed-length); false for <c>VARCHAR(N)</c> and bare strings.
/// Only meaningful when <paramref name="Kind"/> is
/// <see cref="DataKind.String"/> and <paramref name="MaxLength"/> is
/// set. Persisted via <c>ColumnFlagsV2.IsBlankPadded</c>.
/// </param>
public sealed record ColumnDescriptorV2(
    string Name,
    DataKind Kind,
    EncoderKind Encoder,
    bool IsNullable,
    bool IsArray = false,
    int[]? FixedShape = null,
    bool IsTombstoned = false,
    int? MaxLength = null,
    bool IsBlankPadded = false)
{
    /// <summary>
    /// Picks the appropriate <see cref="EncoderKind"/> for a given
    /// <see cref="DataKind"/>. Booleans get the bit-packed special case;
    /// fixed-width scalars get <see cref="EncoderKind.FixedWidth"/>;
    /// everything else (variable-length kinds and structs) gets
    /// <see cref="EncoderKind.VariableSlot"/>.
    /// </summary>
    public static EncoderKind EncoderFor(DataKind kind, bool isArray)
    {
        // Typed-array columns (Int32[], Float64[], …) all use VariableSlot
        // because the per-row payload is variable-length even when the
        // element kind is fixed-width.
        if (isArray) return EncoderKind.VariableSlot;

        return kind switch
        {
            DataKind.Boolean => EncoderKind.BitPackedBoolean,

            // Fixed-stride scalars
            DataKind.Int8 or DataKind.UInt8
                or DataKind.Int16 or DataKind.UInt16
                or DataKind.Int32 or DataKind.UInt32
                or DataKind.Int64 or DataKind.UInt64
                or DataKind.Int128 or DataKind.UInt128
                or DataKind.Float16 or DataKind.Float32 or DataKind.Float64
                or DataKind.Decimal
                or DataKind.Date or DataKind.Time
                or DataKind.Duration
                or DataKind.Timestamp or DataKind.TimestampTz
                or DataKind.Uuid
                or DataKind.Point2D or DataKind.Point3D
                => EncoderKind.FixedWidth,

            // Variable-length kinds — the slot is 16 bytes either inline
            // or sidecar-pointer. Byte-array columns reach VariableSlot via
            // the IsArray short-circuit at the top of this method.
            DataKind.String
                or DataKind.Image
                or DataKind.Audio
                or DataKind.Video
                or DataKind.Json
                or DataKind.PointCloud
                or DataKind.Mesh
                or DataKind.Struct
                => EncoderKind.VariableSlot,

            _ => throw new NotSupportedException(
                $"v2 format does not yet support DataKind.{kind}. " +
                "Either map it to one of the existing kinds or extend EncoderFor with a new case."),
        };
    }

    /// <summary>
    /// Stride in bytes for fixed-width payloads (one row's payload). Only
    /// meaningful when <see cref="Encoder"/> is <see cref="EncoderKind.FixedWidth"/>;
    /// throws for other encoder kinds.
    /// </summary>
    public int FixedWidthStrideBytes => Kind switch
    {
        DataKind.Int8 or DataKind.UInt8 => 1,
        DataKind.Int16 or DataKind.UInt16 or DataKind.Float16 => 2,
        DataKind.Int32 or DataKind.UInt32 or DataKind.Float32 or DataKind.Date => 4,
        DataKind.Int64 or DataKind.UInt64 or DataKind.Float64 or DataKind.Time or DataKind.Duration => 8,
        DataKind.Timestamp or DataKind.TimestampTz => 8, // int64 ticks
        DataKind.Point2D => 8,  // 2 × float32
        DataKind.Point3D => 12, // 3 × float32
        DataKind.Uuid or DataKind.Decimal or DataKind.UInt128 or DataKind.Int128 => 16,
        _ => throw new InvalidOperationException(
            $"FixedWidthStrideBytes is undefined for DataKind.{Kind} (encoder = {Encoder})."),
    };
}
