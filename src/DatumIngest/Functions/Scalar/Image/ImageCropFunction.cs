using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Image;

/// <summary>
/// Crops a rectangular region out of an image. Three call shapes:
/// <list type="bullet">
///   <item><c>image_crop(image, x, y, w, h)</c> — explicit coordinates,
///   returns a single Image.</item>
///   <item><c>image_crop(image, rect)</c> — <c>rect</c> is a struct with
///   numeric fields <c>x</c>, <c>y</c>, <c>w</c>, <c>h</c> (case-insensitive),
///   returns a single Image.</item>
///   <item><c>image_crop(image, rects)</c> — <c>rects</c> is an array of
///   such structs; returns <c>Array&lt;Image&gt;</c>, one cropped image
///   per rect in input order.</item>
/// </list>
/// All numeric kinds accepted for the coordinate values (float values
/// truncate to integer pixel offsets). Returns a null Image when the image
/// or rect argument is null.
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
                new ParameterSpec("rect",  DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("image", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("rects", DataKindMatcher.Exact(DataKind.Struct), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image))),
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

        SKBitmap source = imgArg.AsImage();

        // 5-arg form: explicit coordinates.
        if (args.Length == 5)
        {
            ValueRef xArg = args[1];
            ValueRef yArg = args[2];
            ValueRef wArg = args[3];
            ValueRef hArg = args[4];

            if (xArg.IsNull || yArg.IsNull || wArg.IsNull || hArg.IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));

            ValueRef cropped = CropOne(source, xArg.ToInt32(), yArg.ToInt32(),
                                              wArg.ToInt32(), hArg.ToInt32());
            return new ValueTask<ValueRef>(cropped);
        }

        // 2-arg form: struct or struct array.
        ValueRef rectArg = args[1];
        if (rectArg.IsNull)
        {
            return rectArg.IsArray
                ? new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Image))
                : new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        if (rectArg.IsArray)
        {
            ReadOnlySpan<ValueRef> rectStructs = rectArg.GetArrayElements();
            ValueRef[] images = new ValueRef[rectStructs.Length];
            for (int i = 0; i < rectStructs.Length; i++)
            {
                ValueRef element = rectStructs[i];
                if (element.IsNull)
                {
                    images[i] = ValueRef.Null(DataKind.Image);
                    continue;
                }
                (int x, int y, int w, int h) = ReadRectStruct(element, frame);
                images[i] = CropOne(source, x, y, w, h);
            }
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Image, images));
        }

        (int sx, int sy, int sw, int sh) = ReadRectStruct(rectArg, frame);
        return new ValueTask<ValueRef>(CropOne(source, sx, sy, sw, sh));
    }

    private static ValueRef CropOne(SKBitmap source, int x, int y, int w, int h)
    {
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
        return ValueRef.FromImage(subset.Copy());
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
