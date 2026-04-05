using System.Runtime.InteropServices;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>elastic_deform(img Image, alpha, sigma) → Image</c>. Simard et al.
/// (2003) elastic deformation: generate a per-pixel random displacement
/// field with components in <c>[-1, 1]</c>, smooth each component with a
/// separable Gaussian of sigma <c>sigma</c>, multiply by <c>alpha</c>, then
/// resample the source by bilinear interpolation at the displaced
/// coordinates. Edges clamp at the bitmap border. Marked non-pure.
/// </summary>
public sealed class ElasticDeformFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "elastic_deform";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Simard-style elastic deformation. alpha scales displacement; sigma is the "
        + "Gaussian smoothing radius applied to the random field.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("alpha", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("sigma", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ElasticDeformFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        float alpha = args[1].ToFloat();
        float sigma = args[2].ToFloat();
        if (sigma <= 0f)
        {
            throw new FunctionArgumentException(Name, $"sigma must be positive; got {sigma}.");
        }

        SKBitmap source = args[0].AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int width = rgba.Width;
            int height = rgba.Height;
            int total = width * height;

            Random rng = Random.Shared;
            float[] dx = new float[total];
            float[] dy = new float[total];
            for (int i = 0; i < total; i++)
            {
                dx[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
                dy[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            }

            int radius = System.Math.Max(1, (int)System.Math.Ceiling(sigma * 3));
            float[] kernel = BuildGaussianKernel(radius, sigma);
            dx = SeparableGaussian(dx, width, height, kernel, radius);
            dy = SeparableGaussian(dy, width, height, kernel, radius);
            for (int i = 0; i < total; i++)
            {
                dx[i] *= alpha;
                dy[i] *= alpha;
            }

            ReadOnlySpan<byte> src = rgba.GetPixelSpan();
            byte[] outBytes = new byte[total * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    BilinearSample(src, width, height, x + dx[idx], y + dy[idx], outBytes, idx * 4);
                }
            }

            SKBitmap result = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(outBytes, 0, result.GetPixels(), outBytes.Length);
            return new ValueTask<ValueRef>(ValueRef.FromImage(result));
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static float[] BuildGaussianKernel(int radius, float sigma)
    {
        int size = 2 * radius + 1;
        float[] k = new float[size];
        float sum = 0f;
        float twoSigmaSq = 2f * sigma * sigma;
        for (int i = 0; i < size; i++)
        {
            float d = i - radius;
            k[i] = MathF.Exp(-d * d / twoSigmaSq);
            sum += k[i];
        }
        for (int i = 0; i < size; i++) k[i] /= sum;
        return k;
    }

    private static float[] SeparableGaussian(float[] field, int width, int height, float[] kernel, int radius)
    {
        float[] temp = new float[width * height];
        float[] result = new float[width * height];

        // Horizontal pass.
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                float s = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sx = System.Math.Clamp(x + k, 0, width - 1);
                    s += field[rowBase + sx] * kernel[k + radius];
                }
                temp[rowBase + x] = s;
            }
        }
        // Vertical pass.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float s = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int sy = System.Math.Clamp(y + k, 0, height - 1);
                    s += temp[sy * width + x] * kernel[k + radius];
                }
                result[y * width + x] = s;
            }
        }
        return result;
    }

    private static void BilinearSample(
        ReadOnlySpan<byte> source, int width, int height,
        float fx, float fy, byte[] destination, int destOffset)
    {
        fx = System.Math.Clamp(fx, 0f, width - 1.001f);
        fy = System.Math.Clamp(fy, 0f, height - 1.001f);
        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = System.Math.Min(x0 + 1, width - 1);
        int y1 = System.Math.Min(y0 + 1, height - 1);
        float u = fx - x0;
        float v = fy - y0;

        for (int c = 0; c < 4; c++)
        {
            float tl = source[(y0 * width + x0) * 4 + c];
            float tr = source[(y0 * width + x1) * 4 + c];
            float bl = source[(y1 * width + x0) * 4 + c];
            float br = source[(y1 * width + x1) * 4 + c];
            float top = tl + (tr - tl) * u;
            float bot = bl + (br - bl) * u;
            float val = top + (bot - top) * v;
            destination[destOffset + c] =
                val <= 0f ? (byte)0 : val >= 255f ? (byte)255 : (byte)val;
        }
    }
}
