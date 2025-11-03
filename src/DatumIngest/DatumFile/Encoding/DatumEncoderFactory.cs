using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Creates the canonical <see cref="DatumColumnEncoder"/> for a given
/// <see cref="DatumColumnDescriptor"/>.
/// </summary>
/// <remarks>
/// Encoder instances are stateless and reusable; the factory returns shared singletons for
/// all fixed-shape encoders. Only the <see cref="DictionaryColumnEncoder"/> requires the caller
/// to decide whether to use it (based on observed cardinality), so it is excluded from the
/// default selection logic.
/// </remarks>
public static class DatumEncoderFactory
{
    private static readonly FloatColumnEncoder FloatScalarEncoder = new();
    private static readonly IntegerColumnEncoder IntegerEncoder = new();
    private static readonly BooleanColumnEncoder BooleanEncoder = new();
    private static readonly DateColumnEncoder DateEncoder = new();
    private static readonly DateTimeColumnEncoder DateTimeEncoder = new();
    private static readonly TimeColumnEncoder TimeEncoder = new();
    private static readonly DurationColumnEncoder DurationEncoder = new();
    private static readonly UuidColumnEncoder UuidEncoder = new();
    private static readonly StringColumnEncoder StringEncoder = new();
    private static readonly BinaryColumnEncoder BinaryEncoder = new();
    private static readonly FixedShapeFloatColumnEncoder FloatEncoder = new();
    private static readonly ArrayColumnEncoder ArrayEncoder = new();
    private static readonly StructColumnEncoder StructEncoder = new();
    private static readonly DictionaryColumnEncoder DictionaryEncoder = new();

    /// <summary>
    /// Returns the default encoder for the column described by <paramref name="descriptor"/>.
    /// The selection is based on <see cref="DatumColumnDescriptor.Kind"/> and
    /// <see cref="DatumColumnDescriptor.IsDictionaryEligible"/>.
    /// </summary>
    /// <param name="descriptor">The column schema descriptor.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when no encoder is available for the given <see cref="DataKind"/>.
    /// </exception>
    public static DatumColumnEncoder GetEncoder(DatumColumnDescriptor descriptor)
    {
        // Dictionary encoding applies to any low-cardinality column regardless of kind.
        if (descriptor.IsDictionaryEligible)
        {
            return DictionaryEncoder;
        }

        return descriptor.Kind switch
        {
            DataKind.Float32 => FloatScalarEncoder,
            DataKind.Float64 => FloatScalarEncoder,
            // Byte-array via the new IsArray flag must match before scalar UInt8.
            // Switch expression checks `when` clauses in declaration order; the more
            // specific case wins. PR3 will remove the legacy UInt8Array arm below.
            DataKind.UInt8 when descriptor.IsArray => BinaryEncoder,
            DataKind.UInt8 => IntegerEncoder,
            DataKind.Int8 => IntegerEncoder,
            DataKind.Int16 => IntegerEncoder,
            DataKind.UInt16 => IntegerEncoder,
            DataKind.Int32 => IntegerEncoder,
            DataKind.UInt32 => IntegerEncoder,
            DataKind.Int64 => IntegerEncoder,
            DataKind.UInt64 => IntegerEncoder,
            DataKind.Boolean => BooleanEncoder,
            DataKind.Date => DateEncoder,
            DataKind.DateTime => DateTimeEncoder,
            DataKind.Time => TimeEncoder,
            DataKind.Duration => DurationEncoder,
            DataKind.Uuid => UuidEncoder,
            DataKind.String => StringEncoder,
            DataKind.JsonValue => StringEncoder,
            DataKind.UInt8Array => BinaryEncoder,
            DataKind.Image => BinaryEncoder,
            DataKind.Vector => FloatEncoder,
            DataKind.Matrix => FloatEncoder,
            DataKind.Tensor => FloatEncoder,
            DataKind.Array => ArrayEncoder,
            DataKind.Struct => StructEncoder,
            _ => throw new NotSupportedException($"No encoder available for DataKind.{descriptor.Kind}.")
        };
    }
}
