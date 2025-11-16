namespace DatumIngest.Functions.Image;

using DatumIngest.Functions;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Applies a perspective warp transformation to an image.
/// Two overloads:
/// <list type="bullet">
///   <item><c>perspective_warp(img, intensity[, format])</c> — random perspective distortion where
///     <c>intensity</c> controls the maximum corner displacement as a fraction of image dimensions.</item>
///   <item><c>perspective_warp(img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, format])</c>
///     — explicit corner offsets (normalized 0–1 coordinates for each destination corner).</item>
/// </list>
/// </summary>
public sealed class PerspectiveWarpFunction : IScalarFunction, ICostAwareFunction, IImagePipelineFunction
{
    /// <inheritdoc />
    public string Name => "perspective_warp";

    /// <inheritdoc />
    public int QueryUnitCost => 50;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        // 2 args: img, intensity
        // 3 args: img, intensity, format
        // 9 args: img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y
        // 10 args: img, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y, format
        if (argumentKinds.Length is not (2 or 3 or 9 or 10))
        {
            throw new ArgumentException(
                "perspective_warp() requires 2-3 arguments (image, intensity[, format]) " +
                "or 9-10 arguments (image, tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, format]).");
        }

        if (argumentKinds[0] is not (DataKind.Image or DataKind.UInt8Array))
        {
            throw new ArgumentException(
                $"perspective_warp() first argument must be Image or UInt8Array, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length is 2 or 3)
        {
            if (!DataValue.IsNumericScalarKind(argumentKinds[1]))
            {
                throw new ArgumentException(
                    $"perspective_warp() second argument (intensity) must be numeric, got {argumentKinds[1]}.");
            }
        }
        else
        {
            // 9 or 10 args: corner coordinates at positions 1..8
            string[] cornerNames =
            [
                "tl_x", "tl_y", "tr_x", "tr_y", "bl_x", "bl_y", "br_x", "br_y"
            ];

            for (int i = 0; i < 8; i++)
            {
                if (!DataValue.IsNumericScalarKind(argumentKinds[i + 1]))
                {
                    throw new ArgumentException(
                        $"perspective_warp() argument {i + 2} ({cornerNames[i]}) must be numeric, got {argumentKinds[i + 1]}.");
                }
            }
        }

        // Check optional format argument (last arg if String)
        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.String)
        {
            throw new ArgumentException(
                $"perspective_warp() third argument (format) must be String, got {argumentKinds[2]}.");
        }

        if (argumentKinds.Length == 10 && argumentKinds[9] != DataKind.String)
        {
            throw new ArgumentException(
                $"perspective_warp() tenth argument (format) must be String, got {argumentKinds[9]}.");
        }

        return DataKind.Image;
    }

    /// <inheritdoc />
    public void ValidateAuxiliaryArguments(ReadOnlySpan<DataKind> auxiliaryKinds)
    {
        // Pipeline form drops the implicit image arg. Auxiliary shapes:
        //   [intensity]                                                    -> random warp
        //   [intensity, format]                                            -> random warp with format
        //   [tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y]               -> explicit corners
        //   [tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y, format]       -> explicit corners with format
        if (auxiliaryKinds.Length is not (1 or 2 or 8 or 9))
        {
            throw new ArgumentException(
                "perspective_warp() requires 1-2 auxiliary arguments (intensity[, format]) " +
                "or 8-9 auxiliary arguments (tl_x, tl_y, tr_x, tr_y, bl_x, bl_y, br_x, br_y[, format]).");
        }

        if (auxiliaryKinds.Length is 1 or 2)
        {
            if (auxiliaryKinds[0] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[0]))
            {
                throw new ArgumentException(
                    $"perspective_warp() intensity must be numeric, got {auxiliaryKinds[0]}.");
            }

            if (auxiliaryKinds.Length == 2
                && auxiliaryKinds[1] != DataKind.Unknown
                && auxiliaryKinds[1] != DataKind.String)
            {
                throw new ArgumentException(
                    $"perspective_warp() format must be String, got {auxiliaryKinds[1]}.");
            }
            return;
        }

        // 8 or 9 args: corner coordinates at positions 0..7
        string[] cornerNames =
        [
            "tl_x", "tl_y", "tr_x", "tr_y", "bl_x", "bl_y", "br_x", "br_y"
        ];

        for (int i = 0; i < 8; i++)
        {
            if (auxiliaryKinds[i] != DataKind.Unknown && !DataValue.IsNumericScalarKind(auxiliaryKinds[i]))
            {
                throw new ArgumentException(
                    $"perspective_warp() {cornerNames[i]} must be numeric, got {auxiliaryKinds[i]}.");
            }
        }

        if (auxiliaryKinds.Length == 9
            && auxiliaryKinds[8] != DataKind.Unknown
            && auxiliaryKinds[8] != DataKind.String)
        {
            throw new ArgumentException(
                $"perspective_warp() format must be String, got {auxiliaryKinds[8]}.");
        }
    }

    /// <inheritdoc />
    public SKBitmap Apply(SKBitmap input, ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        float width = input.Width;
        float height = input.Height;

        SKPoint[] sourceCorners =
        [
            new(0, 0),
            new(width, 0),
            new(0, height),
            new(width, height)
        ];

        SKPoint[] destinationCorners;

        if (auxiliaryArgs.Length is 1 or 2)
        {
            float intensity = auxiliaryArgs[0].ToFloat();

            Random random = new();
            destinationCorners =
            [
                new((float)(random.NextDouble() * intensity * width),
                    (float)(random.NextDouble() * intensity * height)),
                new(width - (float)(random.NextDouble() * intensity * width),
                    (float)(random.NextDouble() * intensity * height)),
                new((float)(random.NextDouble() * intensity * width),
                    height - (float)(random.NextDouble() * intensity * height)),
                new(width - (float)(random.NextDouble() * intensity * width),
                    height - (float)(random.NextDouble() * intensity * height))
            ];
        }
        else
        {
            float topLeftX = auxiliaryArgs[0].ToFloat() * width;
            float topLeftY = auxiliaryArgs[1].ToFloat() * height;
            float topRightX = auxiliaryArgs[2].ToFloat() * width;
            float topRightY = auxiliaryArgs[3].ToFloat() * height;
            float bottomLeftX = auxiliaryArgs[4].ToFloat() * width;
            float bottomLeftY = auxiliaryArgs[5].ToFloat() * height;
            float bottomRightX = auxiliaryArgs[6].ToFloat() * width;
            float bottomRightY = auxiliaryArgs[7].ToFloat() * height;

            destinationCorners =
            [
                new(topLeftX, topLeftY),
                new(topRightX, topRightY),
                new(bottomLeftX, bottomLeftY),
                new(bottomRightX, bottomRightY)
            ];
        }

        SKMatrix perspectiveMatrix = ComputePerspectiveMatrix(sourceCorners, destinationCorners);

        SKBitmap transformed = new(input.Width, input.Height);
        using SKCanvas canvas = new(transformed);
        canvas.SetMatrix(perspectiveMatrix);
        canvas.DrawBitmap(input, 0, 0);

        return transformed;
    }

    /// <inheritdoc />
    public SKEncodedImageFormat? FormatOverride(ReadOnlySpan<DataValue> auxiliaryArgs)
    {
        // Format is the trailing String arg at position 1 (intensity form) or 8 (explicit form).
        int formatIndex = auxiliaryArgs.Length switch
        {
            2 => 1,
            9 => 8,
            _ => -1,
        };

        if (formatIndex < 0 || auxiliaryArgs[formatIndex].IsNull)
        {
            return null;
        }
        return ImageEncoder.ParseFormatString(auxiliaryArgs[formatIndex].AsString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments) =>
        throw new InvalidOperationException(
            "perspective_warp() must be lowered to a FusedImagePipelineExpression at plan time " +
            "and should never reach the runtime evaluator. This indicates the " +
            "ImagePipelineLowerer pass did not run, or ran but failed to lower this call.");

    /// <summary>
    /// Computes a perspective transformation matrix that maps the four source corners
    /// to the four destination corners by solving the 8-equation linear system.
    /// </summary>
    private static SKMatrix ComputePerspectiveMatrix(SKPoint[] source, SKPoint[] destination)
    {
        // Solve for the 8 unknowns of the perspective matrix using the
        // standard 4-point correspondence system:
        //   x' = (a*x + b*y + c) / (g*x + h*y + 1)
        //   y' = (d*x + e*y + f) / (g*x + h*y + 1)
        //
        // Rearranged into Ax = b form with 8 unknowns [a, b, c, d, e, f, g, h].

        float x0 = source[0].X, y0 = source[0].Y;
        float x1 = source[1].X, y1 = source[1].Y;
        float x2 = source[2].X, y2 = source[2].Y;
        float x3 = source[3].X, y3 = source[3].Y;

        float u0 = destination[0].X, v0 = destination[0].Y;
        float u1 = destination[1].X, v1 = destination[1].Y;
        float u2 = destination[2].X, v2 = destination[2].Y;
        float u3 = destination[3].X, v3 = destination[3].Y;

        // Build 8×8 matrix A and 8-vector b
        double[,] matrixA =
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

        double[] vectorB = [u0, v0, u1, v1, u2, v2, u3, v3];

        double[] solution = SolveLinearSystem(matrixA, vectorB);

        return new SKMatrix(
            (float)solution[0], (float)solution[1], (float)solution[2],
            (float)solution[3], (float)solution[4], (float)solution[5],
            (float)solution[6], (float)solution[7], 1f);
    }

    /// <summary>
    /// Solves an 8×8 linear system Ax = b using Gaussian elimination with partial pivoting.
    /// </summary>
    private static double[] SolveLinearSystem(double[,] matrix, double[] vector)
    {
        int size = 8;
        double[,] augmented = new double[size, size + 1];

        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, size] = vector[row];
        }

        // Forward elimination with partial pivoting
        for (int column = 0; column < size; column++)
        {
            int maxRow = column;
            double maxValue = System.Math.Abs(augmented[column, column]);

            for (int row = column + 1; row < size; row++)
            {
                double absValue = System.Math.Abs(augmented[row, column]);

                if (absValue > maxValue)
                {
                    maxValue = absValue;
                    maxRow = row;
                }
            }

            // Swap rows
            if (maxRow != column)
            {
                for (int k = 0; k <= size; k++)
                {
                    (augmented[column, k], augmented[maxRow, k]) =
                        (augmented[maxRow, k], augmented[column, k]);
                }
            }

            double pivot = augmented[column, column];

            for (int row = column + 1; row < size; row++)
            {
                double factor = augmented[row, column] / pivot;

                for (int k = column; k <= size; k++)
                {
                    augmented[row, k] -= factor * augmented[column, k];
                }
            }
        }

        // Back substitution
        double[] result = new double[size];

        for (int row = size - 1; row >= 0; row--)
        {
            double sum = augmented[row, size];

            for (int column = row + 1; column < size; column++)
            {
                sum -= augmented[row, column] * result[column];
            }

            result[row] = sum / augmented[row, row];
        }

        return result;
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame) =>
        ImageCostHelper.ComputeSupplementalCost(arguments, in frame);
}
