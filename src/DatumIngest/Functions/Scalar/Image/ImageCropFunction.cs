using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Crops a rectangular region out of an image. Two call shapes:
/// <list type="bullet">
///   <item><c>image_crop(image, x, y, w, h)</c> — explicit coordinates.</item>
///   <item><c>image_crop(image, rect)</c> — <c>rect</c> is a struct with
///   numeric fields <c>x</c>, <c>y</c>, <c>w</c>, <c>h</c> (case-insensitive).</item>
/// </list>
/// All numeric kinds accepted for the coordinate values (float values
/// truncate to integer pixel offsets). Returns a null Image when any argument
/// is null.
/// </summary>
public sealed class ImageCropFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "image_crop";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Crops a rectangular region from an image. "
        + "Coordinates are in pixels; float values truncate to integers. "
        + "Pass either four numeric coordinates or a struct {x, y, w, h}.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("x",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("y",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("w",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("h",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("rect",  DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ImageCropFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef imgArg = args[0];

        if (imgArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        int x, y, w, h;
        if (args.Length == 2)
        {
            ValueRef rectArg = args[1];
            if (rectArg.IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));

            (x, y, w, h) = ReadRectStruct(rectArg, frame);
        }
        else
        {
            ValueRef xArg = args[1];
            ValueRef yArg = args[2];
            ValueRef wArg = args[3];
            ValueRef hArg = args[4];

            if (xArg.IsNull || yArg.IsNull || wArg.IsNull || hArg.IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));

            x = xArg.ToInt32();
            y = yArg.ToInt32();
            w = wArg.ToInt32();
            h = hArg.ToInt32();
        }

        SKBitmap source = imgArg.AsImage();

        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"image_crop: width and height must be positive, got w={w}, h={h}.");
        }

        var rect = new SKRectI(x, y, x + w, y + h);
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, rect))
        {
            throw new InvalidOperationException(
                $"image_crop: region ({x},{y},{w},{h}) falls outside the {source.Width}×{source.Height} image.");
        }

        // subset shares pixel memory with source — Copy() produces an owned bitmap.
        return new ValueTask<ValueRef>(ValueRef.FromImage(subset.Copy()));
    }

    private static (int X, int Y, int W, int H) ReadRectStruct(ValueRef rect, EvaluationFrame frame)
    {
        TypeDescriptor? desc = frame.Types?.GetDescriptor(rect.TypeId);
        if (desc?.Fields is null)
        {
            throw new FunctionArgumentException(Name,
                "rect struct has no registered field descriptors; expected fields x, y, w, h.");
        }

        int xIdx = desc.FindFieldIndex("x");
        int yIdx = desc.FindFieldIndex("y");
        int wIdx = desc.FindFieldIndex("w");
        int hIdx = desc.FindFieldIndex("h");
        if (xIdx < 0 || yIdx < 0 || wIdx < 0 || hIdx < 0)
        {
            throw new FunctionArgumentException(Name,
                $"rect struct must have fields x, y, w, h; got [{string.Join(", ", desc.Fields.Select(f => f.Name))}].");
        }

        ReadOnlySpan<ValueRef> fields = rect.GetStructFields();
        return (fields[xIdx].ToInt32(), fields[yIdx].ToInt32(),
                fields[wIdx].ToInt32(), fields[hIdx].ToInt32());
    }
}
