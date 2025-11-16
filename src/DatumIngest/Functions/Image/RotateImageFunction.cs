namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Rotates an image by the specified number of degrees clockwise.
/// <c>rotate(img, degrees)</c> or <c>rotate(img, degrees, format)</c>.
/// For non-90°-multiple rotations the canvas expands to avoid clipping.
/// The optional format argument controls output encoding (<c>'jpeg'</c>, <c>'png'</c>, <c>'webp'</c>).
/// </summary>
public sealed class RotateImageFunction : IScalarFunction, ICostAwareFunction
{
    /// <inheritdoc />
    public string Name => "rotate";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException("rotate() requires 2 or 3 arguments: image, degrees[, format].");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"rotate() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"rotate() third argument (format) must be String, got {argumentKinds[2]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        float degrees = arguments[1].AsFloat32();

        string? formatOverride = arguments.Length == 3 ? arguments[2].AsString() : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("rotate");

        // Compute expanded canvas size to avoid clipping
        double radians = degrees * System.Math.PI / 180.0;
        double sinAngle = System.Math.Abs(System.Math.Sin(radians));
        double cosAngle = System.Math.Abs(System.Math.Cos(radians));

        int newWidth = (int)System.Math.Round(original.Width * cosAngle + original.Height * sinAngle);
        int newHeight = (int)System.Math.Round(original.Width * sinAngle + original.Height * cosAngle);

        SKBitmap rotated = new(newWidth, newHeight);
        using SKCanvas canvas = new(rotated);

        canvas.Clear(SKColors.Transparent);
        canvas.Translate(newWidth / 2f, newHeight / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-original.Width / 2f, -original.Height / 2f);
        canvas.DrawBitmap(original, 0, 0);

        return DataValue.FromImageHandle(new ImageHandle(rotated, outputFormat));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(frame.Source, frame.SidecarRegistry);
        float degrees = arguments[1].AsFloat32();

        string? formatOverride = arguments.Length == 3 ? arguments[2].AsString(frame.Source) : null;
        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        SKBitmap original = inputHandle.GetBitmap("rotate");

        // Compute expanded canvas size to avoid clipping
        double radians = degrees * System.Math.PI / 180.0;
        double sinAngle = System.Math.Abs(System.Math.Sin(radians));
        double cosAngle = System.Math.Abs(System.Math.Cos(radians));

        int newWidth = (int)System.Math.Round(original.Width * cosAngle + original.Height * sinAngle);
        int newHeight = (int)System.Math.Round(original.Width * sinAngle + original.Height * cosAngle);

        SKBitmap rotated = new(newWidth, newHeight);
        using SKCanvas canvas = new(rotated);

        canvas.Clear(SKColors.Transparent);
        canvas.Translate(newWidth / 2f, newHeight / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-original.Width / 2f, -original.Height / 2f);
        canvas.DrawBitmap(original, 0, 0);

        return DataValue.FromImageHandle(new ImageHandle(rotated, outputFormat), frame.Target);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
