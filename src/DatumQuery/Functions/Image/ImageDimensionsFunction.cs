namespace DatumQuery.Functions.Image;

using DatumQuery.Model;

/// <summary>
/// Returns image dimensions as a vector, with layout controlled by a format string.
/// <c>dimensions(img, format)</c> accepts Image or UInt8Array and a format string.
/// Supported formats: <c>'HWC'</c> → [height, width, channels],
/// <c>'CHW'</c> → [channels, height, width], <c>'WH'</c> → [width, height],
/// <c>'WHC'</c> → [width, height, channels].
/// </summary>
public sealed class ImageDimensionsFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "dimensions";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("dimensions() requires exactly 2 arguments: image, format.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"dimensions() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"dimensions() second argument must be String (format), got {argumentKinds[1]}.");
        }

        return DataKind.Vector;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Vector);
        }

        byte[] imageBytes = input.Kind == DataKind.Image ? input.AsImage() : input.AsUInt8Array();
        ImageDimensions? dimensions = ImageHeaderParser.TryParseHeader(imageBytes);

        if (dimensions is null)
        {
            return DataValue.Null(DataKind.Vector);
        }

        string format = arguments[1].AsString().ToUpperInvariant();

        float[] result = format switch
        {
            "HWC" => [dimensions.Height, dimensions.Width, dimensions.Channels],
            "CHW" => [dimensions.Channels, dimensions.Height, dimensions.Width],
            "WH" => [dimensions.Width, dimensions.Height],
            "WHC" => [dimensions.Width, dimensions.Height, dimensions.Channels],
            _ => throw new ArgumentException(
                $"dimensions() unknown format '{format}'. Supported: HWC, CHW, WH, WHC.")
        };

        return DataValue.FromVector(result);
    }
}
