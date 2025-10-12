namespace DatumIngest.Functions.Image;

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
public sealed class PerspectiveWarpFunction : IScalarFunction, ICostAwareFunction
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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle();
        SKBitmap original = inputHandle.GetBitmap("perspective_warp");

        float width = original.Width;
        float height = original.Height;

        SKPoint[] sourceCorners =
        [
            new(0, 0),             // top-left
            new(width, 0),         // top-right
            new(0, height),        // bottom-left
            new(width, height)     // bottom-right
        ];

        SKPoint[] destinationCorners;
        string? formatOverride;

        if (arguments.Length is 2 or 3)
        {
            // Random perspective warp with intensity
            float intensity = arguments[1].AsFloat32();
            formatOverride = arguments.Length == 3 ? arguments[2].AsString() : null;

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
            // Explicit corner coordinates (normalized 0–1)
            float topLeftX = arguments[1].AsFloat32() * width;
            float topLeftY = arguments[2].AsFloat32() * height;
            float topRightX = arguments[3].AsFloat32() * width;
            float topRightY = arguments[4].AsFloat32() * height;
            float bottomLeftX = arguments[5].AsFloat32() * width;
            float bottomLeftY = arguments[6].AsFloat32() * height;
            float bottomRightX = arguments[7].AsFloat32() * width;
            float bottomRightY = arguments[8].AsFloat32() * height;
            formatOverride = arguments.Length == 10 ? arguments[9].AsString() : null;

            destinationCorners =
            [
                new(topLeftX, topLeftY),
                new(topRightX, topRightY),
                new(bottomLeftX, bottomLeftY),
                new(bottomRightX, bottomRightY)
            ];
        }

        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        // Compute perspective matrix from source → destination corner mapping
        SKMatrix perspectiveMatrix = ComputePerspectiveMatrix(sourceCorners, destinationCorners);

        SKBitmap transformed = new(original.Width, original.Height);
        using SKCanvas canvas = new(transformed);
        canvas.SetMatrix(perspectiveMatrix);
        canvas.DrawBitmap(original, 0, 0);

        return DataValue.FromImageHandle(new ImageHandle(transformed, outputFormat));
    }

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
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        DataValue input = arguments[0];

        if (input.IsNull)
        {
            return DataValue.Null(DataKind.Image);
        }

        ImageHandle inputHandle = input.GetImageHandle(store);
        SKBitmap original = inputHandle.GetBitmap("perspective_warp");

        float width = original.Width;
        float height = original.Height;

        SKPoint[] sourceCorners =
        [
            new(0, 0),             // top-left
            new(width, 0),         // top-right
            new(0, height),        // bottom-left
            new(width, height)     // bottom-right
        ];

        SKPoint[] destinationCorners;
        string? formatOverride;

        if (arguments.Length is 2 or 3)
        {
            // Random perspective warp with intensity
            float intensity = arguments[1].AsFloat32();
            formatOverride = arguments.Length == 3 ? arguments[2].AsString(store) : null;

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
            // Explicit corner coordinates (normalized 0–1)
            float topLeftX = arguments[1].AsFloat32() * width;
            float topLeftY = arguments[2].AsFloat32() * height;
            float topRightX = arguments[3].AsFloat32() * width;
            float topRightY = arguments[4].AsFloat32() * height;
            float bottomLeftX = arguments[5].AsFloat32() * width;
            float bottomLeftY = arguments[6].AsFloat32() * height;
            float bottomRightX = arguments[7].AsFloat32() * width;
            float bottomRightY = arguments[8].AsFloat32() * height;
            formatOverride = arguments.Length == 10 ? arguments[9].AsString(store) : null;

            destinationCorners =
            [
                new(topLeftX, topLeftY),
                new(topRightX, topRightY),
                new(bottomLeftX, bottomLeftY),
                new(bottomRightX, bottomRightY)
            ];
        }

        SKEncodedImageFormat outputFormat = ImageEncoder.ResolveFormat(inputHandle, formatOverride);

        // Compute perspective matrix from source → destination corner mapping
        SKMatrix perspectiveMatrix = ComputePerspectiveMatrix(sourceCorners, destinationCorners);

        SKBitmap transformed = new(original.Width, original.Height);
        using SKCanvas canvas = new(transformed);
        canvas.SetMatrix(perspectiveMatrix);
        canvas.DrawBitmap(original, 0, 0);

        return DataValue.FromImageHandle(new ImageHandle(transformed, outputFormat), store);
    }

    /// <inheritdoc />
    public long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result) =>
        ImageCostHelper.ComputeSupplementalCost(arguments);
}
