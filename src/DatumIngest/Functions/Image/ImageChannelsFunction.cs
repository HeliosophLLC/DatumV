namespace DatumQuery.Functions.Image;

using DatumQuery.Model;

/// <summary>
/// Returns the number of color channels in an image by parsing the image header.
/// <c>channels(img)</c> accepts Image or UInt8Array.
/// Returns 1 for grayscale, 3 for RGB, 4 for RGBA, etc.
/// </summary>
public sealed class ImageChannelsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "channels";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("channels() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"channels() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        byte[] imageBytes = input.Kind == DataKind.Image ? input.AsImage() : input.AsUInt8Array();
        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return DataValue.FromScalar(dimensions.Channels);
    }
}
