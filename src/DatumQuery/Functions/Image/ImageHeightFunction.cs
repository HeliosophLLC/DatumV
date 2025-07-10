namespace Axon.QueryEngine.Functions.Image;

using Axon.QueryEngine.Model;

/// <summary>
/// Returns the height of an image in pixels by parsing the image header.
/// <c>height(img)</c> accepts Image or UInt8Array.
/// </summary>
public sealed class ImageHeightFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "height";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("height() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException($"height() requires Image or UInt8Array, got {argumentKinds[0]}.");
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

        return DataValue.FromScalar(dimensions.Height);
    }
}
