namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

/// <summary>
/// Loads encoded image bytes (UInt8Array) as an Image value for use with image transform
/// and analysis functions. This is the entry point for the fused image pipeline: the raw
/// bytes are wrapped as <see cref="DataKind.Image"/> without decoding, so downstream
/// transforms benefit from lazy decode via <see cref="ImageHandle"/>.
/// <c>load_image(bytes)</c> accepts UInt8Array (e.g. <c>file_bytes</c> from a ZIP source).
/// </summary>
public sealed class LoadImageFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "load_image";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("load_image() requires exactly 1 argument.");
        }

        if (argumentKinds[0] is not DataKind.UInt8Array)
        {
            throw new ArgumentException(
                $"load_image() requires UInt8Array, got {argumentKinds[0]}.");
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

        byte[] bytes = input.AsUInt8Array();
        return DataValue.FromImage(bytes);
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        byte[] bytes = input.AsUInt8Array(frame.Source, frame.SidecarRegistry);
        return DataValue.FromImage(bytes, frame.Target);
    }
}
