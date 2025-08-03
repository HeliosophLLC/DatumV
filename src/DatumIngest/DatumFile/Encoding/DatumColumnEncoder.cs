using DatumIngest.Model;

namespace DatumIngest.DatumFile.Encoding;

/// <summary>
/// Converts a row group's worth of <see cref="DataValue"/> instances for a single column
/// into a compressed, encoded <see cref="DatumEncodedPage"/> ready to be written to a
/// <c>.datum</c> file.
/// </summary>
/// <remarks>
/// Each concrete subclass handles one or more <see cref="DataKind"/> values and chooses
/// the canonical encoding and compression for that kind. The <see cref="DatumEncoderFactory"/>
/// maps column descriptors to the appropriate encoder instance.
/// </remarks>
public abstract class DatumColumnEncoder
{
    /// <summary>
    /// Encodes a single column page using an empty writer context.
    /// Suitable for test scenarios and any caller that does not need blob externalization.
    /// </summary>
    /// <param name="values">All row values for this column page, in row order.</param>
    /// <param name="descriptor">The column schema descriptor for this column.</param>
    public DatumEncodedPage Encode(IReadOnlyList<DataValue> values, DatumColumnDescriptor descriptor)
        => Encode(values, descriptor, DatumEncoderContext.Empty);

    /// <summary>
    /// Encodes a single column page and returns the compressed payload plus footer metadata.
    /// </summary>
    /// <param name="values">All row values for this column page, in row order.</param>
    /// <param name="descriptor">The column schema descriptor for this column.</param>
    /// <param name="context">
    /// Writer context carrying the file path and row group index. Required by
    /// <see cref="BinaryColumnEncoder"/> for blob externalization; other encoders may ignore it.
    /// </param>
    public abstract DatumEncodedPage Encode(
        IReadOnlyList<DataValue> values,
        DatumColumnDescriptor descriptor,
        DatumEncoderContext context);
}
