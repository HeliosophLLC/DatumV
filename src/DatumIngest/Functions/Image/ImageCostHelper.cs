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
    /// Computes the supplemental QU cost based on the pixel count of the first
    /// image argument. Returns zero when the image is small enough that only the
    /// base cost applies, or when no image argument is found.
    /// </summary>
    /// <param name="arguments">The evaluated function arguments.</param>
    /// <returns>Supplemental QU to add on top of the function's base cost.</returns>
    /// <remarks>
    /// Inline-values only: throws if any image argument is arena- or sidecar-backed.
    /// Use <see cref="ComputeSupplementalCost(ReadOnlySpan{DataValue}, in InvocationFrame)"/>
    /// for the production path that reads from a live store / sidecar registry.
    /// </remarks>
    internal static long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].Kind is not (DataKind.Image or DataKind.UInt8Array) || arguments[i].IsNull)
            {
                continue;
            }

            // Fast path: ImageHandle with already-decoded bitmap (typical for chained pipelines
            // where a prior function already decoded the bitmap on this same handle).
            ImageHandle? handle = arguments[i].TryGetOwnedImageHandle();

            if (handle is not null && handle.HasBitmap)
            {
                SKBitmap bitmap = handle.GetBitmap("cost");
                long pixelCount = (long)bitmap.Width * bitmap.Height;
                return pixelCount / ICostAwareFunction.PixelsPerQueryUnit;
            }

            // Slow path: payload is raw bytes (provider-sourced column). Read dimensions
            // from the image container header via SKCodec — no full pixel decode required.
            byte[] imageBytes = arguments[i].Kind == DataKind.Image
                ? arguments[i].AsImage()
                : arguments[i].AsUInt8Array();

            using MemoryStream stream = new(imageBytes, writable: false);
            using SKCodec? codec = SKCodec.Create(stream);

            if (codec is not null)
            {
                long pixelCount = (long)codec.Info.Width * codec.Info.Height;
                return pixelCount / ICostAwareFunction.PixelsPerQueryUnit;
            }
        }

        return 0;
    }

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

            ImageHandle? handle = arguments[i].TryGetOwnedImageHandle();

            if (handle is not null && handle.HasBitmap)
            {
                SKBitmap bitmap = handle.GetBitmap("cost");
                long pixelCount = (long)bitmap.Width * bitmap.Height;
                return pixelCount / ICostAwareFunction.PixelsPerQueryUnit;
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
