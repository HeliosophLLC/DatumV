using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>perspective_warp</c> — projects the four image corners onto new
/// destination corners.
/// <list type="bullet">
///   <item><c>perspective_warp(img Image, intensity) → Image</c> — random
///   perspective distortion; <c>intensity</c> bounds each corner's
///   displacement as a fraction of the image dimensions. Non-pure.</item>
///   <item><c>perspective_warp(img Image, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y) → Image</c>
///   — explicit normalised destination corner coordinates (0–1 in each
///   axis, corresponding to bitmap-space (0,0)–(W,H)). Pure for fixed
///   corners, but the function's IsPure is false to cover the random
///   variant; CSE won't collapse repeated calls.</item>
/// </list>
/// </summary>
public sealed class PerspectiveWarpFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "perspective_warp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Perspective distortion. Intensity form: random corner displacement bounded by intensity. "
        + "Explicit form: normalised 0–1 destination corner coordinates for tl/tr/bl/br.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",     DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("intensity", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("tl_x",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("tl_y",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("tr_x",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("tr_y",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("bl_x",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("bl_y",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("br_x",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("br_y",  DataKindMatcher.Family(DataKindFamily.NumericScalar)),
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
        FunctionMetadata.Validate<PerspectiveWarpFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
            {
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
            }
        }

        SKBitmap source = args[0].AsImage();
        float w = source.Width;
        float h = source.Height;
        SKPoint[] srcCorners = [new(0, 0), new(w, 0), new(0, h), new(w, h)];
        SKPoint[] dstCorners = args.Length == 2
            ? RandomCorners(w, h, args[1].ToFloat())
            : ExplicitCorners(w, h, args);

        SKMatrix matrix = SolvePerspectiveMatrix(srcCorners, dstCorners);

        SKBitmap output = new(source.Width, source.Height);
        using (SKCanvas canvas = new(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(source, 0, 0);
        }
        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }

    private static SKPoint[] RandomCorners(float w, float h, float intensity)
    {
        Random rng = Random.Shared;
        return
        [
            new((float)(rng.NextDouble() * intensity * w),
                (float)(rng.NextDouble() * intensity * h)),
            new(w - (float)(rng.NextDouble() * intensity * w),
                (float)(rng.NextDouble() * intensity * h)),
            new((float)(rng.NextDouble() * intensity * w),
                h - (float)(rng.NextDouble() * intensity * h)),
            new(w - (float)(rng.NextDouble() * intensity * w),
                h - (float)(rng.NextDouble() * intensity * h)),
        ];
    }

    private static SKPoint[] ExplicitCorners(float w, float h, ReadOnlySpan<ValueRef> args) =>
    [
        new(args[1].ToFloat() * w, args[2].ToFloat() * h),
        new(args[3].ToFloat() * w, args[4].ToFloat() * h),
        new(args[5].ToFloat() * w, args[6].ToFloat() * h),
        new(args[7].ToFloat() * w, args[8].ToFloat() * h),
    ];

    /// <summary>
    /// Solves the 8-equation linear system for a perspective matrix mapping
    /// the four source corners onto the four destination corners.
    /// </summary>
    private static SKMatrix SolvePerspectiveMatrix(SKPoint[] src, SKPoint[] dst)
    {
        double x0 = src[0].X, y0 = src[0].Y;
        double x1 = src[1].X, y1 = src[1].Y;
        double x2 = src[2].X, y2 = src[2].Y;
        double x3 = src[3].X, y3 = src[3].Y;
        double u0 = dst[0].X, v0 = dst[0].Y;
        double u1 = dst[1].X, v1 = dst[1].Y;
        double u2 = dst[2].X, v2 = dst[2].Y;
        double u3 = dst[3].X, v3 = dst[3].Y;

        double[,] a =
        {
            { x0, y0, 1, 0, 0, 0, -u0 * x0, -u0 * y0 },
            { 0, 0, 0, x0, y0, 1, -v0 * x0, -v0 * y0 },
            { x1, y1, 1, 0, 0, 0, -u1 * x1, -u1 * y1 },
            { 0, 0, 0, x1, y1, 1, -v1 * x1, -v1 * y1 },
            { x2, y2, 1, 0, 0, 0, -u2 * x2, -u2 * y2 },
            { 0, 0, 0, x2, y2, 1, -v2 * x2, -v2 * y2 },
            { x3, y3, 1, 0, 0, 0, -u3 * x3, -u3 * y3 },
            { 0, 0, 0, x3, y3, 1, -v3 * x3, -v3 * y3 },
        };
        double[] b = [u0, v0, u1, v1, u2, v2, u3, v3];
        double[] x = GaussianEliminate(a, b);
        return new SKMatrix(
            (float)x[0], (float)x[1], (float)x[2],
            (float)x[3], (float)x[4], (float)x[5],
            (float)x[6], (float)x[7], 1f);
    }

    /// <summary>Gaussian elimination with partial pivoting for an 8×8 system.</summary>
    private static double[] GaussianEliminate(double[,] matrix, double[] rhs)
    {
        const int n = 8;
        double[,] aug = new double[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++) aug[r, c] = matrix[r, c];
            aug[r, n] = rhs[r];
        }

        for (int col = 0; col < n; col++)
        {
            int maxRow = col;
            double maxVal = System.Math.Abs(aug[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = System.Math.Abs(aug[r, col]);
                if (v > maxVal) { maxVal = v; maxRow = r; }
            }
            if (maxRow != col)
            {
                for (int k = 0; k <= n; k++)
                {
                    (aug[col, k], aug[maxRow, k]) = (aug[maxRow, k], aug[col, k]);
                }
            }
            double pivot = aug[col, col];
            if (pivot == 0.0)
            {
                throw new FunctionArgumentException(Name,
                    "perspective corner configuration produces a degenerate (non-invertible) system.");
            }
            for (int r = col + 1; r < n; r++)
            {
                double factor = aug[r, col] / pivot;
                for (int k = col; k <= n; k++) aug[r, k] -= factor * aug[col, k];
            }
        }

        double[] result = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = aug[r, n];
            for (int c = r + 1; c < n; c++) sum -= aug[r, c] * result[c];
            result[r] = sum / aug[r, r];
        }
        return result;
    }
}
