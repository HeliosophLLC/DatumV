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
    private static readonly ScalarColumnEncoder ScalarEncoder = new();
    private static readonly UInt8ColumnEncoder UInt8Encoder = new();
    private static readonly FixedNumericColumnEncoder FixedNumericEncoder = new();
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
            DataKind.Float32 => ScalarEncoder,
            DataKind.UInt8 => UInt8Encoder,
            DataKind.Int8 => FixedNumericEncoder,
            DataKind.Int16 => FixedNumericEncoder,
            DataKind.UInt16 => FixedNumericEncoder,
            DataKind.Int32 => FixedNumericEncoder,
            DataKind.UInt32 => FixedNumericEncoder,
            DataKind.Int64 => FixedNumericEncoder,
            DataKind.UInt64 => FixedNumericEncoder,
            DataKind.Float64 => FixedNumericEncoder,
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
            _ => throw new NotSupportedException($"No encoder available for DataKind.{descriptor.Kind}.")
        };
    }
}
