using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Image;

/// <summary>
/// <c>compression_artifact_score(img) → Float32</c>. JPEG blockiness
/// estimator in <c>[0, 1]</c>. Compares the average absolute luminance
/// gradient at 8×8 block boundaries against the gradient at interior
/// pixels: <c>(boundary − interior) / interior</c>, clamped to
/// <c>[0, 1]</c>. Larger values indicate more visible block edges.
/// Returns 0 for images smaller than 16×16 (no full block boundary to
/// measure against).
/// </summary>
public sealed class CompressionArtifactScoreFunction : IFunction, IScalarFunction
{
    private const int BlockSize = 8;

    /// <inheritdoc />
    public static string Name => "compression_artifact_score";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "JPEG blockiness score in [0, 1] measuring 8×8 block boundary discontinuities relative to interior gradients.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CompressionArtifactScoreFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef imgArg = arguments.Span[0];
        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Float32));
        }

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            if (width < BlockSize * 2 || height < BlockSize * 2)
            {
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
            }

            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();
            float[] gray = new float[width * height];
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 4;
                gray[i] = pixels[o] * ImagePixelAccess.Bt601RedWeight
                        + pixels[o + 1] * ImagePixelAccess.Bt601GreenWeight
                        + pixels[o + 2] * ImagePixelAccess.Bt601BlueWeight;
            }

            double boundarySum = 0.0;
            int boundaryCount = 0;
            double interiorSum = 0.0;
            int interiorCount = 0;

            // Vertical block boundaries (every Nth column).
            for (int x = BlockSize; x < width - 1; x += BlockSize)
            {
                for (int y = 0; y < height; y++)
                {
                    boundarySum += System.Math.Abs(gray[y * width + x] - gray[y * width + x - 1]);
                    boundaryCount++;
                }
            }
            // Horizontal block boundaries (every Nth row).
            for (int y = BlockSize; y < height - 1; y += BlockSize)
            {
                int row = y * width;
                int prevRow = row - width;
                for (int x = 0; x < width; x++)
                {
                    boundarySum += System.Math.Abs(gray[row + x] - gray[prevRow + x]);
                    boundaryCount++;
                }
            }
            // Interior horizontal gradients (skip the block-boundary columns).
            for (int x = 1; x < width; x++)
            {
                if (x % BlockSize == 0) continue;
                for (int y = 0; y < height; y++)
                {
                    interiorSum += System.Math.Abs(gray[y * width + x] - gray[y * width + x - 1]);
                    interiorCount++;
                }
            }
            // Interior vertical gradients (skip the block-boundary rows).
            for (int y = 1; y < height; y++)
            {
                if (y % BlockSize == 0) continue;
                int row = y * width;
                int prevRow = row - width;
                for (int x = 0; x < width; x++)
                {
                    interiorSum += System.Math.Abs(gray[row + x] - gray[prevRow + x]);
                    interiorCount++;
                }
            }

            if (boundaryCount == 0 || interiorCount == 0)
            {
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
            }

            double avgBoundary = boundarySum / boundaryCount;
            double avgInterior = interiorSum / interiorCount;

            float score = avgInterior > 0.001
                ? (float)System.Math.Clamp((avgBoundary - avgInterior) / avgInterior, 0.0, 1.0)
                : 0f;
            return new ValueTask<ValueRef>(ValueRef.FromFloat32(score));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
