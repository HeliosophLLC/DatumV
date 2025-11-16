namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

/// <summary>
/// Returns the width of an image in pixels by parsing the image header.
/// <c>width(img)</c> accepts Image or UInt8Array.
/// </summary>
public sealed class ImageWidthFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "width";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("width() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"width() requires Image or UInt8Array, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        byte[] imageBytes = input.Kind == DataKind.Image ? input.AsImage() : input.AsUInt8Array();
        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            return DataValue.Null(DataKind.Float32);
        }

        return DataValue.FromFloat32(dimensions.Width);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Float32);
        }

        ReadOnlySpan<byte> imageBytes = input.AsByteSpan(frame.Source, frame.SidecarRegistry);
        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            return DataValue.Null(DataKind.Float32);
        }

        return DataValue.FromFloat32(dimensions.Width);
    }
}
