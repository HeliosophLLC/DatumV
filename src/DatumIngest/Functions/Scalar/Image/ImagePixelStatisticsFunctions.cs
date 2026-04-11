using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// <c>image_pixel_mean(img) → Float32</c> — mean over R, G, B (alpha excluded).
/// <c>image_pixel_mean(img, channels Array&lt;Int32&gt;) → Array&lt;Float32&gt;</c>
/// — per-channel means for the requested indices (0=R, 1=G, 2=B, 3=A).
/// </summary>
public sealed class ImagePixelMeanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_pixel_mean";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Mean pixel value. Scalar form averages R, G, B (alpha excluded). "
        + "With a channels Array<Int32> (values 0=R, 1=G, 2=B, 3=A) returns per-channel means as Array<Float32>.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",    DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("channels", DataKindMatcher.Exact(DataKind.Int32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImagePixelMeanFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        bool perChannel = args.Length == 2;

        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(perChannel
                ? ValueRef.NullArray(DataKind.Float32)
                : ValueRef.Null(DataKind.Float32));
        }

        int[]? channels = perChannel ? ImagePixelStats.ReadChannelIndices(args[1], Name) : null;

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int totalPixels = rgba.Width * rgba.Height;
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();

            if (channels is null)
            {
                if (totalPixels == 0)
                {
                    return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
                }
                double sum = 0.0;
                for (int i = 0; i < totalPixels; i++)
                {
                    int o = i * 4;
                    sum += pixels[o] + pixels[o + 1] + pixels[o + 2];
                }
                float mean = (float)(sum / ((double)totalPixels * 3));
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(mean));
            }

            float[] result = new float[channels.Length];
            if (totalPixels > 0)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    int ch = channels[c];
                    double sum = 0.0;
                    for (int i = 0; i < totalPixels; i++)
                    {
                        sum += pixels[i * 4 + ch];
                    }
                    result[c] = (float)(sum / totalPixels);
                }
            }
            return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}

/// <summary>
/// <c>image_pixel_std(img) → Float32</c> — population std over R, G, B.
/// <c>image_pixel_std(img, channels Array&lt;Int32&gt;) → Array&lt;Float32&gt;</c>
/// — per-channel population stds for the requested indices.
/// </summary>
public sealed class ImagePixelStdFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_pixel_std";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Population standard deviation of pixel values. Scalar form aggregates R, G, B (alpha excluded). "
        + "With a channels Array<Int32> (0=R, 1=G, 2=B, 3=A) returns per-channel stds as Array<Float32>.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image",    DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("channels", DataKindMatcher.Exact(DataKind.Int32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImagePixelStdFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];
        bool perChannel = args.Length == 2;

        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(perChannel
                ? ValueRef.NullArray(DataKind.Float32)
                : ValueRef.Null(DataKind.Float32));
        }

        int[]? channels = perChannel ? ImagePixelStats.ReadChannelIndices(args[1], Name) : null;

        SKBitmap source = imgArg.AsImage();
        SKBitmap rgba = ImagePixelAccess.AsRgba8888(source, out SKBitmap? owned);
        try
        {
            int totalPixels = rgba.Width * rgba.Height;
            ReadOnlySpan<byte> pixels = rgba.GetPixelSpan();

            if (channels is null)
            {
                if (totalPixels == 0)
                {
                    return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
                }
                long n = (long)totalPixels * 3;
                double sum = 0.0;
                for (int i = 0; i < totalPixels; i++)
                {
                    int o = i * 4;
                    sum += pixels[o] + pixels[o + 1] + pixels[o + 2];
                }
                double mean = sum / n;
                double squared = 0.0;
                for (int i = 0; i < totalPixels; i++)
                {
                    int o = i * 4;
                    double dr = pixels[o] - mean;
                    double dg = pixels[o + 1] - mean;
                    double db = pixels[o + 2] - mean;
                    squared += dr * dr + dg * dg + db * db;
                }
                float std = (float)System.Math.Sqrt(squared / n);
                return new ValueTask<ValueRef>(ValueRef.FromFloat32(std));
            }

            float[] result = new float[channels.Length];
            if (totalPixels > 0)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    int ch = channels[c];
                    double sum = 0.0;
                    for (int i = 0; i < totalPixels; i++)
                    {
                        sum += pixels[i * 4 + ch];
                    }
                    double mean = sum / totalPixels;
                    double squared = 0.0;
                    for (int i = 0; i < totalPixels; i++)
                    {
                        double d = pixels[i * 4 + ch] - mean;
                        squared += d * d;
                    }
                    result[c] = (float)System.Math.Sqrt(squared / totalPixels);
                }
            }
            return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
        }
        finally
        {
            owned?.Dispose();
        }
    }
}

internal static class ImagePixelStats
{
    /// <summary>
    /// Reads a <c>channels</c> argument into an <c>int[]</c>, validating that
    /// each value is in <c>[0, 3]</c> (R=0, G=1, B=2, A=3). Accepts both the
    /// primitive <c>int[]</c> materialised form (built by
    /// <see cref="ValueRef.FromPrimitiveArray{T}"/>) and the <c>ValueRef[]</c>
    /// form (built by SQL array literals).
    /// </summary>
    public static int[] ReadChannelIndices(ValueRef arg, string funcName)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(funcName, "channels argument is null.");
        }

        int[] result;
        if (arg.Materialized is int[] direct)
        {
            result = new int[direct.Length];
            Array.Copy(direct, result, direct.Length);
        }
        else
        {
            ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
            result = new int[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                if (!elements[i].TryToInt32(out int v))
                {
                    throw new FunctionArgumentException(funcName,
                        $"channels[{i}] of kind {elements[i].Kind} could not be widened to Int32.");
                }
                result[i] = v;
            }
        }

        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] is < 0 or > 3)
            {
                throw new FunctionArgumentException(funcName,
                    $"channels[{i}] = {result[i]} is out of range; expected 0 (R), 1 (G), 2 (B), or 3 (A).");
            }
        }
        return result;
    }
}
