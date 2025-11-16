using DatumIngest.Model;

using SkiaSharp;

namespace DatumIngest.Functions.Image;

/// <summary>
/// Shared helper for computing resolution-dependent supplemental Query Unit costs
/// from image function arguments. Extracts the decoded bitmap dimensions from the
/// first <see cref="DataKind.Image"/> argument and applies the standard
/// pixels-per-QU formula.
/// </summary>
internal static class ImageCostHelper
{
    /// <summary>
    /// No-arg variant kept for the <see cref="ICostAwareFunction"/> interface contract,
    /// but unreachable now that every image function lowers to a fused pipeline at plan
    /// time. The frame-aware overload below is the only path that actually runs.
    /// </summary>
    internal static long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "ImageCostHelper.ComputeSupplementalCost(arguments) must not be reached: " +
            "image functions are lowered to FusedImagePipelineExpression at plan time, " +
            "and the frame-aware overload is the only path that should run.");

    /// <summary>
    /// Frame-aware cost calculation. Reads byte payloads via
    /// <paramref name="frame"/>'s source store and sidecar registry so arena-backed and
    /// sidecar-backed images resolve correctly.
    /// </summary>
    internal static long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].Kind is not (DataKind.Image or DataKind.UInt8Array) || arguments[i].IsNull)
            {
                continue;
            }

            ReadOnlySpan<byte> imageBytes = arguments[i].AsByteSpan(frame.Source, frame.SidecarRegistry);

            using MemoryStream stream = new(imageBytes.ToArray(), writable: false);
            using SKCodec? codec = SKCodec.Create(stream);

            if (codec is not null)
            {
                long pixelCount = (long)codec.Info.Width * codec.Info.Height;
                return pixelCount / ICostAwareFunction.PixelsPerQueryUnit;
            }
        }

        return 0;
    }
}
