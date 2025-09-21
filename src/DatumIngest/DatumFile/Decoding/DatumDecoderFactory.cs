using DatumIngest.Model;

namespace DatumIngest.DatumFile.Decoding;

/// <summary>
/// Creates the canonical <see cref="DatumColumnDecoder"/> for a given
/// <see cref="DatumColumnDescriptor"/> and the page-level <see cref="DatumEncoding"/>.
/// </summary>
/// <remarks>
/// The page encoding is the primary discriminator: <see cref="DatumEncoding.DictionaryRLE"/>
/// and <see cref="DatumEncoding.ExternalBytes"/> override the kind-based switch regardless
/// of what kind the column was declared as.
/// </remarks>
public static class DatumDecoderFactory
{
    private static readonly FloatColumnDecoder FloatScalarDecoder = new();
    private static readonly IntegerColumnDecoder IntegerDecoder = new();
    private static readonly BooleanColumnDecoder BooleanDecoder = new();
    private static readonly DateColumnDecoder DateDecoder = new();
    private static readonly DateTimeColumnDecoder DateTimeDecoder = new();
    private static readonly TimeColumnDecoder TimeDecoder = new();
    private static readonly DurationColumnDecoder DurationDecoder = new();
    private static readonly UuidColumnDecoder UuidDecoder = new();
    private static readonly StringColumnDecoder StringDecoder = new();
    private static readonly BinaryColumnDecoder BinaryDecoder = new();
    private static readonly FixedShapeFloatColumnDecoder FloatDecoder = new();
    private static readonly DictionaryColumnDecoder DictionaryDecoder = new();
    private static readonly ArrayColumnDecoder ArrayDecoder = new();
    private static readonly StructColumnDecoder StructDecoder = new();

    /// <summary>
    /// Returns the decoder for the given column descriptor and page encoding.
    /// </summary>
    /// <param name="descriptor">The column schema descriptor.</param>
    /// <param name="encoding">The encoding value recorded in the column chunk descriptor.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when no decoder is available for the given combination of kind and encoding.
    /// </exception>
    public static DatumColumnDecoder GetDecoder(DatumColumnDescriptor descriptor, DatumEncoding encoding)
    {
        // Dictionary and externalized encodings override the kind-based dispatch.
        if (encoding == DatumEncoding.DictionaryRLE)
        {
            return DictionaryDecoder;
        }

        if (encoding == DatumEncoding.ExternalBytes)
        {
            return BinaryDecoder;
        }

        return descriptor.Kind switch
        {
            DataKind.Float32 => FloatScalarDecoder,
            DataKind.Float64 => FloatScalarDecoder,
            DataKind.UInt8 => IntegerDecoder,
            DataKind.Int8 => IntegerDecoder,
            DataKind.Int16 => IntegerDecoder,
            DataKind.UInt16 => IntegerDecoder,
            DataKind.Int32 => IntegerDecoder,
            DataKind.UInt32 => IntegerDecoder,
            DataKind.Int64 => IntegerDecoder,
            DataKind.UInt64 => IntegerDecoder,
            DataKind.Boolean => BooleanDecoder,
            DataKind.Date => DateDecoder,
            DataKind.DateTime => DateTimeDecoder,
            DataKind.Time => TimeDecoder,
            DataKind.Duration => DurationDecoder,
            DataKind.Uuid => UuidDecoder,
            DataKind.String => StringDecoder,
            DataKind.JsonValue => StringDecoder,
            DataKind.UInt8Array => BinaryDecoder,
            DataKind.Image => BinaryDecoder,
            DataKind.Vector => FloatDecoder,
            DataKind.Matrix => FloatDecoder,
            DataKind.Tensor => FloatDecoder,
            DataKind.Array => ArrayDecoder,
            DataKind.Struct => StructDecoder,
            _ => throw new NotSupportedException(
                $"No decoder available for DataKind.{descriptor.Kind}.")
        };
    }
}
